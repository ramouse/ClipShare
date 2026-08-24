"""创建型 API 的跨进程幂等协调与请求指纹。"""
import hashlib
import hmac
import json
import re
from collections.abc import Iterator
from contextlib import contextmanager
from datetime import datetime, timedelta
from typing import Any, Literal

from sqlalchemy import text
from sqlalchemy.orm import Session

from app.core.config import get_settings
from app.core.errors import IdempotencyConflictError, IdempotencyKeyInvalidError
from app.db.models import IdempotencyRecord
from app.db.repository import IdempotencyRepository

Operation = Literal["create_share", "upload_file"]
ResourceKind = Literal["share", "file"]

_KEY_PATTERN = re.compile(r"^[A-Za-z0-9._~:/+\-]{8,128}$")
settings = get_settings()


def validate_key(value: str) -> str:
    """冻结 Idempotency-Key 格式并拒绝控制字符、空白和超长值。"""
    if not _KEY_PATTERN.fullmatch(value):
        raise IdempotencyKeyInvalidError(
            "Idempotency-Key 必须为 8–128 位 ASCII，且只能包含字母、数字和 ._~:/+-"
        )
    return value


def canonical_request_hash(payload: dict[str, Any]) -> str:
    """对规范 JSON 请求形状计算稳定 SHA-256。"""
    encoded = json.dumps(
        payload,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def key_hash(value: str) -> str:
    """落库只保存幂等键摘要。"""
    return hashlib.sha256(validate_key(value).encode("ascii")).hexdigest()


def _advisory_lock_id(operation: Operation, hashed_key: str) -> int:
    digest = hashlib.sha256(f"{operation}:{hashed_key}".encode("ascii")).digest()
    return int.from_bytes(digest[:8], byteorder="big", signed=True)


@contextmanager
def locked_request(
    session: Session,
    *,
    operation: Operation,
    key: str,
    request_hash: str,
    now: datetime,
) -> Iterator[IdempotencyRecord | None]:
    """串行化同一 operation/key，并返回可重放记录或创建许可。

    使用 PostgreSQL transaction advisory lock：锁随 commit/rollback/连接异常
    自动释放，不会泄漏到连接池。调用方必须在同一事务内创建业务资源、写入
    幂等记录并提交，才能保证结果与幂等索引原子可见。
    """
    hashed_key = key_hash(key)
    lock_id = _advisory_lock_id(operation, hashed_key)
    session.execute(text("SELECT pg_advisory_xact_lock(:lock_id)"), {"lock_id": lock_id})

    # 业务事务只处理当前键。不得在持有当前键 advisory lock 时顺带锁定/删除
    # 其他键，否则两个并发请求可能形成“键锁 → 行锁 / 行锁 → 键锁”的死锁环。
    # 跨键 TTL 回收必须由独立维护事务执行。
    existing = IdempotencyRepository.get(session, operation=operation, key_hash=hashed_key)
    if existing is not None and existing.expires_at <= now:
        IdempotencyRepository.delete(session, existing)
        existing = None
    if existing is not None and not hmac.compare_digest(existing.request_hash, request_hash):
        raise IdempotencyConflictError("同一 Idempotency-Key 已用于不同请求内容")
    yield existing


def store_result(
    session: Session,
    *,
    operation: Operation,
    key: str,
    request_hash: str,
    resource_kind: ResourceKind,
    resource_code: str,
    now: datetime,
) -> IdempotencyRecord:
    """在业务资源同一事务中保存有限期幂等结果。"""
    if settings.idempotency_ttl_seconds <= 0:
        raise RuntimeError("IDEMPOTENCY_TTL_SECONDS 必须大于 0")
    return IdempotencyRepository.create(
        session,
        operation=operation,
        key_hash=key_hash(key),
        request_hash=request_hash,
        resource_kind=resource_kind,
        resource_code=resource_code,
        expires_at=now + timedelta(seconds=settings.idempotency_ttl_seconds),
    )
