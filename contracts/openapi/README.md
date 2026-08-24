# ClipShare OpenAPI v1 contract

`clipshare-v1.openapi.json` 是 Windows、Android 与 Web 共用的 API v1 冻结快照。
应用仍从 FastAPI 路由生成运行时 schema；沙盒集成测试把实际 schema 写到
`$CLIPSHARE_CONTRACT_ARTIFACT_DIR/clipshare-v1.openapi.actual.json`，并逐字节比较
版本化快照。任何有意变更必须同时评估兼容性、更新 API 契约版本和客户端。

关键扩展字段：

- `x-clipshare-consumes-view`：调用是否消耗一次访问次数；
- `x-clipshare-auto-retry`：`safe`、`idempotency-key` 或 `forbidden`；
- `Idempotency-Key`：创建型请求的可选 8–128 位受限 ASCII header；
- `Idempotency-Replayed`：创建型请求是否返回已存在结果；
- 所有 `/api/` 响应都声明并实际发送 `Cache-Control: no-store`。

消费次数表示服务器授权领取，不表示客户端成功展示或 E2E 解密；消费型请求断线的结果
未知且禁止自动重试，冻结语义见 `docs/adr/0002-v1-消费型读取与端到端解密语义.md`。

快照只能由 `scripts/test-sandbox.ps1 -Mode Full` 的一次性测试容器生成和验证，
不得用宿主 Python 直接导出，以免绕过 G0 数据库与制品隔离门禁。
