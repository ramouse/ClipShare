"""Alembic 迁移环境：数据库地址与目标元数据均取自应用配置。"""
from logging.config import fileConfig

from alembic import context
from sqlalchemy import engine_from_config, pool

# 导入应用模型：将其注册进 Base.metadata，供 autogenerate 与线上迁移使用
import app.db.models  # noqa: F401
from app.core.config import get_settings
from app.db.base import Base

# 这是 Alembic 的 Config 对象，提供对 .ini 文件内配置的访问
config = context.config

# 数据库地址一律以应用配置为准（容器内由 compose 注入 DATABASE_URL 环境变量），
# 避免 alembic.ini 中的占位 URL 与真实环境漂移
config.set_main_option("sqlalchemy.url", get_settings().database_url)

# 按 .ini 文件配置 Python 日志
if config.config_file_name is not None:
    fileConfig(config.config_file_name)

# autogenerate 以应用全部 ORM 模型的元数据为比较基准
target_metadata = Base.metadata


def run_migrations_offline() -> None:
    """离线模式：仅用 URL 生成 SQL，不建立连接（用于 SQL 预览）。"""
    url = config.get_main_option("sqlalchemy.url")
    context.configure(
        url=url,
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
        compare_server_default=True,  # server_default 漂移也要参与对比
    )

    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    """在线模式：连接数据库执行迁移；NullPool 保证迁移连接即用即断。"""
    connectable = engine_from_config(
        config.get_section(config.config_ini_section, {}),
        prefix="sqlalchemy.",
        poolclass=pool.NullPool,
    )

    with connectable.connect() as connection:
        context.configure(
            connection=connection,
            target_metadata=target_metadata,
            compare_server_default=True,  # server_default 漂移也要参与对比
        )

        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
