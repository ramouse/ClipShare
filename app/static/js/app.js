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
  const submitBtn = document.getElementById("submit-btn");
  const errorBox = document.getElementById("create-error");
  const resultBox = document.getElementById("create-result");
  const linkInput = document.getElementById("share-link");
  const copyBtn = document.getElementById("copy-btn");
  const qrImg = document.getElementById("qr-img");
  const openBtn = document.getElementById("open-btn");

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

  /** 创建成功：填充结果区（链接/二维码/跳转按钮），显示结果卡片。 */
  function showResult(data) {
    linkInput.value = data.url;
    qrImg.src = API_PREFIX + "/shares/" + encodeURIComponent(data.code) + "/qr";
    openBtn.href = "/s/" + encodeURIComponent(data.code);
    resultBox.classList.remove("d-none");
    resultBox.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  form.addEventListener("submit", function (event) {
    event.preventDefault();
    hideError();

    if (!contentInput.value.trim()) {
      showError("内容不能为空");
      contentInput.focus();
      return;
    }

    submitBtn.disabled = true;
    submitBtn.textContent = "创建中…";

    fetch(API_PREFIX + "/shares", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(buildPayload()),
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
