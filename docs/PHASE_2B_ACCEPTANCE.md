# DeleteAudit Phase 2B 验收记录

## 1. 裁定

Phase 2B.1–2B.4 的当前进程实时接入范围已完成，可作为 Alpha 源码阶段签字。该裁定只覆盖：

- 用户手动启动的本机 Windows Event Log 只读接入；
- live evidence 持久化与有界时间批次刷新；
- ReadOnly Live History；
- 按需、只读的关联/删除会话/风险派生分析；
- 显式、live-owned canonical projection 及其 WPF 页面。

它不授权或暗示 Phase 2C、Windows Service、USN、ProgramData、安装包、Sysmon/审计策略配置、签名、外部锚点或生产部署。DeleteAudit 仍是 Alpha，不是完整或生产级取证系统。

## 2. 动态验收

```text
.NET SDK:   8.0.423
Restore:    成功
Build:      7/7 projects，0 warning，0 error
Unit:       278/278，Failed 0，Skipped 0
Integration:184/184，Failed 0，Skipped 0
Total:      462/462
Repeat:     全量测试连续两轮数量一致且全部通过
```

CLI home、NuGet cache、TEMP、测试数据库与测试输出均位于仓库忽略的 `artifacts/` 下。验收不读取真实 Event Log，不启动 watcher，不访问远程资源，也不修改系统状态。

## 3. Phase 2B.1：live evidence

`db/migrations/0004_phase_2b_live_evidence.sql` 显式新增：

- `live_capture_sessions`
- `live_capture_records`
- `live_capture_completions`

每条已保存记录使用 `live_evidence_id = live_session_id + ":" + received_sequence`，并保留 channel/capture 元数据、raw XML、raw XML SHA-256、parser raw identity、event ID、分类结果和结构化错误。删除事实、进程上下文与 Security 4663 补强保持不同 outcome；后两者不能单独呈现为删除事实。

0004 不借用 `import_session`、输入文件 digest、离线 channel epoch、离线 ingest sequence 或离线 entry hash。append-only trigger 约束应用自身的误写，不构成对数据库文件写入者的防篡改保证。

## 4. Phase 2B.2：有界刷新与 Live History

批次上限为 64 条；满批立即进入持久化，部分批次从第一条进入空批次起通常约 5 秒后进入持久化，同批后续记录不重新开始期限。五秒是调度目标而非严格完成保证；异常终止仍可能丢失最多 63 条尚未提交记录。写入 fault 通过单一 lifecycle 收口进入 Error，不自动重试或重启；已经提交的证据保留。

Live History 的查询连接全部为 SQLite `ReadOnly`，筛选参数化，默认 50/服务端最大 200 分页，稳定排序包含唯一 identity。Raw XML 只在选择具体记录后读取，并由 SQLite `length`/`substr` 返回最多 262,144 字符的明确只读预览。ViewModel 采用 latest-request-wins：新请求取消旧请求，generation 检查拒绝 stale result、stale failure 和 Dispose 后完成。

## 5. Phase 2B.3：按需派生分析

用户显式选择会话并点击分析后，应用从只读连接重新解析已保存记录，直接复用：

- `WindowsEventXmlParser`
- `DeleteEventCorrelator`
- `DeleteSessionAggregator`
- Phase 1A 风险规则

Sysmon 1 只补充进程上下文；Security 4663 只有明确 DELETE/DELETE_CHILD 才成为补强；无可靠匹配时缺失字段保持缺失。分析展示关联方法、置信度、时间差、来源 evidence ID、删除会话和风险信号，但不写回数据库、不成为新证据。每次分析最多读取 5,000 条，并明确显示截断。

## 6. Phase 2B.4：live-owned canonical projection

`db/migrations/0005_phase_2b4_live_projection.sql` 只能显式应用，新增：

- `live_channel_epochs`
- `live_projected_records`
- `live_projection_runs`

投影事务只写这三类 live-owned 对象。它绝不写入或伪装成：

