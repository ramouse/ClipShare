"""文件服务层单元测试：短码冲突重试、过期懒删、404/410 分支（全程 mock）。"""
from datetime import datetime, timedelta
from typing import TypedDict
from unittest.mock import Mock, patch

import pytest
from sqlalchemy.exc import IntegrityError

from app.core.errors import (
    ShareFileExpiredError,
    ShareFileNotFoundError,
    ShareFileViewsExhaustedError,
    ShortcodeGenerationError,
)
from app.db.models import ShareFile
from app.domain.expiry import Expiry
from app.domain.shortcode import ALPHABET
from app.services import file_service

# 固定参考时间，保证断言与运行时刻无关
NOW = datetime(2026, 8, 13, 12, 0, 0)


def _make_file(
    *,
    code: str = "ab12cd",
    expires_at: datetime | None = None,
    max_views: int | None = None,
    preview_count: int = 0,
    download_count: int = 0,
) -> ShareFile:
    return ShareFile(
        code=code,
        original_name="a.txt",
        stored_name="s" * 32,
        size_bytes=100,
        content_type="text/plain",
        encrypted=False,
        preview_count=preview_count,
        download_count=download_count,
        expires_at=expires_at,
        max_views=max_views,
    )


def _integrity_error() -> IntegrityError:
    return IntegrityError("INSERT INTO share_files", {}, ValueError("duplicate key value"))


class _CreateArgs(TypedDict):
    """create_file_share 关键字参数形状：保证测试调用与签名不漂移。"""

    original_name: str
    stored_name: str
    size_bytes: int
    content_type: str
    encrypted: bool
    expiry: Expiry
    max_views: int | None
    now: datetime


def _create_args() -> _CreateArgs:
    return _CreateArgs(
        original_name="a.txt",
        stored_name="s" * 32,
        size_bytes=100,
        content_type="text/plain",
        encrypted=False,
        expiry=Expiry.ONE_DAY,
        max_views=3,
        now=NOW,
    )


# ---- 创建：短码双写 + 冲突重试 ----


def test_create_file_share_success_writes_both_tables() -> None:
    """正常创建：短码中心表先登记（kind=file），业务表随后写入，提交一次。"""
    session = Mock()
    record = _make_file(code="ab12cd")
    with (
        patch.object(file_service, "generate_shortcode", return_value="ab12cd"),
        patch.object(file_service.ShortcodeRepository, "create") as shortcode_create,
        patch.object(
            file_service.ShareFileRepository, "create", return_value=record
        ) as file_create,
    ):
        result = file_service.create_file_share(session, **_create_args())
    assert result is record
    shortcode_create.assert_called_once_with(session, code="ab12cd", kind="file")
    file_create_kwargs = file_create.call_args.kwargs
    assert file_create_kwargs["code"] == "ab12cd"
    assert file_create_kwargs["expires_at"] == NOW + timedelta(days=1)
    assert file_create_kwargs["max_views"] == 3
    session.commit.assert_called_once()


def test_create_file_share_retries_on_shortcode_conflict() -> None:
    """短码冲突：第一次业务表唯一约束冲突，回滚后换码重试第二次成功。"""
    session = Mock()
    record = _make_file(code="new123")
    with (
        patch.object(file_service, "generate_shortcode", side_effect=["dup001", "new123"]),
        patch.object(file_service.ShortcodeRepository, "create") as shortcode_create,
        patch.object(
            file_service.ShareFileRepository, "create", side_effect=[_integrity_error(), record]
        ) as file_create,
    ):
        result = file_service.create_file_share(session, **_create_args())
    assert result is record
    assert file_create.call_count == 2
    # 两次尝试的短码各不相同，且都先登记进短码中心表
    assert shortcode_create.call_args_list[0].kwargs["code"] == "dup001"
    assert shortcode_create.call_args_list[1].kwargs["code"] == "new123"
    assert shortcode_create.call_args_list[0].kwargs["kind"] == "file"
    assert session.rollback.call_count == 1
    session.commit.assert_called_once()


def test_create_file_share_gives_up_after_max_retries() -> None:
    """连续冲突超过上限：抛 ShortcodeGenerationError（映射 500）。"""
    session = Mock()
    side_effect = [_integrity_error()] * file_service.SHORTCODE_MAX_RETRIES
    with (
        patch.object(file_service.ShortcodeRepository, "create"),
        patch.object(file_service.ShareFileRepository, "create", side_effect=side_effect),
        pytest.raises(ShortcodeGenerationError),
    ):
        file_service.create_file_share(session, **_create_args())
    assert session.rollback.call_count == file_service.SHORTCODE_MAX_RETRIES
    session.commit.assert_not_called()


def test_create_file_share_code_is_base62() -> None:
    """短码由服务层随机生成（6 位 Base62）：形状与字符集符合契约。"""
    session = Mock()
    with (
        patch.object(file_service.ShortcodeRepository, "create"),
        patch.object(
            file_service.ShareFileRepository, "create", return_value=_make_file()
        ) as file_create,
    ):
        file_service.create_file_share(session, **_create_args())
    code = file_create.call_args.kwargs["code"]
    assert len(code) == 6
    assert all(ch in ALPHABET for ch in code)


# ---- 元数据读取：不消耗次数，过期懒删 ----


