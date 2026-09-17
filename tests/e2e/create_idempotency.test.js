"use strict";

const http = require("http");
const { webcrypto } = require("crypto");
const { JSDOM, requestInterceptor } = require("jsdom");
const {
  getSameOriginSandboxUrl,
  getSandboxBaseUrl,
} = require("../../test_harness/sandbox_guard.js");

const BASE = getSandboxBaseUrl();
const PUBLIC_BASE = "https://share.example.test";

function httpGet(url) {
  return new Promise((resolve, reject) => {
    const target = getSameOriginSandboxUrl(url, BASE);
    const request = http.get(target, (response) => {
      if (response.statusCode !== 200) {
        reject(new Error(`HTTP ${response.statusCode} for ${url}`));
        return;
      }
      const chunks = [];
      response.on("data", (chunk) => chunks.push(chunk));
      response.on("end", () => resolve(Buffer.concat(chunks)));
    });
    request.on("error", reject);
  });
}

function resources() {
  return {
    interceptors: [
      requestInterceptor(async (request) => {
        const target = getSameOriginSandboxUrl(request.url, BASE, ["/static/"]);
        const body = await httpGet(target);
        const contentType = request.url.endsWith(".css") ? "text/css" : "text/javascript";
        return new Response(body, { headers: { "Content-Type": contentType } });
      }),
    ],
  };
}

async function waitFor(predicate, description, timeoutMs = 8000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) {
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error(`timeout waiting for ${description}`);
}

async function main() {
  const pageUrl = getSameOriginSandboxUrl(`${BASE}/`, BASE, ["/"]);
  const html = (await httpGet(pageUrl)).toString("utf8");
  const requests = [];
  const dom = new JSDOM(html, {
    url: pageUrl,
    runScripts: "dangerously",
    resources: resources(),
    beforeParse(window) {
      Object.defineProperty(window, "crypto", { configurable: true, value: webcrypto });
      window.HTMLElement.prototype.scrollIntoView = function () {};
      window.fetch = (url, options) => {
        requests.push({ url: String(url), options });
        if (requests.length === 1 || requests.length === 3) {
          return Promise.reject(new Error("simulated response loss"));
        }
        const code = requests.length === 2 ? "idem01" : "idem02";
        return Promise.resolve({
          ok: true,
          status: 201,
          json: () =>
            Promise.resolve({
              code,
              url: `${PUBLIC_BASE}/s/${code}`,
              expires_at: null,
              max_views: null,
              created_at: "2026-08-24T00:00:00.000000Z",
            }),
        });
      };
    },
  });

  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error("timeout waiting for create-page scripts")), 8000);
    if (dom.window.document.readyState === "complete") {
      clearTimeout(timeout);
      resolve();
      return;
    }
    dom.window.addEventListener("load", () => {
      clearTimeout(timeout);
      resolve();
    }, { once: true });
  });
  const document = dom.window.document;
  await waitFor(() => document.getElementById("create-form"), "create form");
  document.getElementById("content").value = "same logical request";
  const form = document.getElementById("create-form");
  form.dispatchEvent(new dom.window.Event("submit", { bubbles: true, cancelable: true }));
  await waitFor(
    () => document.getElementById("create-error").textContent.includes("复用同一幂等请求"),
    "unknown-outcome guidance"
  );
  form.dispatchEvent(new dom.window.Event("submit", { bubbles: true, cancelable: true }));
  await waitFor(
    () => !document.getElementById("create-result").classList.contains("d-none"),
    "idempotent retry result"
  );

  if (requests.length !== 2) {
    throw new Error(`expected two attempts, got ${requests.length}`);
  }
  const firstKey = requests[0].options.headers["Idempotency-Key"];
  const secondKey = requests[1].options.headers["Idempotency-Key"];
  if (!/^web:[0-9a-f]{32}$/.test(firstKey) || firstKey !== secondKey) {
    throw new Error("retry did not reuse the same high-entropy Idempotency-Key");
  }
  if (requests[0].options.body !== requests[1].options.body) {
    throw new Error("retry did not reuse the exact serialized request body");
  }
  if (document.getElementById("share-link").value !== `${PUBLIC_BASE}/s/idem01`) {
    throw new Error("idempotent retry result did not preserve the API public URL");
  }

  // 加密创建必须额外复用同一 key/IV/marker；重试时重新加密会导致幂等冲突或孤儿资源。
  const content = document.getElementById("content");
  content.value = "encrypted logical request";
  content.dispatchEvent(new dom.window.Event("input", { bubbles: true }));
  const encrypt = document.getElementById("encrypt-toggle");
  encrypt.checked = true;
  encrypt.dispatchEvent(new dom.window.Event("change", { bubbles: true }));
  form.dispatchEvent(new dom.window.Event("submit", { bubbles: true, cancelable: true }));
  await waitFor(
    () => requests.length === 3 && document.getElementById("create-error").textContent.includes("复用同一幂等请求"),
    "encrypted unknown-outcome guidance"
  );
  form.dispatchEvent(new dom.window.Event("submit", { bubbles: true, cancelable: true }));
  await waitFor(() => requests.length === 4, "encrypted idempotent retry");

  if (
    requests[2].options.headers["Idempotency-Key"] !==
      requests[3].options.headers["Idempotency-Key"] ||
    requests[2].options.body !== requests[3].options.body
  ) {
    throw new Error("encrypted retry did not reuse the same key and ciphertext body");
  }
  const encryptedPayload = JSON.parse(requests[2].options.body);
  if (!encryptedPayload.content.startsWith("ENC1:")) {
    throw new Error("encrypted request did not send an ENC1 marker");
  }
  await waitFor(
    () => document.getElementById("share-link").value.startsWith(`${PUBLIC_BASE}/s/idem02#k=`),
    "encrypted result fragment"
  );
  dom.window.close();
  console.log("Create-page idempotency retry: 8 passed, 0 failed");
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
