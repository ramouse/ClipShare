"""C1 客户端基线：幂等、错误、时间与缓存语义的真实 PostgreSQL 契约测试。"""
import re
import threading
from collections.abc import Iterator
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import text

from app.core.config import get_settings
from app.db.base import Base
from app.db.session import engine
from app.main import app

API_PREFIX = "/api/v1"
RFC3339_UTC = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$")
settings = get_settings()


@pytest.fixture(scope="session", autouse=True)
def _ensure_tables(_verified_sandbox_database: None) -> Iterator[None]:
    Base.metadata.create_all(engine)
    yield


@pytest.fixture(autouse=True)
def _isolated_contract_state(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> Iterator[Path]:
    monkeypatch.setattr(settings, "file_storage_dir", str(tmp_path))
    yield tmp_path
    with engine.begin() as connection:
        connection.execute(text("DELETE FROM idempotency_records"))
        connection.execute(text("DELETE FROM shares"))
        connection.execute(text("DELETE FROM share_files"))
        connection.execute(text("DELETE FROM shortcodes"))


@pytest.fixture()
def client() -> Iterator[TestClient]:
    with TestClient(app) as test_client:
        yield test_client


def test_database_session_timezone_is_utc() -> None:
    """数据库会话必须固定 UTC，不能依赖镜像或宿主机的默认时区。"""
    with engine.connect() as connection:
        assert connection.execute(text("SHOW TIME ZONE")).scalar_one() == "UTC"


def test_share_idempotency_replays_same_resource_and_stores_no_raw_key(
    client: TestClient,
) -> None:
    key = "windows:create:00000001"
    payload = {"content": "C1 idempotent share", "expiry": "24h", "max_views": 5}
    first = client.post(
        f"{API_PREFIX}/shares", json=payload, headers={"Idempotency-Key": key}
    )
    replay = client.post(
        f"{API_PREFIX}/shares", json=payload, headers={"Idempotency-Key": key}
    )

    assert first.status_code == replay.status_code == 201
    assert first.json() == replay.json()
    assert first.headers["idempotency-replayed"] == "false"
    assert replay.headers["idempotency-replayed"] == "true"
    assert first.headers["cache-control"] == replay.headers["cache-control"] == "no-store"
    assert RFC3339_UTC.fullmatch(first.json()["created_at"])
    assert RFC3339_UTC.fullmatch(first.json()["expires_at"])

    with engine.connect() as connection:
        share_count = connection.execute(text("SELECT COUNT(*) FROM shares")).scalar_one()
        record = connection.execute(
            text("SELECT key_hash, request_hash FROM idempotency_records")
        ).one()
    assert share_count == 1
    assert len(record.key_hash) == len(record.request_hash) == 64
    assert key not in record.key_hash


def test_share_idempotency_rejects_key_reuse_for_different_request(
    client: TestClient,
) -> None:
    headers = {"Idempotency-Key": "android:create:00000001"}
    first = client.post(f"{API_PREFIX}/shares", json={"content": "first"}, headers=headers)
    conflict = client.post(
        f"{API_PREFIX}/shares", json={"content": "different"}, headers=headers
    )
    assert first.status_code == 201
    assert conflict.status_code == 409
    assert conflict.headers["content-type"].startswith("application/problem+json")
    assert conflict.headers["cache-control"] == "no-store"
    assert conflict.json()["type"] == "idempotency_conflict"
    with engine.connect() as connection:
        assert connection.execute(text("SELECT COUNT(*) FROM shares")).scalar_one() == 1


def test_concurrent_share_retries_converge_on_one_committed_resource() -> None:
    """两个独立连接同时使用同键时，由事务锁收敛为一次创建和一次重放。"""
    barrier = threading.Barrier(2)
    key = "windows:concurrent:00000001"
    payload = {"content": "concurrent request", "expiry": "7d"}

    def send() -> tuple[int, dict[str, object], str]:
        with TestClient(app) as thread_client:
            barrier.wait(timeout=10)
            response = thread_client.post(
                f"{API_PREFIX}/shares",
                json=payload,
                headers={"Idempotency-Key": key},
            )
            return (
                response.status_code,
                response.json(),
                response.headers["idempotency-replayed"],
            )

    with ThreadPoolExecutor(max_workers=2) as executor:
        results = list(executor.map(lambda _: send(), range(2)))

    assert [status for status, _, _ in results] == [201, 201]
    assert results[0][1] == results[1][1]
    assert {replayed for _, _, replayed in results} == {"false", "true"}
    with engine.connect() as connection:
        assert connection.execute(text("SELECT COUNT(*) FROM shares")).scalar_one() == 1
        assert (
            connection.execute(text("SELECT COUNT(*) FROM idempotency_records")).scalar_one()
            == 1
        )


def test_invalid_idempotency_key_is_problem_detail(client: TestClient) -> None:
    response = client.post(
        f"{API_PREFIX}/shares",
        json={"content": "invalid key"},
        headers={"Idempotency-Key": "bad key"},
    )
    assert response.status_code == 400
    assert response.headers["content-type"].startswith("application/problem+json")
    assert response.headers["cache-control"] == "no-store"
    assert response.json() == {
        "type": "idempotency_key_invalid",
        "title": "幂等键格式错误",
        "status": 400,
        "detail": "Idempotency-Key 必须为 8–128 位 ASCII，且只能包含字母、数字和 ._~:/+-",
    }


def test_idempotent_create_does_not_purge_unrelated_expired_records_in_business_lock(
    client: TestClient,
) -> None:
    with engine.begin() as connection:
        connection.execute(
            text(
                """
                INSERT INTO idempotency_records (
                    operation, key_hash, request_hash, resource_kind, resource_code, expires_at
                ) VALUES (
                    'create_share', :key_hash, :request_hash, 'share', 'gone00',
                    TIMESTAMP '2026-01-01 00:00:00'
                )
                """
            ),
            {"key_hash": "a" * 64, "request_hash": "b" * 64},
        )
    response = client.post(
        f"{API_PREFIX}/shares",
        json={"content": "do not cross-lock unrelated keys"},
        headers={"Idempotency-Key": "windows:no-cross-lock:0001"},
    )
    assert response.status_code == 201
    with engine.connect() as connection:
        records = connection.execute(
            text("SELECT key_hash, expires_at FROM idempotency_records ORDER BY key_hash")
        ).all()
    assert len(records) == 2
    assert any(record.key_hash == "a" * 64 for record in records)


def test_file_idempotency_replays_without_leaving_duplicate_disk_file(
    client: TestClient, _isolated_contract_state: Path
) -> None:
    headers = {"Idempotency-Key": "windows:upload:00000001"}
    upload = {"file": ("note.txt", b"same bytes", "text/plain")}
    first = client.post(f"{API_PREFIX}/files", files=upload, headers=headers)
    replay = client.post(f"{API_PREFIX}/files", files=upload, headers=headers)

    assert first.status_code == replay.status_code == 201
    assert first.json() == replay.json()
    assert first.headers["idempotency-replayed"] == "false"
    assert replay.headers["idempotency-replayed"] == "true"
    assert RFC3339_UTC.fullmatch(first.json()["created_at"])
    with engine.connect() as connection:
        assert connection.execute(text("SELECT COUNT(*) FROM share_files")).scalar_one() == 1
        assert (
            connection.execute(text("SELECT COUNT(*) FROM idempotency_records")).scalar_one()
            == 1
        )
    assert len(list(_isolated_contract_state.iterdir())) == 1


def test_file_idempotency_conflict_cleans_second_temporary_upload(
    client: TestClient, _isolated_contract_state: Path
) -> None:
    headers = {"Idempotency-Key": "android:upload:00000001"}
    first = client.post(
        f"{API_PREFIX}/files",
        files={"file": ("note.txt", b"first bytes", "text/plain")},
        headers=headers,
    )
    conflict = client.post(
        f"{API_PREFIX}/files",
        files={"file": ("note.txt", b"different bytes", "text/plain")},
        headers=headers,
    )
    assert first.status_code == 201
    assert conflict.status_code == 409
    assert conflict.json()["type"] == "idempotency_conflict"
    with engine.connect() as connection:
        assert connection.execute(text("SELECT COUNT(*) FROM share_files")).scalar_one() == 1
    assert len(list(_isolated_contract_state.iterdir())) == 1


def test_framework_errors_preserve_allow_and_use_problem_media_type(
    client: TestClient,
) -> None:
    response = client.put(f"{API_PREFIX}/shares", json={"content": "wrong method"})
    assert response.status_code == 405
    assert "POST" in response.headers["allow"]
    assert response.headers["content-type"].startswith("application/problem+json")
    assert response.headers["cache-control"] == "no-store"
    assert set(response.json()) == {"type", "title", "status", "detail"}


def test_get_path_validation_uses_problem_details_contract(client: TestClient) -> None:
    response = client.get(f"{API_PREFIX}/shares/not-valid!/raw")
    assert response.status_code == 422
    assert response.headers["content-type"].startswith("application/problem+json")
    assert response.headers["cache-control"] == "no-store"
    assert response.json()["type"] == "validation_error"
