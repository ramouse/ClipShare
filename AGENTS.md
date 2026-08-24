# ClipShare Agent 协作规范

本文件只约束 `E:\project\clipshare` 仓库及其子目录。所有 Agent 都是当前任务临时创建的项目内协作者，不得注册为用户级或全局 Agent。

## 1. 作用域与绝对边界

- 所有文件写入只能发生在 `E:\project\clipshare` 下；不得修改 `C:\Users\mouse\.codex`、用户级配置、插件、其他仓库或项目树外文件。
- Agent 的读取、写入、缓存、运行和工具工作目录全部限定在 `E:\project\clipshare`。不得遍历或读取 `E:\project` 的兄弟目录；项目书与开发手册的适用约束以仓库内 `docs/依据/项目书与开发手册约束快照.md` 为准，外部原件只由用户或人工主控同步。
- 运行期测试数据库、模拟器数据、临时证书、日志、覆盖率和测试报告必须重定向到仓库内 `.sandbox/<run-id>/`。Windows/Android 合约测试允许复用两个精确命名的离线容器 `clipshare-test-dotnet`、`clipshare-test-kotlin` 及其容器内工具链缓存；除此之外不得创建常驻测试容器或项目外缓存。
- 不得执行 `git commit`、`git push`、创建标签、发布版本、部署、访问生产数据库或修改 GitHub 仓库设置；这些动作只由主控在明确授权后执行。
- 不得读取、导出或打印系统凭据、GitHub 令牌、签名私钥、Android keystore、PFX、生产 `.env` 或真实用户剪贴板内容。

## 2. 权威资料顺序

发生冲突时按以下顺序处理：

1. 用户在当前任务中的明确要求；
2. `AGENTS.md` 的安全和权限边界；
3. `docs/依据/项目书与开发手册约束快照.md`、`docs/客户端开发实施方案.md`、`docs/adr/` 与 `docs/系统设计.md`；
4. `docs/API.md` 与自动生成的 OpenAPI 契约；
5. 仓库内其他版本化文档；
6. 代码实际行为。

文档与代码不一致时不得静默选择其一：实现 Agent 必须停止扩大改动，在报告中列出差异，由主控裁决并同步文档、代码和测试。

## 3. Agent 角色与权限

| 角色 | 职责 | 写权限 |
|---|---|---|
| 主控 Agent | 拆分模块、发放写租约、冻结接口、验收、整合与提交 | 可修改当前模块范围；唯一可提交者 |
| 实现 Agent | 按冻结契约实现代码和测试，提交验证证据 | 每次仅一个；只写任务明确列出的文件 |
| 安全审查 Agent | 对抗性检查隐私、密钥、网络、存储、权限和供应链 | 只读 |
| QA/发布审查 Agent | 检查沙盒、测试矩阵、覆盖率、制品和上线门禁 | 只读 |
| 文档 Agent | 根据真实代码补充仓库内文档和学习材料 | 只写任务明确列出的文档文件 |

硬约束：同一时刻项目临时子 Agent 总数最多 3 个（运行、等待以及已完成但尚未关闭的都
计入）；同一时刻最多一个实现 Agent 写业务代码。审查 Agent 不得“顺手修复”；Agent
不得自行扩展写入范围。临时 Agent 完成报告后必须立即关闭，不保留空闲实例。

## 4. 模块流水线

每个模块严格执行以下顺序：

1. 主控冻结需求、接口、写入文件清单、风险和验收门；
2. 一个实现 Agent 编写代码与测试；
3. 安全审查 Agent 与 QA 审查 Agent 并行只读审查；
4. 原实现 Agent 根据问题清单修复；
5. 在专用沙盒运行静态检查、单元、契约、集成和平台测试；
6. 文档 Agent 按真实实现同步文档；
7. 主控检查 diff、测试证据、风险和回滚方案后决定是否提交。

任一步失败，模块状态仍为未完成，不得启动依赖它的下一模块，不得以“本机能运行”“人工看起来正常”绕过门禁。

## 5. 测试沙盒红线

`v0.3-G0` 已验收，但 Agent 仍禁止直接运行宿主 `pytest`、前端 E2E 或任何绕过 `scripts/test-sandbox.ps1` 的数据库/HTTP 测试。原因是集成夹具包含清表行为，开发 Compose 复用持久卷；只有沙盒入口具备数据库、URL、令牌与清理熔断。

沙盒完成后，测试必须同时满足：

