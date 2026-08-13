"""有效期策略单元测试。"""
from datetime import datetime, timedelta

from app.domain.expiry import Expiry, expires_at, is_expired

# 固定的参考时间，保证断言与运行时刻无关
NOW = datetime(2026, 8, 13, 12, 0, 0)


def test_one_hour() -> None:
    """1 小时档：到期时间 = now + 1h。"""
    assert expires_at(Expiry.ONE_HOUR, NOW) == NOW + timedelta(hours=1)


def test_enum_value_literals() -> None:
    """枚举字面量锁定：对外 API 使用 1h/24h/7d/forever，变更即破坏契约。"""
    assert Expiry.ONE_HOUR.value == "1h"
    assert Expiry.ONE_DAY.value == "24h"
    assert Expiry.SEVEN_DAYS.value == "7d"
    assert Expiry.FOREVER.value == "forever"
    assert Expiry("1h") is Expiry.ONE_HOUR


def test_one_day() -> None:
    """24 小时档：到期时间 = now + 1 天。"""
    assert expires_at(Expiry.ONE_DAY, NOW) == NOW + timedelta(days=1)


def test_seven_days() -> None:
    """7 天档：到期时间 = now + 7 天。"""
    assert expires_at(Expiry.SEVEN_DAYS, NOW) == NOW + timedelta(days=7)


def test_forever_returns_none() -> None:
    """永久档：到期时间为 None。"""
    assert expires_at(Expiry.FOREVER, NOW) is None


def test_expired_at_boundary() -> None:
    """边界：now 恰好等于到期时间时视为已过期。"""
    assert is_expired(NOW, NOW) is True


def test_not_expired_in_future() -> None:
    """未来到期时间：未过期。"""
    assert is_expired(NOW + timedelta(hours=1), NOW) is False


def test_expired_when_now_past_deadline() -> None:
    """now 超过到期时间：已过期。"""
    assert is_expired(NOW, NOW + timedelta(seconds=1)) is True


def test_none_never_expired() -> None:
    """无到期时间（永久）：永不判定为过期。"""
    assert is_expired(None, NOW) is False
