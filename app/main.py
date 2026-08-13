"""ClipShare 应用入口。"""
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path

import structlog
from fastapi import FastAPI
from fastapi.staticfiles import StaticFiles

from app.api.routes.files import router as files_router
from app.api.routes.health import router as health_router
from app.api.routes.pages import router as pages_router
from app.api.routes.shares import router as shares_router
from app.core.config import get_settings
from app.core.errors import register_exception_handlers
from app.core.logging import configure_logging
from app.core.security import SecurityHeadersMiddleware, limiter

settings = get_settings()
configure_logging(settings.log_level, settings.environment)

logger = structlog.get_logger(__name__)

# 静态资源与模板固定相对 app 包定位，不依赖进程工作目录
STATIC_DIR = Path(__file__).resolve().parent / "static"


@asynccontextmanager
async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
    logger.info("app.startup", version="0.1.0", environment=settings.environment)
    yield
    logger.info("app.shutdown")


def create_app() -> FastAPI:
    """应用工厂：便于测试注入与未来扩展。"""
    app = FastAPI(
        title=settings.app_name,
        version="0.1.0",
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
    return app


app = create_app()
