"""集成测试共享夹具：只连接通过 G0 证明的临时 PostgreSQL。"""
from collections.abc import Iterator

from test_harness.sandbox_guard import SandboxGuardError, require_test_sandbox

# 必须早于 SQLAlchemy 与任何 app.db 模块导入；验证过程不创建引擎、不连接数据库。
SANDBOX_PROOF = require_test_sandbox()

import pytest  # noqa: E402  护栏必须先于数据库相关导入执行
from sqlalchemy import text  # noqa: E402
from sqlalchemy.orm import Session  # noqa: E402

from app.core.security import limiter  # noqa: E402
from app.db.base import Base  # noqa: E402
from app.db.models import Share  # noqa: E402, F401  导入以将模型注册进 Base.metadata
from app.db.session import SessionLocal, engine  # noqa: E402


def require_connected_sandbox_database() -> None:
    """Recheck the server identity before any fixture executes DDL or DML."""
    with engine.connect() as connection:
        database_name, database_port = connection.execute(
            text("SELECT current_database(), inet_server_port()")
        ).one()
    if (
        database_name != SANDBOX_PROOF.database_name
        or database_port != SANDBOX_PROOF.database_port
    ):
        raise SandboxGuardError("connected PostgreSQL server does not match the sandbox proof")


@pytest.fixture(scope="session", autouse=True)
def _verified_sandbox_database() -> None:
    """Connect once and fail before any integration fixture can execute DDL or DML."""
    require_connected_sandbox_database()


@pytest.fixture(autouse=True)
def _isolated_rate_limiter() -> Iterator[None]:
    """每个用例独立使用内存限流状态，避免整套测试按执行顺序相互干扰。"""
    limiter.reset()
    yield
    limiter.reset()


@pytest.fixture()
def db_session(_verified_sandbox_database: None) -> Iterator[Session]:
    """每个用例独立的数据库会话。

    开始前幂等建表（兼容未跑迁移的环境）；用例结束后清空 shares 表，
    保证用例之间数据互不干扰。
    """
    Base.metadata.create_all(engine)
    with SessionLocal() as session:
        yield session
    with engine.begin() as conn:
        # 清空顺序：先业务表再短码中心表（中心表行引用业务表语义）
        conn.execute(text("DELETE FROM idempotency_records"))
        conn.execute(text("DELETE FROM shares"))
        conn.execute(text("DELETE FROM share_files"))
        conn.execute(text("DELETE FROM shortcodes"))
