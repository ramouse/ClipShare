"""Share 仓储集成测试：依赖容器内 PostgreSQL 真实执行 SQL。"""
from concurrent.futures import ThreadPoolExecutor

import pytest
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.db.repository import ShareRepository
from app.db.session import SessionLocal


def test_create_then_get_by_code(db_session: Session) -> None:
    """create 后可按短码完整查回，字段与构造参数一致。"""
    share = ShareRepository.create(
        db_session,
        code="Ab3x9Z",
        content="你好，世界",
        expires_at=None,
        max_views=3,
    )
    assert share.id is not None
    assert share.view_count == 0
    assert share.created_at is not None

    found = ShareRepository.get_by_code(db_session, "Ab3x9Z")
    assert found is not None
    assert found.id == share.id
    assert found.content == "你好，世界"
    assert found.max_views == 3
    assert found.expires_at is None


def test_duplicate_code_violates_unique_index(db_session: Session) -> None:
    """重复短码触发唯一约束，抛出 IntegrityError。"""
    ShareRepository.create(
        db_session, code="dup123", content="第一次", expires_at=None, max_views=None
    )
    db_session.commit()

    with pytest.raises(IntegrityError):
        ShareRepository.create(
            db_session, code="dup123", content="第二次", expires_at=None, max_views=None
        )
        db_session.commit()


def test_get_by_code_miss_returns_none(db_session: Session) -> None:
    """未命中的短码返回 None。"""
    assert ShareRepository.get_by_code(db_session, "zzz999") is None


def test_increment_missing_row_raises_lookup_error(db_session: Session) -> None:
    """对不存在的记录自增时抛 LookupError。"""
    with pytest.raises(LookupError, match="不存在"):
        ShareRepository.increment_view_count(db_session, 999_999)


def test_increment_view_count_is_atomic_under_concurrency(db_session: Session) -> None:
    """并发原子性：10 线程各 increment 10 次后 view_count 恰好为 100。

    依赖 SQL 层原子自增（UPDATE ... SET view_count = view_count + 1），
    任何「读-改-写」实现都会在此用例下丢更新。
    """
    share = ShareRepository.create(
        db_session, code="cc99kk", content="并发计数", expires_at=None, max_views=None
    )
    db_session.commit()
    share_id = share.id

    def worker() -> None:
        # 每个线程独立会话：Session 非线程安全，连接由连接池按需分配
        with SessionLocal() as session:
            for _ in range(10):
                ShareRepository.increment_view_count(session, share_id)
            session.commit()

    with ThreadPoolExecutor(max_workers=10) as pool:
        futures = [pool.submit(worker) for _ in range(10)]
        for future in futures:
            future.result()  # 任一线程抛异常都会在此重新抛出，避免测试假绿

    # 用独立会话读回数据库真值：fixture 会话的身份映射里仍缓存着 view_count=0
    # 的旧实例（expire_on_commit=False 下提交后属性不过期），直接复用会误判
    with SessionLocal() as verification_session:
        fresh = ShareRepository.get_by_code(verification_session, "cc99kk")
    assert fresh is not None
    assert fresh.view_count == 100


def test_increment_same_session_sees_fresh_value(db_session: Session) -> None:
    """RETURNING 生效性：同一会话自增后立即 get，读到的是新值而非缓存旧值。"""
    share = ShareRepository.create(
        db_session, code="rtn001", content="同会话", expires_at=None, max_views=None
    )
    db_session.commit()

    updated = ShareRepository.increment_view_count(db_session, share.id)
    assert updated.view_count == 1
    found = ShareRepository.get_by_code(db_session, "rtn001")
    assert found is not None
    assert found.view_count == 1


def test_guarded_increment_under_limit(db_session: Session) -> None:
    """守卫式自增：未达上限时正常 +1。"""
    share = ShareRepository.create(
        db_session, code="grd001", content="守卫", expires_at=None, max_views=5
    )
    db_session.commit()

    updated = ShareRepository.increment_view_count_guarded(db_session, share.id)
    assert updated is not None
    assert updated.view_count == 1


def test_guarded_increment_blocks_when_exhausted(db_session: Session) -> None:
    """守卫式自增：已达上限时返回 None 且计数不变（服务端红线强制）。"""
    share = ShareRepository.create(
        db_session, code="grd002", content="守卫耗尽", expires_at=None, max_views=1
    )
    db_session.commit()

    first = ShareRepository.increment_view_count_guarded(db_session, share.id)
    assert first is not None
    db_session.commit()

    blocked = ShareRepository.increment_view_count_guarded(db_session, share.id)
    assert blocked is None
    with SessionLocal() as verification_session:
        fresh = ShareRepository.get_by_code(verification_session, "grd002")
    assert fresh is not None
    assert fresh.view_count == 1  # 未被超卖


def test_guarded_increment_missing_row_returns_none(db_session: Session) -> None:
    """守卫式自增：记录不存在返回 None。"""
    assert ShareRepository.increment_view_count_guarded(db_session, 999_999) is None
