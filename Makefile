# ClipShare 开发命令（Linux/macOS/Git Bash；Windows 亦可直接执行等价的 docker compose 命令）
.PHONY: up down lint format test logs ps

up:            ## 构建并启动开发环境
	docker compose up -d --build

down:          ## 停止并移除容器
	docker compose down

lint:          ## 代码检查（ruff + mypy，容器内执行）
	docker compose run --rm app ruff check .
	docker compose run --rm app mypy app cli

format:        ## 自动格式化（容器内执行）
	docker compose run --rm app ruff format .

test:          ## 运行全部测试（容器内执行）
	docker compose run --rm app pytest

logs:          ## 跟踪应用日志
	docker compose logs -f app

ps:            ## 查看容器状态
	docker compose ps
