/* ClipShare Service Worker（v0.2 PWA 离线壳）
 *
 * 缓存策略（红线）：
 *   - /static/* 与 /manifest.webmanifest：缓存优先（离线可用），未命中回源并写入缓存；
 *   - "/" 首页：网络优先（内容可能变化），离线时回退缓存的页面壳；
 *   - /s/* 与 /api/*：直连绝不缓存——访问计数语义与内容新鲜度依赖每次真实请求，
 *     缓存会导致「次数不消耗 / 内容过期」的坏行为（pwa.test.js 同时回归此红线）。
 *   - 跨域请求与非 GET 请求：一律直连不处理。
 *
 * 清单漂移防线：PRECACHE_URLS 为手写清单，新增/删除静态资源时必须同步维护，
 * tests/e2e/pwa.test.js 会正则提取本清单并逐个请求断言 200。
 * 静态资源内容变更时递增 CACHE_VERSION 即可全量刷新（旧缓存 activate 阶段清理）。
 */
"use strict";

const CACHE_VERSION = "v1";
const CACHE_NAME = `clipshare-${CACHE_VERSION}`;

// 预缓存清单（手写，必须与仓库实际静态资源一致，pwa.test.js 逐个断言 200）
const PRECACHE_URLS = [
  "/",
  "/manifest.webmanifest",
  "/static/icons/icon-192.png",
  "/static/icons/icon-512.png",
  "/static/icons/maskable-512.png",
  "/static/css/style.css",
  "/static/js/app.js",
  "/static/js/view.js",
  "/static/js/crypto.js",
  "/static/js/pwa.js",
  "/static/vendor/bootstrap.min.css",
  "/static/vendor/bootstrap.bundle.min.js",
  "/static/vendor/marked.min.js",
  "/static/vendor/highlight.min.js",
  "/static/vendor/highlight-github.min.css",
  "/static/vendor/dompurify.min.js",
];

// 安装阶段：预缓存全部清单；预缓存完成后立即接管（skipWaiting），
// 用户下次打开页面即为新版本，无需等待旧 SW 控制期结束
self.addEventListener("install", (event) => {
  event.waitUntil(
    caches
      .open(CACHE_NAME)
      .then((cache) => cache.addAll(PRECACHE_URLS))
      .then(() => self.skipWaiting())
  );
});

// 激活阶段：清掉旧版本缓存（只保留当前 CACHE_NAME），并让本 SW 立即
// 控制已打开的页面（clients.claim），保证离线壳第一时间生效
self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys
            .filter((key) => key.startsWith("clipshare-") && key !== CACHE_NAME)
            .map((key) => caches.delete(key))
        )
      )
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", (event) => {
  const { request } = event;
  // 仅处理同源 GET：跨域资源与非 GET 请求一律直连，不进入缓存逻辑
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  const pathname = url.pathname;

  // 红线：/s/* 与 /api/* 直连不缓存（见文件头注释）
  if (pathname.startsWith("/s/") || pathname.startsWith("/api/")) return;

  if (pathname === "/") {
    // 首页：网络优先；断网时回退缓存的页面壳（PWA 离线打开能力）
    event.respondWith(fetch(request).catch(() => caches.match("/")));
    return;
  }

  if (pathname.startsWith("/static/") || pathname === "/manifest.webmanifest") {
    // 静态资源：缓存优先，未命中回源并写入缓存（下次离线可用）
    event.respondWith(
      caches.match(request).then((cached) => {
        if (cached) return cached;
        return fetch(request).then((response) => {
          if (response.ok) {
            // 写入缓存前克隆：响应体只能消费一次
            const copy = response.clone();
            caches.open(CACHE_NAME).then((cache) => cache.put(request, copy));
          }
          return response;
        });
      })
    );
  }
  // 其余同源 GET（/docs、/healthz、/openapi.json 等）：直连不缓存
});
