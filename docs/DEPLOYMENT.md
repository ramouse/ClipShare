# ClipShare 生产部署手册（从零到上线）

> 适用版本：M6 生产部署基础设施（docker-compose.prod.yml / conf/nginx.conf / scripts/deploy.sh / scripts/backup.sh）
> 目标读者：负责把系统部署到公网服务器的同学。本手册按「首次从零上线」顺序撰写，各章节可独立查阅。

## 0. 架构总览

```
浏览器 ──HTTP/HTTPS──▶ nginx:80/443（反向代理，唯一公网入口）
                          │ proxy_pass
                          ▼
                    app:8000（FastAPI，生产镜像 INSTALL_DEV=false）
                          │
                          ▼
              db:5432（PostgreSQL 16，持久卷 pgdata）
```

- **只有 nginx 暴露宿主机端口**（80，443 待 HTTPS 后启用）；app/db 在 Docker 内部网络互访，公网不可直连数据库；
- **生产镜像不挂载源码**：运行的是构建时的源码快照，更新代码 = 重建镜像；
- **全部密钥在服务器 `.env`**：`docker-compose.prod.yml` 用 `${VAR:?错误信息}` 强制校验，缺失直接报错拒绝启动；
- **项目名 `clipshare-prod`**：与开发环境（clipshare）物理隔离，本机同时跑两个环境互不影响。

## 1. 服务器选购与安全组

- 推荐 **2 vCPU / 2 GB 内存 / 40 GB SSD / 1 Mbps+ 带宽** 起步（本项目很轻，2C2G 足够；验收期流量小，无需高配）；
- 系统推荐 **Ubuntu 22.04 / 24.04 LTS**（本手册命令均按 Ubuntu 编写）；
- 云厂商控制台 **安全组/防火墙放行**：`22`（SSH）、`80`（HTTP）、`443`（HTTPS，配置后）；
  **其余端口一律不放行**（含 5432——数据库绝不暴露公网）；
- 记下公网 IP：无域名时可直接 `http://IP` 访问（最低要求）；有域名走 §5 HTTPS。

## 2. 安装 Docker 与 Compose

```bash
# 官方一键脚本（已包含 Compose 插件）
curl -fsSL https://get.docker.com | sh
# 把当前用户加入 docker 组（免 sudo 执行 docker，重登生效）
sudo usermod -aG docker $USER
# 验证
docker --version
docker compose version
```

国内网络拉 Docker Hub 镜像慢/失败时的缓解（本项目 postgres/nginx 基础镜像可能用到）：

```bash
# 方式 A（推荐，一劳永逸）：Docker 配置 registry-mirrors
sudo tee /etc/docker/daemon.json <<'EOF'
{ "registry-mirrors": ["https://docker.m.daocloud.io"] }
EOF
sudo systemctl restart docker

# 方式 B（临时）：从镜像源拉取后改名
docker pull docker.m.daocloud.io/library/nginx:alpine
docker tag docker.m.daocloud.io/library/nginx:alpine nginx:alpine
```

## 3. 克隆仓库与配置 .env

```bash
git clone git@github.com:ramouse/ClipShare.git   # 或 https 方式
cd ClipShare
cp .env.prod.example .env
vim .env        # 逐项填写，见下方变量说明
```

### 3.1 变量逐项说明

| 变量 | 必填 | 说明 |
|------|------|------|
| `POSTGRES_USER` | 是 | 数据库用户，默认 `clipshare` 即可 |
| `POSTGRES_PASSWORD` | 是 | **数据库密码，必须替换为随机强密码**。生成：`openssl rand -hex 32`（纯十六进制，无 URL 特殊字符，可直接嵌入连接串）。**禁止沿用开发环境的 `clipshare` 弱口令** |
| `POSTGRES_DB` | 否 | 数据库名，默认 `clipshare` |
| `DATABASE_URL` | 是 | 应用连接串，指向本编排的 db 服务，模板已给出；**内嵌密码必须与 `POSTGRES_PASSWORD` 一致**（改密码时两处同步改，否则应用连不上库，报 `password authentication failed`） |
| `PUBLIC_BASE_URL` | 是 | 分享链接前缀，与 `/s/{短码}` 拼接成分享链接。有域名填 `https://你的域名`，无域名填 `http://服务器IP` |
| `APP_NAME` | 否 | 应用名（页面/OpenAPI 标题），默认 ClipShare |
| `LOG_LEVEL` | 否 | 日志级别，默认 INFO；生产环境 structlog 输出 JSON |
| `SHARE_MAX_CONTENT_LENGTH` | 否 | 单条分享长度上限（字符），默认 100000 |
| `RATE_LIMIT_CREATE` | 否 | 创建接口限流，默认 `30/minute`（按 IP，内存瞬时计数） |
| `RATE_LIMIT_READ` | 否 | 读取接口限流，默认 `60/minute` |

