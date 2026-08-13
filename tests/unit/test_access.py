"""访问次数策略单元测试。"""
from app.domain.access import is_exhausted, remaining_views


def test_exhausted_at_boundary() -> None:
    """边界：view_count 恰好等于 max_views 时视为耗尽。"""
    assert is_exhausted(1, 1) is True
    assert is_exhausted(5, 5) is True


def test_exhausted_over_limit() -> None:
    """超过上限：视为耗尽。"""
    assert is_exhausted(5, 6) is True


def test_not_exhausted_below_limit() -> None:
    """未达上限：未耗尽。"""
    assert is_exhausted(5, 4) is False
    assert is_exhausted(5, 0) is False


def test_single_use_not_exhausted_before_first_view() -> None:
    """一次性分享（max_views=1）：首次访问前未耗尽，访问一次后耗尽。"""
    assert is_exhausted(1, 0) is False
    assert is_exhausted(1, 1) is True


def test_unlimited_never_exhausted() -> None:
    """不限次（max_views=None）：任何次数都不耗尽。"""
    assert is_exhausted(None, 0) is False
    assert is_exhausted(None, 1_000_000) is False


def test_remaining_views_normal() -> None:
    """正常区间内的剩余次数计算。"""
    assert remaining_views(5, 0) == 5
    assert remaining_views(5, 3) == 2
    assert remaining_views(5, 5) == 0


def test_remaining_views_never_negative() -> None:
    """超限时剩余次数钳制为 0，不出现负数。"""
    assert remaining_views(5, 10) == 0


def test_remaining_views_unlimited() -> None:
    """不限次时剩余次数为 None。"""
    assert remaining_views(None, 100) is None