- Compose 项目名包含唯一 `run-id`；数据库名必须以 `_test` 结尾；应用环境必须为 `test`；三项任一不满足即拒绝执行清表或迁移。
- PostgreSQL、文件存储和消息数据使用临时卷或 `tmpfs`；不挂载生产/开发数据卷；源码不得以可写方式挂载，优先复制进一次性测试镜像。
- 容器间测试默认不映射宿主端口并只使用 `internal` 网络；平台测试确需宿主访问时只绑定 `127.0.0.1` 随机端口。测试客户端拒绝任何公网主机和已知生产地址。
- Android Test 只能连接 MockWebServer 或映射到本机沙盒服务的 `10.0.2.2`；Windows Test 只能连接 loopback mock。
- 普通单元/集成测试只使用假剪贴板、假托盘、假快捷键、假通知和临时文件系统；真实系统集成仅在一次性 Windows Sandbox/临时 VM 或可丢弃 Android Emulator 中验证。
- 测试构建不得包含生产密钥。开发证书只能在一次性沙盒中生成和信任，结束即销毁；Agent 永不接触生产签名材料。
- .NET/NuGet/MSBuild 的 `DOTNET_CLI_HOME`、`NUGET_PACKAGES`、`NUGET_HTTP_CACHE_PATH`、`NUGET_SCRATCH`、`TMP`、`TEMP`、`BaseOutputPath`、`BaseIntermediateOutputPath` 和 `TestResultsDirectory` 必须逐进程指向 `.sandbox/<run-id>/`；禁用首次体验、遥测和 MSBuild 节点复用。
- Android/Gradle/Kotlin 的 `GRADLE_USER_HOME`、`ANDROID_USER_HOME`、`ANDROID_AVD_HOME`、`ANDROID_PREFS_ROOT`、Gradle/Kotlin build 目录和临时目录必须指向 `.sandbox/<run-id>/`；测试使用 `--no-daemon`，模拟器使用 `-no-snapshot-save`，结束后验证无 ADB/emulator/Kotlin daemon 残留。
- Python/npm 的 `PIP_CACHE_DIR`、`PYTHONPYCACHEPREFIX`、pytest cache/coverage、`npm_config_cache`、Node 临时目录和全部报告目录必须指向 `.sandbox/<run-id>/`；不得生成仓库根目录的缓存和测试结果。
- 后端/集成测试的 Docker/Testcontainers 资源必须带 `com.clipshare.sandbox.run-id=<run-id>` 标签并在结束后删除。客户端合约测试只允许固定复用带 `com.clipshare.test-container=true` 标签的 `clipshare-test-dotnet`、`clipshare-test-kotlin`；每次将当前源码同步到容器内 `/work/current`，在 `network=none` 下执行，结束后停止且保持 `restart=no`。不得创建专用 Buildx builder，不得导入/导出本地 BuildKit 缓存，不得执行影响其他项目的全局 prune。
- Windows 系统集成使用仓库内生成的 `.wsb`，必须设置 `Networking=Disable`、`ClipboardRedirection=Disable`，源码映射为只读，mock/backend 在沙盒内部运行。Android 系统 E2E 只在可丢弃模拟器/临时 CI VM 运行，测试构建的 URL 守卫和 mock 未匹配即失败策略必须同时生效。
- 上述设置只能由沙盒脚本传给子进程，不得持久修改宿主全局环境变量、用户配置、证书库、防火墙或 SDK 配置。
- 退出时必须清理所有 run-id 资源并验证无遗留网络、卷、模拟器快照、证书和后台进程；两个固定客户端测试容器是唯一例外，但必须处于停止、离线、`restart=no` 状态，失败也必须停止。

明确禁止：连接 `47.120.13.250` 或其他生产/公网 API 做测试；对非测试数据库执行 `DROP`、`TRUNCATE`、`DELETE`；读取宿主真实剪贴板；把测试证书导入宿主永久证书库。

## 6. 客户端平台红线

- Windows 自动上传、开机启动和自动覆盖剪贴板默认关闭，必须由用户明确开启；必须提供暂停、去重、回环抑制和立即清理本地历史的能力。
- Windows 剪贴板监听使用事件机制，不轮询；UI 层不得直接调用 Win32、HTTP、数据库或密钥存储。
- Android 不实现后台剪贴板监听，不申请默认输入法、悬浮窗、全盘存储或忽略电池优化来规避系统限制。
- Android 只通过前台显式粘贴、系统 Sharesheet、SAF 文件选择器和用户触发同步接收内容；后台重试使用受平台约束的调度机制。
- Release 构建只允许 HTTPS；不得关闭证书校验、使用不安全 TrustManager 或把能力令牌放入 URL/query/log。
- E2E 密钥永不上传服务器；跨端实现必须通过 WebCrypto、Windows 和 Android 的固定测试向量。

## 7. 企业级完成定义

模块只有同时满足以下条件才可标记完成。检查按模块适用性执行：适用项必须通过；不适用项必须在 PR 中逐项说明理由并由主控批准，禁止仅勾选跳过：

- 需求和 OpenAPI/密码学契约已冻结且有版本；
- 编译器警告视为错误，格式、静态分析和类型检查零错误；
- 共享/核心逻辑行覆盖率不低于 90%、分支覆盖率不低于 85%，平台适配层行覆盖率不低于 80%，关键安全路径场景覆盖率 100%；
- 当前阶段适用的单元、契约、集成、UI、E2E、离线/超时/重试和安全测试在沙盒内通过；各阶段最小必需检查以 `docs/客户端开发实施方案.md` §10 为准；
- Critical/High 漏洞为 0，依赖锁定，SBOM、校验和与构建来源证明可生成；
- 文档、变更记录、威胁模型、回滚方案和测试证据已同步；
- 未触碰生产环境，未在项目目录外留下任何 Agent 或测试状态。

生产发布还必须满足 `docs/客户端开发实施方案.md` 的 Release Gate；未签名制品、自签名测试制品或未通过全部必需检查的制品禁止上线。

## 8. Agent 交付报告格式

每个 Agent 的最终报告必须包含：

1. 结论：完成、未完成或阻塞；
2. 读取和修改的文件清单；
3. 执行的验证及结果；若未执行，说明原因；
4. 沙盒 run-id、网络和数据隔离证据；
5. 安全、兼容性和回滚风险；
6. 明确声明未提交、未部署、未修改项目目录外内容。
