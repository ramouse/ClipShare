"""安全响应头中间件与速率限制器。"""
from fastapi import Request
from slowapi import Limiter
from slowapi.util import get_remote_address
from starlette.middleware.base import BaseHTTPMiddleware, RequestResponseEndpoint
from starlette.responses import Response
from starlette.types import ASGIApp

# 安全响应头：由中间件统一追加到每个响应
SECURITY_HEADERS: dict[str, str] = {
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Referrer-Policy": "no-referrer",
    # CSP（M4 页面渲染期策略）：API 的 JSON/PNG 响应带上无副作用，页面据此收窄执行面
    "Content-Security-Policy": (
        "default-src 'self'; "  # 一切资源默认仅同源（含 fetch 的 connect-src）
        "img-src 'self' data:; "  # 二维码由同源 API 提供；data: 为预留内联图场景
        "script-src 'self'; "  # 禁内联脚本与事件处理器（页面 JS 全部走 /static 文件）
        "style-src 'self'; "  # 样式全部走文件：Bootstrap/highlight.js 主题均为 .css，
        # 页面无 <style>/style 属性依赖（JS 的 element.style 赋值不受 CSP 限制），
        # 故无需 'unsafe-inline'，保留对注入样式的封堵
        "base-uri 'none'; "  # 禁 <base>，防基址劫持
        "frame-ancestors 'none'; "  # 与 X-Frame-Options: DENY 双保险防点击劫持
        "form-action 'self'; "  # 表单只能提交到同源（审查加固）
        "object-src 'none'"  # 禁插件类对象嵌入（审查加固）
    ),
}


class SecurityHeadersMiddleware(BaseHTTPMiddleware):
    """为所有响应追加安全响应头。"""

    def __init__(self, app: ASGIApp) -> None:
        super().__init__(app)

    async def dispatch(self, request: Request, call_next: RequestResponseEndpoint) -> Response:
        response = await call_next(request)
        for name, value in SECURITY_HEADERS.items():
            response.headers[name] = value
        return response


# 速率限制器：内存型存储（仅进程内瞬时计数，重启即清零），key 为客户端 IP。
# IP 仅用于限流判定，不落日志、不落库（项目书隐私红线：不记录用户个人信息与 IP）。
# M6 遗留：get_remote_address 直接信任 X-Forwarded-For 首个 IP，生产环境置于
# 反向代理后时，代理必须剥离/覆盖该头（否则限流可被伪造 IP 绕过）。
limiter = Limiter(key_func=get_remote_address, storage_uri="memory://")
