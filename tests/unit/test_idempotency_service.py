"""创建型 API 幂等键与请求指纹的纯单元契约。"""
from datetime import datetime
from unittest.mock import MagicMock, patch

import pytest

from app.core.errors import IdempotencyConflictError, IdempotencyKeyInvalidError
from app.db.models import IdempotencyRecord
from app.services import idempotency_service


@pytest.mark.parametrize(
    "key",
    [
        "short",
        "contains space",
        "contains\nnewline",
        "汉字不允许",
        "x" * 129,
    ],
)
def test_invalid_idempotency_keys_are_rejected(key: str) -> None:
    with pytest.raises(IdempotencyKeyInvalidError):
        idempotency_service.validate_key(key)


def test_valid_idempotency_key_is_accepted_but_only_digest_is_storable() -> None:
    key = "client:windows/request-0001"
    assert idempotency_service.validate_key(key) == key
    digest = idempotency_service.key_hash(key)
    assert len(digest) == 64
    assert key not in digest


def test_canonical_request_hash_is_order_independent_and_content_sensitive() -> None:
    left = {"content": "你好", "max_views": 5, "nested": {"b": 2, "a": 1}}
    reordered = {"nested": {"a": 1, "b": 2}, "max_views": 5, "content": "你好"}
    changed = {**left, "max_views": 1}
    assert idempotency_service.canonical_request_hash(left) == (
        idempotency_service.canonical_request_hash(reordered)
    )
    assert idempotency_service.canonical_request_hash(left) != (
        idempotency_service.canonical_request_hash(changed)
    )


def test_locked_request_rejects_same_key_with_different_fingerprint() -> None:
    session = MagicMock()
    existing = IdempotencyRecord(
        operation="create_share",
        key_hash="a" * 64,
        request_hash="b" * 64,
        resource_kind="share",
        resource_code="abc123",
        expires_at=datetime(2099, 1, 1),
    )
    with (
        patch.object(idempotency_service.IdempotencyRepository, "purge_expired") as purge,
        patch.object(idempotency_service.IdempotencyRepository, "get", return_value=existing),
        pytest.raises(IdempotencyConflictError),
        idempotency_service.locked_request(
            session,
            operation="create_share",
            key="request-key-0001",
            request_hash="c" * 64,
            now=datetime(2026, 8, 23),
        ),
    ):
        pass
    session.execute.assert_called_once()
    purge.assert_not_called()
