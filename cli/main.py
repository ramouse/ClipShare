"""ClipShare 命令行分享工具（M5 创新点 B）。

用法：
    clipshare send TEXT|@FILE [--expiry 1h|24h|7d|forever] [--max-views 1|5|0]
    clipshare get CODE|URL [--base-url URL]

约定：
- 退出码：0 成功 / 1 网络或 API 错误 / 2 参数错误（argparse 非法参数天然为 2）；
- 服务器地址：--base-url > 环境变量 CLIPSHARE_BASE_URL > 默认 http://localhost:8000；
  注意：在 docker compose run 容器内使用时默认值指向容器自身，须显式
  --base-url http://app:8000（compose 服务名解析到真实应用容器）；
- 错误信息一律输出到 stderr，成功输出（分享链接 / 内容）到 stdout；
- get 输出内容原样保留（末尾缺换行时补一个，保证终端可读）；
- 加密分享的内容在服务器上是密文标记串（ENC1:...），get 原样输出该串。
"""

import argparse
import os
import sys
from collections.abc import Sequence
from pathlib import Path

import httpx

DEFAULT_BASE_URL = "http://localhost:8000"
ENV_BASE_URL = "CLIPSHARE_BASE_URL"
API_TIMEOUT_SECONDS = 15.0

EXIT_OK = 0
EXIT_API_ERROR = 1
EXIT_USAGE_ERROR = 2


class CliError(Exception):
    """CLI 业务错误：携带退出码（1=网络/API 错误，2=参数错误）。"""

    def __init__(self, message: str, exit_code: int = EXIT_API_ERROR) -> None:
        super().__init__(message)
        self.exit_code = exit_code


def build_api_url(base_url: str, path: str) -> str:
    """拼接 API 地址：base_url 去除尾部斜杠后追加路径。"""
    return f"{base_url.rstrip('/')}{path}"


def _api_error_message(response: httpx.Response) -> str:
    """从 Problem Details 响应中提取人类可读错误信息（detail > title > HTTP 状态码）。"""
    try:
        body = response.json()
    except ValueError:
        body = None
    if isinstance(body, dict):
        detail = body.get("detail")
        if isinstance(detail, str) and detail:
            return detail
        title = body.get("title")
        if isinstance(title, str) and title:
            return title
    return f"服务器返回错误（HTTP {response.status_code}）"


def send_share(
    *,
    base_url: str,
    content: str,
    expiry: str,
    max_views: int | None,
    client: httpx.Client,
) -> str:
    """创建分享：POST /api/v1/shares，返回分享网页链接；失败抛 CliError。"""
    payload: dict[str, object] = {"content": content, "expiry": expiry}
    if max_views is not None:
        payload["max_views"] = max_views
    try:
        response = client.post(build_api_url(base_url, "/api/v1/shares"), json=payload)
    except httpx.HTTPError as exc:
        raise CliError(f"网络请求失败：{exc}") from exc
    if response.status_code != 201:
        raise CliError(_api_error_message(response))
    try:
        body = response.json()
    except ValueError as exc:
        raise CliError("服务器响应不是有效 JSON") from exc
    url = body.get("url")
    if not isinstance(url, str) or not url:
        raise CliError("服务器响应缺少 url 字段")
    return url


def get_share(*, base_url: str, code: str, client: httpx.Client) -> str:
    """读取分享原始内容：GET /api/v1/shares/{code}/raw，原样返回；失败抛 CliError。"""
    try:
        response = client.get(build_api_url(base_url, f"/api/v1/shares/{code}/raw"))
    except httpx.HTTPError as exc:
        raise CliError(f"网络请求失败：{exc}") from exc
    if response.status_code != 200:
        raise CliError(_api_error_message(response))
    return response.text


def extract_code(code_or_url: str) -> str:
    """从短码或完整分享链接中提取短码；无法解析抛 CliError（参数错误，退出码 2）。"""
    if "/" not in code_or_url:
        return code_or_url
    # 先剥离 fragment 与 query string，再按路径解析
    path = code_or_url.split("#", 1)[0].split("?", 1)[0].rstrip("/")
    parts = path.split("/")
    if len(parts) < 2 or parts[-2] != "s" or not parts[-1]:
        raise CliError(f"无法从链接中解析短码：{code_or_url}", EXIT_USAGE_ERROR)
    return parts[-1]


