"""文件分享资源路由：上传 / 元数据 / 预览 / 下载（挂载前缀 /api/v1）。

流式红线落点：上传走 FileStorage.save_streamed 64KB 逐块落盘，路由层绝不
file.read() 全量读入内存；下载经 Starlette FileResponse 流式输出。
用户文件名绝不进入磁盘路径（磁盘名 secrets 生成，文件名仅存元数据）。
"""
from pathlib import Path
from typing import Literal

from fastapi import APIRouter, Depends, File, Form, Request, Response, UploadFile
from fastapi.responses import FileResponse
from sqlalchemy.orm import Session

from app.api.deps import get_db
from app.core.config import get_settings
from app.core.errors import (
    ShareFileContentMissingError,
    ShareFilePreviewNotAvailableError,
    ShareFileTypeNotAllowedError,
    ShareFileValidationError,
)
from app.core.security import limiter
from app.core.time import utcnow
from app.domain.access import remaining_views
from app.domain.expiry import Expiry
from app.domain.filename import sanitize_filename
from app.schemas.file import FileCreatedResponse, FileReadResponse
from app.services import file_service
from app.services.file_storage import FileStorage

router = APIRouter(prefix="/files", tags=["files"])

settings = get_settings()


def _get_storage() -> FileStorage:
    """按当前配置构造存储服务：测试可 monkeypatch 配置项注入隔离目录。"""
    return FileStorage(Path(settings.file_storage_dir))


def _file_extension(name: str) -> str:
    """提取文件名扩展名（小写、去前导点）；无扩展名返回空串。"""
    return Path(name).suffix.lower().lstrip(".")


def _is_previewable(*, encrypted: bool, size_bytes: int, original_name: str) -> bool:
    """计算 preview_available：未加密 + 大小不超截断上限 + 扩展名在预览白名单。

    元数据响应与预览端点共用同一规则，保证「提示可预览」与实际行为一致。
    """
    return (
        not encrypted
        and size_bytes <= settings.file_preview_max_size
        and _file_extension(original_name) in settings.file_preview_extensions_set
    )


def _parse_max_views(raw: str) -> int | None:
    """解析 max_views 表单值：空串 → None（不限次）；"1"/"5" → 对应次数；其余 422。

    前端「不传」即发送空串，故不能直接用 Literal 约束（空串需映射为 None），
    按任务约定以 str 接收后手动校验。
    """
    if raw == "":
        return None
    if raw in ("1", "5"):
        return int(raw)
    # 与 FastAPI 内置校验同 shape 同 type（validation_error），客户端按 type 分流行为一致
    raise ShareFileValidationError("max_views 仅支持 1 或 5（留空表示不限次数）")


@router.post("", status_code=201, response_model=FileCreatedResponse, summary="上传文件")
@limiter.limit(settings.rate_limit_upload)
def upload_file(
    request: Request,
    file: UploadFile = File(...),
    expiry: Literal["1h", "24h", "7d", "forever"] = Form("24h"),
    max_views: str = Form(""),
    encrypted: bool = Form(False),
    db: Session = Depends(get_db),
) -> FileCreatedResponse:
    """流式上传文件：净化文件名 → 白名单 415 → 参数校验 422 → 流式落盘。

    落盘超限 413 由存储层中断并自清理半成品；服务层加密上限兜底 422 时
    路由补偿删除已落盘文件——任何异常路径都保证磁盘无孤儿文件。
    """
    original_name = sanitize_filename(file.filename or "")
    ext = _file_extension(original_name)
    if ext not in settings.file_allowed_extensions_set:
        raise ShareFileTypeNotAllowedError(f"扩展名「{ext or '无'}」不在允许白名单内")
    expiry_value = Expiry(expiry)
    max_views_value = _parse_max_views(max_views)

    storage = _get_storage()
    stored_name: str | None = None
    try:
        stored_name = storage.save_streamed(file.file, max_size=settings.file_max_size)
        # 大小从已落盘的文件对象取：仅 seek 计数，不读取内容进内存（流式红线）
        file.file.seek(0, 2)
        size_bytes = file.file.tell()
        record = file_service.create_file_share(
            db,
            original_name=original_name,
            stored_name=stored_name,
            size_bytes=size_bytes,
            content_type=file.content_type or "application/octet-stream",
            encrypted=encrypted,
            expiry=expiry_value,
            max_views=max_views_value,
            now=utcnow(),
        )
    except Exception:
        # 任何异常路径（含加密超限 422、短码冲突耗尽 500）：补偿删除已落盘文件
        if stored_name is not None:
            storage.delete(stored_name)
        raise
    return FileCreatedResponse(
        code=record.code,
        url=file_service.file_url(record.code, settings.public_base_url),
        original_name=record.original_name,
        size_bytes=record.size_bytes,
        encrypted=record.encrypted,
        expires_at=record.expires_at,
        max_views=record.max_views,
        created_at=record.created_at,
    )


