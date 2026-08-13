# ClipShare — 轻量级云剪切板分享系统

[![CI](https://github.com/ramouse/ClipShare/actions/workflows/ci.yml/badge.svg)](https://github.com/ramouse/ClipShare/actions)

> 比特工场 2026 暑期技能提升项目。跨设备快速分享文本/代码片段：打开网页 → 粘贴 → 生成链接/二维码 → 任何设备打开即取。

## 功能特性

- **完全匿名**：无需注册登录，不留存任何个人信息
- **有效期控制**：1 小时 / 24 小时 / 7 天 / 永久，过期禁止访问
- **访问次数限制**：1 次 / 5 次 / 无限制，超限禁止访问
- **分享链接 + 二维码**：多人可同时获取内容
- **内容智能识别**：纯文本 / Markdown / 代码高亮 / JSON（开发中）
- **端到端加密分享**：服务器零明文（开发中）
- **CLI 快速分享工具**（开发中）

## 技术栈

| 层 | 技术 |
|----|------|
| 后端 | Python 3.12 · FastAPI · SQLAlchemy 2.0 · Alembic |
| 数据库 | PostgreSQL 16 |
| 前端 | Bootstrap 5 · 原生 JS · marked · highlight.js · DOMPurify |
| 部署 | Docker · Docker Compose · GitHub Actions CI |

## 快速开始

```bash
docker compose up -d --build
# 打开 http://localhost:8000
```

## 开发

```bash
docker compose run --rm app pytest         # 测试
docker compose run --rm app ruff check .   # 代码检查
docker compose run --rm app mypy app       # 类型检查
docker compose run --rm app ruff format .  # 格式化
```

## 文档

- API 文档：应用运行后访问 `/docs`（OpenAPI）
- 部署文档：`docs/DEPLOYMENT.md`（M7 交付）
- 开发方案与路线图：`E:\project\clipshare-docs\开发方案.md`

## 许可证

MIT
