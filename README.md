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
- **企业级工程实践**：176 项 Python 测试 + 126 项 Node 浏览器/API E2E + 专用目标守卫 + ruff + mypy strict + GitHub Actions CI + Docker 一键部署

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

`clipshare` 是随项目安装的命令行工具（`[project.scripts]` 入口）。要求 Python 3.12
或更高版本，建议安装到虚拟环境中：

```bash
# Linux / macOS
python3.12 -m venv .venv
source .venv/bin/activate
python -m pip install .
```

```powershell
# Windows PowerShell：创建虚拟环境并安装
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install .

# 方式一：不激活虚拟环境，后续每次都使用完整路径
.\.venv\Scripts\clipshare.exe --help

# 方式二：激活虚拟环境，之后可以直接使用裸命令 clipshare
& .\.venv\Scripts\Activate.ps1
clipshare --help
```

> 如果 PowerShell 禁止运行 `Activate.ps1`，可以仅为当前 PowerShell 进程临时放开：
> `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`，然后重新执行激活命令。
> 关闭该 PowerShell 窗口后策略自动失效，不会永久修改系统执行策略。

以下示例假定 `clipshare` 已在当前 shell 中可用：Linux / macOS 已执行
`source .venv/bin/activate`，Windows 已执行 `& .\.venv\Scripts\Activate.ps1`。
如果 Windows 不激活虚拟环境，请将以下每条命令开头的 `clipshare` 替换为
`.\.venv\Scripts\clipshare.exe`。

```bash
# 创建文本分享：成功后输出 /s/{code} 网页链接
clipshare send "你好，ClipShare"
clipshare send "@notes.txt" --expiry 7d --max-views 5

# 上传文件：multipart 文件句柄流式发送，不整读进内存；上限 100MB
# 当前输出为 /api/v1/files/{code} 文件元数据 API 地址
clipshare upload ./report.pdf --expiry 7d --max-views 5

# 读取文本：接受短码或 /s/{code} 完整网页链接
clipshare get AbCdEf
clipshare get http://localhost:8000/s/AbCdEf

# 保存文本或下载文件：文件短码会自动回退探测文件端点
clipshare get AbCdEf --output ./report.pdf
clipshare get AbCdEf --output ./report.pdf --progress
```

> `clipshare get AbCdEf --output ./file` 对文本分享写 UTF-8 原文；对文件分享下载原文件
> （文本端点返回 404 `share_not_found` 时自动探测 `/api/v1/files/{code}`）。
> `--progress` 仅在下载文件时向 stderr 输出累计字节数。

参数与约定：

| 项 | 说明 |
|----|------|
| `send` 参数 | `TEXT\|@FILE`：直接传文本，或以 `@` 开头传 UTF-8 文件路径并把文件内容创建为文本分享 |
| `upload` 参数 | `PATH`：上传原始文件，流式 multipart 发送（大文件走 600s 放宽超时） |
| `get` 参数 | 裸短码或 `/s/{code}` 完整网页链接；完整链接只用于提取短码，不决定请求服务器 |
| `get --output` | 保存到文件：文本写 UTF-8 原文 / 文件流式写盘（走 600s 放宽超时） |
| `--expiry` | `1h` / `24h`（默认）/ `7d` / `forever` |
| `--max-views` | `1` / `5` / `0`（0 = 不限，默认） |
| `--base-url` | 请求服务器；优先级：`--base-url` > 环境变量 `CLIPSHARE_BASE_URL` > `http://localhost:8000` |
| 退出码 | `0` 成功 / `1` 网络或 API 错误 / `2` 参数错误 |
| 输出分流 | 分享链接与内容输出到 stdout；错误、保存路径提示与下载进度输出到 stderr |

### 连接远程服务器

完整分享链接只用于提取短码，CLI **不会**自动采用链接中的服务器地址。连接远程服务器时，
必须通过 `--base-url` 或 `CLIPSHARE_BASE_URL` 指定请求目标。

Linux / macOS：

```bash
export CLIPSHARE_BASE_URL=https://paste.example.com
clipshare send "跨机器分享"
clipshare get https://paste.example.com/s/AbCdEf
```

