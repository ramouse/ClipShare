/* PWA 入口（v0.2）：Service Worker 注册。
 *
 * - 独立外部文件满足 CSP script-src 'self'（页面禁内联脚本，见 app/core/security.py）；
 * - 注册延迟到 window load：不与首屏渲染竞争带宽；
 * - /sw.js 由页面路由返回（带 Service-Worker-Allowed: / 响应头，见 app/api/routes/pages.py），
 *   使 SW 作用域覆盖全站（若用默认作用域 /static/，将无法控制 "/" 页面，离线壳失效）；
 * - 注册失败静默忽略：PWA 是渐进增强，SW 不可用不影响站点正常使用。
 */
"use strict";

window.addEventListener("load", () => {
  if ("serviceWorker" in navigator) {
    navigator.serviceWorker.register("/sw.js").catch(() => {
      // 静默：仅关闭离线壳能力，不做任何页面级反馈
    });
  }
});
