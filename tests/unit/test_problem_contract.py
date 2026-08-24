"""版本化错误目录必须覆盖每个业务异常，防止客户端机器码静默漂移。"""
import asyncio
import json
from pathlib import Path
from unittest.mock import MagicMock, patch

from sqlalchemy.exc import StatementError

from app.core.errors import AppError, unhandled_error_handler
from app.db.session import engine


def _all_app_error_types() -> set[type[AppError]]:
    """递归枚举业务异常，避免未来增加中间基类后漏出版本化目录。"""
    discovered: set[type[AppError]] = set()
    pending = list(AppError.__subclasses__())
    while pending:
        error_type = pending.pop()
        if error_type in discovered:
            continue
        discovered.add(error_type)
        pending.extend(error_type.__subclasses__())
    return discovered


def test_problem_catalog_matches_all_app_error_subclasses() -> None:
    catalog = json.loads(
        Path("contracts/problem-details/v1.json").read_text(encoding="utf-8")
    )
    catalog_statuses = {
        item["type"]: item["status"] for item in catalog["errors"]
    }
    fixed_catalog_errors = {
        item["type"]: item["status"]
        for item in catalog["errors"]
        if isinstance(item["status"], int)
    }
    catalog_types = [item["type"] for item in catalog["errors"]]
    app_error_types = _all_app_error_types()
    app_error_codes = [error_type.type for error_type in app_error_types]
    app_errors = {error_type.type: error_type.status for error_type in app_error_types}
    framework_only = {
        "http_error",
        "internal_error",
        "rate_limited",
    }
    assert catalog["contract_version"] == "1.0.0"
    assert catalog["media_type"] == "application/problem+json"
    assert catalog["required_fields"] == ["type", "title", "status", "detail"]
    assert len(catalog_types) == len(set(catalog_types))
    assert len(app_error_codes) == len(set(app_error_codes))
    assert app_errors.items() <= fixed_catalog_errors.items()
    assert set(catalog_statuses) - set(app_errors) == framework_only
    assert catalog_statuses["http_error"] == "http-status"


def test_database_engine_hides_bound_parameters_in_errors() -> None:
    assert engine.hide_parameters is True


def test_unhandled_database_error_never_logs_or_returns_sensitive_values() -> None:
    sensitive_marker = "CLIPBOARD-SECRET-MARKER-8f50b9"
    exc = StatementError(
        f"driver included {sensitive_marker}",
        "INSERT INTO shares (content) VALUES (:content)",
        {"content": sensitive_marker},
        RuntimeError(sensitive_marker),
    )
    with patch("app.core.errors.logger.error") as log_error:
        response = asyncio.run(unhandled_error_handler(MagicMock(), exc))

    log_error.assert_called_once_with("unhandled_error", error_type="StatementError")
    assert sensitive_marker.encode() not in response.body
    assert b"INSERT INTO shares" not in response.body
    assert response.headers["cache-control"] == "no-store"
