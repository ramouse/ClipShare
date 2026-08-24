"""OpenAPI v1 冻结快照与客户端机器语义门禁。"""
import json
import os
from pathlib import Path
from typing import Any

from app.main import API_CONTRACT_VERSION, app

EXPECTED_OPERATIONS = {
    ("post", "/api/v1/shares"): "createShare",
    ("get", "/api/v1/shares/{code}"): "readShare",
    ("get", "/api/v1/shares/{code}/raw"): "readShareRaw",
    ("get", "/api/v1/shares/{code}/qr"): "getShareQr",
    ("post", "/api/v1/files"): "uploadFile",
    ("get", "/api/v1/files/{code}"): "getFileMetadata",
    ("get", "/api/v1/files/{code}/preview"): "previewFile",
    ("get", "/api/v1/files/{code}/download"): "downloadFile",
    ("get", "/healthz"): "healthCheck",
}


def _serialized_schema() -> str:
    return json.dumps(app.openapi(), ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def test_openapi_matches_versioned_snapshot_and_writes_actual_to_sandbox() -> None:
    actual = _serialized_schema()
    artifact_root = Path(os.environ["CLIPSHARE_CONTRACT_ARTIFACT_DIR"]).resolve()
    if not artifact_root.is_relative_to(Path("/sandbox").resolve()):
        raise AssertionError("契约制品目录必须位于测试容器 /sandbox 下")
    artifact_root.mkdir(parents=True, exist_ok=True)
    (artifact_root / "clipshare-v1.openapi.actual.json").write_text(actual, encoding="utf-8")

    snapshot = Path("contracts/openapi/clipshare-v1.openapi.json")
    assert snapshot.is_file(), "缺少冻结 OpenAPI 快照；从沙盒 actual 制品审阅后纳入版本控制"
    assert actual == snapshot.read_text(encoding="utf-8")


def test_openapi_exposes_stable_operation_ids_problem_details_and_retry_policy() -> None:
    schema: dict[str, Any] = app.openapi()
    assert schema["info"]["version"] == API_CONTRACT_VERSION == "1.0.0"
    assert "ProblemDetail" in schema["components"]["schemas"]

    for (method, path), operation_id in EXPECTED_OPERATIONS.items():
        operation = schema["paths"][path][method]
        assert operation["operationId"] == operation_id
        if path.startswith("/api/"):
            for response in operation["responses"].values():
                assert response["headers"]["Cache-Control"]["schema"]["const"] == "no-store"

    for method, path in (("post", "/api/v1/shares"), ("post", "/api/v1/files")):
        operation = schema["paths"][path][method]
        assert operation["x-clipshare-auto-retry"] == "idempotency-key"
        assert operation["x-clipshare-consumes-view"] is False
        assert "Idempotency-Replayed" in operation["responses"]["201"]["headers"]
        header = next(
            parameter
            for parameter in operation["parameters"]
            if parameter["in"] == "header" and parameter["name"] == "Idempotency-Key"
        )
        assert header["required"] is False
        header_schema = header["schema"]
        constraint_schema = (
            header_schema
            if "minLength" in header_schema
            else next(
                variant
                for variant in header_schema.get("anyOf", [])
                if variant.get("type") == "string"
            )
        )
        assert constraint_schema["minLength"] == 8
        assert constraint_schema["maxLength"] == 128
        assert constraint_schema["pattern"] == r"^[A-Za-z0-9._~:/+\-]{8,128}$"

    upload = schema["paths"]["/api/v1/files"]["post"]
    assert upload["x-clipshare-max-encrypted-plaintext-bytes"] == 10_485_760
    assert upload["x-clipshare-max-encrypted-marker-chars"] == 16_777_216

    for method, path in (
        ("get", "/api/v1/shares/{code}"),
        ("get", "/api/v1/shares/{code}/raw"),
        ("get", "/api/v1/files/{code}/preview"),
        ("get", "/api/v1/files/{code}/download"),
    ):
        operation = schema["paths"][path][method]
        assert operation["x-clipshare-consumes-view"] is True
        assert operation["x-clipshare-auto-retry"] == "forbidden"

    share_conflict = schema["paths"]["/api/v1/shares"]["post"]["responses"]["409"]
    media = share_conflict["content"]["application/problem+json"]
    assert media["schema"]["$ref"] == "#/components/schemas/ProblemDetail"


def test_openapi_declares_every_error_as_problem_details_including_get_422() -> None:
    schema: dict[str, Any] = app.openapi()
    for path, path_item in schema["paths"].items():
        if not path.startswith("/api/"):
            continue
        for method, operation in path_item.items():
            if method not in {"get", "post", "put", "patch", "delete"}:
                continue
            if method == "get":
                assert "422" in operation["responses"]
            for status, response in operation["responses"].items():
                if status.isdigit() and int(status) >= 400:
                    problem = response["content"]["application/problem+json"]
                    assert problem["schema"]["$ref"] == (
                        "#/components/schemas/ProblemDetail"
                    )


def test_openapi_declares_non_json_success_media_and_binary_download_headers() -> None:
    schema: dict[str, Any] = app.openapi()
    expected_media = {
        "/api/v1/shares/{code}/raw": ("text/plain", None),
        "/api/v1/shares/{code}/qr": ("image/png", "binary"),
        "/api/v1/files/{code}/preview": ("text/plain", None),
        "/api/v1/files/{code}/download": ("application/octet-stream", "binary"),
    }
    for path, (media_type, expected_format) in expected_media.items():
        success = schema["paths"][path]["get"]["responses"]["200"]
        media_schema = success["content"][media_type]["schema"]
        assert media_schema["type"] == "string"
        if expected_format is not None:
            assert media_schema["format"] == expected_format

    download = schema["paths"]["/api/v1/files/{code}/download"]["get"]
    assert "Content-Disposition" in download["responses"]["200"]["headers"]


def test_openapi_time_fields_are_fixed_utc_and_created_at_is_non_nullable() -> None:
    schema: dict[str, Any] = app.openapi()
    expected_pattern = r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$"
    for component_name in (
        "ShareCreatedResponse",
        "ShareReadResponse",
        "FileCreatedResponse",
        "FileReadResponse",
    ):
        component = schema["components"]["schemas"][component_name]
        assert "created_at" in component["required"]
        created = component["properties"]["created_at"]
        assert created["type"] == "string"
        assert created["format"] == "date-time"
        assert created["pattern"] == expected_pattern
        assert "anyOf" not in created

        expires = component["properties"]["expires_at"]
        string_variant = next(
            item for item in expires["anyOf"] if item.get("type") == "string"
        )
        assert string_variant["format"] == "date-time"
        assert string_variant["pattern"] == expected_pattern
        assert {item.get("type") for item in expires["anyOf"]} == {"string", "null"}
