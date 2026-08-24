"use strict";

const assert = require("assert/strict");
const crypto = require("crypto");

const {
  SANDBOX_TOKEN_PATH,
  SandboxGuardError,
  getSameOriginSandboxUrl,
  validateSandboxEnvironment,
} = require("../../test_harness/sandbox_guard.js");

const TOKEN = Buffer.from("node-unit-proof-never-used-outside-this-process", "utf8");
const RUN_ID = "g0-20260823t120000z-012345abcdef";

function validEnvironment() {
  return {
    ENVIRONMENT: "test",
    CLIPSHARE_SANDBOX_RUN_ID: RUN_ID,
    CLIPSHARE_SANDBOX_TOKEN_FILE: SANDBOX_TOKEN_PATH,
    CLIPSHARE_SANDBOX_TOKEN_SHA256: crypto.createHash("sha256").update(TOKEN).digest("hex"),
    CLIPSHARE_BASE_URL: "http://app-test:8000",
  };
}

function readToken(path) {
  assert.equal(path, SANDBOX_TOKEN_PATH);
  return TOKEN;
}

function rejects(change, expectedPattern) {
  const environment = validEnvironment();
  change(environment);
  assert.throws(
    () => validateSandboxEnvironment(environment, readToken),
    (error) => error instanceof SandboxGuardError && expectedPattern.test(error.message)
  );
}

const valid = validateSandboxEnvironment(validEnvironment(), readToken);
assert.equal(valid.runId, RUN_ID);
assert.equal(valid.baseUrl, "http://app-test:8000");
assert.equal(
  getSameOriginSandboxUrl("/static/app.js", valid.baseUrl, ["/static/"]),
  "http://app-test:8000/static/app.js"
);
assert.throws(
  () => getSameOriginSandboxUrl("http://47.120.13.250/payload.js", valid.baseUrl),
  SandboxGuardError
);
assert.throws(
  () => getSameOriginSandboxUrl("/api/v1/shares", valid.baseUrl, ["/static/"]),
  SandboxGuardError
);

rejects((environment) => {
  environment.CLIPSHARE_BASE_URL = "https://app-test:8000";
}, /HTTP/);
rejects((environment) => {
  environment.CLIPSHARE_BASE_URL = "http://47.120.13.250";
}, /allowlist/);
rejects((environment) => {
  environment.CLIPSHARE_BASE_URL = "http://example.com";
}, /allowlist/);
rejects((environment) => {
  environment.CLIPSHARE_BASE_URL = "http://127.0.0.1:8000";
}, /allowlist/);
rejects((environment) => {
  environment.CLIPSHARE_BASE_URL = "http://app-test:8080";
}, /port 8000/);
rejects((environment) => {
  environment.CLIPSHARE_BASE_URL = "http://app-test:8000/api/v1";
}, /server root/);
rejects((environment) => {
  environment.CLIPSHARE_BASE_URL = "http://user:password@app-test:8000";
}, /credentials/);
rejects((environment) => {
  environment.CLIPSHARE_BASE_URL = "http://app-test:8000/?target=prod";
}, /query or fragment/);
rejects((environment) => {
  delete environment.CLIPSHARE_SANDBOX_TOKEN_SHA256;
}, /missing/);
rejects((environment) => {
  environment.CLIPSHARE_SANDBOX_TOKEN_SHA256 = crypto
    .createHash("sha256")
    .update("different")
    .digest("hex");
}, /does not match/);
rejects((environment) => {
  environment.CLIPSHARE_SANDBOX_RUN_ID = "../escape";
}, /RUN_ID/);
rejects((environment) => {
  environment.ENVIRONMENT = "production";
}, /ENVIRONMENT/);

console.log("sandbox guard pure tests passed: valid sandbox accepted; unsafe proofs rejected");
