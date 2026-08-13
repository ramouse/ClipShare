# 项目说明（CLAUDE.md）

## 项目概述
这是一个 Python + FastAPI 的云剪切板分享系统（pastebin 类）项目，比特工场 2026 暑期技能提升项目。
目标：匿名文本分享 + 有效期/访问次数控制 + 自研创新点（E2E 加密、CLI 工具），按项目书特等奖标准交付。
当前阶段：M1-M6 已完成（脚手架/领域与数据层/REST API/Web 前端/E2E 加密+CLI/生产部署基础设施），M7 文档交付收尾中（README/API 文档/开发心得/演示脚本/成员贡献 + 前端冒烟接入 CI）。

## 技术栈
- 运行时：Python 3.12（全部在 Docker 容器内运行，本机无需装 Python）
- 框架：FastAPI（自动生成 OpenAPI 文档）
- 数据库：PostgreSQL 16 + SQLAlchemy 2.0 + Alembic 迁移
- 前端：Bootstrap 5 + 原生 JS（marked / highlight.js / DOMPurify）
- 测试：pytest + pytest-cov（单元 / 集成 / E2E 三层）
- 代码风格：ruff + mypy strict（配置见 pyproject.toml）

## 目录结构
app/
├── main.py              # 入口：应用工厂 create_app()
├── api/                 # 表现层
│   └── routes/
│       ├── health.py    # 健康检查 /healthz
│       └── shares.py    # 分享 API（M3）
├── core/                # 配置与横切关注点
│   ├── config.py        # pydantic-settings 配置
│   └── logging.py       # structlog 结构化日志
├── domain/              # 领域层：纯业务逻辑（M2）
├── db/                  # 数据层：会话与 Repository（M2）
├── schemas/             # Pydantic 请求/响应模型（M3）
├── services/            # 应用服务编排（M3）
├── templates/           # Jinja2 页面（M4）
└── static/              # 前端静态资源（M4）
tests/
├── unit/                # 领域逻辑单元测试
├── integration/         # API + DB 集成测试
└── e2e/                 # 端到端测试

## 命名规范
- 文件名：snake_case（如 share_service.py）
- 变量/函数：snake_case
- 类：PascalCase（如 ShareRepository）
- 常量：UPPER_SNAKE_CASE（如 MAX_CONTENT_LENGTH）
- 路由文件按资源名命名（如 shares.py、health.py）

## 重要约定
- 所有接口返回 JSON；错误统一 Problem Details 风格：{ "type", "title", "status", "detail" }（M3 起）
- HTTP 状态码语义准确：创建成功 201、参数错误 422、资源不存在 404
- 分层依赖单向：api → services → domain，禁止路由文件直接操作数据库
- 每个路由文件只处理一种资源
- 时间约定：全链路统一 naive UTC（应用与 DB 容器均 UTC 时钟），禁止混用 aware datetime
- Commit 遵循 Conventional Commits，格式 `<type>(<scope>): <subject>`：
  - type：feat / fix / refactor / test / docs / chore / ci
  - scope：模块号（M1–M7）或分层（api / domain / db）
  - **subject 与 body 用中文撰写**，type 保留英文；subject 祈使句、≤50 字符；body 说明"为什么"与影响范围
  - 示例：`feat(M3): 新增创建分享接口`
  - 每个模块原子提交，保证 git log 可完整追溯开发过程

## 禁止事项
- 不用旧版兼容写法，按 3.12 标准写（如 `list[str]` 而非 `List[str]`）
- 禁止在 async 路由里放阻塞调用（DB 访问走同步 def 路由或线程池）
- 禁止将用户输入直接 innerHTML 渲染（必须转义 / DOMPurify 消毒）
- 禁止用自增 ID 作分享链接（用 secrets 随机 Base62 短码）
- 禁止将密钥、密码写入代码或提交仓库（一律环境变量）
- 不记录用户个人信息与 IP（项目书隐私红线，服务端强制）

## 启动方式（全部在容器内执行）
docker compose up -d --build           # 启动开发环境（http://localhost:8000）
docker compose run --rm app pytest     # 运行测试
docker compose run --rm app ruff check .   # 代码检查
docker compose run --rm app mypy app cli   # 类型检查（app 与 cli 两个包）

## 测试接口
docker compose run --rm app pytest         # 全部测试
docker compose run --rm app alembic check  # 迁移与模型一致性
curl http://localhost:8000/healthz         # 健康检查冒烟（返回 {"status":"ok"}）
curl http://localhost:8000/docs            # OpenAPI 交互文档
npm install && npm run e2e                 # M7：前端冒烟（jsdom）+ 加密 E2E（宿主机，服务器运行中）
