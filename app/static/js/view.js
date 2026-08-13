/* ClipShare 查看页逻辑：
 * 1. 页面加载后 fetch GET /api/v1/shares/{code}（且仅此一次——切换渲染模式不重复请求，
 *    访问计数只经 API 一次）；
 * 2. 成功 → 内容类型自动识别（JSON → Markdown → 代码 → 纯文本），并提供
 *    「文本 / Markdown / 代码」标签页手动切换兜底；
 * 3. v0.2 双探针：文本端点 404 且 type=share_not_found → 再探测文件端点
 *    GET /api/v1/files/{code}，200 且 kind="file" → 渲染文件卡片
 *    （图标/大小/元信息/加密徽章/下载/预览）；仍 404 → 友好错误页；
 * 4. 失败 → 按 API 错误体 type（share_not_found / share_expired /
 *    share_views_exhausted / file_* 等）渲染友好错误页。
 *
 * XSS 红线（项目书强制）：
 * - 纯文本 / JSON 模式一律 textContent 赋值，禁止 innerHTML；
 * - Markdown：marked 渲染输出必须先经 DOMPurify.sanitize 才能 innerHTML；
 * - 代码：highlight.js 输出同样先 DOMPurify.sanitize 再 innerHTML；
 * - 文件预览复用上述消毒渲染管线（detectType + renderByMode）；
 * - 元信息、错误信息一律 textContent。
 */
