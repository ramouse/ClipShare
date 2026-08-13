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
}
# 注：CSP 属于页面渲染期策略（M4 前端页面按需配置），纯 JSON/PNG 的 API 响应无需 CSP


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
