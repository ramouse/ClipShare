"""分享 REST API 集成测试：真实 FastAPI 应用 + PostgreSQL。"""
from collections.abc import Iterator
from datetime import datetime, timedelta

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import text

from app.core.config import get_settings
from app.core.time import utcnow
from app.db.base import Base
from app.db.repository import ShareRepository
from app.db.session import SessionLocal, engine
from app.main import app

settings = get_settings()
API_PREFIX = "/api/v1"
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"
XSS_PAYLOAD = '<script>alert("xss")</script>'


@pytest.fixture(scope="session", autouse=True)
def _ensure_tables() -> Iterator[None]:
    """会话级幂等建表：兼容未跑迁移的数据库环境（与 conftest 保持一致）。"""
    Base.metadata.create_all(engine)
    yield


@pytest.fixture(autouse=True)
def _clean_shares() -> Iterator[None]:
    """每个用例结束后清空 shares 表，保证用例间数据互不干扰。"""
    yield
    with engine.begin() as conn:
        conn.execute(text("DELETE FROM shares"))


@pytest.fixture()
def client() -> Iterator[TestClient]:
    """API 测试客户端：复用生产应用实例（含限流、安全头、异常处理器）。"""
    with TestClient(app) as test_client:
        yield test_client


def _create_share(client: TestClient, content: str = "hello", **overrides: object) -> dict:
    """通过 API 创建分享并断言成功，返回响应体。"""
    payload: dict[str, object] = {"content": content, **overrides}
    response = client.post(f"{API_PREFIX}/shares", json=payload)
    assert response.status_code == 201, response.text
    return response.json()


def test_create_share_success(client: TestClient) -> None:
    """创建成功：201 + 完整响应形状 + url 拼接 + expires_at 计算。"""
    response = client.post(
        f"{API_PREFIX}/shares",
        json={"content": "你好，ClipShare", "expiry": "7d", "max_views": 5},
    )
    assert response.status_code == 201
    assert response.headers["content-type"].startswith("application/json")
    body = response.json()
    assert set(body) == {"code", "url", "expires_at", "max_views", "created_at"}
    assert body["code"] != ""
    assert body["max_views"] == 5
    assert body["url"] == f"{settings.public_base_url.rstrip('/')}/s/{body['code']}"
    created_at = datetime.fromisoformat(body["created_at"])
    expires_at = datetime.fromisoformat(body["expires_at"])
    # 应用侧 now 与 DB 侧 now 存在毫秒级误差，用容差断言时长
    assert abs((expires_at - created_at - timedelta(days=7)).total_seconds()) < 2


def test_create_share_defaults(client: TestClient) -> None:
    """默认值：expiry=24h、max_views=null。"""
    body = _create_share(client, content="默认参数")
    assert body["expires_at"] is not None
    assert body["max_views"] is None
    created_at = datetime.fromisoformat(body["created_at"])
    expires_at = datetime.fromisoformat(body["expires_at"])
    assert abs((expires_at - created_at - timedelta(days=1)).total_seconds()) < 2


def test_create_share_invalid_expiry_422(client: TestClient) -> None:
    """非法 expiry 值 → 422 Problem Details。"""
    response = client.post(f"{API_PREFIX}/shares", json={"content": "x", "expiry": "3h"})
    assert response.status_code == 422
    body = response.json()
    assert body["type"] == "validation_error"
    assert body["title"] == "请求参数校验失败"
    assert body["status"] == 422
    assert "expiry" in body["detail"]


def test_create_share_invalid_max_views_422(client: TestClient) -> None:
    """非法 max_views 值（不在 1/5/null 枚举内）→ 422。"""
    response = client.post(f"{API_PREFIX}/shares", json={"content": "x", "max_views": 3})
    assert response.status_code == 422
    assert response.json()["type"] == "validation_error"


