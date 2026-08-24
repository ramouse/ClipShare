# ClipShare Problem Details v1

`v1.json` 冻结客户端可以程序化处理的错误 `type` 与 HTTP 状态。响应媒体类型为
`application/problem+json`，固定字段为 `type`、`title`、`status`、`detail`；客户端
必须忽略 `title/detail` 的文案变化，只按 `type/status` 分流。

重试最终还受具体 OpenAPI operation 的 `x-clipshare-auto-retry` 约束：消费型读取
即使遇到超时也不得自动重试；创建型请求只有携带同一 `Idempotency-Key` 才可重试；
429 必须服从 `Retry-After`。
