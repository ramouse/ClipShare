# ClipShare API 文档

> 本文档为书面版 API 文档（与代码一一对应）。应用运行时可通过 `/docs`（OpenAPI/Swagger UI）查看自动生成的交互式文档，二者关系见文末 §7。
> 若本文档与代码不一致，以代码为准。

- Base URL：`/api/v1`
- 所有请求/响应均为 JSON（`application/json`），UTF-8 编码；唯一例外是 `raw`/`qr`/`preview`/`download` 端点（见端点表）
- 所有响应（含错误响应）均携带安全响应头：`X-Content-Type-Options: nosniff`、`X-Frame-Options: DENY`、`Referrer-Policy: no-referrer`、`Content-Security-Policy`（详见 app/core/security.py）

## 1. 端点总览

| 方法 | 路径 | 说明 | 成功响应 |
|------|------|------|----------|
| POST | `/api/v1/shares` | 创建分享 | `201` JSON |
| GET | `/api/v1/shares/{code}` | 读取分享（消耗 1 次访问次数） | `200` JSON |
| GET | `/api/v1/shares/{code}/raw` | 读取分享原始文本（消耗 1 次，语义同读取接口） | `200` text/plain |
| GET | `/api/v1/shares/{code}/qr` | 分享二维码 PNG（不消耗次数、不判过期，仅校验存在） | `200` image/png |
| POST | `/api/v1/files` | 上传文件（multipart，流式） | `201` JSON |
| GET | `/api/v1/files/{code}` | 文件元数据（不消耗次数） | `200` JSON |
| GET | `/api/v1/files/{code}/preview` | 文本预览（消耗 1 次，截断读头部） | `200` text/plain |
| GET | `/api/v1/files/{code}/download` | 下载文件（消耗 1 次，流式输出） | `200` 文件流 |
| GET | `/healthz` | 健康检查（容器探活/部署验证） | `200` JSON |

页面路由（**不在 OpenAPI 中**，`include_in_schema=False`）：

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/` | 创建分享页面（HTML） |
| GET | `/s/{code}` | 查看分享页面 shell（HTML；任意短码均返回同一 shell，内容与错误由前端 JS 调 API 渲染） |
| GET | `/manifest.webmanifest` | PWA 应用清单（v0.2-E，`application/manifest+json`） |
| GET | `/sw.js` | Service Worker 注册入口（v0.2-E，带 `Service-Worker-Allowed: /` 头） |

## 2. 创建分享

### POST /api/v1/shares

请求体：

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `content` | string | 是 | 分享内容，长度 1 ～ 100000 字符（上限由 `SHARE_MAX_CONTENT_LENGTH` 配置，默认 100000） |
| `expiry` | string | 否 | 有效期档位：`1h` / `24h`（默认）/ `7d` / `forever` |
| `max_views` | int \| null | 否 | 访问次数上限：`1` / `5` / `null`（null = 不限，默认） |

成功响应 `201`：

```json
{
  "code": "AbCdEf",
  "url": "http://localhost:8000/s/AbCdEf",
  "expires_at": "2026-08-14T08:13:00.430978",
  "max_views": 5,
  "created_at": "2026-08-13T08:13:00.430978"
}
```

| 字段 | 说明 |
|------|------|
| `code` | 6 位 Base62 短码（`secrets` 安全随机生成，不可猜测；唯一索引兜底碰撞） |
| `url` | 分享网页地址：`public_base_url` + `/s/{code}`（部署时由 `PUBLIC_BASE_URL` 环境变量配置） |
| `expires_at` | 到期时间（naive UTC）；`forever` 时返回 `null` |
| `max_views` | 与请求一致；不限时返回 `null` |

### curl 示例

```bash
curl -s -X POST http://localhost:8000/api/v1/shares \
  -H "Content-Type: application/json" \
  -d '{"content":"你好，ClipShare","expiry":"24h","max_views":5}'