(function () {
  "use strict";

  const API_PREFIX = "/api/v1";
  const root = document.getElementById("view-root");
  const code = root.dataset.code;

  const loadingBox = document.getElementById("view-loading");
  const contentBox = document.getElementById("view-content");
  const errorBox = document.getElementById("view-error");
  const errorIcon = document.getElementById("error-icon");
  const errorTitle = document.getElementById("error-title");
  const errorDetail = document.getElementById("error-detail");
  const metaCreated = document.getElementById("meta-created");
  const metaExpires = document.getElementById("meta-expires");
  const metaViews = document.getElementById("meta-views");
  const metaEncrypted = document.getElementById("meta-encrypted");
  const modeTabs = document.querySelectorAll("#mode-tabs .nav-link");
  const contentEl = document.getElementById("content");
  const fileCard = document.getElementById("view-file");
  const fileIcon = document.getElementById("file-icon");
  const fileName = document.getElementById("file-name");
  const fileSize = document.getElementById("file-size");
  const fileMetaCreated = document.getElementById("file-meta-created");
  const fileMetaExpires = document.getElementById("file-meta-expires");
  const fileMetaViews = document.getElementById("file-meta-views");
  const fileEncryptedBadge = document.getElementById("file-encrypted-badge");
  const fileDownloadBtn = document.getElementById("file-download-btn");
  const filePreviewBtn = document.getElementById("file-preview-btn");
  const filePreviewArea = document.getElementById("file-preview-area");

  /** 已拉取的分享数据缓存：渲染模式切换只读缓存，绝不二次请求。 */
  let shareData = null;
  /** 当前渲染模式（由自动识别或用户手动选择决定）。 */
  let currentMode = "text";
  /** 文件卡片数据（renderFileCard 填充；下载解密与重拉元数据使用）。 */
  let fileData = null;

  /* ------------------------------------------------------------------ */
  /* 错误处理                                                             */
  /* ------------------------------------------------------------------ */

  /** 各错误 type 的友好文案（title 为人类可读标题，detail 为补充说明）。 */
  const ERROR_MESSAGES = {
    share_not_found: {
      icon: "🔍",
      title: "分享不存在",
      detail: "短码不存在或已被删除，请检查链接是否完整无误。",
    },
    share_expired: {
      icon: "⏰",
      title: "分享已过期",
      detail: "该分享的有效期已过，已无法访问。",
    },
    share_views_exhausted: {
      icon: "🚫",
      title: "分享访问次数已耗尽",
      detail: "该分享的访问次数已达上限，无法继续访问。",
    },
    rate_limited: {
      icon: "🐢",
      title: "请求过于频繁",
      detail: "请求频率过高，请稍后再试。",
    },
    // M5 端到端加密：内容为密文标记串（ENC1: 前缀）时由前端处理
    share_encrypted: {
      icon: "🔒",
      title: "分享已加密",
      detail:
        "该分享使用了端到端加密，内容与密钥均不经过服务器，只能保存密文。请使用包含密钥的完整链接访问（密钥位于链接 # 号之后），或联系分享者重新获取链接。",
    },
    key_invalid: {
      icon: "🔑",
      title: "密钥缺失或错误",
      detail:
        "无法用当前链接中的密钥解密内容。请确认复制的是分享时生成的完整链接（# 号之后的密钥没有丢失），或向分享者重新获取链接。",
    },
    // v0.2 文件分享：双探针与下载/预览错误的友好文案
    file_not_found: {
      icon: "📄",
      title: "文件不存在",
      detail: "文件短码不存在或已被删除，请检查链接是否完整无误。",
    },
    file_expired: {
      icon: "⏰",
      title: "文件已过期",
      detail: "该文件分享的有效期已过，已无法访问。",
    },
    file_views_exhausted: {
      icon: "🚫",
      title: "文件访问次数已耗尽",
      detail: "该文件的预览/下载次数已达上限，无法继续访问。",
    },
    preview_not_available: {
      icon: "👁️",
      title: "文件不支持预览",
      detail: "该文件为加密内容、超过预览大小上限或扩展名不在预览白名单内，请直接下载查看。",
    },
    file_content_missing: {
      icon: "🗑️",
      title: "文件内容缺失",
      detail: "文件内容可能已被清理，无法获取。",
    },
    // 文件加密但链接中无密钥（downloadFile 缺密钥分支）
    file_encrypted: {
      icon: "🔒",
      title: "文件已加密",
      detail:
        "该文件使用了端到端加密，下载需要密钥。请使用包含密钥的完整链接访问（密钥位于链接 # 号之后），或联系分享者重新获取链接。",
    },
  };

  /** 渲染友好错误页：已知 type 用固定文案，未知 type 兜底使用 API 返回的 title/detail。 */
  function showError(errorBody) {
    const known = ERROR_MESSAGES[errorBody.type] || null;
    errorIcon.textContent = known ? known.icon : "⚠️";
    errorTitle.textContent = known ? known.title : (errorBody.title || "出错了");
    errorDetail.textContent = known ? known.detail : (errorBody.detail || "无法加载分享内容，请稍后重试");
    loadingBox.classList.add("d-none");
    contentBox.classList.add("d-none");
    fileCard.classList.add("d-none");
    errorBox.classList.remove("d-none");
  }

  /* ------------------------------------------------------------------ */
  /* 时间与元信息                                                         */
  /* ------------------------------------------------------------------ */

  /**
   * 解析 API 返回的 naive UTC 时间（无时区标记，如 "2026-08-13T08:13:00.430978"）。
   * JS 的 Date 对无时区字符串按本地时间解析，必须补 "Z" 让其按 UTC 解析，
   * 否则展示时间会比真实时间偏 8 小时（中国时区）。
   */
  function parseIsoUtc(value) {
    if (!value) {
      return null;
    }
    return new Date(
      value.includes("T") && !/([Z]|[+-]\d\d:?\d\d)$/.test(value) ? value + "Z" : value
    );
  }

  function formatDateTime(date) {
    if (!date || Number.isNaN(date.getTime())) {
      return "—";
    }
    return date.toLocaleString("zh-CN", { hour12: false });
  }

  /** 填充元信息徽章：过期时间 null → 永久；剩余次数 null → 无限。 */
  function renderMeta(data) {
    const created = parseIsoUtc(data.created_at);
    const expires = parseIsoUtc(data.expires_at);
    metaCreated.textContent = "创建于 " + formatDateTime(created);
    metaExpires.textContent = expires ? "过期于 " + formatDateTime(expires) : "永久有效";
    metaViews.textContent =
      data.remaining_views === null ? "不限访问次数" : "剩余 " + data.remaining_views + " 次";
  }

  /* ------------------------------------------------------------------ */
  /* 内容类型自动识别                                                     */
  /* ------------------------------------------------------------------ */

  /**
   * 自动识别内容类型：json → markdown → code → text 依次判定。
   * 返回 "json" | "markdown" | "code" | "text"。
   */
  function detectType(content) {
    const trimmed = content.trim();

    // 1) JSON：以 { 或 [ 开头且可被 JSON.parse 解析（避免把纯数字/布尔误判为 JSON）
    if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
      try {
        JSON.parse(trimmed);
        return "json";
      } catch (e) {
        // 不是合法 JSON，继续后续判定
      }
    }

    // 2) Markdown：启发式打分，得分 ≥ 2 判为 Markdown
    let mdScore = 0;
    const lines = trimmed.split("\n");
    for (const line of lines) {
      const l = line.trim();
      if (/^```/.test(l) || /^~~~/.test(l)) {
        mdScore += 3; // 围栏代码块：强信号
      } else if (/^#{1,6}\s/.test(l)) {
        mdScore += 2; // 标题
      } else if (/^>\s?/.test(l)) {
        mdScore += 1; // 引用
      } else if (/^[-*+]\s/.test(l) || /^\d+[.)]\s/.test(l)) {
        mdScore += 1; // 列表
      } else if (/^[-*_]{3,}$/.test(l)) {
        mdScore += 1; // 分隔线
      } else if (/\|.*\|/.test(l)) {
        mdScore += 1; // 表格行
      }
    }
    if (/\[[^\]]+\]\([^)]*\)/.test(trimmed)) {
      mdScore += 1; // 内联链接
    }
    if (/\*\*[^*]+\*\*|__[^_]+__/.test(trimmed)) {
      mdScore += 1; // 加粗
    }
    if (mdScore >= 2) {
      return "markdown";
    }

    // 3) 代码：行尾符号与编程关键字启发式，得分 ≥ 2 判为代码
    let codeScore = 0;
    for (const line of lines) {
      if (/[;{}]\s*$/.test(line)) {
        codeScore += 1; // 行尾 ; { }
      } else if (/^\s*(function|def|class|import|from|const|let|var|return|if|else|for|while|public|private|static|include|package)\b/.test(line)) {
        codeScore += 2; // 编程关键字开头
      } else if (/=>/.test(line)) {
        codeScore += 1; // 箭头函数
      }
    }
    if (codeScore >= 2) {
      return "code";
    }

    // 4) 兜底：纯文本
    return "text";
  }

  /* ------------------------------------------------------------------ */
  /* 渲染（XSS 红线落实点）                                               */
  /* ------------------------------------------------------------------ */

  /** 按模式渲染任意文本到任意容器（文本分享内容区与文件预览区共用）。 */
  function renderByMode(mode, text, target) {
    if (mode === "text") {
      renderText(text, target);
    } else if (mode === "json") {
      renderJson(text, target);
    } else if (mode === "markdown") {
      renderMarkdown(text, target);
    } else if (mode === "code") {
      renderCode(text, target);
    }
  }

  /** 通用渲染入口：清空内容容器后按当前模式渲染（分享内容区专用）。 */
  function render() {
    contentEl.replaceChildren();
    renderByMode(currentMode, shareData.content, contentEl);
  }

  /** 纯文本 / JSON：textContent 赋值（data-raw 控制 pre-wrap 样式）。 */
  function renderText(text, target) {
    target.dataset.raw = "true";
    target.textContent = text;
  }

  function renderJson(text, target) {
    target.dataset.raw = "true";
    try {
      const parsed = JSON.parse(text);
      target.textContent = JSON.stringify(parsed, null, 2);
    } catch (e) {
      // 后端只存原始字符串，识别为 JSON 后仍可能解析失败（罕见），退化为纯文本
      target.textContent = text;
    }
  }

  /** Markdown：marked 渲染 → DOMPurify 消毒 → innerHTML（唯一允许用户内容进 innerHTML 的路径）。 */
  function renderMarkdown(text, target) {
    target.dataset.raw = "false";
    const rawHtml = marked.parse(text);
    const safeHtml = DOMPurify.sanitize(rawHtml, { USE_PROFILES: { html: true } });
    target.innerHTML = safeHtml;
    // 外链一律新窗口打开并禁止携带 referrer（分享链接本身就是访问凭据）
    target.querySelectorAll("a[href]").forEach(function (a) {
      if (a.getAttribute("href").startsWith("http")) {
        a.target = "_blank";
        a.rel = "noopener noreferrer";
      }
    });
  }

  /** 代码：highlight.js 自动识别语言 → DOMPurify 消毒 → 注入 <pre><code>。 */
  function renderCode(text, target) {
    target.dataset.raw = "false";
    const highlighted = hljs.highlightAuto(text);
    const safeHtml = DOMPurify.sanitize(highlighted.value);

    const pre = document.createElement("pre");
    const codeEl = document.createElement("code");
    codeEl.className = "hljs"; // hljs 主题（highlight-github.min.css）依赖该 class
    codeEl.innerHTML = safeHtml; // 已消毒，安全
    pre.appendChild(codeEl);
    target.appendChild(pre);
  }

  /* ------------------------------------------------------------------ */
  /* 渲染模式切换（手动兜底）                                             */
  /* ------------------------------------------------------------------ */

  function setActiveTab(mode) {
    // 标签页仅「文本 / Markdown / 代码」三档（页面契约）：JSON 是自动识别出的
    // 展示形态，归属「文本」标签家族——自动识别为 JSON 时高亮「文本」标签，
    // 用户可手动切到「文本」查看原始 JSON 字符串
    const tabMode = mode === "json" ? "text" : mode;
    modeTabs.forEach(function (tab) {
      tab.classList.toggle("active", tab.dataset.mode === tabMode);
    });
  }

  modeTabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      if (!shareData) {
        return;
      }
      currentMode = tab.dataset.mode;
      setActiveTab(currentMode);
      render();
    });
  });

  /* ------------------------------------------------------------------ */
  /* v0.2 文件卡片：图标 / 大小 / 元信息 / 下载 / 预览                     */
  /* ------------------------------------------------------------------ */

  /** 文件扩展名 → 图标 emoji 映射（未知类型兜底 📁）。 */
  function iconForName(name) {
    const ext = name.toLowerCase().split(".").pop();
    const groups = [
      [["png", "jpg", "jpeg", "gif", "webp", "bmp", "ico", "svg", "avif"], "🖼️"],
      [["mp3", "wav", "ogg", "flac"], "🎵"],
      [["mp4", "webm", "mov", "mkv"], "🎬"],
      [["zip", "rar", "7z", "tar", "gz", "bz2", "xz"], "📦"],
      [["py", "js", "ts", "jsx", "tsx", "java", "c", "cpp", "h", "go", "rs", "php", "rb", "sh", "html", "css", "scss", "sql"], "⚙️"],
      [["pdf", "doc", "docx", "rtf", "odt"], "📄"],
      [["xls", "xlsx", "csv", "tsv", "ods"], "📊"],
      [["ppt", "pptx", "odp"], "📽️"],
      [["md", "markdown"], "📝"],
      [["txt", "log", "ini", "cfg", "conf", "yaml", "yml", "toml", "xml", "json"], "📃"],
    ];
    for (const group of groups) {
      if (group[0].includes(ext)) {
        return group[1];
      }
    }
    return "📁";
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

  /** 渲染文件卡片：图标/文件名/大小/元信息/加密徽章；preview_available 控制预览按钮。 */
  function renderFileCard(data) {
    fileData = data;
    fileIcon.textContent = iconForName(data.original_name);
    fileName.textContent = data.original_name;
    fileSize.textContent = formatSize(data.size_bytes);
    fileMetaCreated.textContent = "创建于 " + formatDateTime(parseIsoUtc(data.created_at));
    fileMetaExpires.textContent = data.expires_at
      ? "过期于 " + formatDateTime(parseIsoUtc(data.expires_at))
      : "永久有效";
    fileMetaViews.textContent =
      data.remaining_views === null ? "不限访问次数" : "剩余 " + data.remaining_views + " 次";
    fileEncryptedBadge.classList.toggle("d-none", !data.encrypted);
    // 加密文件不支持预览（服务端 preview_available=false 双保险），按钮按响应字段控制
    filePreviewBtn.classList.toggle("d-none", !data.preview_available);
    loadingBox.classList.add("d-none");
    contentBox.classList.add("d-none");
    errorBox.classList.add("d-none");
    fileCard.classList.remove("d-none");
  }

  /** 保存 Blob 为本地文件：objectURL + a[download] + 点击 + 撤销（下载红线落点）。 */
  function saveBlob(blob, filename) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }

  /** 下载失败时按 Problem Details 渲染错误页（HTTP 错误 + 网络错误两条路径）。 */
  function showFetchError(response, fallbackTitle) {
    response
      .json()
      .catch(function () {
        return null;
      })
      .then(function (body) {
        showError(body || { title: fallbackTitle, detail: "HTTP " + response.status });
      });
  }

  /**
   * 下载文件：GET /files/{code}/download → blob；
   * 加密文件 → readKeyFromHash + importKey + decryptBytes 解密后再保存；
   * 成功后重拉元数据刷新剩余次数（下载消耗一次）。
   */
  async function downloadFile() {
    if (!fileData) {
      return;
    }
    try {
      // 缺密钥零成本前置（审查加固）：元数据已带 encrypted 标记，
      // 先校验密钥再发下载请求，避免无密钥时白白消耗一次访问次数
      if (fileData.encrypted && !readKeyFromHash()) {
        showError(ERROR_MESSAGES.file_encrypted);
        return;
      }
      const response = await fetch(
        API_PREFIX + "/files/" + encodeURIComponent(code) + "/download"
      );
      if (!response.ok) {
        showFetchError(response, "下载失败");
        return;
      }
      let blob = await response.blob();
      if (fileData.encrypted) {
        const keyB64 = readKeyFromHash();
        if (!keyB64) {
          // 缺密钥：链接未携带 #k= 片段，提示用完整链接
          showError(ERROR_MESSAGES.file_encrypted);
          return;
        }
        try {
          const key = await ClipShareCrypto.importKeyFromBase64Url(keyB64);
          const plain = await ClipShareCrypto.decryptBytes(
            new Uint8Array(await blob.arrayBuffer()),
            key
          );
          blob = new Blob([plain]);
        } catch (err) {
          // 密钥错误 / 密文损坏
          showError(ERROR_MESSAGES.key_invalid);
          return;
        }
      }
      saveBlob(blob, fileData.original_name);
      refreshFileMeta();
    } catch (err) {
      showError({ title: "网络错误", detail: "下载失败，请检查网络后重试。" });
    }
  }

  /**
   * 预览文件：GET /files/{code}/preview → text → 复用消毒渲染管线
   * （detectType + renderByMode）渲染进 #file-preview-area。
   * 预览内容与文本分享同一套 XSS 防护（textContent / DOMPurify）。
   */
  async function previewFile() {
    if (!fileData) {
      return;
    }
    try {
      const response = await fetch(
        API_PREFIX + "/files/" + encodeURIComponent(code) + "/preview"
      );
      if (!response.ok) {
        showFetchError(response, "预览失败");
        return;
      }
      const text = await response.text();
      filePreviewArea.replaceChildren();
      filePreviewArea.classList.remove("d-none");
      renderByMode(detectType(text), text, filePreviewArea);
      filePreviewArea.scrollIntoView({ behavior: "smooth", block: "nearest" });
    } catch (err) {
      showError({ title: "网络错误", detail: "预览失败，请检查网络后重试。" });
    }
  }

  /** 下载成功后重拉文件元数据：刷新剩余次数（下载消耗一次，UI 即时同步）。 */
  async function refreshFileMeta() {
    try {
      const response = await fetch(API_PREFIX + "/files/" + encodeURIComponent(code));
      const body = await response.json().catch(function () {
        return null;
      });
      if (response.ok && body && body.kind === "file") {
        renderFileCard(body);
      }
      // 刷新失败（过期/耗尽/网络抖动）：静默，不打断已完成的下载
    } catch (err) {
      // 静默忽略
    }
  }

  fileDownloadBtn.addEventListener("click", downloadFile);
  filePreviewBtn.addEventListener("click", previewFile);

  /* ------------------------------------------------------------------ */
  /* M5 端到端加密：从链接 fragment 取密钥并解密                           */
  /* ------------------------------------------------------------------ */

  /**
   * 从 URL fragment（#k=<base64url(key)>）解析密钥。
   * fragment 不随 HTTP 请求发送（也不会出现在 Referer 中），因此密钥只存在于
   * 浏览器地址栏与页面内存，服务器永远拿不到。
   * 返回 base64url 字符串；无密钥返回 null。
   */
  function readKeyFromHash() {
    const m = /(?:^|&)k=([A-Za-z0-9_-]+)/.exec(location.hash.replace(/^#/, ""));
    return m ? m[1] : null;
  }

  /** 解密成功：显示「已端到端解密」徽章。 */
  function showEncryptedBadge() {
    if (metaEncrypted) {
      metaEncrypted.classList.remove("d-none");
    }
  }

  /**
   * 密文 → 明文（浏览器内解密，密钥来自链接 fragment）。
   * 成功返回明文；密钥缺失/错误/密文损坏一律返回 null（由调用方渲染对应错误页）。
   */
  async function decryptShareContent(ciphertext) {
    const keyB64 = readKeyFromHash();
    if (!keyB64) {
      showError(ERROR_MESSAGES.share_encrypted);
      return null;
    }
    try {
      const key = await ClipShareCrypto.importKeyFromBase64Url(keyB64);
      const plaintext = await ClipShareCrypto.decryptContent(ciphertext, key);
      showEncryptedBadge();
      return plaintext;
    } catch (err) {
      showError(ERROR_MESSAGES.key_invalid);
      return null;
    }
  }

  /* ------------------------------------------------------------------ */
  /* 主流程：文本探针 → 文件探针（双探针）                                  */
  /* ------------------------------------------------------------------ */

  /** 加载成功：填充元信息、自动识别类型、首次渲染、激活对应标签页。 */
  function showContent(data) {
    shareData = data;
    currentMode = detectType(data.content);
    renderMeta(data);
    setActiveTab(currentMode);
    loadingBox.classList.add("d-none");
    fileCard.classList.add("d-none");
    contentBox.classList.remove("d-none");
    render();
  }

  /**
   * 文件探针：GET /api/v1/files/{code}。200 且 kind="file" → 文件卡片；
   * 404 → 展示文件端点的错误体（file_not_found）；其余错误照常渲染。
   */
  async function probeFileShare() {
    try {
      const response = await fetch(API_PREFIX + "/files/" + encodeURIComponent(code));
      const body = await response.json().catch(function () {
        return null;
      });
      if (response.ok && body && body.kind === "file") {
        renderFileCard(body);
        return;
      }
      if (body === null) {
        // 防御：后端恒返回 JSON，此分支理论上不可达；避免 spinner 永久停留
        showError({ title: "响应异常", detail: "服务器返回了无法解析的响应。" });
        return;
      }
      showError(body);
    } catch (err) {
      showError({ title: "网络错误", detail: "无法连接服务器，请检查网络后重试。" });
    }
  }

  async function loadShare() {
    try {
      const response = await fetch(API_PREFIX + "/shares/" + encodeURIComponent(code));
      const body = await response.json().catch(function () {
        return null;
      });
      if (response.ok) {
        if (body === null) {
          // 防御：后端恒返回 JSON，此分支理论上不可达；避免 spinner 永久停留
          showError({ title: "响应异常", detail: "服务器返回了无法解析的响应。" });
          return;
        }
        // M5：内容是密文标记串 → 解密后再走既有类型识别渲染；类型识别永远作用于明文
        if (ClipShareCrypto.isEncryptedContent(body.content)) {
          const plaintext = await decryptShareContent(body.content);
          if (plaintext === null) {
            return; // 错误页已由 decryptShareContent 渲染（密钥缺失/错误）
          }
          body.content = plaintext;
        }
        showContent(body);
        return;
      }
      // 非 2xx：文本端点 404 且 type=share_not_found → 双探针文件端点
      if (response.status === 404 && body && body.type === "share_not_found") {
        await probeFileShare();
        return;
      }
      // 其余错误：body 为 Problem Details {type, title, status, detail}
      showError(body || { title: "请求失败", detail: "HTTP " + response.status });
    } catch (err) {
      showError({ title: "网络错误", detail: "无法连接服务器，请检查网络后重试。" });
    }
  }

  loadShare();
})();
