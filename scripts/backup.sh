#!/usr/bin/env bash
# =============================================================================
# ClipShare 数据库备份脚本（在目标服务器执行，可被 cron 定时调用）
#
# 用法：
#   bash scripts/backup.sh              保留最近 7 份（默认）
#   bash scripts/backup.sh 14           保留最近 14 份
#   KEEP_N=14 bash scripts/backup.sh    同上（环境变量写法）
#
# cron 示例（每天 02:30 执行，日志追加到独立文件）：
#   30 2 * * * cd /opt/clipshare && bash scripts/backup.sh 7 >> /var/log/clipshare-backup.log 2>&1
#   （crontab -e 编辑；确认 cron 服务已启用：systemctl status cron）
#
# 备份内容：
#   1. PostgreSQL 逻辑备份（pg_dump），gzip 压缩，文件名带时间戳（clipshare-*.sql.gz）；
#   2. 文件分享卷快照（v0.2）：./data/files 目录存在且非空时打包
#      （clipshare-files-*.tar.gz），清理时与 SQL 备份分两组各保留 KEEP 份。
# 恢复说明见文件末尾注释与 docs/DEPLOYMENT.md §6.3。
# =============================================================================
set -euo pipefail

# ===== 可配置区 =====
# 仓库目录（默认取脚本上级目录，即仓库根）
APP_DIR="${APP_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.prod.yml}"
# 备份存放目录：默认放在仓库同级（仓库外），建议后续改到独立磁盘/挂载点
BACKUP_DIR="${BACKUP_DIR:-$(dirname "$APP_DIR")/clipshare-backups}"
# 保留份数：命令行参数 > KEEP_N 环境变量 > 默认 7
KEEP="${1:-${KEEP_N:-7}}"
# ====================

cd "$APP_DIR"

# 密钥红线：.env 缺失立即退出，不产出半成品备份
[ -f .env ] || { echo "错误：缺少 .env —— 备份终止" >&2; exit 1; }

# 从 .env 读取数据库连接信息（单行 KEY=VALUE 格式；值含 # 或引号时不适用，请保持 .env 为简单格式）
# 注意：pipefail 下 grep 未命中会返回 1 直接杀死脚本，必须 || true 兜底，
# 让下方「变量缺失」的友好报错路径生效（2026-08-13 生产实测踩坑）
get_env() { grep "^$1=" .env | head -1 | cut -d= -f2- || true; }
PGUSER="$(get_env POSTGRES_USER)"
PGDB="$(get_env POSTGRES_DB)"
PGPASSWORD="$(get_env POSTGRES_PASSWORD)"
if [ -z "$PGUSER" ] || [ -z "$PGDB" ] || [ -z "$PGPASSWORD" ]; then
    echo "错误：.env 缺少 POSTGRES_USER / POSTGRES_DB / POSTGRES_PASSWORD" >&2
    exit 1
fi

mkdir -p "$BACKUP_DIR"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$BACKUP_DIR/clipshare-$STAMP.sql.gz"

echo "[备份] 开始：pg_dump ${PGUSER}@db:5432/${PGDB} → $OUT"
# exec -T：禁用 TTY 以支持管道；PGPASSWORD 经 -e 注入容器进程（避免明文出现在命令行历史）
# 连接串内嵌密码若与 .env 的 POSTGRES_PASSWORD 不同步，可改用：
#   docker compose -f "$COMPOSE_FILE" exec -T db pg_dump -U "$PGUSER" -d "$PGDB"
# 但需在 db 容器环境已带密码的场景（本编排 db 服务的 POSTGRES_PASSWORD 已注入，可免 -e）
docker compose -f "$COMPOSE_FILE" exec -T -e PGPASSWORD="$PGPASSWORD" db \
    pg_dump -U "$PGUSER" -d "$PGDB" | gzip > "$OUT"

# 校验产物非空且非损坏（gzip -t 校验完整性；pg_dump 失败时 gzip 也可能输出非空文件，此步可发现异常）
gzip -t "$OUT"
if [ ! -s "$OUT" ]; then echo "错误：备份文件为空，请检查 pg_dump 输出" >&2; exit 1; fi
echo "[备份] 完成：$OUT（大小 $(du -h "$OUT" | cut -f1)）"

