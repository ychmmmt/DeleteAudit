# Phase 1A 验收记录

> **公开仓库说明**：本文记录公开前的私有封版历史；其中 commit hash 不属于当前公开仓库历史，仅用于本地归档核验。文中出现的 `C:\Dev\DeleteAudit` 等路径是当时私有开发环境的记录，公开版本的仓库根目录按 README 所述自动解析。

## 封版结论

DeleteAudit Phase 1A（离线审计核心）已通过动态验收，可以作为后续工作的可恢复基线。

## 验收环境

- Microsoft .NET SDK：8.0.423
- SDK 位置：`C:\Dev\DeleteAudit\artifacts\dotnet-sdk`
- `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`
- NuGet、CLI home 与临时目录均重定向到项目 `artifacts` 目录

## Build

- 项目数量：6
- Warning：0
- Error：0
- 结果：通过

## Test

- Unit：25/25
- Integration：3/3
- 总计：28/28
- Failed：0
- Skipped：0

## 已知目录外状态

本次验收唯一已知的项目目录外状态，是 .NET SDK 首次运行创建的 ASP.NET Core HTTPS 开发证书。后续验收已设置 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`，未执行证书信任、清理、导出或其他证书修改命令。

## 能力边界

Phase 1A 只提供离线领域模型、脱敏 XML 解析、保守事件关联、会话风险、SQLite 持久化基础与 JSONL 哈希链。当前尚不具备实时监控能力，也不读取真实 Windows Event Log、USN Journal 或 Sysmon 运行数据，不注册 Windows 服务。
