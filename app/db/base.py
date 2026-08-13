"""SQLAlchemy 声明式基类：所有 ORM 模型的公共父类。"""
from sqlalchemy.orm import DeclarativeBase


class Base(DeclarativeBase):
    """应用内全部 ORM 模型的基类，Alembic autogenerate 以 Base.metadata 为准。"""
