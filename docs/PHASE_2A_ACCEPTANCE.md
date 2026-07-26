# DeleteAudit Phase 2A 验收记录

> **公开仓库说明**：本文记录公开前的私有封版历史；其中 commit hash 不属于当前公开仓库历史，仅用于本地归档核验。文中出现的 `C:\Dev\DeleteAudit` 等路径是当时私有开发环境的记录，公开版本的仓库根目录按 README 所述自动解析。

验收日期：2026-07-25

## 1. 基线

- Phase 1C 封版提交：`a4a03019e124a9ad6007bfd8c3f763c093ddb60d`（Complete DeleteAudit Phase 1C offline viewer）
- Phase 2A 在该基线之上以工作区未提交修改形式开发，经两轮完整只读审计与一轮定点只读验收后正式封版。

## 2. Phase 2A 准确定位

**用户手动开启、当前进程内运行的 Windows Event Log 实时接入预览与会话统计。**

### 具备

- 只读检测本机已有的 `Microsoft-Windows-Sysmon/Operational` 与 `Security` 通道（区分 available / unavailable / access_denied / disabled / unknown_error）
- 用户主动开始 / 停止；不自动启动
- 仅订阅 Sysmon 1/23/26 与 Security 4663，由服务端 XPath 过滤
- 默认不回放历史（`readExistingEvents: false`）
- 不保存 bookmark
- 有界 Channel 承接投递
- 使用 Phase 1A 解析器（`WindowsEventXmlParser`）分类事件
- 会话摘要持久化
- 通道状态、分类计数、错误、丢弃和诊断展示

### 不具备

- 实时原始 XML 持久化
- 实时删除事实持久化
- 进程上下文关联（`DeleteEventCorrelator` 未接入实时管线）
- 删除会话聚合（`DeleteSessionAggregator` 未接入实时管线）
- 风险计算（风险模型未接入实时管线）
- 实时证据哈希链
- 删除拦截、阻止或恢复
- 自动安装 / 配置 Sysmon
- 自动修改审计策略
- 服务常驻或自动启动

**上述不具备的能力属于 Phase 2B 或更后阶段。Phase 2A 不构成完整的实时删除审计，也不得如此宣称。**

## 3. 数据诚实性

- 不伪造 `import_session`
- 不伪造输入文件 SHA-256
- 不伪造 channel epoch
- 不借用离线导入身份
- 不伪造哈希链锚点
- UI 在实时页顶部以固定（非滚动区）红框明确披露：当前仅保存监控会话摘要，实时事件原始 XML、删除事实、关联结果和风险结果不会保存，停止监控或关闭应用后无法在"删除事件"或"原始证据"页面回看本次实时事件详情
- 页面名称为"实时接入预览"
- 分类计数拆分为：`DeleteFact`、`ProcessContext`、`SecurityEvidence`、`Ignored`、`Error`、`Dropped`、`LateDiscarded`
- "已分类事件"等于前三项之和；进程上下文与安全补强永不作为删除事实呈现或合并计数

## 4. 安全和资源边界

- 单条实时 XML 上限 1,048,576 UTF-16 code units（生产常量，UI 与调用方无法放大）
- 超限 XML 不入队、不解析、不截断后伪装成完整事件；计入 Received 与 Error，诊断不含原始 XML
- Channel 容量有界（默认 2048）
- `AllowSynchronousContinuations = false`（显式设置，测试锁定）
- 诊断最多保留前 256 条真实诊断
- 单条诊断最多 2048 字符（应用层截断 + 数据库 CHECK 双重保证）
- 超出部分只增加 `suppressed_diagnostic_count`，不覆盖已保留诊断
- `EventLogConfiguration`、`EventLogReader`、`EventRecord`、`EventLogWatcher` 均释放
- writer 无条件完成（source 停止失败也不跳过）
- consumer 必须 await 并观察异常
- consumer 结束后才释放 CancellationTokenSource
- watcher fault 后不自动重启
- generation 仅用于进程内生命周期，不是取证意义上的 channel epoch，不持久化
- 最终快照在 source 停止、writer complete、consumer drain 后于单锁内获取
- `SaveSessionAsync` 每个启动会话最多一次
- 通道、Provider 与 EventID 三者不一致时 fail closed，固定计入 Error
- 会话最终状态由锁内的 `_sessionFaulted` 事实决定，不依赖异步发布的 UI 状态

## 5. 数据库

- 新增 `db/migrations/0003_phase_2a_live_monitoring.sql`
- `db/schema.sql` 与 `0002_phase_1b_offline_import.sql` 未修改
- 0003 只新增 live monitoring 会话、通道和诊断结构（`live_monitoring_sessions`、`live_monitoring_channels`、`live_monitoring_diagnostics` 及其索引）
- 运行时不自动迁移
- 0003 缺失时在 probe 与 watcher 之前 fail closed（不创建任何 watcher、不读取任何实时事件）
- 不自动建库（`ReadWrite` 非 Create，另加显式存在性检查）
- 保存使用参数化 SQL 和单事务
- 不执行破坏性 SQL（无 DROP / DELETE / TRUNCATE / VACUUM / ATTACH / DETACH）
- Phase 1C 查询在有 / 无 0003 两种情况下均不回归

## 6. 动态验收

- Restore：成功
- Build：7/7，0 warning，0 error
- Unit：126/126
- Integration：41/41
- 总计：167/167
- Failed：0
- Skipped：0
- `git diff --check`：通过
- 无新增 NuGet 包（`System.Threading.Channels` 属 .NET 8 BCL）

## 7. 最终审计

- P0：0
- P1：0
- P1-A 孤儿 consumer：已关闭
- EventClassified 逐订阅者隔离：已完成（`GetInvocationList()` 逐个 try/catch）
- SnapshotChanged 逐订阅者隔离：已完成
- final_state 使用锁内 session fault 事实，不依赖 UI state
- 未发现锁递归或死锁（锁序恒为 transition gate → session lock，session lock 内无 await）
- 一次性会话落库成立
- 迟到回调 generation 隔离成立
- 计数平衡成立（`Received = DeleteFact + ProcessContext + SecurityEvidence + Ignored + Error + Dropped`；`LateDiscarded` 与 `SuppressedDiagnostics` 在等式之外）
- 固定非持久化披露成立（披露区位于 ScrollViewer 之外，结构测试锁定）

## 8. 已知非阻塞项

1. **P2-C**：`ReleasePipelineAsync` 的 `finally` 内 `CancellationTokenSource.Dispose()` 不在 catch 覆盖范围内；BCL 正常路径幂等且现实中基本不抛出。即便异常，consumer 已终止，无孤儿任务，但会话可能无法落库。相关"从不抛出"注释应在未来调整为"正常路径不抛出"。
2. **`live_consumer_failed`**：静态控制流保证 consumer Task 必被 await、异常必被捕获，但没有为了测试该极窄分支新增生产注入接缝。
3. 持续抛异常的 `SnapshotChanged` 订阅者会消耗有限的 256 条诊断配额，之后转为 suppressed count，属于预期的有界降级。

## 9. 系统边界

明确确认，Phase 2A：

- 未安装或配置 Sysmon
- 未修改审计策略
- 未调用 `wevtutil` / `auditpol` / `sc.exe`
- 未注册服务或任务计划
- 未修改注册表或证书
- 未申请管理员权限
- 未连接远程 Event Log（不使用 `EventLogSession`）
- 未写入或清除 Event Log
- 未访问 D 盘或 OneDrive 副本
- 未创建 `C:\ProgramData\DeleteAudit`
- 默认不自动开始实时接入
- 关闭窗口后不后台常驻
