"""统一错误模型与异常处理器（RFC 9457 Problem Details 风格）。

所有错误响应统一为 {"type", "title", "status", "detail"} 形状：
- type：稳定机器码，供客户端程序化判断（如 share_not_found）
- title：人类可读的简短标题
- status：HTTP 状态码
- detail：补充说明（可含定位信息）
"""
from collections.abc import Mapping
from typing import Any

import structlog
from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from slowapi.errors import RateLimitExceeded
from starlette.exceptions import HTTPException as StarletteHTTPException

logger = structlog.get_logger(__name__)


class ProblemDetail(BaseModel):
    """客户端冻结的 RFC 9457 风格错误契约。"""

    model_config = ConfigDict(extra="forbid")

    type: str = Field(description="稳定机器错误码")
    title: str = Field(description="面向用户的简短标题")
    status: int = Field(ge=400, le=599, description="HTTP 状态码")
    detail: str = Field(description="本次错误的具体说明")


_PROBLEM_DESCRIPTIONS: dict[int, str] = {
    400: "请求或幂等键格式错误",
    404: "资源不存在",
    409: "幂等键冲突",
    410: "资源已过期、耗尽或不可再获取",
    413: "请求内容过大",
    415: "媒体类型或文件类型不受支持",
    422: "请求参数校验失败",
    429: "请求速率超限",
    500: "服务器内部错误",
}


def problem_responses(*statuses: int) -> dict[int | str, dict[str, Any]]:
    """生成使用 ``application/problem+json`` 的 OpenAPI 响应声明。"""
    responses: dict[int | str, dict[str, Any]] = {
        status: {
            "description": _PROBLEM_DESCRIPTIONS[status],
            "content": {
                "application/problem+json": {
                    "schema": {"$ref": "#/components/schemas/ProblemDetail"}
                }
            },
        }
        for status in statuses
    }
    if 429 in responses:
        responses[429]["headers"] = {
            "Retry-After": {
                "description": "客户端再次尝试前应等待的十进制秒数",
                "schema": {"type": "integer", "minimum": 1},
            }
        }
    return responses


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


class ShareFileNotFoundError(AppError):
    """文件分享不存在（404）。"""

    type = "file_not_found"
    title = "文件不存在"
    status = 404


class ShareFileExpiredError(AppError):
    """文件分享已过期（410）；触发时会顺带懒删磁盘文件。"""

    type = "file_expired"
    title = "文件已过期"
    status = 410


class ShareFileViewsExhaustedError(AppError):
    """文件预览/下载次数已耗尽（410）。"""

    type = "file_views_exhausted"
    title = "文件访问次数已耗尽"
    status = 410


class ShareFileTooLargeError(AppError):
    """文件超过大小上限（413）；流式落盘过程中断，磁盘不残留半成品。"""

    type = "file_too_large"
    title = "文件过大"
    status = 413


class ShareFileTypeNotAllowedError(AppError):
    """文件扩展名不在白名单内（415）。"""

    type = "file_type_not_allowed"
    title = "文件类型不允许"
    status = 415


class ShareFileEncryptNotAvailableError(AppError):
    """文件超过加密上限或类型不支持加密（422）：明文直传或降级处理。"""

    type = "file_encrypt_not_available"
    title = "该文件不支持加密"
    status = 422


class ShareFilePreviewNotAvailableError(AppError):
    """文件不支持文本预览（415）：仅允许预览扩展名且在截断上限内。"""

    type = "preview_not_available"
    title = "文件不支持预览"
    status = 415


class ShareFileContentMissingError(AppError):
    """数据库记录存在但磁盘文件缺失（410）：视为内容不可再获取。"""

    type = "file_content_missing"
    title = "文件内容缺失"
    status = 410


class ShareFileValidationError(AppError):
    """文件表单参数校验失败（422）：与 FastAPI 内置校验同 type，客户端分流行为一致。"""

    type = "validation_error"
    title = "请求参数校验失败"
    status = 422


class IdempotencyKeyInvalidError(AppError):
    """Idempotency-Key 不符合冻结格式（400）。"""

    type = "idempotency_key_invalid"
    title = "幂等键格式错误"
    status = 400


class IdempotencyConflictError(AppError):
    """相同幂等键被用于不同请求（409）。"""

    type = "idempotency_conflict"
    title = "幂等键冲突"
    status = 409


class IdempotencyReplayUnavailableError(AppError):
    """幂等记录指向的资源不可恢复（409）。"""

    type = "idempotency_replay_unavailable"
    title = "幂等结果不可恢复"
    status = 409


def _problem_response(
    status: int,
    problem_type: str,
    title: str,
    detail: str,
    headers: Mapping[str, str] | None = None,
) -> JSONResponse:
    """构造不可缓存的 Problem Details 响应，并保留框架要求的响应头。"""
    response_headers = {"Cache-Control": "no-store"}
    if headers is not None:
        response_headers.update(headers)
    return JSONResponse(
        status_code=status,
        content={"type": problem_type, "title": title, "status": status, "detail": detail},
        headers=response_headers,
        media_type="application/problem+json",
    )


async def app_error_handler(request: Request[Any], exc: AppError) -> JSONResponse:
    """业务错误（AppError 子类）→ Problem Details。"""
    return _problem_response(exc.status, exc.type, exc.title, exc.detail)


async def http_error_handler(request: Request[Any], exc: StarletteHTTPException) -> JSONResponse:
    """框架 HTTP 异常（如路由不存在、方法不允许）→ Problem Details。"""
    detail = exc.detail if isinstance(exc.detail, str) else str(exc.detail)
    return _problem_response(exc.status_code, "http_error", "请求错误", detail, exc.headers)


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
    # SQLAlchemy/驱动异常的文本和 traceback 可能含绑定参数；这里只记录异常类型，
    # 不记录异常消息、SQL、参数或堆栈，避免剪贴板正文与文件元数据进入日志。
    logger.error("unhandled_error", error_type=type(exc).__name__)
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