```

> Windows 控制台（PowerShell/CMD）内联中文 JSON 可能被代码页（GBK）破坏导致 400，建议载荷写进 UTF-8 文件再传：`curl -s -X POST ... --data-binary @payload.json`。

## 3. 读取分享

### GET /api/v1/shares/{code}

`{code}` 为 6 位 Base62 短码。读取成功会**消耗一次访问次数**。

成功响应 `200`：

```json
{
  "code": "AbCdEf",
  "content": "你好，ClipShare",
  "expires_at": "2026-08-14T08:13:00.430978",
  "remaining_views": 4,
  "created_at": "2026-08-13T08:13:00.430978"
}
```

`remaining_views`：剩余可访问次数；`max_views` 为 null（不限）时返回 `null`；已超限返回 `0`。

### GET /api/v1/shares/{code}/raw

返回原始文本（`text/plain; charset=utf-8`），供 curl / CLI 直接取内容。语义与读取接口完全一致：消耗次数、不存在 404、过期/超次 410。

```bash
curl -s http://localhost:8000/api/v1/shares/AbCdEf/raw
```

### curl 示例（读取）

```bash
curl -s http://localhost:8000/api/v1/shares/AbCdEf
```

### curl 示例（二维码）

```bash
# 返回 PNG 图片；内容为分享网页地址（扫码打开分享页）
curl -s http://localhost:8000/api/v1/shares/AbCdEf/qr -o qr.png
```

## 4. 文件分享（v0.2）

> 文件分享是文本分享的独立资源类型（`shares` / `share_files` 两套业务表，短码经中心登记表
> `shortcodes` 跨类型唯一——同一短码在文本与文件端点之间数学上无歧义，见「系统设计」§3.6）。
> 所有文件端点均需 POST/GET multipart 或纯路径访问，**不消耗文本分享的限流预算**
> （独立 key：`upload` / `file_read` / `file_download`）。

### 4.1 上传文件

### POST /api/v1/files

请求体（`multipart/form-data`）：

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `file` | file | 是 | 文件内容（**流式上传**：服务端 64KB 逐块落盘并计长，超限即断并清理半成品） |
| `expiry` | string | 否 | 有效期档位：`1h` / `24h`（默认）/ `7d` / `forever` |
| `max_views` | string | 否 | 访问次数上限：`1` / `5` / 空串（空串 = 不限，默认；预览与下载共享次数池） |
| `encrypted` | bool | 否 | 是否 E2E 加密（默认 false）。**仅 ≤10MB**（`FILE_ENCRYPT_MAX_SIZE`）：浏览器全内存加密的固有代价，超限返回 422（`file_encrypt_not_available`），前端隐藏开关 + 服务端双保险 |

限制：

- 单文件上限 100MB（`FILE_MAX_SIZE`，默认值）；nginx 已配套 `client_max_body_size 110m`、
  读写超时 300s（`conf/nginx.conf`），超上限返回 413 `file_too_large`，且**磁盘不残留半成品**；
- 扩展名白名单：`FILE_ALLOWED_EXTENSIONS`（默认见 `app/core/config.py`，覆盖 txt/md/代码/文档/图片/压缩/音视频等），
  不在白名单返回 415 `file_type_not_allowed`（白名单按**净化后**的文件扩展名判定）；
- 用户文件名**只存元数据**：磁盘文件名由服务端 `secrets.token_hex(16)` 生成（无扩展名），
  路径遍历攻击无效（`sanitize_filename` 纯函数净化 + `resolve/is_relative_to` 双重防御）。

成功响应 `201`：

```json
{
  "code": "AbCdEf",
  "url": "http://localhost:8000/api/v1/files/AbCdEf",
  "original_name": "报告.pdf",
  "size_bytes": 1048576,
  "encrypted": false,
  "expires_at": "2026-08-14T08:13:00.430978",
  "max_views": 5,
  "created_at": "2026-08-13T08:13:00.430978"
}
```

### curl 示例（上传）

```bash
curl -s -X POST http://localhost:8000/api/v1/files \
  -F "file=@./报告.pdf;type=application/pdf" \
  -F "expiry=24h" -F "max_views=5"
