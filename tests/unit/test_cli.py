"""CLI 单元测试：参数解析、URL 构造、错误处理（httpx MockTransport 模拟 API，不依赖真服务器）。"""
import json
from collections.abc import Callable

import httpx
import pytest

from cli.main import (
    EXIT_API_ERROR,
    EXIT_USAGE_ERROR,
    CliError,
    build_api_url,
    extract_code,
    main,
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
