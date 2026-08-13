"""M5 CLI 与真实应用的进程内集成冒烟：FastAPI TestClient 直连应用 + PostgreSQL。

TestClient 是 httpx.Client 的子类，直接作为 CLI 函数的注入客户端使用；
在进程内走完整 ASGI 链路（含限流/安全头/异常处理器/真实 DB），
不依赖外部运行中的服务器；宿主机上的真服务器 CLI 冒烟见验证门命令。
"""
from collections.abc import Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import text

from app.core.config import get_settings
from app.db.base import Base
from app.db.session import engine
from app.main import app
from cli.main import CliError, get_share, send_share


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


def test_cli_send_then_get_smoke() -> None:
    """冒烟：send_share 创建 → 提取短码 → get_share 读取 → 内容一致。"""
    with TestClient(app) as client:
        url = send_share(
            base_url="http://testserver",
            content="CLI 冒烟内容：你好，ClipShare",
            expiry="1h",
            max_views=5,
            client=client,
        )
        code = url.rsplit("/", 1)[-1]
        # url 由服务器按 public_base_url 拼接（与浏览器收到的分享链接一致）
        public_base = get_settings().public_base_url.rstrip("/")
        assert url == f"{public_base}/s/{code}"
        assert (
            get_share(base_url="http://testserver", code=code, client=client)
            == "CLI 冒烟内容：你好，ClipShare"
        )


def test_cli_get_unknown_code_raises() -> None:
    """冒烟：get 不存在的短码 → 抛出携带退出码 1 的 CliError（错误路径退出码契约）。"""
    with TestClient(app) as client, pytest.raises(CliError) as exc_info:
        get_share(base_url="http://testserver", code="zzzz99", client=client)
    assert exc_info.value.exit_code == 1
    assert "短码 zzzz99 不存在" in str(exc_info.value)
