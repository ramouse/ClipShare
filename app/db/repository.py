"""Share 仓储：封装 shares 表的全部读写路径，供服务层调用。"""
from datetime import datetime

from sqlalchemy import or_, select, update
from sqlalchemy.orm import Session

from app.db.models import Share


class ShareRepository:
    """分享记录的数据访问入口（同步方法，供同步 def 路由 / 服务层使用）。"""

    @staticmethod
    def create(
        session: Session,
        *,
        code: str,
        content: str,
        expires_at: datetime | None,
        max_views: int | None,
    ) -> Share:
        """新建分享记录并 flush，返回已带主键的 Share 对象。"""
        share = Share(code=code, content=content, expires_at=expires_at, max_views=max_views)
        session.add(share)
        session.flush()
        return share

    @staticmethod
    def get_by_code(session: Session, code: str) -> Share | None:
        """按对外短码查询分享记录，未命中返回 None。"""
        return session.scalars(select(Share).where(Share.code == code)).first()

    @staticmethod
    def increment_view_count(session: Session, share_id: int) -> Share:
        """原子自增访问次数并返回最新记录；记录不存在时抛 LookupError。

        使用 SQL 层原子自增（view_count + 1）而非「读-改-写」，
        并发访问下不会丢更新；returning 让一次往返即拿到最新行。
        """
        result = session.execute(
            update(Share)
            .where(Share.id == share_id)
            .values(view_count=Share.view_count + 1)
            .returning(Share)
        )
        share = result.scalar_one_or_none()
        if share is None:
            raise LookupError(f"share id={share_id} 不存在")
        return share

    @staticmethod
    def increment_view_count_guarded(session: Session, share_id: int) -> Share | None:
        """守卫式原子自增：仅当未达访问上限时 +1，一次往返返回最新记录。

        把「是否耗尽」判定下推到 SQL 谓词（view_count < max_views），与自增
        在同一 UPDATE 内原子完成，杜绝「先判定、后自增」的并发竞态窗口——
        这是项目书「达到限制后禁止再访问」红线在并发下的正确实现方式。
        已达上限或记录不存在时返回 None，由调用方据此区分「已耗尽/不存在」。
        """
        result = session.execute(
            update(Share)
            .where(
                Share.id == share_id,
                or_(Share.max_views.is_(None), Share.view_count < Share.max_views),
            )
            .values(view_count=Share.view_count + 1)
            .returning(Share)
        )
        return result.scalar_one_or_none()