def test_create_share_content_too_long_422(client: TestClient) -> None:
    """超长 content（超过设置项上限）→ 422。"""
    response = client.post(
        f"{API_PREFIX}/shares", json={"content": "x" * (settings.share_max_content_length + 1)}
    )
    assert response.status_code == 422
    assert response.json()["type"] == "validation_error"


def test_create_share_empty_content_422(client: TestClient) -> None:
    """空 content → 422。"""
    response = client.post(f"{API_PREFIX}/shares", json={"content": ""})
    assert response.status_code == 422
    assert response.json()["type"] == "validation_error"


def test_read_share_roundtrip(client: TestClient) -> None:
    """读取 200：内容完整往返（含 XSS 载荷原样存储返回——消毒属 M4 前端职责）。"""
    created = _create_share(client, content=XSS_PAYLOAD, expiry="1h", max_views=5)
    code = created["code"]

    response = client.get(f"{API_PREFIX}/shares/{code}")
    assert response.status_code == 200
    body = response.json()
    assert set(body) == {"code", "content", "expires_at", "remaining_views", "created_at"}
    assert body["code"] == code
    assert body["content"] == XSS_PAYLOAD
    assert body["remaining_views"] == 4
    assert body["expires_at"] == created["expires_at"]
    assert body["created_at"] == created["created_at"]


def test_read_share_unknown_code_404(client: TestClient) -> None:
    """未知短码 → 404，错误体为 Problem Details 形状（type 为稳定机器码）。"""
    response = client.get(f"{API_PREFIX}/shares/zzzz99")
    assert response.status_code == 404
    assert response.json() == {
        "type": "share_not_found",
        "title": "分享不存在",
        "status": 404,
        "detail": "短码 zzzz99 不存在",
    }


def test_unknown_route_returns_problem_details(client: TestClient) -> None:
    """未匹配任何路由的路径 → 404 Problem Details（框架 HTTPException 统一转写）。"""
    response = client.get("/api/v1/nonexistent")
    assert response.status_code == 404
    body = response.json()
    assert body["type"] == "http_error"
    assert body["status"] == 404
    assert body["title"] == "请求错误"


def test_read_share_expired_410(client: TestClient) -> None:
    """已过期分享 → 410（用 repository 直接写入过去的 expires_at 再 GET）。"""
    with SessionLocal() as session:
        ShareRepository.create(
            session,
            code="expired",
            content="过期内容",
            expires_at=utcnow() - timedelta(hours=1),
            max_views=None,
        )
        session.commit()

    response = client.get(f"{API_PREFIX}/shares/expired")
    assert response.status_code == 410
    body = response.json()
    assert body["type"] == "share_expired"
    assert body["status"] == 410


def test_read_share_views_exhausted_410(client: TestClient) -> None:
    """次数耗尽：max_views=1 首次 200、第二次 410，且 DB 计数保持 1（防超卖断言）。"""
    created = _create_share(client, content="一次性", max_views=1)
    code = created["code"]

    first = client.get(f"{API_PREFIX}/shares/{code}")
    assert first.status_code == 200
    assert first.json()["remaining_views"] == 0

    second = client.get(f"{API_PREFIX}/shares/{code}")
    assert second.status_code == 410
    assert second.json()["type"] == "share_views_exhausted"

    # 用独立会话读回数据库真值，断言未被超卖（防读身份映射缓存旧值）
    with SessionLocal() as session:
        share = ShareRepository.get_by_code(session, code)
    assert share is not None
    assert share.view_count == 1


def test_read_raw_share(client: TestClient) -> None:
    """raw 端点：text/plain 返回原文；语义与读取接口一致（消耗次数、404/410）。"""
    created = _create_share(client, content="原始文本", max_views=1)
    code = created["code"]

    response = client.get(f"{API_PREFIX}/shares/{code}/raw")
    assert response.status_code == 200
    assert response.headers["content-type"] == "text/plain; charset=utf-8"
    assert response.text == "原始文本"

    # 次数耗尽后 raw 同样 410
    exhausted = client.get(f"{API_PREFIX}/shares/{code}/raw")
    assert exhausted.status_code == 410
    assert exhausted.json()["type"] == "share_views_exhausted"

    # 未知短码 404
    missing = client.get(f"{API_PREFIX}/shares/zzzz99/raw")
    assert missing.status_code == 404
    assert missing.json()["type"] == "share_not_found"