> **安全红线**：`.env` 已被 `.gitignore` 排除（`.env.*` 全忽略，仅放行 `.env.prod.example` 模板），
> 任何人不得把 `.env` 提交、转发或发到群里。泄露口令 = 数据库裸奔。

## 4. 部署

### 4.1 一键部署（推荐）

```bash
bash scripts/deploy.sh
```

脚本自动完成 5 步（幂等，可重复执行）：

1. `git pull` 更新代码；
2. 校验 `.env` 与 compose 配置（`docker compose config -q`，缺失变量在此步报错）；
3. 构建生产镜像（`INSTALL_DEV=false`，无测试工具、无源码挂载）并 `up -d`；
4. 数据库迁移 `alembic upgrade head`（幂等，无新迁移时无操作）；
5. 健康检查：轮询 `http://127.0.0.1/healthz` 直到返回 200。

### 4.2 手工等价序列（调试/学习时用）

```bash
docker compose -f docker-compose.prod.yml config -q                              # 配置校验
docker compose -f docker-compose.prod.yml build app                              # 构建生产镜像
docker compose -f docker-compose.prod.yml up -d                                  # 启动全部服务
docker compose -f docker-compose.prod.yml run --rm app alembic upgrade head      # 数据库迁移
curl http://127.0.0.1/healthz                                                    # 健康检查
```

### 4.3 上线验证清单

```bash
curl http://服务器IP/healthz                    # 期望 {"status":"ok"}
curl http://服务器IP/docs                      # OpenAPI 交互文档可打开
# 浏览器创建一条分享 → 打开分享链接 → 二维码可扫（手机访问）
docker compose -f docker-compose.prod.yml ps   # 三个容器均 Up (healthy)
```

## 5. HTTPS（Let's Encrypt + certbot）

> 无域名可跳过本节（IP + HTTP 为最低验收线）；有域名强烈建议配置，隐私与防篡改双保险。

**前置**：域名 A 记录解析到本服务器（如 `paste.example.com → 服务器IP`）；安全组放行 80/443。

> ⚠️ 本架构 nginx 运行在容器内、配置文件只读挂载自仓库，**certbot 的 `--nginx` 插件不可用**（它会去读写宿主机 `/etc/nginx`，对容器无效）。必须使用 `certonly --webroot` 模式，证书落盘后由容器挂载使用。

```bash
# 1. 宿主机创建 webroot 目录并安装 certbot（不装 python3-certbot-nginx，nginx 在容器里）
sudo mkdir -p /var/www/certbot
sudo apt update && sudo apt install -y certbot

# 2. 让 nginx 暴露 ACME 校验路由 + 挂载证书与 webroot：
#    a) conf/nginx.conf 的 80 段内临时加入：
#       location /.well-known/acme-challenge/ { root /var/www/certbot; }
#    b) docker-compose.prod.yml 的 nginx 服务：取消 "- 443:443" 注释，并挂载
#       - /var/www/certbot:/var/www/certbot:ro
#       - /etc/letsencrypt:/etc/letsencrypt:ro
docker compose -f docker-compose.prod.yml up -d

# 3. 签发证书（webroot 模式：certbot 向 nginx 容器回源校验，不改动任何配置）
sudo certbot certonly --webroot -w /var/www/certbot -d paste.example.com
# 成功后证书位于 /etc/letsencrypt/live/paste.example.com/

# 4. 启用 443：取消 conf/nginx.conf 文末 443 server 段的注释（按实际域名替换 server_name），
#    然后校验并重载
docker compose -f docker-compose.prod.yml exec nginx nginx -t
docker compose -f docker-compose.prod.yml exec nginx nginx -s reload
```