```

### 4.2 读取文件元数据

### GET /api/v1/files/{code}

**不消耗**预览/下载次数（与文本分享二维码同语义——前端双探针先探此端点渲染文件卡片）。

成功响应 `200`：

```json
{
  "code": "AbCdEf",
  "kind": "file",
  "original_name": "报告.pdf",
  "size_bytes": 1048576,
  "encrypted": false,
  "content_type": "application/pdf",
  "preview_available": false,
  "expires_at": "2026-08-14T08:13:00.430978",
  "remaining_views": 4,
  "created_at": "2026-08-13T08:13:00.430978"
}
```

| 字段 | 说明 |
|------|------|
| `kind` | 固定 `"file"`：前端与文本分享分流依据之一 |
| `preview_available` | 是否可预览：**未加密** + 大小 ≤ 200KB（`FILE_PREVIEW_MAX_SIZE`） + 扩展名在预览白名单（`FILE_PREVIEW_EXTENSIONS`，默认代码/文本类） |
| `remaining_views` | 预览 + 下载**共享次数池**的剩余次数；不限次返回 `null` |

### 4.3 文本预览

### GET /api/v1/files/{code}/preview

- 返回文件**头部**最多 200KB 字节（截断读，绝不全量读入内存——流式红线）；
- 判定顺序：元数据（404/410，不消耗）→ 可预览性（415，不消耗）→ 消耗一次预览计数 → 截断读盘；
- 加密文件、超预览上限、扩展名不在预览白名单 → 415 `preview_not_available`；
- 磁盘文件缺失（记录尚在）→ 410 `file_content_missing`（预检前置，不白白消耗次数）。

```bash
curl -s http://localhost:8000/api/v1/files/AbCdEf/preview
```

### 4.4 下载文件

### GET /api/v1/files/{code}/download

- 消耗一次下载计数（与预览共享次数池，守卫式原子自增下推 SQL，并发不超卖）；
- 中文文件名经 RFC 5987 `filename*` 编码（Content-Disposition），浏览器按原名保存；
- `FileResponse` 流式输出：大文件不整读进内存（流式红线）。

```bash
curl -s http://localhost:8000/api/v1/files/AbCdEf/download -o 报告.pdf
```

### 4.5 文件加密语义（E2E 加密文件）

- 与文本加密同一密钥链路：密文 = `ENC1:` 标记 + IV + cipher，密钥仅存于分享链接 fragment（`#k=…`）；
- 文件内容为**字节级**加密（`encryptBytes/decryptBytes`，Uint8Array 输入输出，明文二进制不经过字符串层）；
- 服务端只存密文（集成测试做磁盘字节级零明文断言）；加密文件**不可预览**——预览端点
  返回密文头部无意义，且浏览器无法解密截断的密文，故 `preview_available=false` 且端点直接 415；
- 加密文件下载后由浏览器解密再 `saveBlob` 保存；`clipshare get --output` 对文件短码下载**密文原样**，
  解密仍需浏览器（与文本分享一致：服务器协议上拿不到密钥）。

### 4.6 文件端点错误码（新增部分）

| type | HTTP | 触发场景 |
|------|------|----------|
| `file_not_found` | 404 | 短码在文件业务表不存在（前端双探针：文本端点 404 后再探文件端点） |
| `file_expired` | 410 | 文件已过期（访问时顺带懒删磁盘文件） |
| `file_views_exhausted` | 410 | 预览/下载共享次数池已耗尽 |
| `file_content_missing` | 410 | 数据库记录存在但磁盘文件缺失（如手工清理），预检前置不消耗次数 |
| `file_too_large` | 413 | 超过 100MB 上限（流式落盘中断且无残留） |
| `file_type_not_allowed` | 415 | 扩展名不在白名单 |
| `preview_not_available` | 415 | 加密 / 超预览上限 / 扩展名不在预览白名单 |
| `file_encrypt_not_available` | 422 | `encrypted=true` 但文件 >10MB 加密上限（服务端兜底） |

## 5. 统一错误模型

所有错误响应均为 RFC 9457 Problem Details 风格：

```json
{
  "type": "share_not_found",
  "title": "分享不存在",
  "status": 404,
  "detail": "短码 zzzz99 不存在"
}
```

| 字段 | 说明 |
|------|------|
| `type` | 稳定机器码，供客户端程序化判断（如前端按 type 渲染错误页） |
| `title` | 人类可读的简短标题 |
| `status` | HTTP 状态码 |
| `detail` | 补充说明（可含定位信息） |

> 全局异常处理器（app/core/errors.py）保证**所有响应均为 JSON**（含未预期异常，兜底为 500）。

### 错误码清单（type → 状态码 → 触发场景）

| type | HTTP | 触发场景 |
|------|------|----------|
| `share_not_found` | 404 | 短码不存在（读取 / raw / 二维码） |
| `share_expired` | 410 | 分享已过期 |
| `share_views_exhausted` | 410 | 分享访问次数已耗尽 |
| `validation_error` | 422 | 请求参数校验失败（content 缺失/超长/空、expiry 或 max_views 不在枚举内）；detail 汇总所有字段错误 |
| `http_error` | 404/405 等 | 路由不存在、方法不允许等框架级错误 |
| `rate_limited` | 429 | 速率限制（附 `Retry-After` 响应头） |
| `internal_error` | 500 | 未预期异常（兜底） |
| `shortcode_generation_failed` | 500 | 短码连续 5 次冲突（理论概率极低） |

### curl 示例（错误）

```bash
# 404：短码不存在
curl -s http://localhost:8000/api/v1/shares/zzzz99

# 410：过期 / 超次
curl -s http://localhost:8000/api/v1/shares/AbCdEf

# 422：非法参数
curl -s -X POST http://localhost:8000/api/v1/shares \
  -H "Content-Type: application/json" \
  -d '{"content":"x","expiry":"3h"}'

# 429：超过速率限制（响应头含 Retry-After）
curl -s -i -X POST http://localhost:8000/api/v1/shares \
  -H "Content-Type: application/json" \
  -d '{"content":"x"}'
```