- `raw_events`
- `delete_events`
- `delete_sessions`
- `channel_epochs`
- `import_sessions`
- offline ingest sequence
- offline hash chain

每条 canonical row 保留源 `live_evidence_id` 与 `source_received_sequence`，使用会话内密集 `live_ingest_sequence`。projection identity 由 evidence identity 确定；epoch identity 由 capture session、channel 与明确报告的 machine 共同确定，缺失 machine 与空字符串不合并。可缺字段保持 NULL，不推测命令行、父进程、用户、SID、Process GUID 或对象类型。

投影前重新计算 raw XML SHA-256、重新运行 Phase 1A parser，并核对 capture 保存的 parser identity 与 outcome。canonical payload digest 覆盖全部规范字段；entry hash 覆盖前一 live entry、evidence identity、epoch、live sequence、raw digest 和 payload digest。首条记录从独立零锚点开始，不连接离线链。

重放同一会话不会重复创建 projection、epoch、sequence 或 hash。已有投影必须是源 evidence 接收顺序的完整前缀且 continuity 验证通过，才允许追加。单次投影、epoch、projected rows 与 completed run 在一个 immediate transaction 中提交；失败回滚后保留原始 live evidence，并尽力追加独立 failed run 诊断。

continuity 验证会重新读取 source、重算 parser/payload/digest/hash，并检测缺节点、乱序、source identity 不一致、epoch 首记录不一致和内容修改。该结果只辅助检测连续性与意外修改：没有签名或外部锚点，有写权限的人可以重建整链，故不具备防篡改能力。

## 7. Readiness、查询与 WPF

runtime 不执行 migration。projection readiness 使用 ReadOnly 连接，并精确验证：

- main schema 中的 STRICT table；
- 完整普通列集合、type、NOT NULL、primary-key ordinal；
- 完整外键集合及 `NO ACTION` 行为；
- 完整 table-declared、非 partial、无表达式的 UNIQUE 集合；
- 无 hidden/generated column；
- unconditional `BEFORE UPDATE/DELETE ... SELECT RAISE(ABORT, ...)` trigger。

缺数据库、缺 0005 或任一结构变异都只使 projection 页面 fail closed 为 unavailable；已有离线、实时预览、Live History 和派生分析不自动降级或迁移。

projection query 只用 ReadOnly 连接和参数化 SELECT。source 只能来自固定白名单；路径/进程搜索对 LIKE 元字符转义；排序只在两个固定 SQL 片段中选择；分页默认 50、服务端最大 200，稳定排序包含 projection identity。

WPF 的“实时规范投影”页面包含常驻边界披露、所选 capture session、显式投影/刷新/连续性验证、source/path/process 筛选、分页、虚拟化列表、live evidence/epoch/projection identity、hash、loading/empty/error/unavailable/incomplete 状态。选择会话或打开页面不会自动投影、启动 watcher 或轮询。

## 8. 对抗性覆盖

测试覆盖正常投影、三种 source、ignored 排除、乱序 source insertion、重放幂等、并发投影、取消、分页/筛选、缺 0005、generated column、缺外键、额外 UNIQUE、partial update guard、UPDATE/DELETE/REPLACE 防护、projection fault 回滚、source/offline 保留、hash 篡改、缺链节点、read-only 查询数据库不变、latest-request-wins、session 切换、Dispose、stale completion、oversized page 与 WPF 虚拟化/可访问性。

测试只生成虚构 XML 和仓库 `artifacts/` 下的临时 SQLite；不使用个人日志或真实删除操作。

## 9. 已知保留边界

- 异常终止仍可能丢失最多 63 条未提交记录。
- `live_capture_completions.stopped_utc` 尚无跨表时间先后约束。
- 本地 SQLite、append-only trigger 与 continuity hash 不能抵抗拥有数据库文件写权限的对手。
- 没有签名、外部锚点、USN 缺口检测、服务隔离、ProgramData ACL 或生产保留策略。
- GitHub secret-scanning 配置不属于源码验收。

完整 P2 台账见 `PHASE_2B_COMPLETION.md`。
