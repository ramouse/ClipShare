"""Pure negative and positive tests for the destructive-test fuse."""

from __future__ import annotations

import hashlib
from collections.abc import Callable

import pytest

from test_harness.sandbox_guard import (
    FIXED_PROOF_PATH,
    SandboxGuardError,
    validate_sandbox_environment,
)

RUN_ID = "g0-20260823t120000z-012345abcdef"
TOKEN = b"unit-test-proof-never-used-outside-this-process"
TOKEN_SHA256 = hashlib.sha256(TOKEN).hexdigest()


def valid_environment() -> dict[str, str]:
    return {
        "ENVIRONMENT": "test",
        "DATABASE_URL": (
            "postgresql+psycopg://clipshare:local-only@db-test:5432/clipshare_test"
        ),
        "CLIPSHARE_SANDBOX_RUN_ID": RUN_ID,
        "CLIPSHARE_SANDBOX_TOKEN_FILE": FIXED_PROOF_PATH,
        "CLIPSHARE_SANDBOX_TOKEN_SHA256": TOKEN_SHA256,
    }


def token_reader(expected_token: bytes = TOKEN) -> Callable[[str], bytes]:
    def read(path: str) -> bytes:
        assert path == FIXED_PROOF_PATH
        return expected_token

    return read


def test_accepts_valid_internal_test_database_and_proof() -> None:
    proof = validate_sandbox_environment(valid_environment(), token_reader())

    assert proof.run_id == RUN_ID
    assert proof.database_host == "db-test"
    assert proof.database_port == 5432
    assert proof.database_name == "clipshare_test"


@pytest.mark.parametrize("environment", ["development", "production", "Test", ""])
def test_rejects_non_test_environment(environment: str) -> None:
    values = valid_environment()
    values["ENVIRONMENT"] = environment

    with pytest.raises(SandboxGuardError, match="ENVIRONMENT"):
        validate_sandbox_environment(values, token_reader())


def test_rejects_development_database() -> None:
    values = valid_environment()
    values["DATABASE_URL"] = (
        "postgresql+psycopg://clipshare:local-only@db-test:5432/clipshare"
    )

    with pytest.raises(SandboxGuardError, match="_test"):
        validate_sandbox_environment(values, token_reader())


def test_rejects_production_database_host() -> None:
    values = valid_environment()
    values["DATABASE_URL"] = (
        "postgresql+psycopg://clipshare:local-only@47.120.13.250:5432/clipshare_test"
    )

    with pytest.raises(SandboxGuardError, match="allowlist"):
        validate_sandbox_environment(values, token_reader())


@pytest.mark.parametrize("host", ["localhost", "127.0.0.1", "[::1]"])
def test_rejects_loopback_database_host(host: str) -> None:
    values = valid_environment()
    values["DATABASE_URL"] = (
        f"postgresql+psycopg://clipshare:local-only@{host}:5432/clipshare_test"
    )

    with pytest.raises(SandboxGuardError, match="allowlist"):
        validate_sandbox_environment(values, token_reader())


def test_rejects_non_dedicated_database_port() -> None:
    values = valid_environment()
    values["DATABASE_URL"] = (
        "postgresql+psycopg://clipshare:local-only@db-test:15432/clipshare_test"
    )

    with pytest.raises(SandboxGuardError, match="port 5432"):
        validate_sandbox_environment(values, token_reader())


@pytest.mark.parametrize(
    "override",
    [
        "host=47.120.13.250",
        "hostaddr=47.120.13.250",
        "dbname=clipshare",
        "host=db-test&host=47.120.13.250",
    ],
)
def test_rejects_database_query_parameter_overrides(override: str) -> None:
    values = valid_environment()
    values["DATABASE_URL"] += f"?{override}"

    with pytest.raises(SandboxGuardError, match="parameters are forbidden"):
        validate_sandbox_environment(values, token_reader())


def test_rejects_database_url_fragment() -> None:
    values = valid_environment()
    values["DATABASE_URL"] += "#host=47.120.13.250"

    with pytest.raises(SandboxGuardError, match="parameters are forbidden"):
        validate_sandbox_environment(values, token_reader())


def test_rejects_missing_token_proof() -> None:
    values = valid_environment()
    del values["CLIPSHARE_SANDBOX_TOKEN_SHA256"]

    with pytest.raises(SandboxGuardError, match="missing"):
        validate_sandbox_environment(values, token_reader())


def test_rejects_token_digest_mismatch() -> None:
    values = valid_environment()
    values["CLIPSHARE_SANDBOX_TOKEN_SHA256"] = hashlib.sha256(b"different").hexdigest()

    with pytest.raises(SandboxGuardError, match="does not match"):
        validate_sandbox_environment(values, token_reader())


@pytest.mark.parametrize(
    "run_id",
    ["", "g0-latest", "G0-20260823t120000z-012345abcdef", "../escape", RUN_ID + "x"],
)
def test_rejects_invalid_run_id(run_id: str) -> None:
    values = valid_environment()
    values["CLIPSHARE_SANDBOX_RUN_ID"] = run_id

    with pytest.raises(SandboxGuardError, match="RUN_ID"):
        validate_sandbox_environment(values, token_reader())


def test_rejects_non_fixed_token_path() -> None:
    values = valid_environment()
    values["CLIPSHARE_SANDBOX_TOKEN_FILE"] = "/tmp/clipshare-token"  # noqa: S105, S108

    with pytest.raises(SandboxGuardError, match="fixed"):
        validate_sandbox_environment(values, token_reader())
