"""数据库引擎与会话工厂（DATABASE_URL 由 compose / 环境变量注入）。"""
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker

from app.core.config import get_settings

# 应用唯一引擎：pool_pre_ping 在取连接时探测有效性，剔除网络抖动产生的死连接
engine = create_engine(get_settings().database_url, pool_pre_ping=True)

# autoflush=False：由业务代码显式控制 flush 时机；
# expire_on_commit=False：提交后对象属性仍可直接读取（读多写少的查询场景）
SessionLocal = sessionmaker(bind=engine, autoflush=False, expire_on_commit=False)