## 6. 速率限制

- **额度**（按客户端 IP，slowapi 内存型存储，进程内瞬时计数、重启清零）：
  - 创建：`RATE_LIMIT_CREATE`，默认 `30/minute`
  - 读取（含 raw/qr）：`RATE_LIMIT_READ`，默认 `60/minute`
  - 文件（v0.2，独立预算与文本互不影响）：上传 `RATE_LIMIT_UPLOAD`（默认 `30/minute`）、
    元数据/预览 `RATE_LIMIT_FILE_READ`（`60/minute`）、下载 `RATE_LIMIT_FILE_DOWNLOAD`（`60/minute`）
- 超限返回 `429` Problem Details（type=`rate_limited`），并携带 `Retry-After` 头指示秒数。
- 限流检查在路由函数内执行；FastAPI 的请求体校验发生在调用路由函数**之前**——校验失败（422）在限流检查前返回，**不消耗限流额度**（实测：连续 30 次 422 后合法请求仍返回 201）。
- **部署注意**：slowapi 信任 `X-Forwarded-For` 首个 IP。生产环境反向代理必须用 `$remote_addr` **覆盖**该头（conf/nginx.conf 已落实，勿改为追加），否则客户端可伪造 IP 绕过限流。
- **隐私说明**：IP 仅用于限流判定（内存瞬时计数），不落日志、不落库（隐私红线）。

## 7. 端到端加密与 API 的关系

端到端加密（创新点 A）**完全不改变 API 契约**——服务器对密文透明：

1. 浏览器用 WebCrypto AES-256-GCM 加密明文，得到密文标记串：

   ```
   ENC1:<iv_b64url>.<cipher_b64url>
   ```

   - `ENC1:` 为版本前缀（密文识别标记；未来升级格式用 `ENC2:` 等新前缀，旧数据仍可解析）
   - `iv`：12 字节随机数（每次加密不同，同一密钥多次加密结果互不相同）
   - `cipher`：含 16 字节 GCM 认证标签（密钥错误/篡改时解密直接报错）
   - 密钥：32 字节随机数，base64url 编码后拼入分享链接 fragment（`#k=…`）——**fragment 不随 HTTP 请求发送，服务器协议上拿不到密钥**

2. 前端将密文串作为 `content` 走普通 `POST /api/v1/shares`；服务器按普通字符串存储，创建/读取/计数/过期/限流全部零改动。

3. 查看页读到 `ENC1:` 前缀的内容时，从链接 fragment 提取密钥在浏览器内解密；密钥缺失/错误/密文损坏时渲染对应错误页（前端本地构造 type：`share_encrypted` / `key_invalid`，不经后端）。

注意：

- **服务器只保存密文**（数据库层零明文，有集成测试断言）；密文对服务器不可读，加密内容无法通过 API 在服务端解密。
- 密文约膨胀为原文 1.4 倍（base64url 编码 4/3 + 前缀与 IV 开销）——加密分享的内容长度需按密文长度校验（前端在加密后、发送前本地校验，超限给出友好提示而非服务端 422）。
- 分享链接中的密钥必须完整复制（含 `#` 之后部分）；二维码只编码不含密钥的 URL，加密分享不能靠扫码传递密钥。
- **文件加密（v0.2）**：字节级加解密（`encryptBytes/decryptBytes`，见 §4.5），仅 ≤10MB（服务端 422 兜底）；加密文件不可预览、`clipshare get --output` 下载密文原样。

## 8. 与 /docs（OpenAPI）的关系

- `/docs` 由 FastAPI 从路由代码自动生成（Swagger UI，可在线调试），列出端点、请求/响应 schema、参数——它是**机器可读契约的交互形态**。
- 本文档是**书面版**：补充了 OpenAPI 无法表达的内容——统一错误模型与全部错误码语义（§5）、文件分享业务语义（§4：计数语义/预览规则/加密语义）、速率限制策略与部署注意（§6）、E2E 加密与 API 的关系（§7）、业务语义（页面 shell 路由不计次数、二维码不耗次数等）、curl 使用示例与平台注意事项。
- 两者同源于代码：路由定义在 `app/api/routes/shares.py`、`app/api/routes/files.py`（挂载前缀 `/api/v1`）、请求/响应模型在 `app/schemas/share.py`、`app/schemas/file.py`、错误模型在 `app/core/errors.py`。若发现不一致，以代码为准。
