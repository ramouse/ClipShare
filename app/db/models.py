"""ORM 模型：shares / shortcodes / share_files 表。"""
from datetime import datetime

from sqlalchemy import BigInteger, CheckConstraint, String, Text, func, text
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


class Shortcode(Base):
    """短码中心登记表：跨资源类型（share/file）全局唯一。

    code 为主键，天然唯一——任何一类资源占用过的短码不可能再被另一类
    资源使用，杜绝「文本短码与文件短码撞车」的跨表歧义。
    创建资源时与业务表同事务双写（kind 区分资源类型），读取按端点查各自业务表。
    """

    __tablename__ = "shortcodes"

    # 对外短码：主键即唯一约束（跨 share/file 全局唯一）
    code: Mapped[str] = mapped_column(String(8), primary_key=True)
    # 资源类型：share（文本分享）/ file（文件分享），DB 层约束防脏数据
    kind: Mapped[str] = mapped_column(String(16), nullable=False, index=True)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now(), nullable=False)

    __table_args__ = (
        CheckConstraint("kind IN ('share', 'file')", name="ck_shortcodes_kind"),
    )


class ShareFile(Base):
    """文件分享记录：短码 + 磁盘名 + 元数据 + 有效期 / 访问次数控制。

    磁盘文件以 stored_name（secrets 随机十六进制，无扩展名）命名，
    不暴露用户文件名与真实扩展名；original_name 仅存元数据供展示。
    预览与下载共享访问次数池（used = preview_count + download_count），
    元数据读取不消耗次数；id 仅为内部主键，对外标识一律使用短码 code。
    """

    __tablename__ = "share_files"

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    # 对外短码：唯一索引（全局唯一由 shortcodes 中心表兜底）
    code: Mapped[str] = mapped_column(String(8), unique=True, index=True, nullable=False)
    # 原始文件名（净化后）：仅元数据，绝不作磁盘路径
    original_name: Mapped[str] = mapped_column(String(255), nullable=False)
    # 磁盘文件名：secrets.token_hex(16)，32 字符，无扩展名；唯一约束防共用磁盘名
    stored_name: Mapped[str] = mapped_column(String(64), unique=True, nullable=False)
    # 文件字节数（BigInteger：100MB 上限远超 32 位 int 范围安全，统一宽列）
    size_bytes: Mapped[int] = mapped_column(BigInteger, nullable=False)
    # 上传时嗅探的 MIME 类型（存储层记录，下载时透传）
    content_type: Mapped[str] = mapped_column(String(128), nullable=False)
    # 是否 E2E 加密（ENC1 标记；前端解密链路）
    encrypted: Mapped[bool] = mapped_column(
        default=False, server_default=text("false"), nullable=False
    )
    # 预览次数 / 下载次数：共享 max_views 次数池
    preview_count: Mapped[int] = mapped_column(default=0, server_default=text("0"), nullable=False)
    download_count: Mapped[int] = mapped_column(default=0, server_default=text("0"), nullable=False)
    # None 表示永久有效
    expires_at: Mapped[datetime | None] = mapped_column(nullable=True)
    # None 表示不限访问次数（预览 + 下载共享）
    max_views: Mapped[int | None] = mapped_column(nullable=True)
    created_at: Mapped[datetime] = mapped_column(server_default=func.now(), nullable=False)