# 文件分享卷快照（v0.2）：data/files 目录存在且非空时才打包；
# tar -C 到目录内取相对路径（" ."），恢复时同样 -C 到目标目录展开
FILES_DIR="$APP_DIR/data/files"
if [ -d "$FILES_DIR" ] && [ -n "$(ls -A "$FILES_DIR" 2>/dev/null)" ]; then
    FILES_OUT="$BACKUP_DIR/clipshare-files-$STAMP.tar.gz"
    echo "[备份] 开始：文件卷快照（$FILES_DIR）→ $FILES_OUT"
    tar -czf "$FILES_OUT" -C "$FILES_DIR" .
    if [ ! -s "$FILES_OUT" ]; then echo "错误：文件快照为空，请检查 data/files 目录" >&2; exit 1; fi
    echo "[备份] 完成：$FILES_OUT（大小 $(du -h "$FILES_OUT" | cut -f1)）"
else
    echo "[备份] 跳过文件卷快照：data/files 不存在或为空（尚无文件分享数据）"
fi

# 清理：SQL 备份与文件快照两组分别按修改时间倒序保留最近 KEEP 份
# （时间戳文件名保证唯一，可重复执行；两组模式互不重叠：clipshare-*.sql.gz
#  不匹配 clipshare-files-*.tar.gz，各自独立计数）
for PATTERN in "clipshare-*.sql.gz" "clipshare-files-*.tar.gz"; do
    # pipefail 陷阱：ls 无匹配文件时退出码 2 会直接杀死脚本（与 get_env 同款，
    # 生产实测踩坑），必须 || true 兜底让"无备份可清理"成为正常路径
    COUNT=$(ls -1 "$BACKUP_DIR"/$PATTERN 2>/dev/null || true | wc -l)
    if [ "$COUNT" -gt "$KEEP" ]; then
        ls -1t "$BACKUP_DIR"/$PATTERN | tail -n +$((KEEP + 1)) | while IFS= read -r old; do
            echo "[清理] 删除过期备份：$old"
            rm -f "$old"
        done
    fi
done
SQL_COUNT=$(ls -1 "$BACKUP_DIR"/clipshare-*.sql.gz 2>/dev/null || true | wc -l)
FILES_COUNT=$(ls -1 "$BACKUP_DIR"/clipshare-files-*.tar.gz 2>/dev/null || true | wc -l)
echo "[备份] 结束：$SQL_COUNT 份 SQL 备份 + $FILES_COUNT 份文件快照，各组保留上限 $KEEP 份"

# =============================================================================
# 恢复说明（数据事故时使用，先备份再恢复）：
#
# 1) 停止应用写入（可选但推荐）：
#      docker compose -f docker-compose.prod.yml stop app
#
# 2) 恢复备份（选择目标备份文件）：
#      gunzip -c clipshare-20260813-023000.sql.gz \
#        | docker compose -f docker-compose.prod.yml exec -T -e PGPASSWORD="$(grep '^POSTGRES_PASSWORD=' .env | cut -d= -f2-)" \
#          db psql -U "$(grep '^POSTGRES_USER=' .env | cut -d= -f2-)" -d "$(grep '^POSTGRES_DB=' .env | cut -d= -f2-)"
#    说明：psql 回放的是 SQL 语句流，表会先 drop 再重建，可覆盖现有数据；
#    若需整库级原子替换，可用 createdb/dropdb 重建空库后再回放（先导出库内角色权限）。
#
# 3) 校验恢复结果并重启应用：
#      docker compose -f docker-compose.prod.yml exec -T db psql -U <user> -d <db> -c "SELECT count(*) FROM shares;"
#      docker compose -f docker-compose.prod.yml start app
#
# 4) 文件分享卷恢复（v0.2，与 SQL 备份配套）：
#      mkdir -p /opt/clipshare/data/files
#      tar -xzf clipshare-files-20260813-023000.tar.gz -C /opt/clipshare/data/files/
#    恢复后确认目录归属容器内 uid 1000（否则 app 容器写盘报 Permission denied）：
#      chown -R 1000:1000 /opt/clipshare/data/files
#
# 5) 建议：备份文件定期 rsync/scp 到异地或对象存储（防止服务器磁盘故障丢备份）。
# =============================================================================
