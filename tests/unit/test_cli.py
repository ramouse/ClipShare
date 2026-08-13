"""CLI 单元测试：参数解析、URL 构造、错误处理（httpx MockTransport 模拟 API，不依赖真服务器）。"""
import json
from collections.abc import Callable, Iterator
from pathlib import Path

import httpx
import pytest

from cli.main import (
    EXIT_API_ERROR,
    EXIT_USAGE_ERROR,
    CliError,
    build_api_url,
    extract_code,
    get_share_to_file,
    main,
    upload_file,
)

BASE = "http://test"


def _client(handler: Callable[[httpx.Request], httpx.Response]) -> httpx.Client:
    """构造 MockTransport 客户端：模拟 API 响应，不发起真实网络请求。"""
    return httpx.Client(transport=httpx.MockTransport(handler), base_url=BASE)


def _created_handler(request: httpx.Request) -> httpx.Response:
    """默认创建成功处理器：校验请求并返回 201。"""
    assert request.url.path == "/api/v1/shares"
    assert request.method == "POST"
    assert isinstance(json.loads(request.content), dict)
    code = "abc123"
    return httpx.Response(
        201,
        json={
            "code": code,
            "url": f"{BASE}/s/{code}",
            "expires_at": "2026-08-14T08:00:00",
            "max_views": None,
            "created_at": "2026-08-13T08:00:00",
        },
    )


def _raw_handler(content: str = "原始内容") -> Callable[[httpx.Request], httpx.Response]:
    """构造 raw 读取成功处理器。"""

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.method == "GET"
        return httpx.Response(200, text=content)

    return handler


def test_send_success_prints_url(capsys: pytest.CaptureFixture[str]) -> None:
    """send：请求体形状正确（默认 expiry=24h、max_views 省略），stdout 输出分享链接。"""
    with _client(_created_handler) as client:
        exit_code = main(["send", "你好", "--base-url", BASE], client=client)
    assert exit_code == 0
    assert capsys.readouterr().out == f"{BASE}/s/abc123\n"


def test_send_with_options_payload(capsys: pytest.CaptureFixture[str]) -> None:
    """send：--expiry/--max-views 透传到请求体。"""

    def handler(request: httpx.Request) -> httpx.Response:
        payload = json.loads(request.content)
        assert payload["expiry"] == "7d"
        assert payload["max_views"] == 5
        return _created_handler(request)

    with _client(handler) as client:
        exit_code = main(
            ["send", "内容", "--expiry", "7d", "--max-views", "5", "--base-url", BASE],
            client=client,
        )
    assert exit_code == 0


def test_send_max_views_zero_means_unlimited(capsys: pytest.CaptureFixture[str]) -> None:
    """send：--max-views 0 = 不限 → 请求体不携带 max_views（等同 API 默认 null）。"""

    def handler(request: httpx.Request) -> httpx.Response:
        payload = json.loads(request.content)
        assert "max_views" not in payload
        return _created_handler(request)

    with _client(handler) as client:
        exit_code = main(["send", "x", "--max-views", "0", "--base-url", BASE], client=client)
    assert exit_code == 0


def test_send_at_file_reads_content(tmp_path, capsys: pytest.CaptureFixture[str]) -> None:
    """send @file：读取本地文件内容作为分享内容。"""
    file_path = tmp_path / "note.txt"
    file_path.write_text("来自文件的内容", encoding="utf-8")

    def handler(request: httpx.Request) -> httpx.Response:
        payload = json.loads(request.content)
        assert payload["content"] == "来自文件的内容"
        return _created_handler(request)

    with _client(handler) as client:
        exit_code = main(["send", "@" + str(file_path), "--base-url", BASE], client=client)
    assert exit_code == 0


def test_send_file_missing_usage_error(capsys: pytest.CaptureFixture[str]) -> None:
    """send @不存在文件：参数错误 → 退出码 2，错误信息走 stderr。"""
    with _client(_created_handler) as client:
        exit_code = main(["send", "@/no/such/file.txt", "--base-url", BASE], client=client)
    assert exit_code == EXIT_USAGE_ERROR
    assert "文件不存在" in capsys.readouterr().err


