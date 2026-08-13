/* M5 端到端加密 E2E 测试（宿主机 Node ≥ 20 执行，需服务器运行中）：
 *   1. crypto.js 自检：与浏览器完全同一份源码，Node 24 全局 crypto.subtle/btoa/atob 下
 *      加解密往返、IV 随机性、密钥导出/导入、错误密钥/格式错误抛错；
 *   2. 服务器零明文断言：明文 → 加密 → POST 真实 API → GET 回 content 为密文
 *      （≠ 明文、含 ENC1: 前缀、与提交时逐字节一致）→ 用密钥解密回原文一致。
 * 运行：node tests/e2e/encryption.test.js   （DB 层密文断言由 tests/integration/test_encryption.py 覆盖）
 */
"use strict";

const {
  generateKey,
  encryptContent,
  decryptContent,
  exportKeyToBase64Url,
  importKeyFromBase64Url,
  isEncryptedContent,
} = require("../../app/static/js/crypto.js");

const BASE = process.env.CLIPSHARE_BASE_URL || "http://localhost:8000";
const API = BASE + "/api/v1";
const PLAINTEXT = "M5 端到端加密 E2E 断言 secret-2026";

let passed = 0;
let failed = 0;

function ok(cond, name) {
  if (cond) {
    passed += 1;
    console.log("  PASS  " + name);
  } else {
    failed += 1;
    console.log("  FAIL  " + name);
  }
}

async function expectRejects(promise, name) {
  try {
    await promise;
    ok(false, name);
  } catch (err) {
    ok(true, name);
  }
}

/** 用例 1：加解密往返 + 标记串格式。 */
async function testCryptoRoundtrip() {
  const key = await generateKey();
  const enc = await encryptContent(PLAINTEXT, key);
  ok(enc.startsWith("ENC1:"), "CR 密文含 ENC1: 版本前缀");
  ok(enc.indexOf(".") > 5, "CR 密文含 iv.密文 分隔符");
  const dec = await decryptContent(enc, key);
  ok(dec === PLAINTEXT, "CR 解密还原原文一致");
  ok(isEncryptedContent(enc) && !isEncryptedContent(PLAINTEXT), "CR isEncryptedContent 判定正确");
}

/** 用例 2：同一密钥两次加密结果不同（每次随机 IV）。 */
async function testIvRandomness() {
  const key = await generateKey();
  const enc1 = await encryptContent(PLAINTEXT, key);
  const enc2 = await encryptContent(PLAINTEXT, key);
  ok(enc1 !== enc2, "IV 每次随机（同一明文两次密文不同）");
  ok((await decryptContent(enc2, key)) === PLAINTEXT, "IV 随机不破坏解密");
}

/** 用例 3：密钥导出（base64url）/导入（URL fragment 流程）往返。 */
async function testKeyExportImport() {
  const key = await generateKey();
  const enc = await encryptContent(PLAINTEXT, key);
  const keyB64 = await exportKeyToBase64Url(key);
  ok(/^[A-Za-z0-9_-]{43}$/.test(keyB64), "密钥为 32 字节 base64url（无填充，43 字符）");
  const imported = await importKeyFromBase64Url(keyB64);
  ok((await decryptContent(enc, imported)) === PLAINTEXT, "导出→导入密钥可解密（链接 fragment 流程）");
  await expectRejects(importKeyFromBase64Url("short"), "非法长度密钥导入抛错");
}

/** 用例 4：错误密钥 / 非法格式解密必须抛错。 */
async function testBadKeyAndFormatReject() {
  const key = await generateKey();
  const enc = await encryptContent(PLAINTEXT, key);
  const wrongKey = await generateKey();
  await expectRejects(decryptContent(enc, wrongKey), "错误密钥解密抛错");
  await expectRejects(decryptContent(PLAINTEXT, key), "非密文标记解密抛错");
  await expectRejects(decryptContent("ENC1:not-a-valid-marker", key), "格式损坏密文解密抛错");
  await expectRejects(decryptContent("ENC1:", key), "空密文体解密抛错");
}

/** 用例 5（核心）：服务器零明文 —— 加密 POST → GET 回密文 ≠ 明文 → 密钥解密回原文。 */
async function testServerNeverSeesPlaintext() {
  const key = await generateKey();
  const enc = await encryptContent(PLAINTEXT, key);

  const createResp = await fetch(API + "/shares", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ content: enc, expiry: "1h" }),
  });
  if (createResp.status !== 201) {
    throw new Error("创建失败 HTTP " + createResp.status + "：" + (await createResp.text()));
  }
  const created = await createResp.json();
  const code = created.code;

  const readResp = await fetch(API + "/shares/" + encodeURIComponent(code));
  if (readResp.status !== 200) {
    throw new Error("读取失败 HTTP " + readResp.status);
  }
  const body = await readResp.json();
  ok(body.content !== PLAINTEXT, "E2E GET 回的内容不是明文");
  ok(body.content.startsWith("ENC1:"), "E2E GET 回的内容含 ENC1: 前缀");
  ok(body.content === enc, "E2E 服务器保存的密文与提交时逐字节一致（零明文）");
  ok(!JSON.stringify(body).includes(PLAINTEXT), "E2E 整个响应体不出现明文");
  const dec = await decryptContent(body.content, key);
  ok(dec === PLAINTEXT, "E2E 用密钥解密回原文一致");
}

(async () => {
  try {
    console.log("== M5 端到端加密 E2E（真实 API，Node 24）==");
    await testCryptoRoundtrip();
    await testIvRandomness();
    await testKeyExportImport();
    await testBadKeyAndFormatReject();
    await testServerNeverSeesPlaintext();
    console.log(`\n结果: ${passed} passed, ${failed} failed`);
    // 用 exitCode 而非 process.exit()：后者会强杀 fetch 保活的 socket，
    // 在 Windows 上触发 libuv 断言崩溃（exit 127），且无法正确统计失败
    process.exitCode = failed === 0 ? 0 : 1;
  } catch (err) {
    console.error("E2E 脚本异常:", err);
    process.exitCode = 1;
  }
})();
