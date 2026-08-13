"""shares.view_count 添加 server_default

Revision ID: b1d0aa031613
Revises: 298fbf6207f4
Create Date: 2026-08-13 07:27:29.841595

"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision: str = 'b1d0aa031613'
down_revision: Union[str, Sequence[str], None] = '298fbf6207f4'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    """为 view_count 补上数据库侧默认值。

    上一版 298fbf6207f4 因 alembic 默认不比较 server_default 而生成为空迁移，
    本迁移手写 alter_column 真正落实模型的 server_default=text("0")。
    """
    op.alter_column("shares", "view_count", server_default=sa.text("0"))


def downgrade() -> None:
    """回退：移除数据库侧默认值。"""
    op.alter_column("shares", "view_count", server_default=None)