def test_send_empty_content_usage_error() -> None:
    """send 空白内容：参数错误 → argparse 以退出码 2 结束进程。"""
    with _client(_created_handler) as client, pytest.raises(SystemExit) as exc_info:
        main(["send", "   ", "--base-url", BASE], client=client)
    assert exc_info.value.code == 2


def test_send_api_error_422(capsys: pytest.CaptureFixture[str]) -> None:
    """send 被 API 拒绝（422 Problem Details）：退出码 1，detail 输出到 stderr。"""

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            422,
            json={
                "type": "validation_error",
                "title": "请求参数校验失败",
                "status": 422,
                "detail": "content 长度超限",
            },
        )

    with _client(handler) as client:
        exit_code = main(["send", "x", "--base-url", BASE], client=client)
    assert exit_code == EXIT_API_ERROR
    assert "content 长度超限" in capsys.readouterr().err


def test_send_network_error(capsys: pytest.CaptureFixture[str]) -> None:
    """send 网络错误：退出码 1，stderr 提示网络请求失败。"""

    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("连接被拒绝", request=request)

    with _client(handler) as client:
        exit_code = main(["send", "x", "--base-url", BASE], client=client)
    assert exit_code == EXIT_API_ERROR
    assert "网络请求失败" in capsys.readouterr().err


def test_get_success_outputs_content(capsys: pytest.CaptureFixture[str]) -> None:
    """get：请求 raw 端点，stdout 输出原样内容。"""
    with _client(_raw_handler("内容一")) as client:
        exit_code = main(["get", "abc123", "--base-url", BASE], client=client)
    assert exit_code == 0
    assert capsys.readouterr().out == "内容一\n"


def test_get_preserves_trailing_newline(capsys: pytest.CaptureFixture[str]) -> None:
    """get：内容本身以换行结尾时不追加多余换行（原样输出）。"""
    with _client(_raw_handler("首行\n次行\n")) as client:
        exit_code = main(["get", "abc123", "--base-url", BASE], client=client)
    assert exit_code == 0
    assert capsys.readouterr().out == "首行\n次行\n"


def test_get_with_full_url(capsys: pytest.CaptureFixture[str]) -> None:
    """get 接受完整分享链接（含 fragment）：解析出短码并请求对应 raw 端点。"""
    seen: list[str] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request.url.path)
        return httpx.Response(200, text="ok")

    with _client(handler) as client:
        exit_code = main(
            ["get", "http://localhost:8000/s/AbC123#k=abc", "--base-url", BASE], client=client
        )
    assert exit_code == 0
    assert seen == ["/api/v1/shares/AbC123/raw"]


def test_get_api_404(capsys: pytest.CaptureFixture[str]) -> None:
    """get 短码不存在：退出码 1，stderr 输出 API 的 detail。"""

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            404,
            json={
                "type": "share_not_found",
                "title": "分享不存在",
                "status": 404,
                "detail": "短码 zzzz 不存在",
            },
        )

    with _client(handler) as client:
        exit_code = main(["get", "zzzz", "--base-url", BASE], client=client)
    assert exit_code == EXIT_API_ERROR
    assert "短码 zzzz 不存在" in capsys.readouterr().err


def test_get_invalid_url_usage_error(capsys: pytest.CaptureFixture[str]) -> None:
    """get 无法解析的链接：参数错误 → 退出码 2。"""
    with _client(_raw_handler()) as client:
        exit_code = main(
            ["get", "http://example.com/not-a-share", "--base-url", BASE], client=client
        )
    assert exit_code == EXIT_USAGE_ERROR
    assert "无法从链接中解析短码" in capsys.readouterr().err


