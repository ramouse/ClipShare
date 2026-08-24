"use strict";

const vectors = require("../../contracts/crypto-vectors/enc1/positive-vectors.json");
const negatives = require("../../contracts/crypto-vectors/enc1/negative-vectors.json");
const ClipShareCrypto = require("../../app/static/js/crypto.js");

let passed = 0;
let failed = 0;

function ok(condition, name) {
  if (condition) {
    passed += 1;
    console.log("  PASS  " + name);
  } else {
    failed += 1;
    console.log("  FAIL  " + name);
  }
}

function fromHex(value) {
  if (!/^(?:[0-9a-f]{2})*$/i.test(value)) throw new Error("invalid hex fixture");
  return new Uint8Array(value.match(/.{2}/g)?.map((part) => parseInt(part, 16)) || []);
}

function toBase64Url(bytes) {
  return Buffer.from(bytes).toString("base64url");
}

function equalBytes(left, right) {
  return Buffer.from(left).equals(Buffer.from(right));
}

async function withFixedRandomIv(iv, operation) {
  const descriptor = Object.getOwnPropertyDescriptor(crypto, "getRandomValues");
  Object.defineProperty(crypto, "getRandomValues", {
    configurable: true,
    value(target) {
      if (!(target instanceof Uint8Array) || target.length !== iv.length) {
        throw new Error("production encryption requested an unexpected IV shape");
      }
      target.set(iv);
      return target;
    },
  });
  try {
    return await operation();
  } finally {
    if (descriptor) {
      Object.defineProperty(crypto, "getRandomValues", descriptor);
    } else {
      delete crypto.getRandomValues;
    }
  }
}

async function expectCode(promise, code, name) {
  try {
    await promise;
    ok(false, name);
  } catch (error) {
    ok(error && error.code === code, name);
  }
}

async function verifyVector(vector) {
  const keyBytes = fromHex(vector.key_hex);
  const iv = fromHex(vector.iv_hex);
  const plaintext = fromHex(vector.plaintext_hex);
  ok(toBase64Url(keyBytes) === vector.key_b64url, vector.id + " key 编码");
  ok(toBase64Url(iv) === vector.iv_b64url, vector.id + " IV 编码");

  const imported = await ClipShareCrypto.importKeyFromBase64Url(vector.key_b64url);
  let marker;
  if (vector.kind === "text") {
    ok(
      equalBytes(new TextEncoder().encode(vector.plaintext_utf8), plaintext),
      vector.id + " 严格 UTF-8 明文字节"
    );
    marker = await withFixedRandomIv(iv, () =>
      ClipShareCrypto.encryptContent(vector.plaintext_utf8, imported)
    );
    ok(marker === vector.marker_ascii, vector.id + " Web 生产文本加密 KAT");
    ok(
      (await ClipShareCrypto.decryptContent(vector.marker_ascii, imported)) ===
        vector.plaintext_utf8,
      vector.id + " Web 文本解密"
    );
  } else {
    const encryptedMarkerBytes = await withFixedRandomIv(iv, () =>
      ClipShareCrypto.encryptBytes(plaintext, imported)
    );
    marker = new TextDecoder("ascii", { fatal: true }).decode(encryptedMarkerBytes);
    ok(marker === vector.marker_ascii, vector.id + " Web 生产字节加密 KAT");
    const markerBytes = new TextEncoder().encode(vector.marker_ascii);
    ok(
      equalBytes(await ClipShareCrypto.decryptBytes(markerBytes, imported), plaintext),
      vector.id + " Web 字节解密"
    );
  }
  ok(
    marker.split(".")[1] === vector.cipher_and_tag_b64url,
    vector.id + " ciphertext||tag 逐字节一致"
  );
}

