# ClipShare API 文档

> 本文档为书面版 API 文档（与代码一一对应）。应用运行时可通过 `/docs`（OpenAPI/Swagger UI）查看自动生成的交互式文档，二者关系见文末 §7。
> 若本文档与代码不一致，以代码为准。

- Base URL：`/api/v1`
- 所有请求/响应均为 JSON（`application/json`），UTF-8 编码；唯一例外是 `raw` 与 `qr` 两个端点（见端点表）
- 所有响应（含错误响应）均携带安全响应头：`X-Content-Type-Options: nosniff`、`X-Frame-Options: DENY`、`Referrer-Policy: no-referrer`、`Content-Security-Policy`（详见 app/core/security.py）

## 1. 端点总览

| 方法 | 路径 | 说明 | 成功响应 |
|------|------|------|----------|
| POST | `/api/v1/shares` | 创建分享 | `201` JSON |
| GET | `/api/v1/shares/{code}` | 读取分享（消耗 1 次访问次数） | `200` JSON |
| GET | `/api/v1/shares/{code}/raw` | 读取分享原始文本（消耗 1 次，语义同读取接口） | `200` text/plain |
| GET | `/api/v1/shares/{code}/qr` | 分享二维码 PNG（不消耗次数、不判过期，仅校验存在） | `200` image/png |
| GET | `/healthz` | 健康检查（容器探活/部署验证） | `200` JSON |

页面路由（**不在 OpenAPI 中**，`include_in_schema=False`）：

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/` | 创建分享页面（HTML） |
| GET | `/s/{code}` | 查看分享页面 shell（HTML；任意短码均返回同一 shell，内容与错误由前端 JS 调 API 渲染） |

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

## 4. 统一错误模型

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

## 5. 速率限制

- **额度**（按客户端 IP，slowapi 内存型存储，进程内瞬时计数、重启清零）：
  - 创建：`RATE_LIMIT_CREATE`，默认 `30/minute`
  - 读取（含 raw/qr）：`RATE_LIMIT_READ`，默认 `60/minute`
- 超限返回 `429` Problem Details（type=`rate_limited`），并携带 `Retry-After` 头指示秒数。
- 限流检查在路由函数内执行；FastAPI 的请求体校验发生在调用路由函数**之前**——校验失败（422）在限流检查前返回，**不消耗限流额度**（实测：连续 30 次 422 后合法请求仍返回 201）。
- **部署注意**：slowapi 信任 `X-Forwarded-For` 首个 IP。生产环境反向代理必须用 `$remote_addr` **覆盖**该头（conf/nginx.conf 已落实，勿改为追加），否则客户端可伪造 IP 绕过限流。
- **隐私说明**：IP 仅用于限流判定（内存瞬时计数），不落日志、不落库（隐私红线）。

## 6. 端到端加密与 API 的关系

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

## 7. 与 /docs（OpenAPI）的关系

- `/docs` 由 FastAPI 从路由代码自动生成（Swagger UI，可在线调试），列出端点、请求/响应 schema、参数——它是**机器可读契约的交互形态**。
- 本文档是**书面版**：补充了 OpenAPI 无法表达的内容——统一错误模型与全部错误码语义（§4）、速率限制策略与部署注意（§5）、E2E 加密与 API 的关系（§6）、业务语义（页面 shell 路由不计次数、二维码不耗次数等）、curl 使用示例与平台注意事项。
- 两者同源于代码：路由定义在 `app/api/routes/shares.py`（挂载前缀 `/api/v1`）、请求/响应模型在 `app/schemas/share.py`、错误模型在 `app/core/errors.py`。若发现不一致，以代码为准。
