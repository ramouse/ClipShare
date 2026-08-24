"""客户端时间契约：所有 API 时间必须固定为微秒精度的 RFC 3339 UTC。"""
from datetime import UTC, datetime, timedelta, timezone

from app.core.time import to_rfc3339_utc


def test_naive_utc_is_serialized_with_fixed_microseconds_and_z() -> None:
    assert to_rfc3339_utc(datetime(2026, 8, 23, 12, 34, 56)) == (
        "2026-08-23T12:34:56.000000Z"
    )


def test_fractional_seconds_are_not_truncated() -> None:
    assert to_rfc3339_utc(datetime(2026, 8, 23, 12, 34, 56, 1234)) == (
        "2026-08-23T12:34:56.001234Z"
    )


def test_aware_datetime_is_converted_to_utc() -> None:
    east_eight = timezone(timedelta(hours=8))
    source = datetime(2026, 8, 23, 20, 34, 56, 123456, tzinfo=east_eight)
    assert to_rfc3339_utc(source) == "2026-08-23T12:34:56.123456Z"
    assert source.astimezone(UTC).hour == 12
