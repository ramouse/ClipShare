"""仓储层：封装 shares / shortcodes / share_files 表的全部读写路径。

供服务层调用（同步方法，供同步 def 路由 / 服务层使用）；
路由层禁止直接操作数据库，一律经由服务层编排。
"""
from datetime import datetime

from sqlalchemy import or_, select, update
from sqlalchemy.orm import Session

from app.db.models import Share, ShareFile, Shortcode


class ShareRepository:
    """分享记录的数据访问入口。"""

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


class ShortcodeRepository:
    """短码中心登记表的数据访问入口。

    code 为主键，唯一约束由数据库强制执行；与业务表同事务双写，
    冲突时整个事务回滚（调用方重试）。
    """

    @staticmethod
    def create(session: Session, *, code: str, kind: str) -> Shortcode:
        """登记短码占用（kind 区分资源类型）并 flush。

        唯一约束冲突（短码已被任何资源占用）时抛 IntegrityError，
        由服务层捕获后回滚重试。
        """
        shortcode = Shortcode(code=code, kind=kind)
        session.add(shortcode)
        session.flush()
        return shortcode


class ShareFileRepository:
    """文件分享记录的数据访问入口。"""

    @staticmethod
    def create(
        session: Session,
        *,
        code: str,
        original_name: str,
        stored_name: str,
        size_bytes: int,
        content_type: str,
        encrypted: bool,
        expires_at: datetime | None,
        max_views: int | None,
    ) -> ShareFile:
        """新建文件分享记录并 flush，返回已带主键的 ShareFile 对象。"""
        file_record = ShareFile(
            code=code,
            original_name=original_name,
            stored_name=stored_name,
            size_bytes=size_bytes,
            content_type=content_type,
            encrypted=encrypted,
            expires_at=expires_at,
            max_views=max_views,
        )
        session.add(file_record)
        session.flush()
        return file_record

    @staticmethod
    def get_by_code(session: Session, code: str) -> ShareFile | None:
        """按对外短码查询文件分享记录，未命中返回 None。"""
        return session.scalars(select(ShareFile).where(ShareFile.code == code)).first()

    @staticmethod
    def increment_preview_count_guarded(session: Session, file_id: int) -> ShareFile | None:
        """守卫式原子自增预览次数：仅当未达访问上限时 +1，一次往返返回最新记录。

        预览与下载共享访问次数池（used = preview_count + download_count），
        上限判定与自增在同一 UPDATE 内原子完成，杜绝并发竞态窗口。
        已达上限或记录不存在时返回 None，由调用方据此区分「已耗尽/不存在」。
        """
        result = session.execute(
            update(ShareFile)
            .where(
                ShareFile.id == file_id,
                or_(
                    ShareFile.max_views.is_(None),
                    ShareFile.preview_count + ShareFile.download_count < ShareFile.max_views,
                ),
            )
            .values(preview_count=ShareFile.preview_count + 1)
            .returning(ShareFile)
        )
        return result.scalar_one_or_none()

    @staticmethod
    def increment_download_count_guarded(session: Session, file_id: int) -> ShareFile | None:
        """守卫式原子自增下载次数：仅当未达访问上限时 +1（与预览共享次数池）。

        语义与 increment_preview_count_guarded 相同，仅自增列不同——
        预览与下载任意一方占用次数，都计入同一个 max_views 上限。
        """
        result = session.execute(
            update(ShareFile)
            .where(
                ShareFile.id == file_id,
                or_(
                    ShareFile.max_views.is_(None),
                    ShareFile.preview_count + ShareFile.download_count < ShareFile.max_views,
                ),
            )
            .values(download_count=ShareFile.download_count + 1)
            .returning(ShareFile)
        )
        return result.scalar_one_or_none()
