# DeleteAudit

> **Alpha / experimental.** Windows 10 / Windows 11 · .NET 8 · MIT licensed.
>
> DeleteAudit analyses Windows delete-related event data. It offers offline
> import of event XML/EVTX files and a **live ingestion preview** that the user
> must start manually. The live preview reads existing Sysmon/Security channels
> read-only and stores **only a session summary** — live event detail is not
> persisted. DeleteAudit cannot prevent, block, or recover deletions, does not
> install Sysmon, does not change audit policy, does not request administrator
> rights, and does not run in the background. **It is not a complete or
> production-grade forensic system.**

Windows 删除审计应用（Alpha / 实验性）。

- **状态**：Alpha / experimental，非生产可用
- **支持系统**：Windows 10 / Windows 11
- **开发环境**：.NET 8 SDK
- **许可证**：[MIT](LICENSE)
- **安全策略**：[SECURITY.md](SECURITY.md) · **贡献指南**：[CONTRIBUTING.md](CONTRIBUTING.md) · **行为准则**：[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

## 当前进度

Phase 2A（实时接入预览）**已封版**。在 Phase 1A 离线审计核心、Phase 1B 离线事件导入管线、Phase 1C 离线 WPF 查看器之上，加入用户手动开启的 Windows Event Log 实时接入预览与会话统计。

最近一次动态验收：7 个项目以 0 warning、0 error 构建；Unit **126**、Integration **41**，合计 **167** 项测试全部通过，无 skip、无失败。

各阶段验收记录见 `docs/PHASE_1A_ACCEPTANCE.md`、`docs/PHASE_1B_ACCEPTANCE.md`、`docs/PHASE_1C_ACCEPTANCE.md`、`docs/PHASE_2A_ACCEPTANCE.md`。设计总览见 `docs/PROJECT_PLAN.md`，威胁模型见 `docs/THREAT_MODEL.md`，SQLite 结构见 `db/schema.sql`。

## 快速开始

```bash
git clone <repository-url>
cd DeleteAudit
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

运行开发版查看器：

```bash
dotnet run --project src/DeleteAudit.Viewer
```

仓库可以克隆到任意合法的本地路径。仓库根目录由构建输出向上查找 `DeleteAudit.sln` 自动解析；查看器数据库与 JSONL 输出固定在 `<仓库根>\artifacts\viewer-data`。

### 可选环境变量

```bash
set DELETEAUDIT_REPOSITORY_ROOT=C:\path\to\your\checkout
```

`DELETEAUDIT_REPOSITORY_ROOT` 用于显式指定仓库根（例如部署场景）。它必须是完全限定的本地目录，不接受 UNC 或设备路径。若环境变量未设置且向上找不到 `DeleteAudit.sln`，程序会 **fail closed** 并给出明确错误，不会退回当前工作目录或任意用户目录。

## 实时能力边界

这是本项目最需要被准确理解的部分：

- **默认不读取实时 Event Log，必须由用户手动开启。** 应用启动、切换页面都不会订阅任何通道；只有在“实时接入预览”页点击“开始监控”后才建立订阅。
- **只读订阅本机已经存在的通道**：`Microsoft-Windows-Sysmon/Operational` 与 `Security`，服务端 XPath 只过滤 Sysmon 1/23/26 与 Security 4663。不连接远程日志，不使用 `EventLogSession`。
- **不保存实时事件详情。** 实时事件的原始 XML、删除事实、关联结果和风险结果一律不保存。
- **仅保存监控会话摘要**：起止时间、通道状态、分类计数、有界诊断。停止监控或关闭应用后，无法在“删除事件”或“原始证据”页面回看本次实时事件详情。
- **不拦截、不阻止、不恢复删除。**
- **不安装 Sysmon**，不下载、不配置。
- **不修改审计策略**，不调用 `wevtutil` / `auditpol` / `sc.exe`，不改注册表、证书、服务或计划任务。
- **不申请管理员权限**，不触发 UAC。权限不足时降级为可见状态。
- **不自动启动、不后台常驻。** 点击“停止监控”或关闭窗口即释放全部订阅。
- 不写入、不清除、不修改任何 Event Log；不上传任何数据。
- 从订阅那一刻开始接收，**不回放历史**，不创建也不保存 bookmark。
- **不是完整或生产级取证系统。** 详见 [SECURITY.md](SECURITY.md)。

### 已实现的实时细节

- 只读检测通道是否存在、是否启用、当前用户是否可读，结果区分 `available` / `unavailable` / `access_denied` / `disabled` / `unknown_error`；检测在线程池执行，不阻塞 UI。
- 有界队列（默认 2048 条，`AllowSynchronousContinuations=false`），队列满时计数丢弃并给出限频可见警告，绝不阻塞事件投递线程。
- 单条事件 XML 上限 1,048,576 个 UTF-16 code unit；超限不入队、不解析、不截断冒充完整事件。
- 分类计数按语义拆分：删除事实（Sysmon 23/26）、进程上下文（Sysmon 1）、安全补强（Security 4663 命中 DELETE/DELETE_CHILD）、忽略、错误、丢弃、停止后丢弃。**进程上下文与安全补强永不作为删除事实呈现。**
- 通道、Provider 与 EventID 三者不一致时 fail closed。
- 每会话最多保留前 256 条真实诊断，单条消息最多 2048 字符，超出部分只累加计数。
- 启动前校验实时监控所需的数据库结构；缺失时 fail closed，不创建任何 watcher、不读取任何实时事件。

### 尚未实现，留待 Phase 2B 或更后阶段

实时原始 XML 持久化、实时删除事实持久化、进程上下文关联、删除会话聚合、风险计算、实时证据哈希链。实时管线目前只复用 Phase 1A 的 `WindowsEventXmlParser`；`DeleteEventCorrelator`、`DeleteSessionAggregator` 与风险模型**尚未接入**实时管线。Phase 2B 将为实时证据设计独立的身份与持久化结构，不会伪造 `import_session`、输入文件 SHA-256、channel epoch 或离线哈希链锚点。

## 离线导入边界

- 导入只接受调用方明确给出的、完全限定的单个 `.xml` 或 `.evtx` 文件路径；不扫描目录，不接受重解析点、设备路径或备用数据流。
- WPF 只通过 `OfflineImportPipeline` 写入；UI 不执行 SQL，也不创建或迁移 schema。
- 结构化查询只通过专用只读应用服务，SQLite 连接固定为 `ReadOnly`；列表查询采用参数化筛选和最大 200 项的服务端分页。
- Raw XML 按事件 ID 延迟读取，查询端只通过参数化 `length`/`substr` 返回前 262,144 个字符的只读预览；超限时 UI 明确标记截断并显示原始/预览字符数，复制操作仅复制预览，数据库中的原始证据不被修改。
- EVTX 适配器只构造 `EventLogQuery(filePath, PathType.FilePath)`，不连接日志通道、会话或实时订阅。
- 文件 SHA-256 是导入身份；同内容再次导入返回 `already_imported`。
- 数据库迁移是显式增量（`db/migrations/`），运行时代码只验证 schema，从不自动执行迁移，也不自动建库。
- 缺失字段在 UI 中显示为“未知”；原始值以 nullable 状态保留，不会被推测值覆盖。
- 查看器顶部持续显示能力横幅：“当前支持离线日志分析，以及用户手动开启的实时事件接入预览；实时事件详情暂不持久保存。”

## 数据存放位置

所有运行时产物都在仓库内的 `artifacts\` 下（已被 `.gitignore` 排除）：

| 路径 | 内容 |
| --- | --- |
| `artifacts\viewer-data\` | 查看器 SQLite 数据库与 JSONL 输出 |
| `artifacts\test-output\` | 测试输出 |
| `artifacts\nuget-packages\` | 本地 NuGet 包缓存（见 `NuGet.Config`） |

应用不创建 `C:\ProgramData\DeleteAudit`，不访问用户配置目录，也不写入仓库以外的任何位置。

## 许可证

[MIT](LICENSE) · Copyright (c) 2026 DeleteAudit contributors
