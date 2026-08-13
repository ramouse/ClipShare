"""文件分享 REST API 集成测试：真实 FastAPI 应用 + PostgreSQL（模块 B 验证门）。

fixture 三件套照 test_shares_api.py（建表 / 清表三连 / client），另加存储目录
monkeypatch 到 tmp_path——磁盘与数据库都按用例隔离，互不干扰。
429 限流用例必须放在本文件最后定义（与 test_shares_api.py 相同约定）。
"""
import base64
import hashlib
import re
import urllib.parse
from collections.abc import Iterator
from datetime import datetime, timedelta
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from httpx import Response
from sqlalchemy import text

from app.core.config import get_settings
from app.core.time import utcnow
from app.db.base import Base
from app.db.repository import ShareFileRepository
from app.db.session import SessionLocal, engine
from app.main import app

settings = get_settings()
API_PREFIX = "/api/v1"
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"
# 磁盘文件名形状：secrets.token_hex(16) = 32 位小写十六进制、无扩展名
DISK_NAME_RE = re.compile(r"^[0-9a-f]{32}$")


@pytest.fixture(scope="session", autouse=True)
def _ensure_tables() -> Iterator[None]:
    """会话级幂等建表：兼容未跑迁移的数据库环境（与 conftest 保持一致）。"""
    Base.metadata.create_all(engine)
    yield


@pytest.fixture(autouse=True)
def _clean_file_tables() -> Iterator[None]:
    """每个用例结束后清空业务表与短码中心表，保证用例间数据互不干扰。

    顺序：先业务表（shares/share_files）后中心表（shortcodes），
    与 tests/integration/conftest.py 的 db_session 清理保持一致。
    """
    yield
    with engine.begin() as conn:
        conn.execute(text("DELETE FROM shares"))
        conn.execute(text("DELETE FROM share_files"))
        conn.execute(text("DELETE FROM shortcodes"))


@pytest.fixture()
def client() -> Iterator[TestClient]:
    """API 测试客户端：复用生产应用实例（含限流、安全头、异常处理器）。"""
    with TestClient(app) as test_client:
        yield test_client


