"""创建幂等记录表

Revision ID: 7c31e8b410f2
Revises: 2afe9b4d1b32
Create Date: 2026-08-23 04:10:00
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa

revision: str = "7c31e8b410f2"
down_revision: Union[str, Sequence[str], None] = "2afe9b4d1b32"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "idempotency_records",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("operation", sa.String(length=32), nullable=False),
        sa.Column("key_hash", sa.String(length=64), nullable=False),
        sa.Column("request_hash", sa.String(length=64), nullable=False),
        sa.Column("resource_kind", sa.String(length=16), nullable=False),
        sa.Column("resource_code", sa.String(length=8), nullable=False),
        sa.Column("created_at", sa.DateTime(), server_default=sa.text("now()"), nullable=False),
        sa.Column("expires_at", sa.DateTime(), nullable=False),
        sa.CheckConstraint(
            "operation IN ('create_share', 'upload_file')",
            name="ck_idempotency_operation",
        ),
        sa.CheckConstraint(
            "resource_kind IN ('share', 'file')",
            name="ck_idempotency_resource_kind",
        ),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("operation", "key_hash", name="uq_idempotency_operation_key"),
    )
    op.create_index(
        "ix_idempotency_records_expires_at",
        "idempotency_records",
        ["expires_at"],
        unique=False,
    )


def downgrade() -> None:
    op.drop_index("ix_idempotency_records_expires_at", table_name="idempotency_records")
    op.drop_table("idempotency_records")
