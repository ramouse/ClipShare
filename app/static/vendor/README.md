# 前端第三方库（vendored，运行时零 CDN 依赖）

M4 约定：前端页面在浏览器加载时不访问任何外部域名（项目书「运行时零 CDN 依赖」），
以下库在构建期下载后随仓库提交，由 `/static/vendor/` 原样服务。

| 文件 | 库与版本 | 来源 | 说明 |
|------|----------|------|------|
| `bootstrap.min.css` | Bootstrap 5.3.3 | jsdelivr `npm/bootstrap@5.3.3/dist/css/bootstrap.min.css` | 框架样式 |
| `bootstrap.bundle.min.js` | Bootstrap 5.3.3 | jsdelivr `npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js` | 含 Popper，本页面暂未用到其 JS 组件 |
| `marked.min.js` | marked 12.0.2 | jsdelivr `npm/marked@12.0.2/marked.min.js` | Markdown 渲染 |
| `highlight.min.js` | highlight.js 11.9.0 | cdnjs `ajax/libs/highlight.js/11.9.0/highlight.min.js` | 代码高亮，**common 构建**（36 种流行语言，见下） |
| `highlight-github.min.css` | highlight.js 11.9.0（github 主题） | cdnjs `ajax/libs/highlight.js/11.9.0/styles/github.min.css` | 代码高亮主题 |
| `dompurify.min.js` | DOMPurify 3.1.5 | jsdelivr `npm/dompurify@3.1.5/dist/purify.min.js` | HTML 消毒（XSS 红线） |

## 许可证

- Bootstrap 5.3.3：MIT（© 2011-2024 The Bootstrap Authors）
- marked 12.0.2：MIT（© 2011-2024 Christopher Jeffrey）
- highlight.js 11.9.0：BSD-3-Clause（© 2006-2023 highlight.js 贡献者）
- DOMPurify 3.1.5：Apache-2.0（© Cure53 等贡献者）

## 版本选择说明（2026-08-13 实测）

- **highlight.js 采用 common 构建而非全量构建**：11.x 的 npm 包与主流 CDN
  已不再提供全量 192 语言的 UMD 单文件（`highlight.min.js` 从 npm 包中移除，
  cdnjs 11.9.0 根目录文件实测为 common 构建，36 种语言）。全量构建仅剩
  jsdelivr `+esm` 动态打包产物（ES Module、约 1.1MB）——本项目前端为
  普通 script 架构且面向手机端，common 构建（121KB）覆盖 python/javascript/
  typescript/go/rust/java/c/cpp/sql/html/css 等主流语言，对剪切板场景足够；
  需要全量语言时再评估 ESM 化改造（M5/M7 议题）。
- 升级方式：替换文件 + 更新本表版本号与来源，并复跑
  `tests/integration/test_pages.py::test_static_vendor_assets_served`。
