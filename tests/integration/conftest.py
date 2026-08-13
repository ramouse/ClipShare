"""集成测试共享夹具：连接容器内 PostgreSQL（DATABASE_URL 由 compose 注入）。"""
from collections.abc import Iterator

import pytest
from sqlalchemy import text
from sqlalchemy.orm import Session

from app.db.base import Base
from app.db.models import Share  # noqa: F401  导入以将模型注册进 Base.metadata
from app.db.session import SessionLocal, engine


@pytest.fixture()
def db_session() -> Iterator[Session]:
    """每个用例独立的数据库会话。

    开始前幂等建表（兼容未跑迁移的环境）；用例结束后清空 shares 表，
    保证用例之间数据互不干扰。
    """
    Base.metadata.create_all(engine)
    with SessionLocal() as session:
        yield session
    with engine.begin() as conn:
        # 清空顺序：先业务表再短码中心表（中心表行引用业务表语义）
        conn.execute(text("DELETE FROM shares"))
        conn.execute(text("DELETE FROM share_files"))
        conn.execute(text("DELETE FROM shortcodes"))
