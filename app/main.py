"""ClipShare 应用入口。"""
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

import structlog
from fastapi import FastAPI

from app.api.routes.health import router as health_router
from app.core.config import get_settings
from app.core.logging import configure_logging

settings = get_settings()
configure_logging(settings.log_level, settings.environment)

logger = structlog.get_logger(__name__)


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
    app.include_router(health_router)
    return app


app = create_app()
