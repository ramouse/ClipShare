/* ClipShare 创建页逻辑：表单提交 → POST /api/v1/shares → 展示分享链接/二维码/打开按钮。
 * XSS 红线：本页所有用户可见文本一律 textContent 赋值，不使用 innerHTML。
 */
(function () {
  "use strict";

  const API_PREFIX = "/api/v1";
  // 与后端 settings.share_max_content_length（config.py）保持一致的前端提示上限；
  // 服务端仍为最终校验方（超限返回 422）。
  const MAX_LENGTH = 100000;

  const form = document.getElementById("create-form");
  const contentInput = document.getElementById("content");
  const charCounter = document.getElementById("char-counter");
  const expirySelect = document.getElementById("expiry");
  const maxViewsSelect = document.getElementById("max-views");
  const encryptToggle = document.getElementById("encrypt-toggle");
  const submitBtn = document.getElementById("submit-btn");
  const errorBox = document.getElementById("create-error");
  const resultBox = document.getElementById("create-result");
  const linkInput = document.getElementById("share-link");
  const copyBtn = document.getElementById("copy-btn");
  const qrImg = document.getElementById("qr-img");
  const openBtn = document.getElementById("open-btn");
  const encryptWarning = document.getElementById("encrypt-warning");

  /**
   * 本次创建使用的密钥（base64url 字符串）。
   * 仅存在于浏览器内存与结果链接的 fragment（#k=）中，绝不写入日志/DB/API 请求体。
   * 每次加密创建都会重新生成新密钥（fragment 不随 HTTP 请求发送，服务器拿不到）。
   */
  let activeKeyB64 = null;

  /** 显示错误信息（textContent 赋值，杜绝 XSS）。 */
  function showError(message) {
    errorBox.textContent = message;
    errorBox.classList.remove("d-none");
  }

  function hideError() {
    errorBox.classList.add("d-none");
  }

  /** 字数统计。 */
  contentInput.addEventListener("input", function () {
    charCounter.textContent = contentInput.value.length + " / " + MAX_LENGTH;
  });

  /** 复制文本到剪贴板：优先 Clipboard API，降级 execCommand（非安全上下文可用）。 */
  async function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return;
    }
    const tmp = document.createElement("textarea");
    tmp.value = text;
    tmp.setAttribute("readonly", "");
    tmp.style.position = "absolute";
    tmp.style.left = "-9999px";
    document.body.appendChild(tmp);
    tmp.select();
    const ok = document.execCommand("copy");
    document.body.removeChild(tmp);
    if (!ok) {
      throw new Error("copy failed");
    }
  }

  copyBtn.addEventListener("click", function () {
    copyText(linkInput.value)
      .then(function () {
        const original = copyBtn.textContent;
        copyBtn.textContent = "已复制 ✓";
        setTimeout(function () {
          copyBtn.textContent = original;
        }, 1500);
      })
      .catch(function () {
        // 复制失败时至少让用户手动选中链接
        linkInput.focus();
        linkInput.select();
        showError("复制失败，请手动选择链接复制");
      });
  });

  /** 组装请求体：expiry 四档 / max_views 三档（空串表示不限次）。 */
  function buildPayload() {
    const payload = {
      content: contentInput.value,
      expiry: expirySelect.value,
      max_views: maxViewsSelect.value === "" ? null : Number(maxViewsSelect.value),
    };
    return payload;
  }

  /**
   * 加密模式：明文 → 密文标记串（ENC1:…），密钥拼到分享链接 fragment。
   * 返回 { encoded, keyB64 }；WebCrypto 不可用（非 HTTPS/localhost 环境）时抛错。
   */
  async function encryptPayloadContent(text) {
    if (!window.crypto || !window.crypto.subtle) {
      throw new Error("当前环境不支持 WebCrypto（需 HTTPS 或 localhost 访问）");
    }
    const key = await ClipShareCrypto.generateKey();
    const encoded = await ClipShareCrypto.encryptContent(text, key);
    if (encoded.length > MAX_LENGTH) {
      throw new Error(
        "内容过长：加密后超出服务器长度上限（密文约为原文的 1.4 倍），请缩减内容"
      );
    }
    const keyB64 = await ClipShareCrypto.exportKeyToBase64Url(key);
    return { encoded: encoded, keyB64: keyB64 };
  }

  /** 带密钥的分享链接：加密模式在 URL 后追加 #k=<base64url(key)>（fragment 不随请求发送）。 */
  function shareUrlWithKey(url) {
    if (!activeKeyB64) {
      return url;
    }
    return url + "#k=" + activeKeyB64;
  }

  /** 创建成功：填充结果区（链接/二维码/跳转按钮），显示结果卡片。 */
  function showResult(data) {
    linkInput.value = shareUrlWithKey(data.url);
    openBtn.href = shareUrlWithKey("/s/" + encodeURIComponent(data.code));
    qrImg.src = API_PREFIX + "/shares/" + encodeURIComponent(data.code) + "/qr";
    // 加密模式提示：二维码由服务器生成、不含密钥，必须复制带密钥的链接分享
    encryptWarning.classList.toggle("d-none", !activeKeyB64);
    resultBox.classList.remove("d-none");
    resultBox.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  form.addEventListener("submit", async function (event) {
    event.preventDefault();
    hideError();

    if (!contentInput.value.trim()) {
      showError("内容不能为空");
      contentInput.focus();
      return;
    }

    submitBtn.disabled = true;
    submitBtn.textContent = "创建中…";

    // 加密模式：先加密再发送（服务器只收到密文，密钥只进链接 fragment）
    activeKeyB64 = null;
    const payload = buildPayload();
    if (encryptToggle.checked) {
      try {
        const result = await encryptPayloadContent(payload.content);
        payload.content = result.encoded;
        activeKeyB64 = result.keyB64;
      } catch (err) {
        submitBtn.disabled = false;
        submitBtn.textContent = "创建分享";
        showError(err && err.message ? err.message : "加密失败，请重试");
        return;
      }
    }

    fetch(API_PREFIX + "/shares", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    })
      .then(async function (response) {
        if (response.ok) {
          return response.json();
        }
        // 错误体统一为 Problem Details（RFC 9457）：{type, title, status, detail}
        let detail = "创建失败（HTTP " + response.status + "）";
        try {
          const body = await response.json();
          if (body && typeof body.detail === "string") {
            detail = body.detail;
          }
        } catch (e) {
          // 响应体不是 JSON（理论不会发生：全局处理器保证所有响应均为 JSON）
        }
        throw new Error(detail);
      })
      .then(showResult)
      .catch(function (err) {
        showError(err && err.message ? err.message : "网络错误，请重试");
      })
      .finally(function () {
        submitBtn.disabled = false;
        submitBtn.textContent = "创建分享";
      });
  });
})();
