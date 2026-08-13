"""分享用例编排：服务层唯一业务入口，路由层禁止直接访问数据库。"""
from datetime import datetime

from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.core.errors import (
    ShareExpiredError,
    ShareNotFoundError,
    ShortcodeGenerationError,
    ViewsExhaustedError,
)
from app.db.models import Share
from app.db.repository import ShareRepository, ShortcodeRepository
from app.domain.expiry import Expiry, expires_at, is_expired
from app.domain.shortcode import SHORTCODE_MAX_RETRIES, generate_shortcode


def share_url(code: str, base_url: str) -> str:
    """拼接分享网页地址：base_url + /s/ + code（先去除 base_url 尾部斜杠）。"""
    return f"{base_url.rstrip('/')}/s/{code}"


def create_share(
    session: Session,
    *,
    content: str,
    expiry: Expiry,
    max_views: int | None,
    now: datetime,
) -> Share:
    """创建分享：短码双写（shortcodes + shares）+ 唯一约束冲突重试。

    先向短码中心表登记（kind="share"），再写业务表，同一事务提交——
    任何一类资源（文本/文件）占用过的短码都不可能再被另一类使用，
    跨类型全局唯一由 shortcodes 主键约束兜底。
    now 由调用方注入（naive UTC），便于测试与全链路时间约定统一。
    冲突重试：捕获唯一约束 IntegrityError 后回滚事务并重新生成短码，
    最多 SHORTCODE_MAX_RETRIES 次，仍失败抛 ShortcodeGenerationError（映射 500）。
    """
    expires = expires_at(expiry, now)
    last_error: IntegrityError | None = None
    for _ in range(SHORTCODE_MAX_RETRIES):
        code = generate_shortcode()
        try:
            ShortcodeRepository.create(session, code=code, kind="share")
            share = ShareRepository.create(
                session,
                code=code,
                content=content,
                expires_at=expires,
                max_views=max_views,
            )
            session.commit()
            return share
        except IntegrityError as exc:
            # 冲突后必须回滚：事务处于失败状态，不回滚无法继续执行
            session.rollback()
            last_error = exc
    raise ShortcodeGenerationError(f"连续 {SHORTCODE_MAX_RETRIES} 次短码冲突") from last_error


def view_share(session: Session, *, code: str, now: datetime) -> Share:
    """读取分享并消耗一次访问次数。

    判定顺序：短码不存在 → 404；已过期（is_expired）→ 410；
    守卫式原子自增返回 None（已达上限）→ 410；成功 → 提交计数并返回最新记录。
    过期 / 超次判定与自增在 SQL 层原子完成，杜绝「先判定、后自增」的并发竞态。
    """
    share = ShareRepository.get_by_code(session, code)
    if share is None:
        raise ShareNotFoundError(f"短码 {code} 不存在")
    # 注：过期判定与守卫自增之间存毫秒级窗口，恰在窗口内到期可能放行一次边界访问；
    # is_expired 边界含等号（expiry.py），语义可接受，不属「超次」红线
    if is_expired(share.expires_at, now):
        raise ShareExpiredError("分享已过期")
    updated = ShareRepository.increment_view_count_guarded(session, share.id)
    if updated is None:
        session.rollback()
        raise ViewsExhaustedError("分享访问次数已耗尽")
    session.commit()
    return updated


def qr_share_url(session: Session, *, code: str, base_url: str) -> str:
    """二维码内容：分享网页地址。

    二维码仅编码分享网页 URL，不消耗访问次数、不判过期（过期与否由网页端
    读取时判定——扫码后打开的页面会展示错误页）；仅校验分享存在（不存在 → 404）。
    """
    share = ShareRepository.get_by_code(session, code)
    if share is None:
        raise ShareNotFoundError(f"短码 {code} 不存在")
    return share_url(code, base_url)
