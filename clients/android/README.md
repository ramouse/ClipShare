# ClipShare Android v0.3-A1

本目录同时保留两个边界清晰的 Gradle 工程：

- 当前目录是 Android A1 应用工程；
- `crypto-contract/` 是已通过 C1 的独立 JVM 契约可执行程序，A1 不把它接入产品运行图，端到端加密集成属于 C2。

## A1 范围

已实现的应用边界：

- Jetpack Compose 单 Activity 手动界面；
- 显式文本/URL 发送与 Android `ACTION_SEND` 纯文本接收；
- SAF 显式文件选择、100 MiB 上限与输入/输出流式处理；
- 短码/链接文本读取、文件元数据读取和显式文件下载；
- 消费型读取/下载断线后结果未知且禁止自动重试；
- 前台可见且窗口聚焦时才注册的纯文本剪贴板监听；
- 默认关闭且独立持久化的“监测剪贴板”和“自动同步到已配对设备”开关。

A1 不包含设备身份、配对、端到端加密队列、局域网发现/直连、后台服务、图片/文件剪贴板自动发送或自动同步网络通道。

## 模块

- `app`：Compose 展示、Activity 生命周期和依赖装配；
- `core:model`：冻结领域模型、端口、大小限制与文本策略；
- `core:network`：OkHttp API v1 适配器、端点策略、重试和消费语义；
- `feature:send`、`feature:receive`、`feature:settings`：可在 JVM 单测的状态机；
- `platform:android`：Clipboard、DataStore 和 SAF/ContentResolver 适配器。

UI 不直接访问 HTTP、DataStore、ClipboardManager 或 ContentResolver。

## 构建冻结

- applicationId / namespace：`com.clipshare.android`；
- minSdk 29，compileSdk / targetSdk 36；
- Gradle 9.7.1（复用 C1 已校验 Wrapper）；
- AGP 9.3.2，Kotlin / Compose compiler 2.4.10；
- Compose BOM 2026.04.01。没有升级到要求 compileSdk 37 的 Compose 1.12；
- Java 字节码目标 17；固定容器复用现有 JDK 21 运行 Gradle。

依赖版本集中在 `gradle/libs.versions.toml`，首次批准解析后必须提交各项目 `gradle.lockfile` 和根 `gradle/verification-metadata.xml`，后续固定门禁只允许离线、严格校验运行。

## 网络边界

- Release 只接受用户显式配置的 HTTPS 基础 URL，仓库不内置生产地址；
- Debug/Test 只接受 `localhost`、`127.0.0.1` 或模拟器宿主 `10.0.2.2`；
- Android Debug 网络安全配置只对 `localhost`、`127.0.0.1` 和 `10.0.2.2` 放行明文 HTTP；
- OkHttp 禁用自动连接重试、重定向和 Cookie；
- 创建型请求只在明确收到 429/503 时自动重试，最多三次并复用幂等键；
- 文本读取和文件下载从不自动重试。

## 门禁入口

宿主不得直接运行测试。固定镜像和容器准备完成后使用：

```powershell
pwsh -NoProfile -File scripts/test-android-a1.ps1
```

该门禁在 `--network none` 的固定容器中执行 JVM 单测、Detekt、Android Lint、Debug APK/AAB 和测试 APK 构建，并把证据写入一次性的 `.sandbox/android-a1-*` 目录。设备安装与仪器测试由独立的可丢弃模拟器门禁执行，不得把“测试 APK 已构建”表述为“仪器测试已通过”。

工程预配置 API 29、31、33、34、35、36 的 Build-managed Device；只有在相应系统镜像经过单独下载授权后才运行这些任务。
