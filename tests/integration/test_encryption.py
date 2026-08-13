"""M5 端到端加密集成测试：服务器对密文无感知（API 零改动验证）。

断言核心（服务器零明文）：
- 密文标记串（ENC1: 前缀）经创建/读取/raw 全链路原样往返，API 不做任何转义破坏；
- DB 中 content 字段存的就是密文（SQL 直接查询），明文绝不落库；
- 密文含 + / = - _ 等字符时 JSON/传输层不损坏。
真实加解密行为由 tests/e2e/encryption.test.js（宿主机 Node，真实浏览器同款 crypto.js）验证。
"""
import base64
from collections.abc import Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import text

from app.db.base import Base
from app.db.session import engine
from app.main import app

API_PREFIX = "/api/v1"
PLAINTEXT = "绝密内容 secret-content-42"
# 密文标记串：含 base64url（- _）与标准 base64（+ / =）字符，验证服务器透明存储
MARKER_IV = b"\x01\x02\x03\x04\x05\x06\x07\x08\x09\x0a\x0b\x0c"
MARKER_CIPHER = b"\xff\xfe\x00\x11+/\x3d\xd0\x9f\x92\x88\xab\xcd\xef\x01\x02"
ENCRYPTED_CONTENT = (
    "ENC1:"
    + base64.urlsafe_b64encode(MARKER_IV).decode("ascii")
    + "."
    + base64.b64encode(MARKER_CIPHER).decode("ascii")
)


@pytest.fixture(scope="session", autouse=True)
def _ensure_tables() -> Iterator[None]:
    """会话级幂等建表（与既有集成测试保持一致）。"""
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


def test_encrypted_content_api_roundtrip_unchanged(client: TestClient) -> None:
    """密文分享：创建/读取/raw 全链路原样往返（含 + / = - _ 字符，无任何转义破坏）。"""
    payload = {"content": ENCRYPTED_CONTENT, "expiry": "1h"}
    response = client.post(f"{API_PREFIX}/shares", json=payload)
    assert response.status_code == 201
    code = response.json()["code"]

    read = client.get(f"{API_PREFIX}/shares/{code}")
    assert read.status_code == 200
    body = read.json()
    assert body["content"] == ENCRYPTED_CONTENT  # 服务器存储的即为密文，逐字节一致
    assert body["content"].startswith("ENC1:")
    assert PLAINTEXT not in body["content"]

    raw = client.get(f"{API_PREFIX}/shares/{code}/raw")
    assert raw.status_code == 200
    assert raw.text == ENCRYPTED_CONTENT


def test_db_stores_ciphertext_only(client: TestClient) -> None:
    """服务器零明文断言：SQL 直查 DB，content 字段为密文，明文绝不落库。"""
    payload = {"content": ENCRYPTED_CONTENT, "expiry": "24h"}
    response = client.post(f"{API_PREFIX}/shares", json=payload)
    assert response.status_code == 201
    code = response.json()["code"]

    with engine.begin() as conn:
        row = conn.execute(
            text("SELECT content FROM shares WHERE code = :code"), {"code": code}
        ).one()
    db_content = row[0]
    assert db_content == ENCRYPTED_CONTENT
    assert db_content.startswith("ENC1:")
    assert PLAINTEXT not in db_content


def test_encrypted_content_may_contain_json_sensitive_chars(client: TestClient) -> None:
    """密文可含引号/反斜杠/换行等 JSON 敏感字符：pydantic 校验与存储不破坏密文。"""
    tricky = 'ENC1:YWIx' + '.' + 'Y2Q+eWY9"\\\n'
    response = client.post(f"{API_PREFIX}/shares", json={"content": tricky})
    assert response.status_code == 201
    code = response.json()["code"]

    read = client.get(f"{API_PREFIX}/shares/{code}")
    assert read.status_code == 200
    assert read.json()["content"] == tricky
