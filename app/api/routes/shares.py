"""分享资源路由：创建 / 读取 / 原始文本 / 二维码（挂载前缀 /api/v1）。"""
import io

import qrcode
from fastapi import APIRouter, Depends, Request, Response
from sqlalchemy.orm import Session

from app.api.deps import get_db
from app.core.config import get_settings
from app.core.security import limiter
from app.core.time import utcnow
from app.domain.access import remaining_views
from app.domain.expiry import Expiry
from app.schemas.share import ShareCreatedResponse, ShareCreateRequest, ShareReadResponse
from app.services import share_service

router = APIRouter(prefix="/shares", tags=["shares"])

settings = get_settings()


@router.post("", status_code=201, response_model=ShareCreatedResponse, summary="创建分享")
@limiter.limit(settings.rate_limit_create)
def create_share(
    request: Request,
    payload: ShareCreateRequest,
    db: Session = Depends(get_db),
) -> ShareCreatedResponse:
    """匿名创建文本分享，返回短码与分享网页地址。"""
    share = share_service.create_share(
        db,
        content=payload.content,
        expiry=Expiry(payload.expiry),
        max_views=payload.max_views,
        now=utcnow(),
    )
    return ShareCreatedResponse(
        code=share.code,
        url=share_service.share_url(share.code, settings.public_base_url),
        expires_at=share.expires_at,
        max_views=share.max_views,
        created_at=share.created_at,
    )


@router.get("/{code}/raw", response_class=Response, summary="读取分享原始文本")
@limiter.limit(settings.rate_limit_read)
def read_share_raw(request: Request, code: str, db: Session = Depends(get_db)) -> Response:
    """原始文本（text/plain; charset=utf-8），供 curl / CLI 使用；消耗次数，语义同读取接口。"""
    share = share_service.view_share(db, code=code, now=utcnow())
    return Response(content=share.content, media_type="text/plain")


@router.get("/{code}/qr", response_class=Response, summary="分享二维码")
@limiter.limit(settings.rate_limit_read)
def get_share_qr(request: Request, code: str, db: Session = Depends(get_db)) -> Response:
    """二维码 PNG：内容为分享网页地址；不消耗访问次数、不判过期（仅校验存在）。"""
    url = share_service.qr_share_url(db, code=code, base_url=settings.public_base_url)
    image = qrcode.make(url)
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return Response(content=buffer.getvalue(), media_type="image/png")


@router.get("/{code}", response_model=ShareReadResponse, summary="读取分享")
@limiter.limit(settings.rate_limit_read)
def read_share(request: Request, code: str, db: Session = Depends(get_db)) -> ShareReadResponse:
    """读取分享内容（消耗一次访问次数）；不存在 404，过期 / 超次 410。"""
    share = share_service.view_share(db, code=code, now=utcnow())
    return ShareReadResponse(
        code=share.code,
        content=share.content,
        expires_at=share.expires_at,
        remaining_views=remaining_views(share.max_views, share.view_count),
        created_at=share.created_at,
    )