@pytest.fixture(autouse=True)
def storage_dir(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[Path]:
    """把文件存储目录重定向到临时目录：磁盘状态按用例完全隔离、自动清理。

    路由层每次请求经 _get_storage() 读取最新 settings.file_storage_dir，
    monkeypatch 单例配置对象即可全局生效。
    """
    monkeypatch.setattr(settings, "file_storage_dir", str(tmp_path))
    yield tmp_path


def _upload_file(
    client: TestClient,
    *,
    name: str = "a.txt",
    content: bytes = b"hello",
    content_type: str = "text/plain",
    **data: str,
) -> Response:
    """通过 API 上传文件：expiry / max_views / encrypted 经 data 表单字段传入。"""
    return client.post(
        f"{API_PREFIX}/files",
        files={"file": (name, content, content_type)},
        data=data,
    )


# ---- 上传：201 / 参数 / 校验失败 ----

def test_upload_png_roundtrip_and_shortcode_kind(client: TestClient, storage_dir: Path) -> None:
    """PNG 往返：上传 201 → 下载 200 字节完全一致（sha256 对比）。

    同时断言短码中心表登记 kind='file'（跨类型唯一兜底）与磁盘文件名
    为 32 位十六进制、无扩展名（不暴露用户文件名与类型）。
    """
    png = PNG_MAGIC + b"\x00\x01\x02" * 100 + b"\x00\x00\x00\x00IEND"
    uploaded = _upload_file(client, name="logo.png", content=png, content_type="image/png")
    assert uploaded.status_code == 201, uploaded.text
    code = uploaded.json()["code"]

    downloaded = client.get(f"{API_PREFIX}/files/{code}/download")
    assert downloaded.status_code == 200
    assert downloaded.content == png
    assert hashlib.sha256(downloaded.content).hexdigest() == hashlib.sha256(png).hexdigest()
    assert downloaded.headers["content-type"].startswith("image/png")

    with engine.connect() as conn:
        kind = conn.execute(
            text("SELECT kind FROM shortcodes WHERE code = :code"), {"code": code}
        ).scalar()
    assert kind == "file"

    disk_files = list(storage_dir.iterdir())
    assert len(disk_files) == 1
    assert DISK_NAME_RE.fullmatch(disk_files[0].name)
    assert disk_files[0].name != "logo.png"


def test_upload_defaults_and_max_views_parsing(client: TestClient) -> None:
    """默认参数与 max_views 解析：expiry=24h、encrypted=false、"" → None、"5" → 5。"""
    defaults = _upload_file(client, name="d.txt", content=b"x")
    assert defaults.status_code == 201, defaults.text
    body = defaults.json()
    assert set(body) == {
        "code", "url", "original_name", "size_bytes", "encrypted",
        "expires_at", "max_views", "created_at",
    }
    assert body["max_views"] is None
    assert body["encrypted"] is False
    assert body["size_bytes"] == 1
    assert body["original_name"] == "d.txt"
    assert body["url"] == f"{settings.public_base_url.rstrip('/')}/api/v1/files/{body['code']}"
    created = datetime.fromisoformat(body["created_at"])
    expires = datetime.fromisoformat(body["expires_at"])
    # 应用侧 now 与 DB 侧 now 存在毫秒级误差，用容差断言时长
    assert abs((expires - created - timedelta(days=1)).total_seconds()) < 2

    limited = _upload_file(client, name="l.txt", content=b"y", max_views="5")
    assert limited.status_code == 201, limited.text
    assert limited.json()["max_views"] == 5


def test_upload_invalid_params_422(client: TestClient) -> None:
    """非法表单参数：max_views 非 1/5 → 422；expiry 非四档 → 422（Problem Details）。"""
    bad_views = _upload_file(client, name="v.txt", content=b"x", max_views="3")
    assert bad_views.status_code == 422
    body = bad_views.json()
    assert body["status"] == 422
    assert "max_views" in body["detail"]

    bad_expiry = _upload_file(client, name="e.txt", content=b"x", expiry="3h")
    assert bad_expiry.status_code == 422
    assert "expiry" in bad_expiry.json()["detail"]


# ---- 上传：安全与资源红线 ----

def test_upload_extension_not_allowed_415_no_residue(
    client: TestClient, storage_dir: Path
) -> None:
    """扩展名不在白名单（.exe）→ 415 file_type_not_allowed；磁盘与 DB 均无残留。"""
    response = _upload_file(client, name="virus.exe", content=b"MZ\x90\x00")
    assert response.status_code == 415
    body = response.json()
    assert body["type"] == "file_type_not_allowed"
    assert body["status"] == 415
    assert list(storage_dir.iterdir()) == []
    with engine.connect() as conn:
        count = conn.execute(text("SELECT COUNT(*) FROM share_files")).scalar()
    assert count == 0


def test_upload_too_large_413_no_residue(
    client: TestClient, storage_dir: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """超限 413：流式中断、磁盘无半成品、DB 无记录（全链路无残留断言）。"""
    monkeypatch.setattr(settings, "file_max_size", 10 * 1024)
    response = _upload_file(
        client,
        name="big.txt",
        content=b"x" * (10 * 1024 + 1),
        content_type="application/octet-stream",
    )
    assert response.status_code == 413
    assert response.json()["type"] == "file_too_large"
    assert list(storage_dir.iterdir()) == []
    with engine.connect() as conn:
        count = conn.execute(text("SELECT COUNT(*) FROM share_files")).scalar()
    assert count == 0


def test_upload_encrypt_over_limit_422_no_residue(
    client: TestClient, storage_dir: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """encrypted=true 且超过加密上限 → 422 file_encrypt_not_available；磁盘/DB 无残留。

    服务端兜底校验（不依赖前端开关）：验证路由对已落盘文件的补偿删除。
    """
    monkeypatch.setattr(settings, "file_encrypt_max_size", 1024)
    response = _upload_file(client, name="secret.txt", content=b"x" * 2048, encrypted="true")
    assert response.status_code == 422
    body = response.json()
    assert body["type"] == "file_encrypt_not_available"
    assert body["status"] == 422
    assert list(storage_dir.iterdir()) == []
    with engine.connect() as conn:
        count = conn.execute(text("SELECT COUNT(*) FROM share_files")).scalar()
    assert count == 0


def test_upload_path_traversal_sanitized(client: TestClient) -> None:
    """路径遍历攻击：文件名中的路径成分被净化，磁盘与下载响应均不出现 ".."。"""
    response = _upload_file(client, name="../../etc/passwd.txt", content=b"root:x:0:0")
    assert response.status_code == 201, response.text
    body = response.json()
    assert body["original_name"] == "passwd.txt"

    downloaded = client.get(f"{API_PREFIX}/files/{body['code']}/download")
    assert downloaded.status_code == 200
    cd = downloaded.headers["content-disposition"]
    assert ".." not in cd
    assert "passwd.txt" in cd

    # Windows 反斜杠路径成分同样被净化
    win = _upload_file(client, name=r"C:\\Users\\evil\\note.txt", content=b"note")
    assert win.status_code == 201, win.text
    assert win.json()["original_name"] == "note.txt"


def test_upload_chinese_filename_rfc5987(client: TestClient) -> None:
    """中文文件名：上传 201 原样保存；下载头以 RFC 5987 filename* 编码传输。"""
    name = "测试报告.txt"
    response = _upload_file(client, name=name, content=b"report")
    assert response.status_code == 201, response.text
    assert response.json()["original_name"] == name

    downloaded = client.get(f"{API_PREFIX}/files/{response.json()['code']}/download")
    assert downloaded.status_code == 200
    cd = downloaded.headers["content-disposition"]
    assert cd.startswith("attachment; filename*=utf-8''")
    assert urllib.parse.unquote(cd.split("utf-8''", 1)[1]) == name


# ---- 元数据 / 预览 / 下载：计数语义 ----

def test_get_file_meta_not_consuming_views(client: TestClient) -> None:
    """元数据 200：全字段形状 + preview_available 计算 + 不消耗次数。"""
    uploaded = _upload_file(client, name="note.md", content="# 标题\n内容".encode(), max_views="5")
    code = uploaded.json()["code"]

    meta = client.get(f"{API_PREFIX}/files/{code}")
    assert meta.status_code == 200
    body = meta.json()
    assert set(body) == {
        "code", "kind", "original_name", "size_bytes", "encrypted", "content_type",
        "preview_available", "expires_at", "remaining_views", "created_at",
    }
    assert body["kind"] == "file"
    assert body["original_name"] == "note.md"
    assert body["size_bytes"] == len("# 标题\n内容".encode())
    assert body["encrypted"] is False
    assert body["content_type"] == "text/plain"
    assert body["preview_available"] is True
    assert body["remaining_views"] == 5

    # 连续两次元数据读取 remaining 不变（不消耗）
    again = client.get(f"{API_PREFIX}/files/{code}")
    assert again.json()["remaining_views"] == 5

    # 加密文件 preview_available=False（encrypted 直接否决预览）
    enc = _upload_file(client, name="secret.md", content=b"hidden", encrypted="true")
    enc_meta = client.get(f"{API_PREFIX}/files/{enc.json()['code']}")
    assert enc_meta.status_code == 200
    assert enc_meta.json()["preview_available"] is False


def test_preview_returns_text_and_consumes_preview(client: TestClient) -> None:
    """预览 200：text/plain 返回头部内容；消耗预览计数（remaining 5→4）。"""
    content = ("预览内容" * 100).encode()
    uploaded = _upload_file(client, name="pre.txt", content=content, max_views="5")
    code = uploaded.json()["code"]

    preview = client.get(f"{API_PREFIX}/files/{code}/preview")
    assert preview.status_code == 200
    assert preview.headers["content-type"] == "text/plain; charset=utf-8"
    assert preview.content == content

    meta = client.get(f"{API_PREFIX}/files/{code}")
    assert meta.json()["remaining_views"] == 4


def test_preview_not_available_415_no_consume(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """不可预览三种情形（超截断上限 / 加密 / 非预览白名单）→ 415 且不消耗次数。"""
    monkeypatch.setattr(settings, "file_preview_max_size", 1024)

    # 恰好等于截断上限的 txt：可预览，边界值测试（size <= max_size）
    edge = _upload_file(client, name="edge.txt", content=b"x" * 1024, max_views="5")
    edge_preview = client.get(f"{API_PREFIX}/files/{edge.json()['code']}/preview")
    assert edge_preview.status_code == 200
    assert len(edge_preview.content) == 1024

    # 超过截断上限的 txt：2KB > 1024B → 415
    big = _upload_file(client, name="big.txt", content=b"x" * 2048, max_views="5")
    big_code = big.json()["code"]
    oversize = client.get(f"{API_PREFIX}/files/{big_code}/preview")
    assert oversize.status_code == 415
    assert oversize.json()["type"] == "preview_not_available"
    assert client.get(f"{API_PREFIX}/files/{big_code}").json()["remaining_views"] == 5

    # 加密 txt → 415
    enc = _upload_file(
        client, name="secret.txt", content=b"hidden", encrypted="true", max_views="5"
    )
    enc_code = enc.json()["code"]
    encrypted = client.get(f"{API_PREFIX}/files/{enc_code}/preview")
    assert encrypted.status_code == 415
    assert encrypted.json()["type"] == "preview_not_available"
    assert client.get(f"{API_PREFIX}/files/{enc_code}").json()["remaining_views"] == 5

    # png（上传白名单内但不在预览白名单）→ 415
    png = _upload_file(client, name="pic.png", content=PNG_MAGIC + b"0" * 200, max_views="5")
    png_code = png.json()["code"]
    image = client.get(f"{API_PREFIX}/files/{png_code}/preview")
    assert image.status_code == 415
    assert image.json()["type"] == "preview_not_available"
    assert client.get(f"{API_PREFIX}/files/{png_code}").json()["remaining_views"] == 5


def test_download_bytes_identical_and_content_type(client: TestClient) -> None:
    """下载 200：字节一致（sha256）、content-type 透传、剩余次数 5→4。"""
    payload = b"line1\nline2\n" * 500
    uploaded = _upload_file(
        client, name="data.txt", content=payload, content_type="text/plain", max_views="5"
    )
    code = uploaded.json()["code"]

    downloaded = client.get(f"{API_PREFIX}/files/{code}/download")
    assert downloaded.status_code == 200
    assert downloaded.content == payload
    assert hashlib.sha256(downloaded.content).hexdigest() == hashlib.sha256(payload).hexdigest()
    assert downloaded.headers["content-type"].startswith("text/plain")

    meta = client.get(f"{API_PREFIX}/files/{code}")
    assert meta.json()["remaining_views"] == 4


def test_preview_download_share_view_pool(client: TestClient) -> None:
    """预览与下载共享次数池：max_views=1 时任一先行消耗后，另一端点 410。"""
    first = _upload_file(client, name="pool1.txt", content=b"pool", max_views="1")
    first_code = first.json()["code"]
    assert client.get(f"{API_PREFIX}/files/{first_code}/preview").status_code == 200
    exhausted_download = client.get(f"{API_PREFIX}/files/{first_code}/download")
    assert exhausted_download.status_code == 410
    assert exhausted_download.json()["type"] == "file_views_exhausted"

    second = _upload_file(client, name="pool2.txt", content=b"pool", max_views="1")
    second_code = second.json()["code"]
    assert client.get(f"{API_PREFIX}/files/{second_code}/download").status_code == 200
    exhausted_preview = client.get(f"{API_PREFIX}/files/{second_code}/preview")
    assert exhausted_preview.status_code == 410
    assert exhausted_preview.json()["type"] == "file_views_exhausted"

    # DB 计数防超卖断言：两次尝试只落库一次（预览 + 下载共享同一池）
    with SessionLocal() as session:
        record = ShareFileRepository.get_by_code(session, first_code)
    assert record is not None
    assert record.preview_count + record.download_count == 1


def test_encrypted_disk_bytes_no_plaintext(client: TestClient, storage_dir: Path) -> None:
    """加密文件：服务端为不透明存储——磁盘字节与上传密文完全一致、零明文残留。

    E2E 加密发生在浏览器端（模块 C crypto.js），服务端只记录 encrypted 标记
    并原样落盘：本用例模拟前端已加密的 ENC1 格式字节上传，断言磁盘保存的
    就是密文本身（未被篡改/解密），下载返回密文原样，原始明文无任何残留。
    """
    plaintext = b"TOP SECRET content \xe7\xa7\x98\xe5\xaf\x86\xe5\x86\x85\xe5\xae\xb9"
    # 模拟前端 encryptBytes 输出：ENC1:IV(base64):密文(base64)
    ciphertext = (
        b"ENC1:" + base64.b64encode(b"0123456789abcdef") + b":" + base64.b64encode(plaintext)
    )
    uploaded = _upload_file(client, name="secret.txt", content=ciphertext, encrypted="true")
    assert uploaded.status_code == 201, uploaded.text
    body = uploaded.json()
    assert body["encrypted"] is True
    assert body["size_bytes"] == len(ciphertext)

    disk = next(storage_dir.iterdir())
    disk_bytes = disk.read_bytes()
    assert disk_bytes == ciphertext
    assert b"ENC1" in disk_bytes
    assert plaintext not in disk_bytes

    downloaded = client.get(f"{API_PREFIX}/files/{body['code']}/download")
    assert downloaded.status_code == 200
    assert downloaded.content == ciphertext
    assert plaintext not in downloaded.content


# ---- 文件生命周期：404 / 410 / 跨类型 / 内容缺失 ----

def test_file_not_found_404_and_expired_410(client: TestClient, storage_dir: Path) -> None:
    """未知短码 404 file_not_found；过期文件 410 file_expired 并懒删磁盘。"""
    missing = client.get(f"{API_PREFIX}/files/zzzz99")
    assert missing.status_code == 404
    assert missing.json()["type"] == "file_not_found"

    uploaded = _upload_file(client, name="old.txt", content=b"old")
    code = uploaded.json()["code"]
    assert len(list(storage_dir.iterdir())) == 1
    # 直接回写过期时间（应用层只暴露四档有效期，最短 1h，无法自然造出过期）
    with engine.begin() as conn:
        conn.execute(
            text("UPDATE share_files SET expires_at = :past WHERE code = :code"),
            {"past": utcnow() - timedelta(hours=1), "code": code},
        )
    expired = client.get(f"{API_PREFIX}/files/{code}")
    assert expired.status_code == 410
    assert expired.json()["type"] == "file_expired"
    # 懒删断言：过期访问后磁盘文件已被清理
    assert list(storage_dir.iterdir()) == []


def test_shortcode_cross_type_no_ambiguity(client: TestClient) -> None:
    """短码跨类型无歧义：文件短码在文本端点 404，文本短码在文件端点 404。"""
    file_resp = _upload_file(client, name="cross.txt", content=b"cross")
    assert file_resp.status_code == 201
    file_code = file_resp.json()["code"]

    share_resp = client.post(f"{API_PREFIX}/shares", json={"content": "文本"})
    assert share_resp.status_code == 201
    share_code = share_resp.json()["code"]

    # 文件短码在文本端点必须 404（不得跨表读取到其他资源）
    text_endpoint = client.get(f"{API_PREFIX}/shares/{file_code}")
    assert text_endpoint.status_code == 404
    assert text_endpoint.json()["type"] == "share_not_found"
    # 文本短码在文件端点必须 404
    file_endpoint = client.get(f"{API_PREFIX}/files/{share_code}")
    assert file_endpoint.status_code == 404
    assert file_endpoint.json()["type"] == "file_not_found"


def test_file_content_missing_410(client: TestClient, storage_dir: Path) -> None:
    """DB 记录存在但磁盘文件缺失（外部清理）→ 预览/下载均 410 file_content_missing。"""
    uploaded = _upload_file(client, name="ghost.txt", content=b"ghost")
    code = uploaded.json()["code"]
    for f in storage_dir.iterdir():
        f.unlink()

    downloaded = client.get(f"{API_PREFIX}/files/{code}/download")
    assert downloaded.status_code == 410
    assert downloaded.json()["type"] == "file_content_missing"

    # 预览端点在截断读阶段同样映射 410（文件在可预览性判定后被清走）
    preview = client.get(f"{API_PREFIX}/files/{code}/preview")
    assert preview.status_code == 410
    assert preview.json()["type"] == "file_content_missing"


# ---- 限流（必须最后定义：本文件前面的上传用例已消耗同一窗口内额度） ----

def test_upload_rate_limit_429(client: TestClient) -> None:
    """上传限流冒烟：同一 IP 连续上传超过限额后返回 429 Problem Details。

    必须放在本文件最后定义：本文件前面的上传用例已在同一限流窗口内消耗额度，
    本用例循环发送直到出现 429（pytest 同文件内按定义顺序执行）。
    限流 key 按端点函数独立（upload 与文本 create 预算互不影响，文本预算零新增）。
    """
    limit = int(settings.rate_limit_upload.split("/")[0])
    got_429 = False
    for i in range(limit + 1):
        response = _upload_file(client, name="rl.txt", content=b"x")
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
            f"第 {i + 1} 次上传应 201 或 429，实际 {response.status_code}"
        )
    assert got_429, "连续上传未触发速率限制（429），限流失效"
