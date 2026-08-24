"""时间工具：数据库内部 naive UTC，API 边界 RFC 3339 UTC。"""
from datetime import UTC, datetime
from typing import Annotated

from pydantic import PlainSerializer, WithJsonSchema

RFC3339_UTC_MICROSECOND_PATTERN = (
    r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$"
)


def utcnow() -> datetime:
    """返回当前 naive UTC 时间。

    应用层与数据库沿用 naive UTC，禁止在领域计算中混用 aware datetime——
    naive 与 aware 直接比较会抛 TypeError；API 边界由 ``to_rfc3339_utc``
    显式序列化为带 ``Z`` 的 UTC。

    实现方式：先取 aware UTC（datetime.now(UTC)），再剥离 tzinfo 得到 naive
    表示。
    """
    return datetime.now(UTC).replace(tzinfo=None)


def to_rfc3339_utc(value: datetime) -> str:
    """把内部 datetime 规范化为带 ``Z`` 的 RFC 3339 UTC 字符串。

    数据库存量和领域层继续使用 naive UTC，避免破坏既有比较与迁移；API
    边界必须显式携带 UTC 标识，防止 C#/Kotlin 把无偏移时间解释成本地时区。
    aware 输入会先换算到 UTC，naive 输入按项目既有约定解释为 UTC。
    """
    normalized = (
        value.replace(tzinfo=UTC) if value.tzinfo is None else value.astimezone(UTC)
    )
    return normalized.isoformat(timespec="microseconds").replace("+00:00", "Z")


Rfc3339UtcDatetime = Annotated[
    datetime,
    PlainSerializer(to_rfc3339_utc, return_type=str, when_used="json"),
    WithJsonSchema(
        {
            "type": "string",
            "format": "date-time",
            "pattern": RFC3339_UTC_MICROSECOND_PATTERN,
        },
        mode="serialization",
    ),
]
"""序列化为固定微秒精度 UTC ``Z`` 字符串的 API 时间类型。"""
