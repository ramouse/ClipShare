/* ClipShare 创建页逻辑：
 * 1. 「分享类型」nav-pills 切换文本 / 文件两套表单（本地校验互不干扰）；
 * 2. 文本：表单提交 → POST /api/v1/shares → 展示分享链接/二维码/打开按钮；
 * 3. 文件：FormData(file/expiry/max_views/encrypted) → POST /api/v1/files
 *    → 复用结果卡片；加密路径 File→arrayBuffer→encryptBytes→新 Blob 上传，
 *    密钥只进链接 fragment（#k=）。
 * XSS 红线：本页所有用户可见文本一律 textContent 赋值，不使用 innerHTML。
 * 流式红线：文件上传走 fetch(FormData/File/Blob) 原生分块流式，
 * 禁止 FileReader.readAsDataURL / base64 全量编码。
 */
(function () {
  "use strict";

  const API_PREFIX = "/api/v1";
  // 与后端 settings.share_max_content_length（config.py）保持一致的前端提示上限；
  // 服务端仍为最终校验方（超限返回 422）。
  const MAX_LENGTH = 100000;
  // 与后端 settings.file_max_size（100MB）一致的前端提示上限；
  // 与 settings.file_encrypt_max_size（10MB）一致的加密上限（浏览器全内存加密的固有代价）。
  const FILE_MAX = 100 * 1024 * 1024;
  const FILE_ENCRYPT_MAX = 10 * 1024 * 1024;

  const form = document.getElementById("create-form");
  const typeTabs = document.querySelectorAll("#share-type-tabs .nav-link");
  const textSection = document.getElementById("text-section");
  const fileSection = document.getElementById("file-section");
  const contentInput = document.getElementById("content");
  const charCounter = document.getElementById("char-counter");
  const fileInput = document.getElementById("file-input");
  const fileDropzone = document.getElementById("file-dropzone");
  const fileInfo = document.getElementById("file-info");
  const fileEncryptHint = document.getElementById("file-encrypt-hint");
  const encryptNote = document.getElementById("encrypt-note");
  const encryptNoteFile = document.getElementById("encrypt-note-file");
  const expirySelect = document.getElementById("expiry");
  const maxViewsSelect = document.getElementById("max-views");
  const encryptToggle = document.getElementById("encrypt-toggle");
  const submitBtn = document.getElementById("submit-btn");
  const errorBox = document.getElementById("create-error");
  const resultBox = document.getElementById("create-result");
  const linkInput = document.getElementById("share-link");
  const copyBtn = document.getElementById("copy-btn");
  const qrImg = document.getElementById("qr-img");
  const resultHint = document.getElementById("result-hint");
  const openBtn = document.getElementById("open-btn");
  const encryptWarning = document.getElementById("encrypt-warning");

  /** 当前激活的分享类型："text" | "file"。 */
  let shareType = "text";
  /** 当前选中的文件（input 选择或拖拽落入）。 */
  let selectedFile = null;

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

  /** 人类可读文件大小：B / KB / MB / GB（保留 1 位小数，≥1KB 时）。 */
  function formatSize(bytes) {
    if (bytes < 1024) {
      return bytes + " B";
    }
    const units = ["KB", "MB", "GB", "TB"];
    let value = bytes;
    let index = -1;
    do {
      value /= 1024;
      index += 1;
    } while (value >= 1024 && index < units.length - 1);
    return value.toFixed(1) + " " + units[index];
  }

  /* ------------------------------------------------------------------ */
  /* 分享类型切换（文本 / 文件）                                          */
  /* ------------------------------------------------------------------ */

  function setShareType(type) {
    shareType = type;
    typeTabs.forEach(function (tab) {
      const active = tab.dataset.type === type;
      tab.classList.toggle("active", active);
      tab.setAttribute("aria-selected", active ? "true" : "false");
    });
    textSection.classList.toggle("d-none", type !== "text");
    fileSection.classList.toggle("d-none", type !== "file");
    // 加密说明随类型切换（文本/文件两套文案互不干扰）
    encryptNote.classList.toggle("d-none", type !== "text");
    encryptNoteFile.classList.toggle("d-none", type !== "file");
    // 切换类型时重置提交可用性（文件超限才禁用提交），互不干扰
    submitBtn.disabled = false;
    hideError();
    if (type === "text") {
      // 文本模式加密不受文件大小限制
      encryptToggle.disabled = false;
    } else {
      // 文件模式：加密可用性由当前文件大小决定
      updateFileEncryptState();
    }
  }

  typeTabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      setShareType(tab.dataset.type);
    });
  });

  /* ------------------------------------------------------------------ */
  /* 文件选择与拖拽上传                                                   */
  /* ------------------------------------------------------------------ */

  /**
   * 加密开关与提交按钮联动：>10MB 禁用加密（提示明文直传）；>100MB 禁用提交。
   * 上限均与后端配置一致，服务端为最终校验方（413/422 兜底）。
   */
  function updateFileEncryptState() {
    if (!selectedFile) {
      encryptToggle.disabled = false;
      submitBtn.disabled = false;
      fileEncryptHint.classList.add("d-none");
      return;
    }
    const overEncryptLimit = selectedFile.size > FILE_ENCRYPT_MAX;
    const overMax = selectedFile.size > FILE_MAX;
    if (overEncryptLimit) {
      encryptToggle.checked = false;
      encryptToggle.disabled = true;
    } else {
      encryptToggle.disabled = false;
    }
    // 联动提示：超过 10MB 加密上限时给出原因（明文直传），消除「开关为何灰掉」困惑
    fileEncryptHint.classList.toggle("d-none", !overEncryptLimit);
    submitBtn.disabled = overMax;
    if (overMax) {
      showError("文件超过 100MB 上限，无法分享");
    }
  }

  /** 记录当前文件并刷新信息区 / 加密开关 / 提交按钮状态。 */
  function selectFile(file) {
    selectedFile = file;
    fileInfo.textContent = file.name + "（" + formatSize(file.size) + "）";
    updateFileEncryptState();
  }

  fileInput.addEventListener("change", function () {
    if (fileInput.files.length > 0) {
      selectFile(fileInput.files[0]);
    }
  });

  // 拖拽上传：dragover/dragleave 控制高亮，drop 取第一个文件（与 input 同一状态机）
  fileDropzone.addEventListener("dragover", function (event) {
    event.preventDefault();
    fileDropzone.classList.add("dragging");
  });
  fileDropzone.addEventListener("dragleave", function () {
    fileDropzone.classList.remove("dragging");
  });
  fileDropzone.addEventListener("drop", function (event) {
    event.preventDefault();
    fileDropzone.classList.remove("dragging");
    const dropped = event.dataTransfer.files;
    if (dropped.length > 0) {
      // 同步到 input（保证表单校验与重选体验一致）
      fileInput.files = dropped;
      selectFile(dropped[0]);
    }
  });

  /* ------------------------------------------------------------------ */
  /* 文本输入与复制                                                       */
  /* ------------------------------------------------------------------ */

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

  /* ------------------------------------------------------------------ */
  /* 文本分享（原 M5 流程，保持不变）                                     */
  /* ------------------------------------------------------------------ */

  /** 组装文本请求体：expiry 四档 / max_views 三档（空串表示不限次）。 */
  function buildPayload() {
    const payload = {
      content: contentInput.value,
      expiry: expirySelect.value,
      max_views: maxViewsSelect.value === "" ? null : Number(maxViewsSelect.value),
    };
    return payload;
  }

  /**
   * 文本加密模式：明文 → 密文标记串（ENC1:…），密钥拼到分享链接 fragment。
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

  /* ------------------------------------------------------------------ */
  /* 文件分享（v0.2）                                                    */
  /* ------------------------------------------------------------------ */

  /**
   * 文件加密：File → arrayBuffer → encryptBytes（字节级 AES-GCM）→ 新 Blob。
   * 密钥经 exportKeyToBase64Url 拼入结果链接 fragment；上传体只有密文字节。
   * 仅 ≤10MB 走此路径（updateFileEncryptState 已保证开关可用性）。
   */
  async function encryptFileForUpload(file) {
    if (!window.crypto || !window.crypto.subtle) {
      throw new Error("当前环境不支持 WebCrypto（需 HTTPS 或 localhost 访问）");
    }
    const key = await ClipShareCrypto.generateKey();
    const source = new Uint8Array(await file.arrayBuffer());
    const encrypted = await ClipShareCrypto.encryptBytes(source, key);
    activeKeyB64 = await ClipShareCrypto.exportKeyToBase64Url(key);
    // 用密文 Blob 替换 FormData 中的 file 字段（保留原文件名与类型）
    return new Blob([encrypted], { type: file.type || "application/octet-stream" });
  }

  /**
   * 上传文件：FormData（file/expiry/max_views/encrypted）→ POST /api/v1/files。
   * 流式红线：fetch(FormData+File/Blob) 由浏览器分块流式发送，
   * 绝不手设 Content-Type（boundary 由浏览器生成）、绝不 FileReader base64 编码。
   */
  async function submitFile() {
    if (!selectedFile) {
      showError("请选择要分享的文件");
      return;
    }
    if (selectedFile.size > FILE_MAX) {
      showError("文件超过 100MB 上限，无法分享");
      return;
    }

    // 加密路径：≤10MB 时浏览器全内存加密（服务端 422 双保险兜底）
    activeKeyB64 = null;
    let uploadFile = selectedFile;
    if (encryptToggle.checked) {
      if (selectedFile.size > FILE_ENCRYPT_MAX) {
        showError("文件超过 10MB 加密上限，请关闭加密后明文直传");
        return;
      }
      try {
        uploadFile = await encryptFileForUpload(selectedFile);
      } catch (err) {
        showError(err && err.message ? err.message : "加密失败，请重试");
        return;
      }
    }

    const fd = new FormData();
    fd.append("file", uploadFile, selectedFile.name);
    fd.append("expiry", expirySelect.value);
    fd.append("max_views", maxViewsSelect.value);
    fd.append("encrypted", encryptToggle.checked ? "true" : "false");

    try {
      const response = await fetch(API_PREFIX + "/files", { method: "POST", body: fd });
      if (response.ok) {
        const data = await response.json();
        showResult(data, true);
        return;
      }
      // 错误体统一为 Problem Details（RFC 9457）：{type, title, status, detail}
      let detail = "上传失败（HTTP " + response.status + "）";
      try {
        const body = await response.json();
        if (body && typeof body.detail === "string") {
          detail = body.detail;
        }
      } catch (e) {
        // 响应体不是 JSON（理论不会发生：全局处理器保证所有响应均为 JSON）
      }
      throw new Error(detail);
    } catch (err) {
      showError(err && err.message ? err.message : "网络错误，请重试");
    }
  }

  /* ------------------------------------------------------------------ */
  /* 结果卡片（文本 / 文件共用）                                          */
  /* ------------------------------------------------------------------ */

  /** 带密钥的分享链接：加密模式在 URL 后追加 #k=<base64url(key)>（fragment 不随请求发送）。 */
  function shareUrlWithKey(url) {
    if (!activeKeyB64) {
      return url;
    }
    return url + "#k=" + activeKeyB64;
  }

  /** 文件分享的网页地址：/s/{code}（查看页 shell 双探针分流文本/文件）。 */
  function fileWebUrl(data) {
    return location.origin + "/s/" + encodeURIComponent(data.code);
  }

  /**
   * 创建成功：填充结果区（链接/二维码/跳转按钮），显示结果卡片。
   * 文本：链接取 API 返回的 public url，二维码由 /shares/{code}/qr 生成；
   * 文件：链接取 /s/{code} 网页地址，二维码隐藏（后端文件端点无 QR 接口，
   * 四端点契约只含 upload/meta/preview/download）。
   */
  function showResult(data, isFile) {
    if (isFile) {
      const webUrl = fileWebUrl(data);
      linkInput.value = shareUrlWithKey(webUrl);
      openBtn.href = shareUrlWithKey(webUrl);
      qrImg.classList.add("d-none");
      resultHint.textContent =
        "复制链接或打开分享页即可下载文件。注意：预览与下载共享访问次数，一经消耗不可恢复。";
    } else {
      linkInput.value = shareUrlWithKey(data.url);
      openBtn.href = shareUrlWithKey("/s/" + encodeURIComponent(data.code));
      qrImg.src = API_PREFIX + "/shares/" + encodeURIComponent(data.code) + "/qr";
      qrImg.classList.remove("d-none");
      resultHint.textContent =
        "手机扫码或复制链接即可打开分享页。注意：访问次数一经消耗不可恢复。";
    }
    // 加密模式提示：二维码由服务器生成、不含密钥，必须复制带密钥的链接分享
    encryptWarning.classList.toggle("d-none", !activeKeyB64);
    resultBox.classList.remove("d-none");
    resultBox.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  /* ------------------------------------------------------------------ */
  /* 表单提交：按当前激活类型分流（文本 / 文件校验互不干扰）                */
  /* ------------------------------------------------------------------ */

  form.addEventListener("submit", async function (event) {
    event.preventDefault();
    hideError();

    if (shareType === "file") {
      if (submitBtn.disabled) {
        return; // 文件超限时提交按钮已禁用，防御双击直通
      }
      submitBtn.disabled = true;
      submitBtn.textContent = "上传中…";
      try {
        await submitFile();
      } finally {
        submitBtn.disabled = false;
        submitBtn.textContent = "创建分享";
      }
      return;
    }

    // ---- 文本模式（原 M5 流程） ----
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
      .then(function (data) {
        showResult(data, false);
      })
      .catch(function (err) {
        showError(err && err.message ? err.message : "网络错误，请重试");
      })
      .finally(function () {
        submitBtn.disabled = false;
        submitBtn.textContent = "创建分享";
      });
  });
})();