也可以在每次调用时显式传入服务器地址：

```bash
clipshare send "跨机器分享" --base-url https://paste.example.com
clipshare get https://paste.example.com/s/AbCdEf --base-url https://paste.example.com
```

Windows PowerShell：

```powershell
$env:CLIPSHARE_BASE_URL = "https://paste.example.com"
.\.venv\Scripts\clipshare.exe send "跨机器分享"
.\.venv\Scripts\clipshare.exe get "https://paste.example.com/s/AbCdEf"
```

### 文件上传后的读取

`upload` 当前原样输出后端的文件元数据 API 地址，例如
`https://paste.example.com/api/v1/files/AbCdEf`。`get` 的完整 URL 解析只接受
`/s/{code}` 网页链接，因此请从上传结果最后一段取得短码再下载：

```bash
file_api_url="$(clipshare upload ./report.pdf)"
code="${file_api_url##*/}"
clipshare get "$code" --output ./downloaded-report.pdf --progress
```

### 加密分享与 Docker 注意事项

- 当前 CLI 不支持 `send --encrypted` 或 `upload --encrypted`。加密分享通过 Web 页面创建和解密；
  `clipshare get` 读取加密文本时只会原样输出服务器保存的 `ENC1:…` 密文，不会使用链接中的
  `#k=` 密钥解密。
- 使用 `docker compose exec app clipshare ...` 时，CLI 与 Uvicorn 位于同一个 app 容器，默认
  `http://localhost:8000` 可以直接使用。
- 使用 `docker compose run --rm app clipshare ...` 创建独立临时容器时，`localhost` 指向临时
  容器自身，必须显式传入 `--base-url http://app:8000`。独立的 `docker run` 容器只有加入
  ClipShare Compose 网络后才能解析 `app` 服务名。

## 开发

`v0.3-G0` 测试隔离门已于 2026-08-23 验收通过。所有测试必须从唯一入口运行；禁止直接执行宿主 `pytest`、`npm run e2e` 或复用开发/生产数据库：

```powershell
# 快速验证双语言守卫及危险目标拒绝策略
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-sandbox.ps1 -Mode Guard

# 完整执行迁移、Ruff、mypy、pytest 和真实沙盒 HTTP E2E
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-sandbox.ps1 -Mode Full

# Windows/Android 合约测试：首次准备固定容器，后续只同步代码并离线执行
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-clients.ps1 -Mode Clients
```

脚本使用随机 `clipshare-g0-*` 项目、内部 Docker 网络、临时数据库和仓库内 `.sandbox/<run-id>` 运行目录；不挂载 `clipshare_pgdata`，不映射宿主端口，不执行全局 `prune`。退出时精确清理本次资源并核对运行前后 Docker 库存；只保留已被 `.gitignore` 排除的日志与证据。详见 [G0 验收记录](docs/依据/v0.3-G0-测试沙盒验收记录.md) 和 [客户端开发实施方案](docs/客户端开发实施方案.md) §7。

客户端入口固定复用 `clipshare-test-dotnet`、`clipshare-test-kotlin`。两个容器都以
`network=none` 执行测试，每轮只更新容器内 `/work/current`，完成后停止；只有基础镜像、
Dockerfile 或工具链版本变化时才显式传入 `-Rebuild`。该入口不会启动或修改开发用
`clipshare-app-1`、`clipshare-db-1`，也不会操作其他项目容器。

## 部署

当前 Web/CLI 生产环境：

- 地址：https://47.120.13.250
- 系统：阿里云 Ubuntu 22.04
- 部署：Docker Compose + Nginx
- HTTPS：Let's Encrypt / IP 证书
- 生产环境 `PUBLIC_BASE_URL=https://47.120.13.250`

生产部署（Nginx 反向代理 + HTTPS + 备份）见 [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)。

## 文档

- 书面 API 文档（端点/参数/错误码/curl 示例）：[docs/API.md](docs/API.md)
- 交互式 API 文档：应用运行后访问 `/docs`（OpenAPI）
- 部署手册：[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)

## 许可证

MIT
