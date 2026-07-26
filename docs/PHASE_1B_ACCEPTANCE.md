# Phase 1B 验收记录

> **公开仓库说明**：本文记录公开前的私有封版历史；其中 commit hash 不属于当前公开仓库历史，仅用于本地归档核验。文中出现的 `C:\Dev\DeleteAudit` 等路径是当时私有开发环境的记录，公开版本的仓库根目录按 README 所述自动解析。

## 结论

DeleteAudit Phase 1B（离线事件导入管线）已通过动态验收，可以在当前离线、安全边界内判定完成。

## 验收环境

- Microsoft .NET SDK：8.0.423
- SDK 位置：`C:\Dev\DeleteAudit\artifacts\dotnet-sdk`
- `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`
- `DOTNET_CLI_TELEMETRY_OPTOUT=1`
- `DOTNET_ROOT`、CLI home、NuGet cache、TEMP 和 TMP 均指向项目 `artifacts` 目录
- 新增直接 NuGet 依赖：`System.Diagnostics.EventLog` 8.0.1

## Restore

- 解决方案项目数：6
- 最终验收结果：所有项目均为最新，无需下载或变更包
- Restore：成功

## Build

- 项目数量：6/6
- Warning：0
- Error：0
- 结果：通过

首次整套 build 在测试代码中发现 2 个错误：一个原始插值字符串中的 GUID 大括号转义问题，以及一个 CA1711 测试集合类名问题。跨文件去重修复的中间 build 发现 1 个 CA1859 返回类型分析器错误，最终整套 build 首次尝试又发现 1 个测试辅助方法局部变量重名错误。四处均为最小编译/分析器修复；产品功能和测试断言均未弱化。

## Test

- Unit：45/45
- Integration：11/11
- 总计：56/56
- Failed：0
- Skipped：0

## 已验收能力

- 只读、显式单文件 `.xml`/`.evtx` 输入验证；拒绝目录、设备路径、备用数据流和路径中的重解析点。
- 版本化多事件 XML envelope；按物理顺序保存记录序号，单条坏事件产生诊断并继续。
- EVTX 仅使用文件路径查询模式，并逐条提取原始 XML；没有日志通道、会话或实时 watcher。
- 按源文件 SHA-256 幂等，同内容返回 `already_imported`，同名不同内容允许导入。
- 不同 SHA 文件包含重叠删除事实时，已存在事实及同一输入内的重复事实在会话/风险聚合前排除，并以 `ignored` 和结构化诊断记录；事务内再次发现竞态重复时安全回滚，避免虚增计数。
- 使用显式 schema 增量和参数化 SQL，在一个受控事务中写入导入会话及可持久化事件图；故障注入验证完整回滚。
- 每个已提交导入会话生成独立 UTF-8 JSONL，确定性排序并接入逐条哈希链；只有完整写入后才产生成功 manifest。
- 即使会话中所有记录均解析失败，只要数据库会话已提交，仍生成该会话的 JSONL/输出元数据；导入状态保持 `failed`，成功写出只表示输出完整，不会把导入降级为 `partial_failure`。
- 纯数据报告统计解析结果、事件 ID、删除事实、关联置信度、warning/critical 会话、高风险路径和诊断。
- 输入内容和修改时间在导入前后保持不变。

## EVTX 与系统边界

生产 EVTX reader 构造 `EventLogQuery(normalizedAbsolutePath, PathType.FilePath)`，并通过 `EventLogReader` 读取指定文件；它不会连接本机日志通道，也不使用 `EventLogSession`、`EventLogWatcher` 或 `PathType.LogName`。自动化测试注入文件 reader 替身，证明适配器传递的是规范化文件路径和 `FilePath` 模式，不读取真实 Windows Event Log。

## 持久化与输出边界

`db/schema.sql` 未被覆盖。Phase 1B 只新增 `db/migrations/0002_phase_1b_offline_import.sql`，并要求调用方在运行前显式准备 schema；运行时代码不自动迁移。

核心数据库事务提交成功后才尝试 JSONL 输出。数据库提交失败时 writer 不会被调用，因此不会留下成功 manifest。JSONL 写入失败会返回并持久化 `partial_failure`；可能保留不完整的 JSONL 或 `.pending` 文件以供取证，本阶段不实现自动删除或清理。

JSONL 接口接受调用方提供的完全限定输出目录；本阶段的配置样例和全部验收测试只使用 `C:\Dev\DeleteAudit\artifacts\test-output`。生产部署时的目录 allowlist/ACL 仍属于后续阶段。

导入报告中的删除事实数、关联分布、会话风险与高风险路径只统计本次可持久化且尚未存在的新删除事实；解析成功数和事件 ID 数仍描述输入文件本身。该口径避免重叠导出文件虚增删除事实和风险阈值。

## 最终安全复查

- 未发现 `File.Delete`、`Directory.Delete`、清理命令或其他删除 API。
- 未发现 `DROP`、`DELETE FROM`、`TRUNCATE` 或 `VACUUM`。基线 schema 中的 `BEFORE DELETE` 仅是拒绝删除的只追加保护触发器。
- 未发现 `EventLogSession`、`EventLogWatcher` 或 `PathType.LogName`；EVTX 仅使用 `PathType.FilePath`。
- 未发现 Sysmon、`wevtutil`、`auditpol`、服务、注册表、证书或任务计划修改调用。
- 未发现 `FileSystemWatcher`、进程终止或卷控制 API。
- 配置中的 `D:\` 和 `C:\ProgramData\DeleteAudit` 仅为禁用的未来监控/生产目录声明；`RuntimeWritesEnabled=false`、`AutomaticImportEnabled=false`，本阶段未访问或创建这些位置。
- JSONL writer 的 `CreateNew` 和 pending-manifest rename 只作用于显式输出目录；输入文件始终以只读方式打开，未被写入、移动、重命名或删除。

## 已知目录外状态

本阶段受控命令与测试的所有写入都位于 `C:\Dev\DeleteAudit` 内，包括 SDK、NuGet cache、CLI home、TEMP、build 输出、SQLite 内存数据库和 `artifacts\test-output`。

Phase 1A 首次 SDK 运行曾创建 ASP.NET Core HTTPS 开发证书；该状态在本阶段开始前已经存在。本阶段设置 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`，未创建、信任、导出、清理或修改证书，也未发现新的项目目录外状态写入。

## 尚未具备

本阶段当前仍不具备实时删除监控能力，也不具备真实日志通道订阅、WPF 导入界面、Windows Service、Sysmon 安装/配置、审计策略配置、USN 读取、ProgramData 生产存储、TPM/CNG 真签名、外部不可变锚定或生产输出目录强制策略。当前 JSONL 哈希链用于检测内容不一致，不能宣称具有硬件级或本机管理员级防篡改能力。
