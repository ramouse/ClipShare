# ClipShare Vault v1 跨端契约

本目录是 C#/Kotlin/WebCrypto 的 C2 机器真源。`test-vectors` 中的 secret、nonce prefix 和 counter 只用于测试，禁止进入生产默认值。

## 1. 编码

- JSON 使用 UTF-8、无 BOM；生产 codec 拒绝重复属性、未知属性和 schema/version 降级。
- ID 使用小写 RFC 4122 UUID 文本；进入密码学时按去除连字符后的网络字节序解码为 16 bytes，禁止使用 .NET `Guid.ToByteArray()` 的混合字节序结果。
- 整数为无小数 JSON number；协议上限为 `2^63-1`，避免 SQLite/JVM/.NET 有符号边界分叉。
- Base64URL 严格无 padding，只允许 `A-Z a-z 0-9 - _`，并拒绝非规范 pad bits。
- 文本严格 UTF-8：不 trim、不改换行、不做 Unicode normalization、保留 NUL；孤立 surrogate 和非法 UTF-8 必须拒绝。

## 2. Epoch secret 与 key derivation

每个 epoch 独立使用 CSPRNG 生成 32-byte `VaultEpochSecret`（VES）。epoch 数字是公开标识，不参与生成未来 VES。HKDF 仅在一个 epoch 内分离用途：

```text
PRK = HKDF-Extract-SHA256(salt = vault_id_bytes, IKM = epoch_secret)
info = ASCII("clipshare:vault:key:v1\0")
       || u16be(purpose_utf8.length) || purpose_utf8
       || origin_device_id_bytes
key  = HKDF-Expand-SHA256(PRK, info, 32)
```

记录用途：`folder-metadata`、`item-metadata`、`item-payload`、`file-key-wrap`、`file-manifest`、`event-body`。`file-chunk` 使用每个文件版本独立生成的 32-byte File DEK。`epoch-wrapper` 使用设备 wrapping key，不使用 VES 自包裹。

## 3. 记录 nonce

同一实际 AEAD key 下 nonce 必须唯一。记录 key 已包含 origin device ID；每个 `(vaultId, epoch, purpose, originDeviceId)` 持久化一个 32-bit CSPRNG prefix 和 63-bit counter：

```text
nonce = prefix[4] || u64be(counter)
counter = 1..0x7fffffffffffffff
```

counter 在加密前独立事务递增并提交；允许崩溃留下间隙，禁止回退或复用。`nonce_state` 的复合主键和 `last_counter` 单调检查是持久保证。业务回放若 envelope 已存在则返回既有密文；否则消费新 counter。

文件每个 generation 使用独立 File DEK 和 prefix：

```text
chunk_nonce = file_prefix[4] || u64be(chunk_index)
chunk_index = 0..0x7fffffffffffffff
```

同一 generation 的 chunk 不可改写；新内容使用新 generation、DEK 和 prefix。AAD 中的 counter/chunk index 不替代 nonce 唯一性。

## 4. AAD

除平台 DPAPI 外，Vault AES-GCM AAD 固定为：

```text
ASCII("clipshare:vault:aad:v1\0")
|| u16be(schema_version)
|| u16be(purpose_utf8.length) || purpose_utf8
|| vault_id_bytes[16]
|| origin_device_id_bytes[16]
|| entity_id_bytes[16]
|| u16be(field_utf8.length) || field_utf8
|| u64be(key_epoch)
|| u64be(nonce_counter)
|| u64be(padded_plaintext_bytes)
```

AES-256-GCM 使用 12-byte nonce、128-bit tag，输出布局为 `ciphertext || tag`。任何字段替换、错误 epoch、错误 purpose 或长度变化都必须认证失败。

## 5. Plaintext frame 与 4 KiB 桶

除 `file-chunk` 外，plaintext frame 为：

```text
u64be(payload_length) || payload || zero_padding
```

总长度向上取整到 4096 的整数倍，最小 4096。解密后验证长度不越界且所有 padding byte 为零。文件 chunk 直接加密原始 bytes，`paddedPlaintextBytes` 等于实际 chunk 长度。

## 6. 平台 wrapper 与初始化

每个 wrapper 绑定 `vaultId + initializationId + epoch + platform + state`。状态只允许 `STAGED → READY`。初始化状态：

```text
ABSENT
WRAPPER_STAGED
DATABASE_STAGED
DATABASE_READY_WRAPPER_STAGED
READY
```

STAGED wrapper-only 只能使用原 VES 继续；READY wrapper-only、database-only、ID/digest 不匹配或已有密文但 key 不可用均失败闭锁。只有双方都 absent 才能生成新 VES。

## 7. SyncPolicy

三种 policy 只控制分发/复制，不是密码学 ACL。已持有同一 epoch secret 的设备如果取得其他密文，v1 不承诺无法解密。`syncPolicyOverride=null` 表示继承父级；Vault 根的默认有效值是 `LOCAL_ONLY`。

## 8. 文件

- 默认 chunk plaintext 上限 1,048,576 bytes；最后一块可更短；空文件 chunkCount 为 0。
- manifest plaintext 加密保存文件名、MIME、总长度、chunkCount、chunkSize、文件 SHA-256、File DEK wrapper 和 generation。
- 外层只保留 file/item ID、epoch、chunk index、ciphertext length 和提交状态等必要元数据。
- 完整性验证、flush 和原子移动后才登记正式引用；失败清理临时密文，不产生明文临时文件。

## 9. 统一错误

以下名称是 C2 冻结的跨端失败语义和测试判定类别。C2 核心与平台适配必须按这些语义失败闭锁，未来
接入正式 UI、同步或诊断边界时再把具体异常映射为稳定领域码；不得把 provider 文本、key material 或
原始密文写入日志。C2 不把尚未接入的边界映射表描述为已完成：

```text
invalid_contract
unsupported_version
invalid_encoding
invalid_id
invalid_nonce
nonce_exhausted
authentication_failed
invalid_padding
key_unavailable
wrapper_mismatch
initialization_incomplete
storage_corrupt
conflict
```