@router.get("/{code}", response_model=FileReadResponse, summary="读取文件元数据")
@limiter.limit(settings.rate_limit_file_read)
def get_file_meta(request: Request, code: str, db: Session = Depends(get_db)) -> FileReadResponse:
    """文件元数据：不消耗预览/下载次数（与文本分享二维码同语义）。

    不存在 404；已过期 410（服务层顺带懒删磁盘文件）。
    """
    record = file_service.get_file_meta(db, code=code, now=utcnow(), storage=_get_storage())
    return FileReadResponse(
        code=record.code,
        original_name=record.original_name,
        size_bytes=record.size_bytes,
        encrypted=record.encrypted,
        content_type=record.content_type,
        preview_available=_is_previewable(
            encrypted=record.encrypted,
            size_bytes=record.size_bytes,
            original_name=record.original_name,
        ),
        expires_at=record.expires_at,
        remaining_views=remaining_views(
            record.max_views, record.preview_count + record.download_count
        ),
        created_at=record.created_at,
    )


@router.get("/{code}/preview", response_class=Response, summary="预览文件内容")
@limiter.limit(settings.rate_limit_file_read)
def preview_file(request: Request, code: str, db: Session = Depends(get_db)) -> Response:
    """文本预览：返回文件头部（最多 file_preview_max_size 字节，截断读）。

    判定顺序：元数据（404/410，不消耗）→ 可预览性（415，不消耗）→
    消耗一次预览计数 → 截断读磁盘。预览与下载共享访问次数池。
    """
    record = file_service.get_file_meta(db, code=code, now=utcnow(), storage=_get_storage())
    if not _is_previewable(
        encrypted=record.encrypted,
        size_bytes=record.size_bytes,
        original_name=record.original_name,
    ):
        raise ShareFilePreviewNotAvailableError(
            "文件不支持文本预览（加密 / 超过预览上限 / 扩展名不在预览白名单）"
        )
    storage = _get_storage()
    # 内容缺失预检前置：文件已丢时直接 410，不白白消耗访问次数（审查加固）
    if not storage.path(record.stored_name).is_file():
        raise ShareFileContentMissingError("文件内容缺失，可能已被清理")
    consumed = file_service.consume_file(
        db, code=code, now=utcnow(), storage=storage, mode="preview"
    )
    try:
        preview_bytes = storage.read_preview(
            consumed.stored_name, max_size=settings.file_preview_max_size
        )
    except FileNotFoundError as exc:
        # 预检与读取之间被并发清理的竞态兜底：与下载端点语义一致映射 410
        raise ShareFileContentMissingError("文件内容缺失，可能已被清理") from exc
    return Response(content=preview_bytes, media_type="text/plain")


@router.get("/{code}/download", response_class=FileResponse, summary="下载文件")
@limiter.limit(settings.rate_limit_file_download)
def download_file(request: Request, code: str, db: Session = Depends(get_db)) -> FileResponse:
    """下载文件：消耗一次下载计数，FileResponse 流式输出。

    中文文件名经 RFC 5987 filename* 编码（Starlette 内建处理）；
    磁盘文件缺失（记录尚在）→ 410 file_content_missing。
    """
    storage = _get_storage()
    record = file_service.get_file_meta(db, code=code, now=utcnow(), storage=storage)
    # 内容缺失预检前置：文件已丢时直接 410，不白白消耗访问次数（审查加固）
    path = storage.path(record.stored_name)
    if not path.is_file():
        raise ShareFileContentMissingError("文件内容缺失，可能已被清理")
    consumed = file_service.consume_file(
        db, code=code, now=utcnow(), storage=storage, mode="download"
    )
    return FileResponse(
        path=path,
        filename=consumed.original_name,
        media_type=consumed.content_type or "application/octet-stream",
    )
