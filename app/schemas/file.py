"""文件分享接口的请求 / 响应模型。"""
from datetime import datetime
from typing import Literal

from pydantic import BaseModel


class FileCreatedResponse(BaseModel):
    """文件上传成功响应（201）。"""

    code: str
    url: str
    original_name: str
    size_bytes: int
    encrypted: bool
    expires_at: datetime | None
    max_views: int | None
    created_at: datetime


class FileReadResponse(BaseModel):
    """文件元数据响应（200）：前端据此渲染文件卡片与下载/预览按钮。"""

    code: str
    # 固定 "file"：与文本分享区分，前端双探针分流依据之一
    kind: Literal["file"] = "file"
    original_name: str
    size_bytes: int
    encrypted: bool
    content_type: str
    # 由路由计算：未加密 + 大小不超预览截断上限 + 扩展名在预览白名单
    preview_available: bool
    expires_at: datetime | None
    # 预览 + 下载共享次数池的剩余次数；None 表示不限次
    remaining_views: int | None
    created_at: datetime
