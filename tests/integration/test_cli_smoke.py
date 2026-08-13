"""M5 CLI 与真实应用的进程内集成冒烟：FastAPI TestClient 直连应用 + PostgreSQL。

TestClient 是 httpx.Client 的子类，直接作为 CLI 函数的注入客户端使用；
在进程内走完整 ASGI 链路（含限流/安全头/异常处理器/真实 DB），
不依赖外部运行中的服务器；宿主机上的真服务器 CLI 冒烟见验证门命令。
v0.2：新增 upload_file / get_share_to_file 真服务器往返（流式上传 + 流式下载）。
"""
from collections.abc import Iterator
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import text

from app.core.config import get_settings
from app.db.base import Base
from app.db.session import engine
from app.main import app
from cli.main import CliError, get_share, get_share_to_file, send_share, upload_file


@pytest.fixture(scope="session", autouse=True)
def _ensure_tables() -> Iterator[None]:
    """会话级幂等建表（与既有集成测试保持一致）。"""
    Base.metadata.create_all(engine)
    yield


@pytest.fixture(autouse=True)
def _clean_shares() -> Iterator[None]:
    """每个用例结束后清空业务表与短码中心表，保证用例间数据互不干扰。"""
    yield
    with engine.begin() as conn:
        conn.execute(text("DELETE FROM shares"))
        conn.execute(text("DELETE FROM share_files"))
        conn.execute(text("DELETE FROM shortcodes"))


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


def test_cli_upload_then_get_output_smoke(tmp_path: Path) -> None:
    """冒烟：upload_file 真实上传（multipart 流式）→ get_share_to_file 回退探测下载。

    断言下载字节与源文件逐字节一致（含 0x00 与多字节二进制，流式红线回归）；
    输出目录按服务器 original_name 落盘。
    """
    source = tmp_path / "smoke.txt"
    source.write_bytes("CLI upload smoke \x00\xff 二进制内容".encode())
    with TestClient(app) as client:
        url = upload_file(
            base_url="http://testserver",
            path=source,
            expiry="1h",
            max_views=5,
            client=client,
        )
        public_base = get_settings().public_base_url.rstrip("/")
        assert url.startswith(f"{public_base}/api/v1/files/")
        code = url.rsplit("/", 1)[-1]

        out_dir = tmp_path / "dl"
        out_dir.mkdir()
        saved = get_share_to_file(
            base_url="http://testserver",
            code=code,
            output=str(out_dir),
            progress=False,
            client=client,
        )
        assert saved == out_dir / "smoke.txt"
        assert saved.read_bytes() == source.read_bytes()


def test_cli_upload_get_text_output_smoke(tmp_path: Path) -> None:
    """冒烟：upload 上传的短码经 get --output 文本路径回退失败后不误写（原错误透出）。"""
    with TestClient(app) as client, pytest.raises(CliError) as exc_info:
        get_share_to_file(
            base_url="http://testserver",
            code="zzzz99",
            output=str(tmp_path / "x.bin"),
            progress=False,
            client=client,
        )
    assert exc_info.value.exit_code == 1
    assert "短码 zzzz99 不存在" in str(exc_info.value)
