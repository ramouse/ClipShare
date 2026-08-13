/* ClipShare 端到端加密模块（M5 创新点 A）
 *
 * 核心思想：加密/解密全部在浏览器完成，服务器只存密文（API 与后端零改动）。
 * - 算法：AES-256-GCM（浏览器原生 WebCrypto，零第三方依赖）
 * - 密钥：32 字节随机数（crypto.getRandomValues），以 base64url 编码拼入分享链接
 *   的 fragment（# 之后）——fragment 不随 HTTP 请求发送，服务器永远拿不到密钥；
 * - 密文标记串格式：ENC1:<iv_b64url>.<cipher_b64url>
 *   前缀 ENC1: 用于解密端识别「这是加密内容」，iv 12 字节随机（每次加密不同），
 *   密文含 16 字节 GCM 认证标签（可检测密钥错误与篡改）；
 * - 加载方式：浏览器 <script> 挂到 window.ClipShareCrypto；
 *   Node（≥20）走 module.exports，供 tests/e2e/encryption.test.js 直接 require
 *   （Node 24 自带全局 crypto.subtle 与 btoa/atob，行为与浏览器一致）。
 */
(function (root, factory) {
  "use strict";
  if (typeof module === "object" && typeof module.exports === "object") {
    // Node 环境（测试用）
    module.exports = factory();
  } else if (typeof window === "object") {
    // 浏览器 / jsdom 环境
    window.ClipShareCrypto = factory();
  }
})(typeof window !== "undefined" ? window : this, function () {
  "use strict";

  // 版本前缀：未来升级格式时用 ENC2: 等新前缀，旧数据仍可解析
  var VERSION_PREFIX = "ENC1:";
  // AES-GCM 推荐 12 字节 IV；256 位密钥 = 32 字节
  var IV_BYTES = 12;
  var KEY_BYTES = 32;

  /* ------------------------------------------------------------------ */
  /* base64url 编解码（浏览器原生 btoa/atob，无第三方库）                    */
  /* ------------------------------------------------------------------ */

  /**
   * Uint8Array → base64url（无填充；+/ 映射为 -_）。
   * 分块拼接避免 String.fromCharCode.apply 超过调用栈参数上限（大内容时）。
   */
  function bytesToBase64Url(bytes) {
    var bin = "";
    var CHUNK = 0x8000;
    for (var i = 0; i < bytes.length; i += CHUNK) {
      var part = Array.prototype.slice.call(bytes.subarray(i, i + CHUNK));
      bin += String.fromCharCode.apply(null, part);
    }
    return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  }

  /** base64url → Uint8Array（容忍缺失的 = 填充）。 */
  function base64UrlToBytes(str) {
    var b64 = str.replace(/-/g, "+").replace(/_/g, "/");
    var padded = b64 + "=".repeat((4 - (b64.length % 4)) % 4);
    var bin = atob(padded);
    var bytes = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) {
      bytes[i] = bin.charCodeAt(i);
    }
    return bytes;
  }

  /* ------------------------------------------------------------------ */
  /* 密钥管理                                                             */
  /* ------------------------------------------------------------------ */

  /** 生成 256 位 AES-GCM 密钥（CryptoKey 对象，仅存在于浏览器内存）。 */
  function generateKey() {
    return crypto.subtle.generateKey(
      { name: "AES-GCM", length: KEY_BYTES * 8 },
      true, // 可导出：密钥需要拼入分享链接 fragment
      ["encrypt", "decrypt"]
    );
  }

  /** CryptoKey → base64url 字符串（分享链接 #k= 使用的密钥形式）。 */
  async function exportKeyToBase64Url(key) {
    var raw = await crypto.subtle.exportKey("raw", key);
    return bytesToBase64Url(new Uint8Array(raw));
  }

  /**
   * base64url 字符串 → CryptoKey（查看页从链接 fragment 还原密钥）。
   * async 保证任何错误（格式非法/长度非法）都表现为 Promise reject，而非同步抛错。
   */
  async function importKeyFromBase64Url(keyB64) {
    var raw = base64UrlToBytes(keyB64);
    if (raw.length !== KEY_BYTES) {
      throw new Error("密钥长度非法：期望 " + KEY_BYTES + " 字节");
    }
    return crypto.subtle.importKey("raw", raw, { name: "AES-GCM" }, true, [
      "encrypt",
      "decrypt",
    ]);
  }

  /* ------------------------------------------------------------------ */
  /* 加密 / 解密                                                          */
  /* ------------------------------------------------------------------ */

  /**
   * 解析密文标记串 → {iv, cipher}（Uint8Array）。
   * 格式非法（缺前缀 / 缺分隔符 / iv 长度非法）一律同步抛错。
   * 文本与字节两条解密链路共用同一解析逻辑，保证格式语义完全一致。
   */
  function parseMarker(encoded) {
    if (!isEncryptedContent(encoded)) {
      throw new Error("不是 ClipShare 加密内容（缺少 " + VERSION_PREFIX + " 前缀）");
    }
    var body = encoded.slice(VERSION_PREFIX.length);
    var sep = body.indexOf(".");
    if (sep <= 0 || sep >= body.length - 1) {
      throw new Error("密文格式错误：无法定位 iv 与密文分隔符");
    }
    var iv = base64UrlToBytes(body.slice(0, sep));
    var cipher = base64UrlToBytes(body.slice(sep + 1));
    if (iv.length !== IV_BYTES) {
      throw new Error("密文格式错误：iv 长度非法");
    }
    return { iv: iv, cipher: cipher };
  }

  /**
   * 明文（字符串）→ 密文标记串 "ENC1:<iv>.<cipher>"。
   * 每次加密生成新随机 IV，同一密钥多次加密结果互不相同。
   */
  async function encryptContent(plaintext, key) {
    var iv = crypto.getRandomValues(new Uint8Array(IV_BYTES));
    var data = new TextEncoder().encode(plaintext);
    var cipher = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-GCM", iv: iv }, key, data));
    return VERSION_PREFIX + bytesToBase64Url(iv) + "." + bytesToBase64Url(cipher);
  }

  /**
   * 密文标记串 → 明文（字符串）。密钥错误 / 格式非法一律 reject（调用方 catch 渲染错误页）。
   * v0.2 起解析逻辑抽取为 parseMarker，文本与字节链路共用同一格式。
   */
  async function decryptContent(encoded, key) {
    var parts = parseMarker(encoded);
    var plain = new Uint8Array(
      await crypto.subtle.decrypt({ name: "AES-GCM", iv: parts.iv }, key, parts.cipher)
    );
    return new TextDecoder().decode(plain);
  }

  /**
   * 明文二进制 → 密文字节（ENC1 标记串的 UTF-8 编码）。
   * 文件分享 E2E 加密入口：AES-GCM 直接加密 Uint8Array，密文以字节形式
   * 随 multipart 上传，服务器只存字节密文。
   */
  async function encryptBytes(data, key) {
    var iv = crypto.getRandomValues(new Uint8Array(IV_BYTES));
    var cipher = new Uint8Array(
      await crypto.subtle.encrypt({ name: "AES-GCM", iv: iv }, key, data)
    );
    var marker = VERSION_PREFIX + bytesToBase64Url(iv) + "." + bytesToBase64Url(cipher);
    // 标记串纯 ASCII，TextEncoder 编码无损（被编码的是标记元数据而非明文）
    return new TextEncoder().encode(marker);
  }

  /**
   * 密文字节 → 明文字节（文件分享 E2E 解密）。
   *
   * 红线（v0.2）：明文二进制永不经过字符串层——解密结果直接由
   * subtle.decrypt 以 Uint8Array 输出，禁止 String.fromCharCode /
   * TextDecoder 对明文直转（任意字节经 TextDecoder 会被替换字符 U+FFFD
   * 损坏，0x00 与非法 UTF-8 序列即触发）。此处仅把密文标记串（纯 ASCII：
   * ENC1: 前缀 + base64url）按字节解码为字符串做格式解析，属于元数据层，
   * 与明文无关。
   */
  async function decryptBytes(data, key) {
    // ASCII 安全解码：逐字节 String.fromCharCode 只用于标记解析（0-255 无损）
    var bin = "";
    var CHUNK = 0x8000;
    for (var i = 0; i < data.length; i += CHUNK) {
      var part = Array.prototype.slice.call(data.subarray(i, i + CHUNK));
      bin += String.fromCharCode.apply(null, part);
    }
    var parts = parseMarker(bin);
    return new Uint8Array(
      await crypto.subtle.decrypt({ name: "AES-GCM", iv: parts.iv }, key, parts.cipher)
    );
  }

  /** 判断一段文本是否为 ClipShare 加密内容（版本前缀开头）。 */
  function isEncryptedContent(text) {
    return typeof text === "string" && text.startsWith(VERSION_PREFIX);
  }

  return {
    VERSION_PREFIX: VERSION_PREFIX,
    isEncryptedContent: isEncryptedContent,
    generateKey: generateKey,
    exportKeyToBase64Url: exportKeyToBase64Url,
    importKeyFromBase64Url: importKeyFromBase64Url,
    encryptContent: encryptContent,
    decryptContent: decryptContent,
    parseMarker: parseMarker,
    encryptBytes: encryptBytes,
    decryptBytes: decryptBytes,
  };
});