def test_get_file_meta_not_found() -> None:
    """记录不存在 → ShareFileNotFoundError，不触发删除。"""
    session = Mock()
    storage = Mock()
    with (
        patch.object(file_service.ShareFileRepository, "get_by_code", return_value=None),
        pytest.raises(ShareFileNotFoundError),
    ):
        file_service.get_file_meta(session, code="zzzz99", now=NOW, storage=storage)
    storage.delete.assert_not_called()
    session.commit.assert_not_called()


def test_get_file_meta_expired_lazily_deletes() -> None:
    """已过期 → ShareFileExpiredError，且顺带懒删磁盘文件（mock storage.delete 断言）。"""
    session = Mock()
    storage = Mock()
    record = _make_file(expires_at=NOW - timedelta(seconds=1))
    with (
        patch.object(file_service.ShareFileRepository, "get_by_code", return_value=record),
        pytest.raises(ShareFileExpiredError),
    ):
        file_service.get_file_meta(session, code="ab12cd", now=NOW, storage=storage)
    storage.delete.assert_called_once_with(record.stored_name)
    session.commit.assert_not_called()


def test_get_file_meta_success_no_count_consumed() -> None:
    """正常元数据读取：返回记录、不消耗次数、不写库。"""
    session = Mock()
    storage = Mock()
    record = _make_file(expires_at=None)
    with (
        patch.object(file_service.ShareFileRepository, "get_by_code", return_value=record),
        patch.object(
            file_service.ShareFileRepository, "increment_preview_count_guarded"
        ) as preview_guarded,
        patch.object(
            file_service.ShareFileRepository, "increment_download_count_guarded"
        ) as download_guarded,
    ):
        result = file_service.get_file_meta(session, code="ab12cd", now=NOW, storage=storage)
    assert result is record
    preview_guarded.assert_not_called()
    download_guarded.assert_not_called()
    storage.delete.assert_not_called()
    session.commit.assert_not_called()


# ---- 内容消费：404 / 过期懒删 / 守卫自增 / 次数耗尽 ----


def test_consume_file_not_found() -> None:
    """记录不存在 → ShareFileNotFoundError，不触发删除。"""
    session = Mock()
    storage = Mock()
    with (
        patch.object(file_service.ShareFileRepository, "get_by_code", return_value=None),
        pytest.raises(ShareFileNotFoundError),
    ):
        file_service.consume_file(session, code="zzzz99", now=NOW, storage=storage, mode="preview")
    storage.delete.assert_not_called()


def test_consume_file_expired_lazily_deletes() -> None:
    """已过期 → 懒删磁盘文件 + ShareFileExpiredError。"""
    session = Mock()
    storage = Mock()
    record = _make_file(expires_at=NOW - timedelta(seconds=1))
    with (
        patch.object(file_service.ShareFileRepository, "get_by_code", return_value=record),
        pytest.raises(ShareFileExpiredError),
    ):
        file_service.consume_file(session, code="ab12cd", now=NOW, storage=storage, mode="download")
    storage.delete.assert_called_once_with(record.stored_name)
    session.commit.assert_not_called()


def test_consume_file_views_exhausted() -> None:
    """守卫自增返回 None（已达上限）→ ShareFileViewsExhaustedError，且回滚。"""
    session = Mock()
    storage = Mock()
    record = _make_file(expires_at=None)
    with (
        patch.object(file_service.ShareFileRepository, "get_by_code", return_value=record),
        patch.object(
            file_service.ShareFileRepository, "increment_preview_count_guarded", return_value=None
        ),
        pytest.raises(ShareFileViewsExhaustedError),
    ):
        file_service.consume_file(session, code="ab12cd", now=NOW, storage=storage, mode="preview")
    session.rollback.assert_called_once()
    session.commit.assert_not_called()


def test_consume_file_preview_success() -> None:
    """预览成功：预览计数守卫自增、提交事务、返回最新记录。"""
    session = Mock()
    storage = Mock()
    record = _make_file(expires_at=NOW + timedelta(hours=1))
    with (
        patch.object(file_service.ShareFileRepository, "get_by_code", return_value=record),
        patch.object(
            file_service.ShareFileRepository,
            "increment_preview_count_guarded",
            return_value=record,
        ) as guarded,
    ):
        result = file_service.consume_file(
            session, code="ab12cd", now=NOW, storage=storage, mode="preview"
        )
    assert result is record
    guarded.assert_called_once_with(session, record.id)
    session.commit.assert_called_once()


def test_consume_file_download_success() -> None:
    """下载成功：下载计数守卫自增（与预览共享次数池）、提交事务。"""
    session = Mock()
    storage = Mock()
    record = _make_file(expires_at=NOW + timedelta(hours=1))
    with (
        patch.object(file_service.ShareFileRepository, "get_by_code", return_value=record),
        patch.object(
            file_service.ShareFileRepository,
            "increment_download_count_guarded",
            return_value=record,
        ) as guarded,
    ):
        result = file_service.consume_file(
            session, code="ab12cd", now=NOW, storage=storage, mode="download"
        )
    assert result is record
    guarded.assert_called_once_with(session, record.id)
    session.commit.assert_called_once()


def test_file_url_strips_trailing_slash() -> None:
    """文件 API 地址：base_url 尾部斜杠不重复。"""
    assert file_service.file_url("ab12cd", "https://example.com/") == (
        "https://example.com/api/v1/files/ab12cd"
    )
    assert file_service.file_url("ab12cd", "https://example.com") == (
        "https://example.com/api/v1/files/ab12cd"
    )
