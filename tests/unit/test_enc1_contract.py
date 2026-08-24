"""服务端 ENC1 envelope 解析与加密明文大小边界。"""
import json
from io import BytesIO
from pathlib import Path

import pytest

from app.domain.enc1 import (
    ENC1_MAX_MARKER_BYTES,
    Enc1EnvelopeError,
    encrypted_plaintext_size,
    encrypted_plaintext_size_stream,
)

VECTOR_ROOT = json.loads(
    Path("contracts/crypto-vectors/enc1/positive-vectors.json").read_text(encoding="utf-8")
)
VECTORS = VECTOR_ROOT["vectors"]
NEGATIVE_ROOT = json.loads(
    Path("contracts/crypto-vectors/enc1/negative-vectors.json").read_text(encoding="utf-8")
)


def test_shared_known_answer_vectors_expose_plaintext_size_without_key() -> None:
    assert len(VECTORS) >= 4
    for vector in VECTORS:
        marker = vector["marker_ascii"].encode("ascii")
        plaintext_size = len(bytes.fromhex(vector["plaintext_hex"]))
        assert encrypted_plaintext_size(marker) == plaintext_size
        assert encrypted_plaintext_size_stream(BytesIO(marker)) == plaintext_size


def test_marker_size_limit_is_frozen_for_all_clients() -> None:
    assert ENC1_MAX_MARKER_BYTES == 16_777_216
    size_case = NEGATIVE_ROOT["size_cases"][0]
    assert size_case["operation"] == "marker-one-char-over-limit"
    marker = b"ENC1:" + b"A" * (ENC1_MAX_MARKER_BYTES - len(b"ENC1:") + 1)
    with pytest.raises(Enc1EnvelopeError):
        encrypted_plaintext_size(marker)


def test_shared_negative_vectors_use_only_frozen_error_codes() -> None:
    frozen_codes = set(VECTOR_ROOT["error_codes"])
    for section in (
        "marker_cases",
        "base64url_cases",
        "size_cases",
        "authentication_cases",
        "unicode_cases",
    ):
        assert NEGATIVE_ROOT[section]
        assert {case["expected_error"] for case in NEGATIVE_ROOT[section]} <= frozen_codes


@pytest.mark.parametrize(
    "marker",
    [
        b"",
        *(case["value"].encode("ascii") for case in NEGATIVE_ROOT["marker_cases"]),
        b"ENC1:AAAAAAAAAAAAAAAA.Uw-K-8dFNrmpY7TxxMtziw=",
        b"ENC1:AAAAAAAAAAAAAAAA.Uw-K-8dFNrmpY7TxxMtzix",
    ],
)
def test_noncanonical_or_incomplete_markers_are_rejected(marker: bytes) -> None:
    with pytest.raises(Enc1EnvelopeError):
        encrypted_plaintext_size(marker)
