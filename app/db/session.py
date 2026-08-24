"""数据库引擎与会话工厂（DATABASE_URL 由 compose / 环境变量注入）。"""
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker

from app.core.config import get_settings

settings = get_settings()

# PostgreSQL 的 TIMESTAMP WITHOUT TIME ZONE 依赖会话时区解释；所有连接显式固定 UTC，
# 避免宿主机 / 数据库镜像时区改变后，API 仍把本地时间错误标记成 Z。
connect_args: dict[str, str] = {}
if settings.database_url.startswith(("postgresql://", "postgresql+")):
    connect_args["options"] = "-c timezone=UTC"

# 应用唯一引擎：pool_pre_ping 在取连接时探测有效性，剔除网络抖动产生的死连接
engine = create_engine(
    settings.database_url,
    pool_pre_ping=True,
    connect_args=connect_args,
    # SQLAlchemy 异常对象可能携带 SQL 参数；剪贴板正文与文件元数据属于敏感内容，
    # 即使数据库请求失败也不得被拼进异常文本或日志。
    hide_parameters=True,
)

# autoflush=False：由业务代码显式控制 flush 时机；
# expire_on_commit=False：提交后对象属性仍可直接读取（读多写少的查询场景）
SessionLocal = sessionmaker(bind=engine, autoflush=False, expire_on_commit=False)
