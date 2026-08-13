"""分享服务层单元测试：短码冲突重试、到期时间计算与读取判定（全程 mock，无需数据库）。"""
from datetime import datetime, timedelta
from unittest.mock import Mock, patch

import pytest
from sqlalchemy.exc import IntegrityError

from app.core.errors import (
    ShareExpiredError,
    ShareNotFoundError,
    ShortcodeGenerationError,
    ViewsExhaustedError,
)
from app.db.models import Share
from app.domain.expiry import Expiry
from app.domain.shortcode import ALPHABET
from app.services import share_service

# 固定参考时间，保证断言与运行时刻无关
NOW = datetime(2026, 8, 13, 12, 0, 0)


def _make_share(
    *,
    code: str = "ab12cd",
    expires_at: datetime | None = None,
    max_views: int | None = None,
    view_count: int = 0,
) -> Share:
    return Share(
        code=code, content="内容", expires_at=expires_at, max_views=max_views, view_count=view_count
    )


def _integrity_error() -> IntegrityError:
    return IntegrityError("INSERT INTO shares", {}, ValueError("duplicate key value"))


def test_create_share_success() -> None:
    """正常创建：短码生成一次、入库参数正确、提交事务。"""
    session = Mock()
    share = _make_share(code="ab12cd")
    with patch.object(share_service.ShareRepository, "create", return_value=share) as repo_create:
        result = share_service.create_share(
            session, content="你好", expiry=Expiry.ONE_DAY, max_views=5, now=NOW
        )
    assert result is share
    repo_create.assert_called_once()
    call_kwargs = repo_create.call_args.kwargs
    # 短码由服务层随机生成（6 位 Base62），无法预知具体值，断言形状与字符集
    generated_code = call_kwargs["code"]
    assert len(generated_code) == 6
    assert all(ch in ALPHABET for ch in generated_code)
    assert call_kwargs["content"] == "你好"
    assert call_kwargs["expires_at"] == NOW + timedelta(days=1)
    assert call_kwargs["max_views"] == 5
    session.commit.assert_called_once()


def test_create_share_retries_on_integrity_error() -> None:
    """短码冲突：第一次唯一约束冲突，回滚后重试第二次成功。"""
    session = Mock()
    share = _make_share(code="retry00")
    repo_create = Mock(side_effect=[_integrity_error(), share])
    with patch.object(share_service.ShareRepository, "create", repo_create):
        result = share_service.create_share(
            session, content="x", expiry=Expiry.FOREVER, max_views=None, now=NOW
        )
    assert result is share
    assert repo_create.call_count == 2
    assert session.rollback.call_count == 1
    session.commit.assert_called_once()


def test_create_share_gives_up_after_max_retries() -> None:
    """连续冲突超过上限：抛 ShortcodeGenerationError（映射 500）。"""
    session = Mock()
    side_effect = [_integrity_error()] * share_service.SHORTCODE_MAX_RETRIES
    with (
        patch.object(
            share_service.ShareRepository, "create", side_effect=side_effect
        ) as repo_create,
        pytest.raises(ShortcodeGenerationError),
    ):
        share_service.create_share(
            session, content="x", expiry=Expiry.ONE_HOUR, max_views=None, now=NOW
        )
    assert repo_create.call_count == share_service.SHORTCODE_MAX_RETRIES
    assert session.rollback.call_count == share_service.SHORTCODE_MAX_RETRIES
    session.commit.assert_not_called()


def test_create_share_expires_at_uses_injected_now() -> None:
    """expires_at 由注入的 now 计算：四档档位各自正确。"""
    session = Mock()
    cases = [
        (Expiry.ONE_HOUR, NOW + timedelta(hours=1)),
        (Expiry.ONE_DAY, NOW + timedelta(days=1)),
        (Expiry.SEVEN_DAYS, NOW + timedelta(days=7)),
        (Expiry.FOREVER, None),
    ]
    for expiry, expected in cases:
        with patch.object(
            share_service.ShareRepository, "create", return_value=_make_share()
        ) as repo_create:
            share_service.create_share(session, content="x", expiry=expiry, max_views=None, now=NOW)
        assert repo_create.call_args.kwargs["expires_at"] == expected


def test_share_url_strips_trailing_slash() -> None:
    """分享网页地址：base_url 尾部斜杠不重复。"""
    assert share_service.share_url("ab12cd", "https://example.com/") == "https://example.com/s/ab12cd"
    assert share_service.share_url("ab12cd", "https://example.com") == "https://example.com/s/ab12cd"


def test_view_share_not_found() -> None:
    """短码不存在 → ShareNotFoundError。"""
    session = Mock()
    with (
        patch.object(
            share_service.ShareRepository, "get_by_code", return_value=None
        ) as get_by_code,
        pytest.raises(ShareNotFoundError),
    ):
        share_service.view_share(session, code="zzzz99", now=NOW)
    get_by_code.assert_called_once_with(session, "zzzz99")


def test_view_share_expired() -> None:
    """已过期 → ShareExpiredError，且不触发守卫自增。"""
    session = Mock()
    share = _make_share(expires_at=NOW - timedelta(seconds=1))
    with (
        patch.object(share_service.ShareRepository, "get_by_code", return_value=share),
        patch.object(share_service.ShareRepository, "increment_view_count_guarded") as guarded,
        pytest.raises(ShareExpiredError),
    ):
        share_service.view_share(session, code="ab12cd", now=NOW)
    guarded.assert_not_called()
    session.commit.assert_not_called()


def test_view_share_views_exhausted() -> None:
    """守卫自增返回 None（已达上限）→ ViewsExhaustedError，且回滚。"""
    session = Mock()
    share = _make_share(expires_at=None, max_views=1, view_count=1)
    with (
        patch.object(share_service.ShareRepository, "get_by_code", return_value=share),
        patch.object(
            share_service.ShareRepository, "increment_view_count_guarded", return_value=None
        ),
        pytest.raises(ViewsExhaustedError),
    ):
        share_service.view_share(session, code="ab12cd", now=NOW)
    session.rollback.assert_called_once()
    session.commit.assert_not_called()


def test_view_share_success() -> None:
    """正常读取：守卫自增成功、提交事务、返回最新记录。"""
    session = Mock()
    share = _make_share(expires_at=NOW + timedelta(hours=1), max_views=5, view_count=1)
    with (
        patch.object(share_service.ShareRepository, "get_by_code", return_value=share),
        patch.object(
            share_service.ShareRepository, "increment_view_count_guarded", return_value=share
        ) as guarded,
    ):
        result = share_service.view_share(session, code="ab12cd", now=NOW)
    assert result is share
    guarded.assert_called_once_with(session, share.id)
    session.commit.assert_called_once()
