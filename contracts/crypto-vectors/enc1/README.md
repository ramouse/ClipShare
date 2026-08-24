# ClipShare ENC1 v1 互操作规范

本目录是 Web、C# 与 Kotlin 的单一密码学测试资产来源。固定 key/IV 仅用于测试，禁止进入生产默认值。

## 线格式

```text
ENC1:<iv_b64url>.<cipher_and_tag_b64url>
```

- 前缀大小写敏感，marker 只能包含 ASCII；
- key 为 32 字节原始 AES key；链接表示是严格、无填充 Base64URL，固定 43 字符；
- IV 为 12 字节，生产加密必须为每次操作生成新 CSPRNG IV；
- 算法为 AES-256-GCM，tag 固定 128 bit；
- AAD 固定为空字节串；
- 输出布局固定为 `ciphertext || 16-byte tag`，整体再做 Base64URL；
- Base64URL 只允许 `A-Z a-z 0-9 - _`，禁止 `=`、空白、`+`、`/` 和非规范 pad bits；
- 文件上传体是完整 marker 的 ASCII/UTF-8 字节，不是裸 ciphertext；
- ENC1 只认证内容，不认证文件名、媒体类型或其他外部元数据。

跨端防滥用上限同样属于 v1 客户端契约：加密前文件明文最多 10,485,760 字节；
单个 marker 最多 16,777,216 个 ASCII 字符。前者是文件功能上限，后者是解析器的
防内存/CPU 滥用上限；任何一项变化都必须经过三端兼容性审查。服务端不持有密钥，
只能验证 envelope 并从 `ciphertext||tag` 编码长度推导加密前明文字节数。

`positive-vectors.json#error_codes` 冻结解析/认证失败的跨端领域码，
`negative-vectors.json` 是 Web、.NET 与 JCE 共用的畸形 envelope、Base64URL、认证与
Unicode 负向用例真源。底层 WebCrypto、
.NET 和 JCE 异常不得直接泄漏给 UI 或日志；错误 key 与篡改统一映射为
`authentication_failed`，避免作出平台相关或不可证明的区分。

## 文本

文本在加密前转换为严格 UTF-8：无 BOM、不 trim、不改换行、不做 Unicode normalization，保留 NUL；孤立 UTF-16 surrogate 必须拒绝。文本解密后必须严格验证 UTF-8，不能静默生成替换字符。二进制模式不得经过字符串层。

任何新增非空 AAD、元数据认证或线格式变化都必须使用 `ENC2`，不能改变 `ENC1` 的既有语义。

## 向量来源

`positive-vectors.json` 包含公开 AES-256-GCM 零 key/零 IV 已知答案，以及由独立
.NET AES-GCM 实现生成的非零 key/IV 向量。非零文本向量同时冻结中文、emoji、NUL、
CRLF、预组合 `é` 与分解形式 `e + U+0301`；二进制向量覆盖 `0x00`、`0xff`、
`0x80` 等不得经过字符串层的字节。Web、C# 与 Kotlin 必须在隔离沙盒中逐字节验证
确定性加密输出和固定 marker 解密，不能在测试运行时用被测实现重写期望值。
