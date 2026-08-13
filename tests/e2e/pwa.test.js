/* v0.2 PWA 冒烟测试（宿主机 node 执行，需容器内服务运行中）：
 * 1. manifest.webmanifest 字段契约校验（PWA 安装所需字段齐全且值正确）；
 * 2. 从 sw.js 正则提取 PRECACHE_URLS 手写清单，逐 URL httpGet 断言 200
 *    —— SW 清单漂移头号防线：新增/删除静态资源后清单未同步时立即红灯；
 * 3. 三枚图标（192/512/maskable-512）200；
 * 4. 红线回归：PRECACHE_URLS 中绝不允许出现 /s/* 与 /api/*（计数与新鲜度语义）。
 * 运行：node tests/e2e/pwa.test.js
 */
"use strict";

const http = require("http");

const BASE = "http://localhost:8000";

/** 拉取真实服务器资源（非 200 不抛错，交由断言判定，便于聚合输出）。 */
function httpGet(url) {
  return new Promise((resolve, reject) => {
    const req = http.get(url, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => resolve({ status: res.statusCode, body: Buffer.concat(chunks) }));
    });
    req.on("error", reject);
  });
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

/** 用例 1：manifest 字段契约。 */
async function testManifestFields() {
  const res = await httpGet(`${BASE}/manifest.webmanifest`);
  assert(res.status === 200, "MANIFEST /manifest.webmanifest 返回 200");
  const manifest = JSON.parse(res.body.toString("utf8"));
  assert(manifest.name === "ClipShare 云剪切板", "MANIFEST name = ClipShare 云剪切板");
  assert(manifest.short_name === "ClipShare", "MANIFEST short_name = ClipShare");
  assert(manifest.start_url === "/", "MANIFEST start_url = /");
  assert(manifest.display === "standalone", "MANIFEST display = standalone");
  assert(manifest.background_color === "#ffffff", "MANIFEST background_color = #ffffff");
  assert(manifest.theme_color === "#0d6efd", "MANIFEST theme_color = #0d6efd");
  // 图标声明：192/512 any + 512 maskable
  const purposes = manifest.icons.map((i) => `${i.sizes}:${i.purpose || "any"}`);
  assert(
    purposes.includes("192x192:any") && purposes.includes("512x512:any"),
    "MANIFEST 图标含 192/512 any"
  );
  assert(purposes.includes("512x512:maskable"), "MANIFEST 图标含 512 maskable");
  // 声明的图标 src 必须可访问（双保险：清单声明与文件实体一致）
  for (const icon of manifest.icons) {
    const iconRes = await httpGet(`${BASE}${icon.src}`);
    assert(iconRes.status === 200, `MANIFEST 图标可访问 ${icon.src}`);
  }
}

/** 用例 2：SW 清单漂移检查——PRECACHE_URLS 逐个断言 200。 */
async function testPrecacheUrls() {
  const sw = await httpGet(`${BASE}/static/sw.js`);
  assert(sw.status === 200, "SW /static/sw.js 返回 200");
  const swText = sw.body.toString("utf8");
  // 正则提取手写清单（与 sw.js 实际格式强绑定，清单结构变更时本测试也会亮红灯）
  const match = swText.match(/const PRECACHE_URLS\s*=\s*\[([\s\S]*?)\];/);
  assert(Boolean(match), "SW 可正则提取 PRECACHE_URLS 清单");
  const urls = match ? [...match[1].matchAll(/"([^"]+)"/g)].map((m) => m[1]) : [];
  assert(urls.length >= 15, "SW 清单 URL 数量 ≥ 15（防止正则误提取空清单）");
  // 逐个请求断言 200：任何静态资源变更后清单未同步，此处立即失败
  for (const url of urls) {
    const res = await httpGet(`${BASE}${url}`);
    assert(res.status === 200, `SW 预缓存 URL 200 ${url}`);
  }
  // 红线回归：清单中绝不允许出现 /s/* 与 /api/*
  const forbidden = urls.filter((u) => u.startsWith("/s/") || u.startsWith("/api/"));
  assert(forbidden.length === 0, `SW 清单不含 /s/* 与 /api/*（当前命中: ${forbidden.join(", ") || "无"}）`);
  // 关键策略守卫存在性抽查（代码契约，防止策略被误删）
  assert(swText.includes('pathname.startsWith("/s/")'), "SW 含 /s/ 直连守卫");
  assert(swText.includes('pathname.startsWith("/api/")'), "SW 含 /api/ 直连守卫");
  assert(swText.includes("skipWaiting"), "SW 含 skipWaiting（安装即接管）");
  assert(swText.includes("clients.claim"), "SW 含 clients.claim（激活即控制）");
}

/** 用例 3：三枚图标实体 200 + PNG 魔数。 */
async function testIconFiles() {
  const icons = ["/static/icons/icon-192.png", "/static/icons/icon-512.png", "/static/icons/maskable-512.png"];
  for (const path of icons) {
    const res = await httpGet(`${BASE}${path}`);
    assert(res.status === 200, `ICON ${path} 返回 200`);
    // PNG 魔数校验：89 50 4E 47
    assert(
      res.body.length > 8 && res.body.subarray(0, 4).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47])),
      `ICON ${path} 为合法 PNG`
    );
  }
}

(async () => {
  try {
    console.log("== v0.2 PWA 冒烟（manifest + SW 预缓存清单 + 图标）==");
    await testManifestFields();
    await testPrecacheUrls();
    await testIconFiles();
    console.log(`\n结果: ${passed} passed, ${failed} failed`);
    process.exit(failed === 0 ? 0 : 1);
  } catch (err) {
    console.error("冒烟脚本异常:", err);
    process.exit(1);
  }
})();
