/* M4 前端浏览器级冒烟（可选加分项，宿主机 node 执行）+ v0.2 文件卡片：
 * 用 jsdom 加载真实服务器上的查看页 HTML 与真实 vendor/自研 JS，
 * 桩掉 window.fetch 模拟 API 响应，验证 view.js 的：
 *   1. 内容类型自动识别（JSON / Markdown / 代码 / 纯文本）
 *   2. XSS 红线：marked 与 highlight.js 输出经 DOMPurify 消毒后进 DOM
 *   3. 错误页按 API type 渲染（share_not_found / share_expired / share_views_exhausted）
 *   4. 元信息展示与「文本/Markdown/代码」标签页手动切换
 *   5. v0.2 文件双探针：文本端点 404 share_not_found → 文件端点 200 →
 *      文件卡片渲染（文件名/大小/加密徽章/预览按钮可见性）
 * 运行：node tests/e2e/frontend_smoke.js （需宿主机 node + 容器内服务运行中）
 * 前置：npm install jsdom（见文件末尾注释）
 */
"use strict";

const http = require("http");
const { JSDOM, requestInterceptor } = require("jsdom");

const BASE = "http://localhost:8000";

/** 从真实服务器拉取页面引用的脚本/样式（零本地拷贝，全真链路）。 */
function httpGet(url) {
  return new Promise((resolve, reject) => {
    const req = http.get(url, (res) => {
      if (res.statusCode !== 200) {
        reject(new Error(`HTTP ${res.statusCode} for ${url}`));
        return;
      }
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => resolve(Buffer.concat(chunks)));
    });
    req.on("error", reject);
  });
}

/** 页面外部资源的请求拦截器（jsdom 29 的 resources.interceptors 机制）。 */
function liveResources() {
  return {
    interceptors: [
      requestInterceptor(async (request) => {
        const body = await httpGet(request.url);
        const contentType = request.url.endsWith(".css") ? "text/css" : "text/javascript";
        return new Response(body, { headers: { "Content-Type": contentType } });
      }),
    ],
  };
}

/**
 * 在页面脚本执行前注入的 fetch 桩与 alert 追踪。
 * apiResponder 支持两种形态：
 *   - { status, body }：所有请求返回同一响应（原有用例）；
 *   - (url) => { status, body }：按 URL 分流的响应（v0.2 文件双探针用例）。
 */
function beforeParse(window, apiResponder) {
  window.fetch = (url) => {
    const resp =
      typeof apiResponder === "function" ? apiResponder(String(url)) : apiResponder;
    return Promise.resolve({
      ok: resp.status >= 200 && resp.status < 300,
      status: resp.status,
      json: () => Promise.resolve(resp.body),
    });
  };
  window.alertCalls = [];
  window.alert = (msg) => window.alertCalls.push(msg);
}

/** 等待渲染完成：内容区 / 文件卡片 / 错误区任一出现。 */
async function waitRender(window, timeoutMs = 8000) {
  const doc = window.document;
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const contentHidden = doc.getElementById("view-content").classList.contains("d-none");
    const errorHidden = doc.getElementById("view-error").classList.contains("d-none");
    const fileHidden = doc.getElementById("view-file").classList.contains("d-none");
    if (!contentHidden || !errorHidden || !fileHidden) {
      return true;
    }
    await new Promise((r) => setTimeout(r, 50));
  }
  throw new Error("渲染超时");
}

async function loadViewPage(code, apiResponse) {
  const html = await new Promise((resolve, reject) => {
    http
      .get(`${BASE}/s/${code}`, (res) => {
        const chunks = [];
        res.on("data", (c) => chunks.push(c));
        res.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
      })
      .on("error", reject);
  });
  const dom = new JSDOM(html, {
    url: `${BASE}/s/${code}`,
    runScripts: "dangerously",
    resources: liveResources(),
    beforeParse: (window) => beforeParse(window, apiResponse),
  });
  const ok = await waitRender(dom.window);
  return dom;
}

let passed = 0;
let failed = 0;

function assert(cond, name, extra) {
  if (cond) {
    passed += 1;
    console.log(`  PASS  ${name}`);
  } else {
    failed += 1;
    console.log(`  FAIL  ${name}${extra ? `  -- ${extra}` : ""}`);
  }
}

