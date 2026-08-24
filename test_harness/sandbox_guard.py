"""Fail-closed proof checks for destructive Python test processes.

This module deliberately uses only the Python standard library. Importing and
validating it cannot create a SQLAlchemy engine or connect to a database.
"""

from __future__ import annotations

import hashlib
import hmac
import os
import re
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import unquote, urlsplit

FIXED_PROOF_PATH = "/run/secrets/clipshare_sandbox_token"
RUN_ID_PATTERN = re.compile(r"^g0-[0-9]{8}t[0-9]{6}z-[a-f0-9]{12}$")
SHA256_PATTERN = re.compile(r"^[a-f0-9]{64}$")
ALLOWED_DATABASE_HOSTS = frozenset({"db-test"})
GUARD_REJECTED_EXIT_CODE = 78


class SandboxGuardError(RuntimeError):
    """Raised before a test can touch a non-sandbox resource."""


@dataclass(frozen=True, slots=True)
class SandboxProof:
    """Validated, non-secret sandbox identity."""

    run_id: str
    database_host: str
    database_port: int
    database_name: str


TokenReader = Callable[[str], bytes]


def _required(environ: Mapping[str, str], name: str) -> str:
    value = environ.get(name)
    if value is None or value == "":
        raise SandboxGuardError(f"sandbox proof is missing required field {name}")
    return value


def validate_sandbox_environment(
    environ: Mapping[str, str],
    token_reader: TokenReader,
) -> SandboxProof:
    """Validate sandbox evidence without network or database I/O.

    ``token_reader`` is injected so unit tests can remain pure and never create
    the fixed container secret path on the host.
    """

    if environ.get("ENVIRONMENT") != "test":
        raise SandboxGuardError("ENVIRONMENT must be exactly 'test'")

    database_url = _required(environ, "DATABASE_URL")
    try:
        parsed = urlsplit(database_url)
        database_host = parsed.hostname
        database_port = parsed.port
    except ValueError as exc:
        raise SandboxGuardError("DATABASE_URL is malformed") from exc

    if parsed.scheme not in {"postgresql", "postgresql+psycopg"}:
        raise SandboxGuardError("DATABASE_URL must use PostgreSQL")
    if parsed.query or parsed.fragment:
        raise SandboxGuardError("DATABASE_URL query and fragment parameters are forbidden")
    if database_host is None or database_host not in ALLOWED_DATABASE_HOSTS:
        raise SandboxGuardError("DATABASE_URL host is outside the sandbox allowlist")
    if database_port != 5432:
        raise SandboxGuardError("DATABASE_URL must use the dedicated PostgreSQL port 5432")

    encoded_database_name = parsed.path.removeprefix("/")
    database_name = unquote(encoded_database_name)
    if not database_name or "/" in database_name or not database_name.endswith("_test"):
        raise SandboxGuardError("DATABASE_URL database name must end with '_test'")

    run_id = _required(environ, "CLIPSHARE_SANDBOX_RUN_ID")
    if RUN_ID_PATTERN.fullmatch(run_id) is None:
        raise SandboxGuardError("CLIPSHARE_SANDBOX_RUN_ID has an invalid format")

    token_path = _required(environ, "CLIPSHARE_SANDBOX_TOKEN_FILE")
    if token_path != FIXED_PROOF_PATH:
        raise SandboxGuardError("sandbox token file is not the fixed container secret path")

    expected_digest = _required(environ, "CLIPSHARE_SANDBOX_TOKEN_SHA256")
    if SHA256_PATTERN.fullmatch(expected_digest) is None:
        raise SandboxGuardError("sandbox token SHA-256 proof has an invalid format")

    try:
        token = token_reader(token_path)
    except OSError as exc:
        raise SandboxGuardError("sandbox token file cannot be read") from exc
    if not isinstance(token, bytes) or len(token) == 0:
        raise SandboxGuardError("sandbox token file is empty or invalid")

    actual_digest = hashlib.sha256(token).hexdigest()
    if not hmac.compare_digest(actual_digest, expected_digest):
        raise SandboxGuardError("sandbox token SHA-256 proof does not match")

    return SandboxProof(
        run_id=run_id,
        database_host=database_host,
        database_port=database_port,
        database_name=database_name,
    )


def require_test_sandbox(environ: Mapping[str, str] | None = None) -> SandboxProof:
    """Validate the current process before importing database engine modules."""

    runtime_environment = os.environ if environ is None else environ
    return validate_sandbox_environment(
        runtime_environment,
        lambda path: Path(path).read_bytes(),
    )


def main() -> int:
    """Runtime probe used by the sandbox orchestrator."""

    try:
        proof = require_test_sandbox()
    except SandboxGuardError as exc:
        print(f"sandbox guard rejected execution: {exc}")
        return GUARD_REJECTED_EXIT_CODE
    print(f"sandbox guard accepted run {proof.run_id}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
