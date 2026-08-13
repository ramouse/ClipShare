"""依赖注入：请求级短生命周期数据库会话。"""
from collections.abc import Iterator

from sqlalchemy.orm import Session

from app.db.session import SessionLocal


def get_db() -> Iterator[Session]:
    """请求级数据库会话：请求结束必定 close。

    expire_on_commit=False 下会话身份映射会缓存旧值（M2 审查教训），
    因此会话必须短生命周期——每个请求新建、结束即关闭，绝不跨请求复用，
    否则读到的是身份映射缓存而非数据库真实状态。
    """
    session = SessionLocal()
    try:
        yield session
    finally:
        session.close()