def _resolve_content(text: str) -> str:
    """TEXT|@FILE → 内容：@ 前缀视为文件路径（UTF-8）；文件缺失/非 UTF-8 抛 CliError(2)。"""
    if not text.startswith("@"):
        return text
    path = Path(text[1:])
    if not path.is_file():
        raise CliError(f"文件不存在：{text[1:]}", EXIT_USAGE_ERROR)
    try:
        return path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        raise CliError(f"文件不是有效的 UTF-8 文本：{text[1:]}", EXIT_USAGE_ERROR) from None
    except OSError as exc:
        raise CliError(f"读取文件失败：{text[1:]}（{exc}）", EXIT_USAGE_ERROR) from exc


def _max_views_to_api(value: int) -> int | None:
    """CLI 的 max_views 语义：0=不限 → API 的 null；1/5 原样传递。"""
    return None if value == 0 else value


def build_parser() -> argparse.ArgumentParser:
    """构造参数解析器：中文帮助文案；--base-url 支持环境变量 CLIPSHARE_BASE_URL。"""
    parser = argparse.ArgumentParser(
        prog="clipshare",
        description="ClipShare 云剪切板命令行工具：无需浏览器即可创建与读取文本分享。",
    )
    subparsers = parser.add_subparsers(dest="command", required=True, metavar="{send,get}")

    common = argparse.ArgumentParser(add_help=False)
    common.add_argument(
        "--base-url",
        default=os.environ.get(ENV_BASE_URL, DEFAULT_BASE_URL),
        help=f"服务器地址（默认：环境变量 {ENV_BASE_URL}，缺省 {DEFAULT_BASE_URL}）",
    )

    send_parser = subparsers.add_parser("send", parents=[common], help="创建分享，输出分享链接")
    send_parser.add_argument(
        "text",
        metavar="TEXT|@FILE",
        help="分享内容；以 @ 开头视为文件路径，读取文件内容发送",
    )
    send_parser.add_argument(
        "--expiry",
        choices=("1h", "24h", "7d", "forever"),
        default="24h",
        help="有效期档位（默认 24h）",
    )
    send_parser.add_argument(
        "--max-views",
        choices=(1, 5, 0),
        default=0,
        type=int,
        help="访问次数上限：1 / 5 / 0（0=不限，默认）",
    )

    get_parser = subparsers.add_parser(
        "get", parents=[common], help="读取分享内容，原样输出到标准输出"
    )
    get_parser.add_argument(
        "code_or_url",
        metavar="CODE|URL",
        help="短码或完整分享链接（加密分享将输出密文标记串 ENC1:...）",
    )
    return parser


def main(argv: Sequence[str] | None = None, client: httpx.Client | None = None) -> int:
    """CLI 入口：解析参数 → 执行命令 → 返回退出码（0/1/2）。

    参数错误由 argparse 直接以退出码 2 结束进程；网络/API 错误捕获后
    输出到 stderr 并返回 1。client 仅测试注入（MockTransport），None 时自建。
    """
    parser = build_parser()
    args = parser.parse_args(argv)
    owns_client = client is None
    if client is None:
        client = httpx.Client(timeout=API_TIMEOUT_SECONDS)
    try:
        if args.command == "send":
            content = _resolve_content(args.text)
            if not content.strip():
                # 参数错误：argparse 输出 usage 到 stderr 并以退出码 2 退出
                parser.error("分享内容不能为空")
            url = send_share(
                base_url=args.base_url,
                content=content,
                expiry=args.expiry,
                max_views=_max_views_to_api(args.max_views),
                client=client,
            )
            print(url)
        elif args.command == "get":
            code = extract_code(args.code_or_url)
            content = get_share(base_url=args.base_url, code=code, client=client)
            sys.stdout.write(content)
            if not content.endswith("\n"):
                sys.stdout.write("\n")
        else:  # 防御分支：required=True 下理论上不可达
            parser.error(f"未知命令：{args.command}")
        return EXIT_OK
    except CliError as exc:
        print(f"clipshare: 错误：{exc}", file=sys.stderr)
        return exc.exit_code
    finally:
        if owns_client:
            client.close()
