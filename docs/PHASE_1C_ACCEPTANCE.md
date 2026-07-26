# DeleteAudit Phase 1C 验收记录

> **公开仓库说明**：本文记录公开前的私有封版历史；其中 commit hash 不属于当前公开仓库历史，仅用于本地归档核验。文中出现的 `C:\Dev\DeleteAudit` 等路径是当时私有开发环境的记录，公开版本的仓库根目录按 README 所述自动解析。

验收日期：2026-07-24

## 1. 基线

- Phase 1B 封版提交：`d39392b37596299b92036ea150a43be27f72034f`（Complete DeleteAudit Phase 1B offline import）
- Phase 1C 在该基线之上以工作区未提交修改形式开发，经完整只读审计后于本记录随附的提交正式封版。

## 2. 功能范围

- .NET 8 WPF 离线查看器（`DeleteAudit.Viewer`），新增平台无关层 `DeleteAudit.Application`。
- Dashboard（导入/会话/事件/风险/诊断计数与最近导入时间）。
- 单文件 XML/EVTX 手动导入（仅用户明确选择的完全限定路径，复用既有 `OfflineImportPipeline`）。
- Import History（状态、时间、来源路径筛选 + 分页）。
- Delete Sessions（风险、UTC 时间、路径、进程筛选 + 分页）。
- Delete Events（同上筛选 + 分页，行选择后可打开 Raw XML）。
- Diagnostics（级别、时间、文本筛选 + 分页）。
- Raw XML 只读预览（数据库端有界截取，详见第 4 节）。
- 服务端分页（默认 50、上限 200）与参数化筛选；WPF 列表虚拟化。
- 当前不具备实时删除监控能力；查看器仅分析用户手动导入的离线日志。

## 3. 动态验收

- Restore：成功（项目内 SDK 与 NuGet 缓存，7 个项目全部还原）。
- Build：7/7 项目成功，0 warning，0 error。
- Unit：60/60 通过。
- Integration：29/29 通过。
- 总计：89/89，Failed 0，Skipped 0。
- `git diff --check`：通过（无空白错误）。

## 4. Raw XML P1 修复（原 P1：Raw XML 无界加载可能冻结 WPF 查看器）

- SQLite 数据库端截断：参数化 `length(raw_xml)` + `substr(raw_xml, 1, $preview_limit)`，经 `ExecuteReaderAsync` 读取；不再以 `ExecuteScalarAsync` 物化完整 `raw_xml`。
- 硬上限 262144 个 Unicode 码点，由 `RawXmlDocument.MaxPreviewCharacters` 常量在 Infrastructure SQL 参数强制；查询接口不暴露 limit 参数，UI 与调用方无法传入更大值。
- 完整 raw_xml 不进入 Application/WPF；跨边界仅传输受限预览与 Int64 原始长度。
- 契约包含 `PreviewText`、`OriginalLength`（Int64）、`PreviewLength`、`IsTruncated`、`PreviewLimit`；`IsTruncated ⇔ OriginalLength > PreviewLimit`。
- 截断时 UI 醒目提示：“内容较大，当前仅显示前 262,144 个字符；数据库中的原始证据未被修改。”未截断时不显示警告；页面同时显示原始字符数与当前预览字符数，标题明确为“只读预览”。
- “复制预览”仅复制 `PreviewText`，无导出完整 XML 功能。
- 数据库原始证据不被修改；查询连接保持 SQLite `ReadOnly`，集成测试断言查询前后数据库文件 SHA-256 不变。

## 5. 最终审计

- 提交前完整只读审计覆盖 tracked 修改 10 个、untracked 新增 28 个，共 38 个文件，逐个读取。
- P0：0。
- P1：0。
- 原 Raw XML 无界加载 P1：确认关闭。
- 禁止项全量扫描（删除/进程/注册表/服务/审计策略/实时日志/破坏性 SQL 等）：产品与测试代码 0 实际调用；docs 命中均为明确拒绝规则文字。

## 6. 已知 P2（延期处理，不阻塞封版）

- SQLite Unicode 码点计数（`length`/`substr`）与 .NET UTF-16 code unit 计数（`string.Length`）在非 BMP 字符场景下显示口径可能不一致（预览字符数可能大于 262,144 或大于原始字符数的表面矛盾）；内存仍严格有界，截断状态语义真实。
- `RawXmlDocument.CreatePreview` 工厂未提供第二层预览长度验证；当前唯一构造点为 SQL `substr` 输出，无可触达的绕过入口。
- 两者均不造成无界加载、内容泄漏或数据库写入，延期处理。

## 7. 非阻塞跟进

- 空 Raw XML 时禁用“复制预览”。
- 风险筛选增加 Informational 选项。
- 未来新增 Raw XML 加载入口时，防止旧请求结果覆盖新选择。
- 集中维护预览上限（262,144）相关文案，避免常量与文档漂移。

## 8. 安全边界

- 不读取实时 Windows Event Log（EVTX 仅 `PathType.FilePath`，无 EventLogWatcher/EventLogSession/LogName）。
- 不安装或调用 Sysmon。
- 不修改 Windows 审计策略（不调用 wevtutil/auditpol）。
- 不注册服务，不修改注册表、证书或任务计划。
- 不申请管理员权限。
- 不访问 D 盘（导入端明确拒绝 D 盘输入路径）。
- 不创建 `C:\ProgramData\DeleteAudit`。
- Viewer 查询连接为 SQLite `ReadOnly`（`Cache=Private`，`Pooling=false`）；写入唯一入口为既有 `OfflineImportPipeline`。
- 数据库与 JSONL 输出固定在 `C:\Dev\DeleteAudit\artifacts\viewer-data`，拒绝越界路径与重解析点。
- 当前仅分析用户手动导入的离线日志，无实时监控、拦截或恢复能力。
