# ClipShare — 轻量级云剪切板分享系统

[![CI](https://github.com/ramouse/ClipShare/actions/workflows/ci.yml/badge.svg)](https://github.com/ramouse/ClipShare/actions)

> 比特工场 2026 暑期技能提升项目。跨设备快速分享文本/代码片段：打开网页 → 粘贴 → 生成链接/二维码 → 任何设备打开即取。

## 功能特性

- **完全匿名**：无需注册登录，不留存任何个人信息（不记录 IP、不写访问日志）
- **有效期控制**：1 小时 / 24 小时 / 7 天 / 永久，过期禁止访问
- **访问次数限制**：1 次 / 5 次 / 无限制，超限禁止访问（数据库层原子判定，并发不超卖）
- **分享链接 + 二维码**：多人可同时获取内容
- **内容智能识别**：纯文本 / Markdown / 代码高亮 / JSON，支持手动切换渲染模式
- **端到端加密分享**：AES-256-GCM 浏览器加密，服务器零明文，密钥仅在链接 fragment（`#k=…`）中
- **文件分享（v0.2）**：上传代码/文档/截图等文件（上限 100MB），全链路流式传输；≤10MB 支持 E2E 加密；文本预览 / 下载 / 过期懒删
- **安卓 PWA（v0.2）**：可安装到主屏幕、独立窗口打开，离线可用首页壳（静态资源缓存）
- **CLI 快速分享工具**：`clipshare send` / `clipshare upload` / `clipshare get`，终端即可分享与读取
- **企业级工程实践**：三层测试（155 用例）+ ruff + mypy strict + GitHub Actions CI + Docker 一键部署

## 技术栈

| 层 | 技术 |
|----|------|
| 后端 | Python 3.12 · FastAPI · SQLAlchemy 2.0 · Alembic |
| 数据库 | PostgreSQL 16 |
| 前端 | Bootstrap 5 · 原生 JS · marked · highlight.js · DOMPurify |
| 部署 | Docker · Docker Compose · Nginx 反向代理 · GitHub Actions CI |

## 快速开始

```bash
docker compose up -d --build
# 打开 http://localhost:8000
```

手机访问同一地址（需 HTTPS 或 localhost，PWA 安全上下文要求）：
用 Chrome/Edge 打开页面 → 浏览器菜单「添加到主屏幕」→ 桌面生成 ClipShare 图标，
独立窗口打开、静态资源离线可用。

## CLI 用法

`clipshare` 是随项目安装的命令行工具（`[project.scripts]` 入口，`pip install .` 后即可用）。

```bash
# 发送：输出分享链接
clipshare send "你好，ClipShare"
clipshare send @notes.txt --expiry 7d --max-views 5

# 上传文件：流式上传（64KB 分块，不整读进内存），上限 100MB；≤10MB 支持 --encrypted
clipshare upload ./report.pdf --expiry 7d --max-views 5
clipshare upload ./secret.bin --encrypted --expiry 1h

# 读取：输出分享内容（短码或完整链接均可）
clipshare get AbCdEf
clipshare get http://localhost:8000/s/AbCdEf

# 读取到文件：get 自动回退探测文件端点，--output 按字节流式写盘
clipshare get AbCdEf --output ./report.pdf
```

> `clipshare get AbCdEf --output ./file` 对文本分享写 UTF-8 原文；对文件分享下载原文件
> （短码 404 且 type=share_not_found 时自动回退探测 `/api/v1/files/{code}`，见 docs/API.md §文件分享）。

参数与约定：

| 项 | 说明 |
|----|------|
| `send` 参数 | `TEXT\|@FILE`：直接传文本，或以 `@` 开头传文件路径（UTF-8） |
| `upload` 参数 | `PATH`：文件路径，流式 multipart 上传（大文件走 600s 放宽超时） |
| `--encrypted` | `upload` 专用：E2E 加密（≤10MB，超限服务端 422 拒绝） |
| `get --output` | 保存到文件：文本写 UTF-8 原文 / 文件流式写盘（走 600s 放宽超时） |
| `--expiry` | `1h` / `24h`（默认）/ `7d` / `forever` |
| `--max-views` | `1` / `5` / `0`（0 = 不限，默认） |
| `--base-url` | 服务器地址；优先级：`--base-url` > 环境变量 `CLIPSHARE_BASE_URL` > `http://localhost:8000` |
| 退出码 | `0` 成功 / `1` 网络或 API 错误 / `2` 参数错误 |
| 输出分流 | 分享链接与内容输出到 stdout，错误信息输出到 stderr |

示例（指向远程服务器）：

```bash
clipshare send "跨机器分享" --base-url https://paste.example.com
clipshare get https://paste.example.com/s/AbCdEf
```

注意事项：

- 加密分享的内容在服务器上保存为密文标记串（`ENC1:…`），`clipshare get` 原样输出该密文，解密需在浏览器中用带密钥（`#k=`）的完整链接打开；
- 在 Docker 容器内使用 CLI 时，`localhost:8000` 指向容器自身，需显式 `--base-url http://app:8000`（compose 服务名）。

## 开发

```bash
docker compose run --rm app pytest         # 测试（三层：单元/集成/E2E）
docker compose run --rm app ruff check .   # 代码检查
docker compose run --rm app mypy app cli   # 类型检查（strict）
docker compose run --rm app ruff format .  # 格式化
docker compose run --rm app alembic check  # 迁移与模型一致性检查

# 前端冒烟（宿主机，需 Node ≥ 20 且服务器运行中）
npm install
npm run e2e     # jsdom 前端冒烟 + 端到端加密 E2E
```

## 部署

> **当前状态**：✅ 已部署上线 — **http://47.120.13.250**（阿里云 Ubuntu 22.04，Nginx + Docker 生产环境，健康检查通过）。部署手册见下方链接。

生产部署（Nginx 反向代理 + HTTPS + 备份）见 [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)。

## 文档

- 书面 API 文档（端点/参数/错误码/curl 示例）：[docs/API.md](docs/API.md)
- 交互式 API 文档：应用运行后访问 `/docs`（OpenAPI）
- 部署手册：[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)
- 开发心得：[docs/开发心得.md](docs/开发心得.md)
- 成员贡献：[docs/成员贡献说明.md](docs/成员贡献说明.md)
- 演示视频脚本：[docs/演示视频脚本.md](docs/演示视频脚本.md)
- 学习手册（逐模块技术要点与踩坑记录，位于项目外目录）：`clipshare-docs/学习手册/M1～M7`

## 许可证

MIT
