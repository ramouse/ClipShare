#!/usr/bin/env bash
# =============================================================================
# ClipShare 生产一键部署脚本（在目标服务器执行，幂等，可重复执行）
#
# 流程：更新代码 → 校验 .env/compose 配置 → 构建并启动 → 数据库迁移 → 健康检查
# 用法：
#   bash scripts/deploy.sh            （普通发版/首次部署）
#   可选环境变量：APP_DIR、GIT_BRANCH、HEALTH_URL 等，见下方可配置区
# 前置条件：
#   1. 服务器已安装 Docker + Compose v2（官方脚本：curl -fsSL https://get.docker.com | sh）
#   2. 仓库已克隆到服务器（git clone git@github.com:ramouse/ClipShare.git）
#   3. .env 已按 .env.prod.example 填写（cp .env.prod.example .env 后逐项修改）
# 说明：脚本在 Windows 上开发，目标机为 Linux；执行时用 bash 前缀即可，
#       不依赖脚本可执行位（chmod +x 亦可）。
# =============================================================================
set -euo pipefail

# ===== 可配置区（按需修改，或用环境变量覆盖） =====
# 仓库目录（默认取脚本上级目录，即仓库根）
APP_DIR="${APP_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.prod.yml}"
GIT_REMOTE="${GIT_REMOTE:-origin}"
GIT_BRANCH="${GIT_BRANCH:-main}"
# 健康检查 URL（nginx 对外入口 80 端口；healthz 路径由 nginx 转发到 app）
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1/healthz}"
# 健康检查最多等待轮数（每轮 2 秒）
HEALTH_RETRY="${HEALTH_RETRY:-30}"
# ==================================================

cd "$APP_DIR"

echo "[1/5] 更新代码：git pull ${GIT_REMOTE}/${GIT_BRANCH}"
# 幂等：已是最新时 git pull 无操作。首次部署请先 git clone（或 scp/tar 上传源码后跳过本步）
git fetch "$GIT_REMOTE"
git checkout "$GIT_BRANCH" >/dev/null 2>&1 || true   # 已在该分支时 checkout 报错属正常，忽略
git pull --ff-only "$GIT_REMOTE" "$GIT_BRANCH"

echo "[2/5] 校验 .env 与 compose 配置"
# 密钥红线：.env 缺失或关键变量缺失时，config 阶段即报错，拒绝带病部署
[ -f .env ] || { echo "错误：缺少 .env —— 请先 cp .env.prod.example .env 并填写" >&2; exit 1; }
docker compose -f "$COMPOSE_FILE" config -q

echo "[3/5] 构建并启动（生产镜像：INSTALL_DEV=false，无测试工具、无源码挂载）"
docker compose -f "$COMPOSE_FILE" build app
docker compose -f "$COMPOSE_FILE" up -d

echo "[4/5] 执行数据库迁移（alembic upgrade head，幂等：无新迁移时无操作）"
docker compose -f "$COMPOSE_FILE" run --rm app alembic upgrade head

echo "[5/5] 健康检查：$HEALTH_URL"
for i in $(seq 1 "$HEALTH_RETRY"); do
    if curl -fsS "$HEALTH_URL" >/dev/null 2>&1; then
        echo "部署成功：健康检查通过（$HEALTH_URL 返回 200）"
        exit 0
    fi
    sleep 2
done

echo "部署失败：$HEALTH_URL 在 $((HEALTH_RETRY * 2)) 秒内未就绪" >&2
echo "排查指引：docker compose -f $COMPOSE_FILE ps  查看状态" >&2
echo "          docker compose -f $COMPOSE_FILE logs --tail=100 app nginx  查看日志" >&2
exit 1
