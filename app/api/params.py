"""API 共享路径参数契约。"""
from typing import Annotated

from fastapi import Header, Path

IDEMPOTENCY_KEY_PATTERN = r"^[A-Za-z0-9._~:/+\-]{8,128}$"

ShortcodePath = Annotated[
    str,
    Path(
        min_length=1,
        max_length=8,
        pattern=r"^[A-Za-z0-9]+$",
        description="1–8 位 Base62 分享短码",
    ),
]
"""数据库列边界内的 Base62 短码；非法输入由统一 422 处理器拒绝。"""

IdempotencyKeyHeader = Annotated[
    str | None,
    Header(
        alias="Idempotency-Key",
        description=(
            "可选的创建请求幂等键；8–128 位 ASCII，只允许字母、数字和 ._~:/+-。"
            "服务端仅保存 SHA-256 摘要；格式错误返回 400 idempotency_key_invalid。"
        ),
        json_schema_extra={
            "minLength": 8,
            "maxLength": 128,
            "pattern": IDEMPOTENCY_KEY_PATTERN,
        },
    ),
]
"""只补充 OpenAPI 机器约束，不在 FastAPI 层提前校验，以保留冻结的 400 错误码。"""
