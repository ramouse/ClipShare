"""ClipShare 应用入口。"""
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

import structlog
from fastapi import FastAPI
from fastapi.openapi.utils import get_openapi
from fastapi.staticfiles import StaticFiles

from app.api.routes.files import router as files_router
from app.api.routes.health import router as health_router
from app.api.routes.pages import router as pages_router
from app.api.routes.shares import router as shares_router
from app.core.config import get_settings
from app.core.errors import ProblemDetail, register_exception_handlers
from app.core.logging import configure_logging
from app.core.security import SecurityHeadersMiddleware, limiter

settings = get_settings()
configure_logging(settings.log_level, settings.environment)

logger = structlog.get_logger(__name__)

API_CONTRACT_VERSION = "1.0.0"

# 静态资源与模板固定相对 app 包定位，不依赖进程工作目录
STATIC_DIR = Path(__file__).resolve().parent / "static"


@asynccontextmanager
async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
    logger.info("app.startup", version=API_CONTRACT_VERSION, environment=settings.environment)
    yield
    logger.info("app.shutdown")


def create_app() -> FastAPI:
    """应用工厂：便于测试注入与未来扩展。"""
    app = FastAPI(
        title=settings.app_name,
        version=API_CONTRACT_VERSION,
        description="轻量级云剪切板分享系统 API",
        lifespan=lifespan,
    )
    register_exception_handlers(app)
    # slowapi 0.1.10 无 init_app：限流检查在 @limiter.limit 装饰器内完成，
    # 此处仅按惯例挂载实例，供默认 429 处理器等扩展点使用
    app.state.limiter = limiter
    app.add_middleware(SecurityHeadersMiddleware)
    app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")
    app.include_router(shares_router, prefix="/api/v1")
    app.include_router(files_router, prefix="/api/v1")
    app.include_router(pages_router)
    app.include_router(health_router)

    def custom_openapi() -> dict[str, Any]:
        """生成冻结契约，并注册所有错误响应共用的 ProblemDetail schema。"""
        if app.openapi_schema is not None:
            return app.openapi_schema
        schema = get_openapi(
            title=app.title,
            version=app.version,
            description=app.description,
            routes=app.routes,
        )
        components = schema.setdefault("components", {}).setdefault("schemas", {})
        components["ProblemDetail"] = ProblemDetail.model_json_schema(mode="serialization")
        for path, path_item in schema.get("paths", {}).items():
            if not path.startswith("/api/"):
                continue
            for method in ("get", "post", "put", "patch", "delete"):
                operation = path_item.get(method)
                if operation is None:
                    continue
                for response in operation.get("responses", {}).values():
                    response.setdefault("headers", {}).setdefault(
                        "Cache-Control",
                        {
                            "description": "固定为 no-store，禁止缓存绕过过期和访问次数",
                            "schema": {"type": "string", "const": "no-store"},
                        },
                    )
        app.openapi_schema = schema
        return schema

    app.openapi = custom_openapi  # type: ignore[method-assign]
    return app


app = create_app()
