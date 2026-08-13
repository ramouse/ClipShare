"""统一错误模型与异常处理器（RFC 9457 Problem Details 风格）。

所有错误响应统一为 {"type", "title", "status", "detail"} 形状：
- type：稳定机器码，供客户端程序化判断（如 share_not_found）
- title：人类可读的简短标题
- status：HTTP 状态码
- detail：补充说明（可含定位信息）
"""
from typing import Any

import structlog
from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from slowapi.errors import RateLimitExceeded
from starlette.exceptions import HTTPException as StarletteHTTPException

logger = structlog.get_logger(__name__)


class AppError(Exception):
    """业务错误基类：服务层抛出，由全局异常处理器转成 Problem Details 响应。"""

    type: str = "app_error"
    title: str = "应用错误"
    status: int = 500

    def __init__(self, detail: str | None = None) -> None:
        self.detail = detail or self.title
        super().__init__(self.detail)


class ShareNotFoundError(AppError):
    """分享不存在（404）。"""

    type = "share_not_found"
    title = "分享不存在"
    status = 404


class ShareExpiredError(AppError):
    """分享已过期（410）。"""

    type = "share_expired"
    title = "分享已过期"
    status = 410


class ViewsExhaustedError(AppError):
    """分享访问次数已耗尽（410）。"""

    type = "share_views_exhausted"
    title = "分享访问次数已耗尽"
    status = 410


class ShortcodeGenerationError(AppError):
    """短码连续冲突导致创建失败（500）。"""

    type = "shortcode_generation_failed"
    title = "短码生成失败"
    status = 500


def _problem_response(
    status: int,
    problem_type: str,
    title: str,
    detail: str,
    headers: dict[str, str] | None = None,
) -> JSONResponse:
    """构造 Problem Details 响应。"""
    return JSONResponse(
        status_code=status,
        content={"type": problem_type, "title": title, "status": status, "detail": detail},
        headers=headers,
    )


async def app_error_handler(request: Request[Any], exc: AppError) -> JSONResponse:
    """业务错误（AppError 子类）→ Problem Details。"""
    return _problem_response(exc.status, exc.type, exc.title, exc.detail)


async def http_error_handler(request: Request[Any], exc: StarletteHTTPException) -> JSONResponse:
    """框架 HTTP 异常（如路由不存在、方法不允许）→ Problem Details。"""
    detail = exc.detail if isinstance(exc.detail, str) else str(exc.detail)
    return _problem_response(exc.status_code, "http_error", "请求错误", detail)


async def validation_error_handler(
    request: Request[Any], exc: RequestValidationError
) -> JSONResponse:
    """请求体校验失败 → 422 Problem Details（detail 汇总所有字段错误）。"""
    details = []
    for error in exc.errors():
        location = ".".join(str(part) for part in error["loc"])
        details.append(f"{location}: {error['msg']}")
    return _problem_response(422, "validation_error", "请求参数校验失败", "; ".join(details))


async def unhandled_error_handler(request: Request[Any], exc: Exception) -> JSONResponse:
    """兜底：未预期异常 → 500 Problem Details（保证所有响应均为 JSON）。"""
    logger.error(
        "unhandled_error", error=str(exc), exc_info=(type(exc), exc, exc.__traceback__)
    )
    return _problem_response(500, "internal_error", "服务器内部错误", "服务器内部错误，请稍后重试")


async def rate_limit_exceeded_handler(
    request: Request[Any], exc: RateLimitExceeded
) -> JSONResponse:
    """速率超限 → 429 Problem Details，并携带 Retry-After 重试时间。

    覆盖 slowapi 的默认 429 响应形状（{"error": ...}）。
    """
    headers: dict[str, str] = {}
    limit = exc.limit
    item = limit.limit if limit is not None else None
    window = item.get_expiry() if item is not None else None
    if window is not None:
        headers["Retry-After"] = str(window)
    detail = (
        f"请求过于频繁，请 {window} 秒后重试" if window is not None else "请求过于频繁，请稍后重试"
    )
    return _problem_response(429, "rate_limited", "请求过于频繁", detail, headers)


def register_exception_handlers(app: FastAPI) -> None:
    """注册全部 Problem Details 异常处理器（统一在应用工厂中调用）。"""
    # Starlette 的 add_exception_handler 签名只接受 Exception 宽类型处理器，
    # 而各处理器精确到具体异常子类（参数逆变），属类型系统固有局限，忽略即可
    app.add_exception_handler(AppError, app_error_handler)  # type: ignore[arg-type]
    app.add_exception_handler(StarletteHTTPException, http_error_handler)  # type: ignore[arg-type]
    app.add_exception_handler(RequestValidationError, validation_error_handler)  # type: ignore[arg-type]
    app.add_exception_handler(RateLimitExceeded, rate_limit_exceeded_handler)  # type: ignore[arg-type]
    app.add_exception_handler(Exception, unhandled_error_handler)