**certbot 自动续期**：`certonly` 模式同样会安装系统级续期任务（新版 Ubuntu 为 systemd timer：`systemctl list-timers | grep certbot`），到期前 30 天自动续签。**续签成功后需自动重载 nginx**——创建部署钩子：

```bash
sudo mkdir -p /etc/letsencrypt/renewal-hooks/deploy
sudo tee /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh > /dev/null <<'EOF'
#!/bin/sh
docker compose -f /path/to/docker-compose.prod.yml exec nginx nginx -s reload
EOF
sudo chmod +x /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh

# 演练续期（不实际改证书，验证配置与钩子无误）
sudo certbot renew --dry-run
```

**签发后必须复核**（M6 安全清单）：

- `conf/nginx.conf` 中 `access_log off;` 仍在（隐私红线）；
- `proxy_set_header X-Forwarded-For $remote_addr;` 仍在（**覆盖**而非追加，防限流绕过，见 §8.1）；
- `PUBLIC_BASE_URL` 已改为 `https://你的域名` 并 `docker compose -f docker-compose.prod.yml up -d` 重启生效。

## 6. 备份与恢复

### 6.1 手动备份

```bash
bash scripts/backup.sh            # 生成 clipshare-YYYYMMDD-HHMMSS.sql.gz，保留最近 7 份
bash scripts/backup.sh 14         # 自定义保留 14 份
```

备份文件默认放在仓库同级目录 `clipshare-backups/`（可用 `BACKUP_DIR` 环境变量改到独立磁盘）。

### 6.2 cron 自动备份（推荐）

```bash
crontab -e
# 每天 02:30 备份，保留 7 份，日志落盘
30 2 * * * cd /opt/clipshare && bash scripts/backup.sh 7 >> /var/log/clipshare-backup.log 2>&1
```

### 6.3 恢复

```bash
# 1. 停止应用写入（可选）
docker compose -f docker-compose.prod.yml stop app
# 2. 回放备份（将 BACKUP_FILE 换成目标备份文件）
gunzip -c "$BACKUP_FILE" | docker compose -f docker-compose.prod.yml exec -T -e PGPASSWORD=数据库密码 db \
  psql -U 数据库用户 -d 数据库名
# 3. 校验并重启
docker compose -f docker-compose.prod.yml start app
```

**建议**：备份文件定期 rsync/scp 到异地（另一台机器/对象存储），防止服务器磁盘故障连备份一起丢。

## 7. 日志查看

```bash
docker compose -f docker-compose.prod.yml logs -f app      # 应用日志（production 下 JSON）
docker compose -f docker-compose.prod.yml logs -f nginx    # nginx 错误日志
docker compose -f docker-compose.prod.yml logs -f db       # 数据库日志
docker compose -f docker-compose.prod.yml logs --tail=100 app   # 只看末尾 100 行
```

**隐私说明**：nginx 已 `access_log off`（不记录任何访问日志，含客户端 IP）；应用层 uvicorn 以
`--no-access-log` 启动、structlog 只记事件（短码/状态）不记 IP；限流仅内存瞬时计数不落盘。
所有容器日志经 json-file 驱动限容（单文件 10MB、保留 3 份），不会无限膨胀。

## 8. 安全清单（M6 落实项）

| # | 项 | 落实位置 |
|---|----|---------|
| 1 | **XFF 覆盖**：nginx 用 `$remote_addr` 覆盖客户端伪造的 `X-Forwarded-For`，slowapi 限流 key 不可被伪造绕过 | conf/nginx.conf `location /` 段 |
| 2 | **生产 access log 关闭**：nginx `access_log off`，不记录客户端 IP（隐私红线） | conf/nginx.conf 顶部 |
| 3 | **uvicorn 双保险**：生产 CMD 带 `--no-access-log` | docker-compose.prod.yml app.command |
| 4 | 密钥环境变量化，缺失即拒绝启动（`${VAR:?}`）；.env 不入库 | docker-compose.prod.yml + .gitignore |
| 5 | 生产镜像无测试/检查工具、无源码挂载；仅 nginx 暴露端口 | docker-compose.prod.yml |
| 6 | 日志限容（10MB×3）；数据库持久卷；健康检查（Dockerfile + pg_isready） | docker-compose.prod.yml |
| 7 | 数据库不暴露公网；安全组仅放行 22/80/443 | 云厂商控制台（§1） |

