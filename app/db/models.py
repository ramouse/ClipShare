"""ORM 模型：shares 表。"""
from datetime import datetime

from sqlalchemy import String, Text, func, text
from sqlalchemy.orm import Mapped, mapped_column

from app.db.base import Base


class Share(Base):
    """分享记录：Base62 短码 + 内容 + 有效期 / 访问次数控制。

    id 仅为内部主键，绝不对外暴露；对外标识一律使用唯一短码 code。
    """

    __tablename__ = "shares"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    # 对外短码：唯一索引；当前生成器默认长度 6，列宽预留到 8
    code: Mapped[str] = mapped_column(String(8), unique=True, index=True, nullable=False)
    content: Mapped[str] = mapped_column(Text, nullable=False)
    # None 表示永久有效
    expires_at: Mapped[datetime | None] = mapped_column(nullable=True)
    # None 表示不限访问次数
    max_views: Mapped[int | None] = mapped_column(nullable=True)
    view_count: Mapped[int] = mapped_column(default=0, server_default=text("0"), nullable=False)
    # 由数据库生成，避免依赖应用时钟偏差
    created_at: Mapped[datetime] = mapped_column(server_default=func.now(), nullable=False)