/** 用例 1：Markdown + XSS 载荷 —— 渲染必须经 DOMPurify 消毒。 */
async function testMarkdownXssSanitized() {
  const dom = await loadViewPage("case-mk", {
    status: 200,
    body: {
      code: "case-mk",
      content:
        "# 标题\n\n- 条目A\n- 条目B\n\n<img src=x onerror=alert(1)>\n<script>alert(2)</script>\n\n[链接](https://example.com)",
      expires_at: null,
      remaining_views: null,
      created_at: "2026-08-13T08:00:00",
    },
  });
  const win = dom.window;
  const doc = win.document;
  const contentEl = doc.getElementById("content");
  // 自动识别为 markdown（标题 + 列表得分 ≥ 2）
  assert(doc.querySelector('#mode-tabs .nav-link[data-mode="markdown"]').classList.contains("active"), "MK 自动识别为 Markdown");
  assert(contentEl.querySelector("h1"), "MK 标题渲染");
  assert(contentEl.querySelectorAll("li").length === 2, "MK 列表渲染");
  // XSS：注入的 img[onerror] 与 script 必须被 DOMPurify 清除
  assert(contentEl.querySelectorAll("img[onerror], script, iframe, object").length === 0, "MK XSS 载荷被消毒");
  assert(win.alertCalls.length === 0, "MK 未触发任何 alert");
  // 外链加 target/_blank + rel/noopener（属性赋值，安全）
  const a = contentEl.querySelector("a[href='https://example.com']");
  assert(a && a.target === "_blank" && a.rel === "noopener noreferrer", "MK 外链新窗口打开");
  // 元信息：过期 null → 永久；次数 null → 无限
  assert(doc.getElementById("meta-expires").textContent.includes("永久"), "MK 元信息：永久");
  assert(doc.getElementById("meta-views").textContent.includes("不限"), "MK 元信息：无限");
  dom.window.close();
}

/** 用例 2：JSON 内容 —— 自动识别 JSON 并格式化展示。 */
async function testJsonDetection() {
  const dom = await loadViewPage("case-js", {
    status: 200,
    body: {
      code: "case-js",
      content: '{"name":"clipshare","tags":["a","b"],"nested":{"x":1}}',
      expires_at: null,
      remaining_views: 5,
      created_at: "2026-08-13T08:00:00",
    },
  });
  const doc = dom.window.document;
  // JSON 自动识别后归属「文本」标签（标签页契约仅 文本/Markdown/代码 三档）
  assert(doc.querySelector('#mode-tabs .nav-link[data-mode="text"]').classList.contains("active"), "JS 自动识别为 JSON（归入文本标签）");
  const text = doc.getElementById("content").textContent;
  assert(text.includes('"name": "clipshare"') && text.includes("\n"), "JS 格式化缩进展示");
  assert(doc.getElementById("meta-views").textContent.includes("剩余 5 次"), "JS 元信息：剩余次数");
  dom.window.close();
}

/** 用例 3：代码内容 —— 自动识别为代码并高亮。 */
async function testCodeDetection() {
  const dom = await loadViewPage("case-cd", {
    status: 200,
    body: {
      code: "case-cd",
      content: "def add(a, b):\n    return a + b\n\nprint(add(1, 2))",
      expires_at: null,
      remaining_views: null,
      created_at: "2026-08-13T08:00:00",
    },
  });
  const doc = dom.window.document;
  assert(doc.querySelector('#mode-tabs .nav-link[data-mode="code"]').classList.contains("active"), "CD 自动识别为代码");
  const codeEl = doc.querySelector("#content code.hljs");
  assert(codeEl && codeEl.querySelectorAll("span").length > 0, "CD highlight.js 高亮生效");
  // 高亮输出同样过 DOMPurify：注入 HTML 必须被清除
  assert(!codeEl.innerHTML.includes("<script") && codeEl.querySelectorAll("script").length === 0, "CD 高亮输出无注入");
  dom.window.close();
}

/** 用例 4：纯文本 —— 兜底模式 + textContent 赋值。 */
async function testPlainText() {
  const dom = await loadViewPage("case-tx", {
    status: 200,
    body: {
      code: "case-tx",
      content: "今天天气很好，我们出去玩吧。<b>这不是加粗</b>",
      expires_at: null,
      remaining_views: null,
      created_at: "2026-08-13T08:00:00",
    },
  });
  const doc = dom.window.document;
  const contentEl = doc.getElementById("content");
  assert(doc.querySelector('#mode-tabs .nav-link[data-mode="text"]').classList.contains("active"), "TX 自动识别为纯文本");
  assert(contentEl.textContent === "今天天气很好，我们出去玩吧。<b>这不是加粗</b>", "TX 原文不丢失");
  assert(contentEl.querySelectorAll("b").length === 0, "TX 未作为 HTML 解析（textContent 赋值）");
  dom.window.close();
}

/** 用例 5：手动切换标签页 —— Markdown 内容切到「文本」模式。 */
async function testManualTabSwitch() {
  const dom = await loadViewPage("case-sw", {
    status: 200,
    body: {
      code: "case-sw",
      content: "# 标题\n**加粗**",
      expires_at: null,
      remaining_views: null,
      created_at: "2026-08-13T08:00:00",
    },
  });
  const doc = dom.window.document;
  doc.querySelector('#mode-tabs .nav-link[data-mode="text"]').click();
  const contentEl = doc.getElementById("content");
  assert(contentEl.textContent.includes("# 标题\n**加粗**"), "SW 切换到文本模式原文展示");
  assert(contentEl.querySelectorAll("h1").length === 0, "SW 文本模式无 HTML 渲染");
  dom.window.close();
}