(async () => {
  try {
    console.log("== ENC1 v1 固定互操作向量（WebCrypto）==");
    ok(vectors.test_only === true, "固定 key/IV 资产标记为 test_only");
    ok(vectors.algorithm.aad === "empty", "ENC1 AAD 冻结为空");
    ok(vectors.algorithm.output_layout === "ciphertext||tag", "ENC1 标签布局已冻结");
    ok(vectors.limits.max_encrypted_plaintext_bytes === 10485760, "ENC1 文件明文上限已冻结");
    ok(vectors.limits.max_marker_chars === 16777216, "ENC1 marker 解析上限已冻结");
    ok(vectors.vectors.length >= 4, "ENC1 包含零值与非零跨端向量");
    ok(
      vectors.vectors.some((vector) =>
        vector.id === "nonzero-unicode-nul-crlf-normalization"
      ),
      "ENC1 包含 Unicode/NUL/CRLF/组合分解文本向量"
    );
    ok(
      vectors.vectors.some((vector) => vector.id === "nonzero-binary-boundaries"),
      "ENC1 包含非零二进制边界向量"
    );
    ok(
      vectors.error_codes.includes("authentication_failed") &&
        vectors.error_codes.includes("invalid_utf8"),
      "ENC1 跨端错误码已冻结"
    );
    for (const vector of vectors.vectors) await verifyVector(vector);

    const valid = vectors.vectors[0];
    const key = await ClipShareCrypto.importKeyFromBase64Url(valid.key_b64url);
    ok(negatives.suite === "clipshare-enc1-negative", "ENC1 共用负向向量套件");
    ok(negatives.spec_revision === vectors.spec_revision, "正负向规范修订一致");
    for (const testCase of negatives.marker_cases) {
      await expectCode(
        ClipShareCrypto.decryptContent(testCase.value, key),
        testCase.expected_error,
        testCase.id + " marker 拒绝"
      );
    }
    for (const testCase of negatives.base64url_cases) {
      await expectCode(
        ClipShareCrypto.importKeyFromBase64Url(testCase.value),
        testCase.expected_error,
        testCase.id + " Base64URL 拒绝"
      );
    }
    for (const testCase of negatives.size_cases) {
      if (testCase.operation !== "marker-one-char-over-limit") {
        throw new Error("unsupported size operation " + testCase.operation);
      }
      const marker = "ENC1:" + "A".repeat(vectors.limits.max_marker_chars - 4);
      await expectCode(
        ClipShareCrypto.decryptContent(marker, key),
        testCase.expected_error,
        testCase.id + " 大小边界拒绝"
      );
    }
    for (const testCase of negatives.authentication_cases) {
      const source = vectors.vectors.find(
        (vector) => vector.id === testCase.source_vector_id
      );
      if (testCase.mutation === "flip-last-combined-bit") {
        const fields = source.marker_ascii.split(".");
        const combined = new Uint8Array(Buffer.from(fields[1], "base64url"));
        combined[combined.length - 1] ^= 1;
        await expectCode(
          ClipShareCrypto.decryptContent(fields[0] + "." + toBase64Url(combined), key),
          testCase.expected_error,
          testCase.id + " 认证拒绝"
        );
      } else if (testCase.mutation === "replace-key-a5") {
        const wrongKey = await crypto.subtle.importKey(
          "raw",
          new Uint8Array(32).fill(0xa5),
          { name: "AES-GCM" },
          false,
          ["decrypt"]
        );
        await expectCode(
          ClipShareCrypto.decryptContent(source.marker_ascii, wrongKey),
          testCase.expected_error,
          testCase.id + " 认证拒绝"
        );
      } else {
        throw new Error("unsupported authentication mutation " + testCase.mutation);
      }
    }
    for (const testCase of negatives.unicode_cases) {
      if (testCase.operation === "encode-isolated-high-surrogate") {
        await expectCode(
          ClipShareCrypto.encryptContent("\ud800", key),
          testCase.expected_error,
          testCase.id + " Unicode 拒绝"
        );
      } else if (testCase.operation === "decode-ff") {
        const invalidUtf8Cipher = new Uint8Array(
          await crypto.subtle.encrypt(
            {
              name: "AES-GCM",
              iv: new Uint8Array(12),
              additionalData: new Uint8Array(0),
              tagLength: 128,
            },
            key,
            new Uint8Array([0xff])
          )
        );
        await expectCode(
          ClipShareCrypto.decryptContent(
            "ENC1:AAAAAAAAAAAAAAAA." + toBase64Url(invalidUtf8Cipher),
            key
          ),
          testCase.expected_error,
          testCase.id + " UTF-8 拒绝"
        );
      } else {
        throw new Error("unsupported Unicode operation " + testCase.operation);
      }
    }

    console.log(`\n结果: ${passed} passed, ${failed} failed`);
    process.exitCode = failed === 0 ? 0 : 1;
  } catch (error) {
    console.error("ENC1 向量测试异常:", error);
    process.exitCode = 1;
  }
})();
