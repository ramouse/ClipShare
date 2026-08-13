"""分享接口的请求 / 响应模型。"""
from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field

from app.core.config import get_settings

settings = get_settings()


class ShareCreateRequest(BaseModel):
    """创建分享请求体：content 必填，expiry / max_views 可省略。"""

    content: str = Field(min_length=1, max_length=settings.share_max_content_length)
    expiry: Literal["1h", "24h", "7d", "forever"] = "24h"
    max_views: Literal[1, 5] | None = None


class ShareCreatedResponse(BaseModel):
    """创建分享成功响应（201）。"""

    code: str
    url: str
    expires_at: datetime | None
    max_views: int | None
    created_at: datetime


class ShareReadResponse(BaseModel):
    """读取分享成功响应（200）。"""

    code: str
    content: str
    expires_at: datetime | None
    remaining_views: int | None
    created_at: datetime
