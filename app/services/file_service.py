"""文件分享用例编排：服务层唯一业务入口，路由层禁止直接访问数据库。

磁盘写入由 FileStorage 独占（流式红线），本层只编排元数据与计数语义：
短码双写冲突重试、元数据读取不消耗次数、预览/下载共享次数池、过期懒删。
"""
from datetime import datetime
from typing import Literal

from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.core.config import get_settings
from app.core.errors import (
    ShareFileEncryptNotAvailableError,
    ShareFileExpiredError,
    ShareFileNotFoundError,
    ShareFileViewsExhaustedError,
    ShortcodeGenerationError,
)
from app.db.models import ShareFile
from app.db.repository import ShareFileRepository, ShortcodeRepository
from app.domain.expiry import Expiry, expires_at, is_expired
from app.domain.shortcode import SHORTCODE_MAX_RETRIES, generate_shortcode
from app.services.file_storage import FileStorage

settings = get_settings()


def file_url(code: str, base_url: str) -> str:
    """拼接文件 API 地址：base_url + /api/v1/files/ + code（先去除 base_url 尾部斜杠）。"""
    return f"{base_url.rstrip('/')}/api/v1/files/{code}"


def create_file_share(
    session: Session,
    *,
    original_name: str,
    stored_name: str,
    size_bytes: int,
    content_type: str,
    encrypted: bool,
    expiry: Expiry,
    max_views: int | None,
    now: datetime,
) -> ShareFile:
    """创建文件分享：短码双写（shortcodes + share_files）+ 唯一约束冲突重试。

    磁盘文件已由 FileStorage 提前流式落盘（stored_name 由其生成）；
    此处先向短码中心表登记（kind="file"），再写业务表，同一事务提交——
    短码被任何资源（文本/文件）占用时唯一约束冲突，回滚整个事务并重新
    生成短码重试，最多 SHORTCODE_MAX_RETRIES 次，仍失败抛
    ShortcodeGenerationError（映射 500）。now 由调用方注入（naive UTC）。

    加密红线（服务端兜底校验，不依赖前端）：E2E 加密文件上限
    file_encrypt_max_size（默认 10MB）——浏览器全内存加密的固有代价，
    超过上限的 encrypted=True 请求在此被拒绝（422），防止前端绕过产生
    「带加密标记但永远无法解密」的坏数据。
    """
    if encrypted and size_bytes > settings.file_encrypt_max_size:
        raise ShareFileEncryptNotAvailableError(
            f"文件超过加密上限 {settings.file_encrypt_max_size} 字节"
        )
    expires = expires_at(expiry, now)
    last_error: IntegrityError | None = None
    for _ in range(SHORTCODE_MAX_RETRIES):
        code = generate_shortcode()
        try:
            ShortcodeRepository.create(session, code=code, kind="file")
            record = ShareFileRepository.create(
                session,
                code=code,
                original_name=original_name,
                stored_name=stored_name,
                size_bytes=size_bytes,
                content_type=content_type,
                encrypted=encrypted,
                expires_at=expires,
                max_views=max_views,
            )
            session.commit()
            return record
        except IntegrityError as exc:
            # 冲突后必须回滚：事务处于失败状态，不回滚无法继续执行
            session.rollback()
            last_error = exc
    raise ShortcodeGenerationError(f"连续 {SHORTCODE_MAX_RETRIES} 次短码冲突") from last_error


def get_file_meta(
    session: Session, *, code: str, now: datetime, storage: FileStorage
) -> ShareFile:
    """读取文件元数据：不消耗预览/下载次数（与文本分享二维码同语义）。

    判定顺序：记录不存在 → 404；已过期 → 顺带懒删磁盘文件 + 410。
    过期懒删：过期文件在访问时随带清理磁盘，避免堆积（计划：过期懒删除）。
    """
    record = ShareFileRepository.get_by_code(session, code)
    if record is None:
        raise ShareFileNotFoundError(f"短码 {code} 不存在")
    if is_expired(record.expires_at, now):
        # 懒删：元数据读取路径也承担清理职责，磁盘不残留已过期内容
        storage.delete(record.stored_name)
        raise ShareFileExpiredError("文件已过期")
    return record


def consume_file(
    session: Session,
    *,
    code: str,
    now: datetime,
    storage: FileStorage,
    mode: Literal["preview", "download"],
) -> ShareFile:
    """读取文件内容（预览/下载）并消耗一次访问次数。

    判定顺序：记录不存在 → 404；已过期 → 懒删磁盘文件 + 410；
    守卫式原子自增返回 None（已达上限）→ 410；成功 → 提交计数并返回最新记录。
    mode 决定消耗预览还是下载计数——两者共享 max_views 次数池
    （used = preview_count + download_count），判定与自增在 SQL 层原子完成。
    """
    record = ShareFileRepository.get_by_code(session, code)
    if record is None:
        raise ShareFileNotFoundError(f"短码 {code} 不存在")
    if is_expired(record.expires_at, now):
        # 懒删：过期文件在访问时随带清理磁盘，避免堆积
        storage.delete(record.stored_name)
        raise ShareFileExpiredError("文件已过期")
    if mode == "preview":
        updated = ShareFileRepository.increment_preview_count_guarded(session, record.id)
    else:
        updated = ShareFileRepository.increment_download_count_guarded(session, record.id)
    if updated is None:
        session.rollback()
        raise ShareFileViewsExhaustedError("文件访问次数已耗尽")
    session.commit()
    return updated