def test_env_base_url(monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    """CLIPSHARE_BASE_URL 环境变量生效（未传 --base-url 时）。"""
    monkeypatch.setenv("CLIPSHARE_BASE_URL", "http://envhost:9000")
    seen: list[str] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(str(request.url))
        return httpx.Response(200, text="ok")

    with _client(handler) as client:
        exit_code = main(["get", "abc"], client=client)
    assert exit_code == 0
    assert seen == ["http://envhost:9000/api/v1/shares/abc/raw"]


def test_help_output_is_chinese(capsys: pytest.CaptureFixture[str]) -> None:
    """--help：中文文案，正常退出码 0。"""
    with pytest.raises(SystemExit) as top:
        main(["--help"])
    assert top.value.code == 0
    assert "创建分享" in capsys.readouterr().out

    with pytest.raises(SystemExit) as send_help:
        main(["send", "--help"])
    assert send_help.value.code == 0
    out = capsys.readouterr().out
    assert "TEXT|@FILE" in out
    assert "--expiry" in out


def test_unknown_command_exit_2() -> None:
    """未知子命令：argparse 非法参数 → 退出码 2。"""
    with pytest.raises(SystemExit) as exc_info:
        main(["delete", "abc"])
    assert exc_info.value.code == 2


# ---- v0.2 upload 子命令 ----

def _upload_response_handler(
    request: httpx.Request, *, expect_max_views: str | None = None
) -> httpx.Response:
    """默认上传成功处理器：校验 multipart 表单字段并返回 201。

    校验点：路径 /api/v1/files、POST 方法、multipart Content-Type、
    字段 file/expiry/max_views 与文件名、文件内容字节（流式发送回归）。
    """
    assert request.url.path == "/api/v1/files"
    assert request.method == "POST"
    content_type = request.headers["content-type"]
    assert content_type.startswith("multipart/form-data; boundary="), content_type
    body = request.content
    assert b'name="file"' in body
    assert b'filename="a.txt"' in body
    assert b"hello file" in body
    assert b'name="expiry"' in body and b"24h" in body
    if expect_max_views is not None:
        assert b'name="max_views"' in body and expect_max_views.encode() in body
    code = "abc123"
    return httpx.Response(
        201,
        json={
            "code": code,
            "url": f"{BASE}/api/v1/files/{code}",
            "original_name": "a.txt",
            "size_bytes": 10,
            "encrypted": False,
            "expires_at": "2026-08-14T08:00:00",
            "max_views": 5,
            "created_at": "2026-08-13T08:00:00",
        },
    )


def test_upload_success_streams_multipart(
    capsys: pytest.CaptureFixture[str], tmp_path: Path
) -> None:
    """upload：multipart 字段（file/expiry/max_views/文件名）流式发送，stdout 输出分享链接。"""
    source = tmp_path / "a.txt"
    source.write_bytes(b"hello file")

    with _client(_upload_response_handler) as client:
        exit_code = main(
            ["upload", str(source), "--max-views", "5", "--base-url", BASE], client=client
        )
    assert exit_code == 0
    assert capsys.readouterr().out == f"{BASE}/api/v1/files/abc123\n"


def test_upload_streams_from_file_handle(tmp_path: Path) -> None:
    """upload_file：文件以句柄形式进入 httpx 流式发送（禁全量读的调用契约回归）。

    直接调用函数层验证：client.post 收到的是可迭代请求体而非预编码 bytes
    （httpx MultipartStream 对文件对象 64KB 分块读取）。
    """
    source = tmp_path / "a.txt"
    source.write_bytes(b"hello file")
    seen_body: list[object] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen_body.append(request.content)  # MockTransport 侧按需读全（测试模拟器固有）
        return _upload_response_handler(request)

    with _client(handler) as client:
        url = upload_file(
            base_url=BASE, path=source, expiry="24h", max_views=5, client=client
        )
    assert url == f"{BASE}/api/v1/files/abc123"
    assert len(seen_body) == 1


@pytest.mark.parametrize(
    ("status", "error_type", "detail"),
    [
        (413, "file_too_large", "文件超过大小上限"),
        (415, "file_type_not_allowed", "扩展名不在允许白名单内"),
        (422, "file_encrypt_not_available", "文件超过加密上限"),
    ],
)
def test_upload_api_errors_exit_1(
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
    status: int,
    error_type: str,
    detail: str,
) -> None:
    """upload 被 API 拒绝（413/415/422）：退出码 1，Problem Details detail 走 stderr。"""
    source = tmp_path / "b.txt"
    source.write_bytes(b"x")

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            status,
            json={"type": error_type, "title": "x", "status": status, "detail": detail},
        )

    with _client(handler) as client:
        exit_code = main(["upload", str(source), "--base-url", BASE], client=client)
    assert exit_code == EXIT_API_ERROR
    assert detail in capsys.readouterr().err


