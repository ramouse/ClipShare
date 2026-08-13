"""时间工具：全链路 naive UTC 约定。"""
from datetime import UTC, datetime


def utcnow() -> datetime:
    """返回当前 naive UTC 时间。

    全链路统一约定（CLAUDE.md）：应用层、数据库与 API 响应均使用 naive UTC
    （不带时区信息的 UTC），禁止混用 aware datetime——naive 与 aware 直接比较
    会抛 TypeError。

    实现方式：先取 aware UTC（datetime.now(UTC)），再剥离 tzinfo 得到 naive
    表示。序列化后形如 "2026-08-13T12:00:00"（无 Z 后缀、无偏移量）。
    """
    return datetime.now(UTC).replace(tzinfo=None)