def test_qr_returns_png_without_consuming_views(client: TestClient) -> None:
    """二维码：image/png；不消耗次数（qr 后读取仍成功）；过期分享仍可用；未知 404。"""
    created = _create_share(client, content="二维码", max_views=1)
    code = created["code"]

    response = client.get(f"{API_PREFIX}/shares/{code}/qr")
    assert response.status_code == 200
    assert response.headers["content-type"] == "image/png"
    assert response.content.startswith(PNG_MAGIC)

    # 不消耗次数：qr 之后读取仍成功（max_views=1 还剩一次）
    view = client.get(f"{API_PREFIX}/shares/{code}")
    assert view.status_code == 200

    # 未知短码 → 404（不 410）
    missing = client.get(f"{API_PREFIX}/shares/zzzz99/qr")
    assert missing.status_code == 404

    # 过期分享的二维码仍可用：仅编码 URL，不判过期（由网页端读取时判定）
    with SessionLocal() as session:
        ShareRepository.create(
            session,
            code="expqr1",
            content="x",
            expires_at=utcnow() - timedelta(hours=1),
            max_views=None,
        )
        session.commit()
    expired_qr = client.get(f"{API_PREFIX}/shares/expqr1/qr")
    assert expired_qr.status_code == 200


def test_security_headers_present(client: TestClient) -> None:
    """安全响应头：nosniff / DENY / no-referrer（错误响应同样携带）。"""
    response = client.get(f"{API_PREFIX}/shares/zzzz99")
    assert response.status_code == 404
    assert response.headers["x-content-type-options"] == "nosniff"
    assert response.headers["x-frame-options"] == "DENY"
    assert response.headers["referrer-policy"] == "no-referrer"


def test_unexpected_error_returns_500_problem_details() -> None:
    """兜底处理器：未预期异常 → 500 Problem Details（保证所有响应均为 JSON）。"""
    from fastapi import FastAPI

    from app.core.errors import register_exception_handlers

    test_app = FastAPI()
    register_exception_handlers(test_app)

    @test_app.get("/boom")
    def boom() -> None:
        raise RuntimeError("boom")

    # ServerErrorMiddleware 响应后仍会向上重抛异常供服务器记录，
    # 故需关闭 TestClient 的重抛（raise_server_exceptions=False）才能取到 500 响应
    with TestClient(test_app, raise_server_exceptions=False) as test_client:
        response = test_client.get("/boom")
    assert response.status_code == 500
    body = response.json()
    assert body["type"] == "internal_error"
    assert body["status"] == 500
    assert body["detail"]


def test_create_rate_limit_returns_429(client: TestClient) -> None:
    """速率限制冒烟：同一 IP 连续创建超过限额后返回 429 Problem Details。

    必须放在本文件最后定义：本文件前面的创建用例已在同一限流窗口内消耗额度，
    本用例循环发送直到出现 429（pytest 同文件内按定义顺序执行）。
    """
    limit = int(settings.rate_limit_create.split("/")[0])
    got_429 = False
    for i in range(limit + 1):
        response = client.post(f"{API_PREFIX}/shares", json={"content": "限流"})
        if response.status_code == 429:
            body = response.json()
            assert body["type"] == "rate_limited"
            assert body["title"] == "请求过于频繁"
            assert body["status"] == 429
            assert body["detail"] == "请求过于频繁，请 60 秒后重试"
            assert response.headers["retry-after"] == "60"
            got_429 = True
            break
        assert response.status_code == 201, (
            f"第 {i + 1} 次创建应 201 或 429，实际 {response.status_code}"
        )
    assert got_429, "连续创建未触发速率限制（429），限流失效"