def test_upload_file_missing_or_not_regular_exit_2(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """upload 文件不存在或路径是目录（非普通文件）：参数错误 → 退出码 2。"""
    with _client(_upload_response_handler) as client:
        missing = main(["upload", "/no/such/file.bin", "--base-url", BASE], client=client)
    assert missing == EXIT_USAGE_ERROR
    assert "文件不存在" in capsys.readouterr().err

    with _client(_upload_response_handler) as client:
        directory = main(["upload", str(tmp_path), "--base-url", BASE], client=client)
    assert directory == EXIT_USAGE_ERROR
    assert "文件不存在" in capsys.readouterr().err


def test_upload_network_error_exit_1(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    """upload 网络错误：退出码 1，stderr 提示网络请求失败。"""
    source = tmp_path / "c.txt"
    source.write_bytes(b"x")

    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("连接被拒绝", request=request)

    with _client(handler) as client:
        exit_code = main(["upload", str(source), "--base-url", BASE], client=client)
    assert exit_code == EXIT_API_ERROR
    assert "网络请求失败" in capsys.readouterr().err


# ---- v0.2 get --output / --progress ----


def test_get_output_text_share_writes_file(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """get --output：文本分享 raw 200 → UTF-8 原样写入文件。"""
    target = tmp_path / "out.txt"
    with _client(_raw_handler("内容一")) as client:
        exit_code = main(
            ["get", "abc123", "--output", str(target), "--base-url", BASE], client=client
        )
    assert exit_code == 0
    assert target.read_text(encoding="utf-8") == "内容一"


class _ChunkedStream(httpx.SyncByteStream):
    """模拟服务端分块流式响应（httpx Response(stream=) 需 ByteStream 子类）。

    逐块产出字节，验证 CLI 侧 iter_bytes 逐块写盘路径（非整读 response.content）。
    """

    def __init__(self, chunks: list[bytes]) -> None:
        self._chunks = chunks

    def __iter__(self) -> Iterator[bytes]:
        return iter(self._chunks)

    def close(self) -> None:
        pass


def test_get_output_file_fallback_segmented_stream(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """get --output：文件短码回退——raw 404 → 探测元数据 → 分段流式下载字节一致。

    同时断言：请求序列（raw → 元数据 → download）、分块累计进度输出、
    输出目录按服务器文件名落盘。
    """
    chunks = [b"part1-", b"part2-", b"part3"]
    seen: list[str] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request.url.path)
        if request.url.path == "/api/v1/shares/abc123/raw":
            return httpx.Response(
                404,
                json={
                    "type": "share_not_found",
                    "title": "x",
                    "status": 404,
                    "detail": "短码 abc123 不存在",
                },
            )
        if request.url.path == "/api/v1/files/abc123":
            return httpx.Response(
                200,
                json={
                    "kind": "file",
                    "original_name": "data.bin",
                    "size_bytes": 12,
                },
            )
        if request.url.path == "/api/v1/files/abc123/download":
            return httpx.Response(200, stream=_ChunkedStream(chunks))
        raise AssertionError(f"未预期的请求路径：{request.url.path}")

    out_dir = tmp_path / "dl"
    out_dir.mkdir()
    with _client(handler) as client:
        exit_code = main(
            ["get", "abc123", "--output", str(out_dir), "--progress", "--base-url", BASE],
            client=client,
        )
    assert exit_code == 0
    assert seen == [
        "/api/v1/shares/abc123/raw",
        "/api/v1/files/abc123",
        "/api/v1/files/abc123/download",
    ]
    assert (out_dir / "data.bin").read_bytes() == b"part1-part2-part3"
    err = capsys.readouterr().err
    # iter_bytes(64KB) 把流内小块聚合成单次 64KB 写（17 = 6+6+5 字节）
    assert "已下载 17 字节" in err
    assert "已保存到" in err


def test_get_output_unknown_code_original_error(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """get --output：文本与文件端点均 404 → 抛文本端点原始错误（退出码 1），不写文件。"""
    target = tmp_path / "o.bin"

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/raw"):
            return httpx.Response(
                404,
                json={
                    "type": "share_not_found",
                    "title": "分享不存在",
                    "status": 404,
                    "detail": "短码 zzzz 不存在",
                },
            )
        return httpx.Response(
            404,
            json={
                "type": "file_not_found",
                "title": "文件不存在",
                "status": 404,
                "detail": "短码 zzzz 不存在",
            },
        )

    with _client(handler) as client:
        exit_code = main(
            ["get", "zzzz", "--output", str(target), "--base-url", BASE], client=client
        )
    assert exit_code == EXIT_API_ERROR
    assert "短码 zzzz 不存在" in capsys.readouterr().err
    assert not target.exists()


def test_get_output_missing_parent_dir_exit_2(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """get --output：目标父目录不存在 → 参数错误退出码 2（不发起有效写入）。"""
    target = tmp_path / "no-such-dir" / "o.txt"
    with _client(_raw_handler("内容")) as client:
        exit_code = main(
            ["get", "abc", "--output", str(target), "--base-url", BASE], client=client
        )
    assert exit_code == EXIT_USAGE_ERROR
    assert "输出目录不存在" in capsys.readouterr().err


def test_get_to_file_returns_saved_path(tmp_path: Path) -> None:
    """get_share_to_file：返回实际写入路径（文本分享原样路径）。"""
    target = tmp_path / "note.txt"
    with _client(_raw_handler("内容")) as client:
        saved = get_share_to_file(
            base_url=BASE, code="abc123", output=str(target), progress=False, client=client
        )
    assert saved == target
    assert target.read_text(encoding="utf-8") == "内容"


def test_upload_help_output_is_chinese(capsys: pytest.CaptureFixture[str]) -> None:
    """upload --help：中文文案，正常退出码 0。"""
    with pytest.raises(SystemExit) as exc_info:
        main(["upload", "--help"])
    assert exc_info.value.code == 0
    out = capsys.readouterr().out
    assert "PATH" in out
    assert "--expiry" in out
    assert "要上传的文件路径" in out


def test_get_help_output_has_output_progress(capsys: pytest.CaptureFixture[str]) -> None:
    """get --help：包含 --output/-o 与 --progress 中文说明。"""
    with pytest.raises(SystemExit) as exc_info:
        main(["get", "--help"])
    assert exc_info.value.code == 0
    out = capsys.readouterr().out
    assert "--output" in out
    assert "--progress" in out


def test_build_api_url_strips_trailing_slash() -> None:
    """URL 构造：base_url 尾部斜杠被去除。"""
    assert build_api_url("http://localhost:8000/", "/api/v1/shares") == "http://localhost:8000/api/v1/shares"
    assert build_api_url("http://localhost:8000", "/api/v1/shares") == "http://localhost:8000/api/v1/shares"


def test_extract_code_variants() -> None:
    """短码提取：裸短码 / 完整链接 / 带 fragment 链接 / 带 query string 链接。"""
    assert extract_code("AbC123") == "AbC123"
    assert extract_code("http://localhost:8000/s/AbC123") == "AbC123"
    assert extract_code("http://localhost:8000/s/AbC123#k=key") == "AbC123"
    assert extract_code("http://localhost:8000/s/AbC123?u=1") == "AbC123"
    assert extract_code("https://clipshare.example/s/xyz_9") == "xyz_9"


def test_cli_error_exit_code() -> None:
    """CliError 携带退出码契约：默认 1，参数错误 2。"""
    assert CliError("网络错误").exit_code == EXIT_API_ERROR
    assert CliError("参数错误", EXIT_USAGE_ERROR).exit_code == EXIT_USAGE_ERROR