## 9. 回滚

### 9.1 代码回滚

```bash
cd /opt/clipshare
git log --oneline -10                # 找要回退的提交
git checkout <目标提交哈希>          # 代码回退（或 git revert 生成反向提交）
bash scripts/deploy.sh               # 重建镜像并重新部署（build app 会用当前代码）
```

### 9.2 数据库回滚（谨慎，先备份）

```bash
# 查看迁移历史与当前版本
docker compose -f docker-compose.prod.yml run --rm app alembic history
docker compose -f docker-compose.prod.yml run --rm app alembic current
# 回退一步（回退到上一版本，版本号从 history 输出取）
docker compose -f docker-compose.prod.yml run --rm app alembic downgrade <上一版本号>
```

> 回滚原则：**先备份再回滚**（§6.1）；数据库回滚只应出现在「新迁移引入了破坏性变更」时，
> 日常发版建议「只回退代码、不回退数据库」。

## 10. 常见故障排查

| 症状 | 排查步骤 |
|------|---------|
| `docker compose ... config` 报变量错误 | 提示缺失的变量未在 `.env` 填写；对照 §3.1 补齐 |
| nginx 启动失败 `bind() to 0.0.0.0:80 failed` | 端口被占：`ss -tlnp \| grep :80`，停掉占用进程或改映射端口 |
| app 容器一直 unhealthy / 重启循环 | `docker compose logs app` 看启动日志；多半是连不上库（见下两行） |
| `password authentication failed` | `DATABASE_URL` 内嵌密码与 `POSTGRES_PASSWORD` 不一致（§3.1）；或改了密码未重建 db 卷 |
| `could not translate host name "db"` | app 连库用的 `@db:5432` 来自 compose 网络；确认 app/db 在同一 compose 项目（不要用 `docker run` 单跑） |
| 镜像拉取失败/超时 | 配置 registry-mirrors 或镜像源 retag（§2 方式 B） |
| 迁移失败 `Target database is not up to date` | 先 `alembic upgrade head` 再 `alembic check`；迁移报错看 `alembic history/current` |
| 全站 429 或被误限流 | 确认 nginx `X-Forwarded-For` 是**覆盖**（`$remote_addr`）而非追加（§8 项 1）；调高 `RATE_LIMIT_*` 后 `up -d` 重启 |
| 分享链接拼出来是 `http://localhost:8000` | `PUBLIC_BASE_URL` 没配或改后没重启 app；`up -d` 让新环境变量生效 |
| 磁盘被日志撑满 | 日志已限容；再查 `docker system df`，确认没有手动用 `-v` 挂大目录 |
| 部署后页面 502/超时 | `docker compose ps` 看 app 是否 healthy；`logs --tail=100 app nginx` 看具体错误 |

## 11. 后续演进（M7 待办，本手册仅为记录）

- **jsdom 前端冒烟接入 CI**：`tests/e2e/frontend_smoke.js` 目前依赖宿主机手动执行，
  M7 计划引入 `package.json`（jsdom 依赖）并在 `.github/workflows/ci.yml` 增加前端冒烟 job
  （开发方案 §6 待办「M6/M7 jsdom 冒烟接入 CI」，实现留 M7）；
- **Starlette 弃用警告评估**：pytest 输出的 httpx 相关弃用警告为上游依赖行为（TestClient 内部），
  M6 评估是否升级 starlette/httpx，升级需全量回归；
- 静态资源缓存策略：当前静态文件由 app 直出、无 nginx 层缓存（conf/nginx.conf 有取舍说明），
  流量增长后可把 static 提取为卷 + nginx 直读 + 缓存头。

## 12. 变更记录

| 日期 | 内容 |
|------|------|
| 2026-08-13 | 初版：M6 前半（生产 compose、nginx 反代、部署/备份脚本、本手册） |
