"use strict";

const crypto = require("crypto");
const fs = require("fs");

const SANDBOX_TOKEN_PATH = "/run/secrets/clipshare_sandbox_token";
const RUN_ID_PATTERN = /^g0-[0-9]{8}t[0-9]{6}z-[a-f0-9]{12}$/;
const SHA256_PATTERN = /^[a-f0-9]{64}$/;
const ALLOWED_HTTP_HOSTS = new Set(["app-test"]);
const SANDBOX_HTTP_PORT = "8000";
const GUARD_REJECTED_EXIT_CODE = 78;

class SandboxGuardError extends Error {
  constructor(message) {
    super(message);
    this.name = "SandboxGuardError";
  }
}

function required(environment, name) {
  const value = environment[name];
  if (typeof value !== "string" || value.length === 0) {
    throw new SandboxGuardError(`sandbox proof is missing required field ${name}`);
  }
  return value;
}

function getSameOriginSandboxUrl(candidateText, baseUrlText, allowedPathPrefixes = []) {
  let candidate;
  let baseUrl;
  try {
    baseUrl = new URL(baseUrlText);
    candidate = new URL(candidateText, baseUrl);
  } catch (error) {
    throw new SandboxGuardError("sandbox request URL is malformed");
  }
  if (candidate.origin !== baseUrl.origin || candidate.username || candidate.password) {
    throw new SandboxGuardError("sandbox request URL must remain on the validated origin");
  }
  if (
    allowedPathPrefixes.length > 0 &&
    !allowedPathPrefixes.some((prefix) => candidate.pathname.startsWith(prefix))
  ) {
    throw new SandboxGuardError("sandbox request URL path is outside the allowlist");
  }
  return candidate.href;
}

function validateSandboxEnvironment(environment, readTokenFile) {
  if (environment.ENVIRONMENT !== "test") {
    throw new SandboxGuardError("ENVIRONMENT must be exactly 'test'");
  }

  const runId = required(environment, "CLIPSHARE_SANDBOX_RUN_ID");
  if (!RUN_ID_PATTERN.test(runId)) {
    throw new SandboxGuardError("CLIPSHARE_SANDBOX_RUN_ID has an invalid format");
  }

  const tokenPath = required(environment, "CLIPSHARE_SANDBOX_TOKEN_FILE");
  if (tokenPath !== SANDBOX_TOKEN_PATH) {
    throw new SandboxGuardError("sandbox token file is not the fixed container secret path");
  }

  const expectedDigest = required(environment, "CLIPSHARE_SANDBOX_TOKEN_SHA256");
  if (!SHA256_PATTERN.test(expectedDigest)) {
    throw new SandboxGuardError("sandbox token SHA-256 proof has an invalid format");
  }

  let token;
  try {
    token = readTokenFile(tokenPath);
  } catch (error) {
    throw new SandboxGuardError("sandbox token file cannot be read");
  }
  if (!Buffer.isBuffer(token) || token.length === 0) {
    throw new SandboxGuardError("sandbox token file is empty or invalid");
  }

  const actualDigest = crypto.createHash("sha256").update(token).digest();
  const expectedDigestBytes = Buffer.from(expectedDigest, "hex");
  if (
    actualDigest.length !== expectedDigestBytes.length ||
    !crypto.timingSafeEqual(actualDigest, expectedDigestBytes)
  ) {
    throw new SandboxGuardError("sandbox token SHA-256 proof does not match");
  }

  const baseUrlText = required(environment, "CLIPSHARE_BASE_URL");
  let baseUrl;
  try {
    baseUrl = new URL(baseUrlText);
  } catch (error) {
    throw new SandboxGuardError("CLIPSHARE_BASE_URL is malformed");
  }

  if (baseUrl.protocol !== "http:") {
    throw new SandboxGuardError("CLIPSHARE_BASE_URL must use HTTP inside the sandbox");
  }
  if (baseUrl.username || baseUrl.password) {
    throw new SandboxGuardError("CLIPSHARE_BASE_URL must not contain credentials");
  }
  if (baseUrl.search || baseUrl.hash) {
    throw new SandboxGuardError("CLIPSHARE_BASE_URL must not contain query or fragment data");
  }
  if (baseUrl.pathname !== "/") {
    throw new SandboxGuardError("CLIPSHARE_BASE_URL must identify the server root");
  }

  if (!ALLOWED_HTTP_HOSTS.has(baseUrl.hostname)) {
    throw new SandboxGuardError("CLIPSHARE_BASE_URL host is outside the sandbox allowlist");
  }
  if (baseUrl.port !== SANDBOX_HTTP_PORT) {
    throw new SandboxGuardError("CLIPSHARE_BASE_URL must use sandbox port 8000");
  }

  return Object.freeze({ runId, baseUrl: baseUrl.origin });
}

function getSandboxBaseUrl(
  environment = process.env,
  readTokenFile = (path) => fs.readFileSync(path)
) {
  return validateSandboxEnvironment(environment, readTokenFile).baseUrl;
}

function main() {
  try {
    const proof = validateSandboxEnvironment(process.env, (path) => fs.readFileSync(path));
    console.log(`sandbox guard accepted run ${proof.runId}`);
    return 0;
  } catch (error) {
    if (error instanceof SandboxGuardError) {
      console.error(`sandbox guard rejected execution: ${error.message}`);
      return GUARD_REJECTED_EXIT_CODE;
    }
    console.error("sandbox guard failed unexpectedly");
    return 1;
  }
}

module.exports = {
  ALLOWED_HTTP_HOSTS,
  GUARD_REJECTED_EXIT_CODE,
  RUN_ID_PATTERN,
  SANDBOX_TOKEN_PATH,
  SandboxGuardError,
  getSameOriginSandboxUrl,
  getSandboxBaseUrl,
  validateSandboxEnvironment,
};

if (require.main === module) {
  process.exitCode = main();
}