/** 用例 6/7/8：三种错误 type 的友好错误页。 */
async function testErrorPage(type, expectedTitle) {
  const dom = await loadViewPage(`err-${type}`, {
    status: type === "share_not_found" ? 404 : 410,
    body: { type, title: "占位", status: type === "share_not_found" ? 404 : 410, detail: "占位" },
  });
  const doc = dom.window.document;
  assert(!doc.getElementById("view-error").classList.contains("d-none"), `ERR[${type}] 错误页显示`);
  assert(doc.getElementById("error-title").textContent === expectedTitle, `ERR[${type}] 标题正确`);
  dom.window.close();
}

/** 文件分享的 fetch 桩：文本端点 404 share_not_found → 文件端点返回罐装元数据。 */
function fileResponder(meta) {
  return (url) => {
    if (url.includes("/api/v1/shares/")) {
      return {
        status: 404,
        body: { type: "share_not_found", title: "分享不存在", status: 404, detail: "短码不存在" },
      };
    }
    return { status: 200, body: meta };
  };
}

/**
 * 用例 9：文件双探针 —— 文本端点 404 后探测文件端点，渲染文件卡片
 * （文件名 / 大小格式化 / 元信息 / 加密徽章 / 预览按钮可见性）。
 */
async function testFileCardRender() {
  const meta = {
    code: "file1",
    kind: "file",
    original_name: "报告.md",
    size_bytes: 2048,
    encrypted: false,
    content_type: "text/markdown",
    preview_available: true,
    expires_at: null,
    remaining_views: 5,
    created_at: "2026-08-13T08:00:00",
  };
  const dom = await loadViewPage("file1", fileResponder(meta));
  const doc = dom.window.document;
  assert(!doc.getElementById("view-file").classList.contains("d-none"), "FILE 文件卡片显示");
  assert(doc.getElementById("file-name").textContent === "报告.md", "FILE 文件名渲染");
  assert(doc.getElementById("file-size").textContent === "2.0 KB", "FILE 大小格式化（2048 → 2.0 KB）");
  assert(doc.getElementById("file-meta-created").textContent.includes("创建于"), "FILE 创建时间渲染");
  assert(doc.getElementById("file-meta-expires").textContent.includes("永久"), "FILE 永久有效期");
  assert(doc.getElementById("file-meta-views").textContent.includes("剩余 5 次"), "FILE 剩余次数渲染");
  assert(doc.getElementById("file-encrypted-badge").classList.contains("d-none"), "FILE 未加密无徽章");
  assert(!doc.getElementById("file-preview-btn").classList.contains("d-none"), "FILE 预览按钮可见");
  assert(doc.getElementById("file-preview-area").classList.contains("d-none"), "FILE 预览区默认隐藏");
  dom.window.close();
}

/**
 * 用例 10：加密文件卡片 —— 加密徽章显示、预览按钮隐藏
 * （encrypted 否决预览，preview_available=false 双保险）。
 */
async function testFileCardEncrypted() {
  const meta = {
    code: "file2",
    kind: "file",
    original_name: "secret.bin",
    size_bytes: 10240,
    encrypted: true,
    content_type: "application/octet-stream",
    preview_available: false,
    expires_at: null,
    remaining_views: null,
    created_at: "2026-08-13T08:00:00",
  };
  const dom = await loadViewPage("file2", fileResponder(meta));
  const doc = dom.window.document;
  assert(!doc.getElementById("view-file").classList.contains("d-none"), "ENC 加密文件卡片显示");
  assert(doc.getElementById("file-name").textContent === "secret.bin", "ENC 文件名渲染");
  assert(doc.getElementById("file-size").textContent === "10.0 KB", "ENC 大小格式化");
  assert(!doc.getElementById("file-encrypted-badge").classList.contains("d-none"), "ENC 加密徽章显示");
  assert(doc.getElementById("file-preview-btn").classList.contains("d-none"), "ENC 加密隐藏预览按钮");
  assert(doc.getElementById("file-meta-views").textContent.includes("不限"), "ENC 不限访问次数");
  dom.window.close();
}

(async () => {
  try {
    console.log("== M4 前端浏览器级冒烟 + v0.2 文件卡片（jsdom + 真实服务器资源）==");
    await testMarkdownXssSanitized();
    await testJsonDetection();
    await testCodeDetection();
    await testPlainText();
    await testManualTabSwitch();
    await testErrorPage("share_not_found", "分享不存在");
    await testErrorPage("share_expired", "分享已过期");
    await testErrorPage("share_views_exhausted", "分享访问次数已耗尽");
    await testFileCardRender();
    await testFileCardEncrypted();
    console.log(`\n结果: ${passed} passed, ${failed} failed`);
    process.exit(failed === 0 ? 0 : 1);
  } catch (err) {
    console.error("冒烟脚本异常:", err);
    process.exit(1);
  }
})();
