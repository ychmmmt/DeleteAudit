# DeleteAudit Phase 2B 完成度、审计闭环与 P2 统一台账

本文件合并两份此前分别维护的 P2 清单：项目级产品债（6 项）与最近一次隔离审计列出的
代码/测试债（8 项）。两份清单从未重叠维护，因此**不能把审计报告里的 `P2 = 8` 当作项目
总数**。下表是去重后的唯一权威台账。

分支：`feature/phase-2b-complete`（未推送、无 upstream）
基线：`cb65ed458181204e4a236a0bd6df97bcd62fa120`（main）

## 1. 分类口径

| 类别 | 含义 |
|---|---|
| 产品边界 | Alpha 阶段有意不做的事，不是缺陷 |
| 代码缺陷 | 生产或测试代码的真实错误 |
| 测试债 | 实现正确但覆盖不足 |
| 文档债 | 行为正确但记录缺失 |
| 外部设置 | GitHub 仓库配置，与代码无关 |

## 2. 统一台账

| # | 来源 | 问题 | 类别 | 本轮状态 | 验收证据 |
|---|---|---|---|---|---|
| 1 | 项目级 | 进程异常终止时最多 63 条未提交记录可能丢失 | 产品边界 | **保留** | 定时刷新只把滞留*时间*有界化，条数上界不变；三语 README 与 SECURITY.md 均如实披露 |
| 2 | 项目级 | 没有 Live History 只读界面 | 产品边界 | **已关闭** | Phase 2B.2.2 已实现：`ILiveHistoryQueryService` + `SqliteLiveHistoryQueryService`（全部 ReadOnly、参数化、分页上限 200、raw XML 按需并在 SQLite 内截断）、latest-request-wins `LiveHistoryViewModel`、以及 MainWindow 中真实可用的「实时接入历史」页；覆盖数据库不变、注入/通配符、分页、取消、Dispose 和 stale completion |
| 3 | 项目级 | `live_capture_completions.stopped_utc` 无跨表先后约束 | 代码缺陷（schema） | **保留** | SQLite 的 CHECK 不能跨表；唯一手段是触发器 + 子查询，必须新增 migration |
| 4 | 项目级 | cancellation 测试相邻断言债 | 测试债 | **已关闭** | 五个 cancellation 用例补齐相邻断言：LastError / diagnostics / counters / `CompletionStarted` / `LifecycleCompleted` / `SessionPersisted` / 重复 Stop 不重试也不重复写入 / 下一会话不继承上一会话的诊断与错误 |
| 5 | 项目级 | `docs/PHASE_*` 未记录完整 Phase 2B | 文档债 | **已关闭** | `docs/PHASE_2B_ACCEPTANCE.md` 汇总 2B.1–2B.4 的功能、migration、动态验收、安全边界与残余风险；PROJECT_PLAN、THREAT_MODEL、SECURITY 和三语 README 同步 |
| 6 | 项目级 | secret scanning validity checks / non-provider patterns 关闭 | 外部设置 | **保留** | 与代码无关；本轮明令不得修改 GitHub 设置 |
| 7 | 审计 | `ManualTimerTimeProvider` 正常退出与 `finally` 交接之间可产生第二个 drainer | 代码缺陷（测试基建） | **已关闭** | 改为单一所有权：`EnsureDispatcherRunning` 在 `_sync` 内同时认领标志与排队；`DrainCallbacks` 只有一个释放点，删除了事后猜测的 `finally` |
| 8 | 审计 | `UnsafeQueueUserWorkItem` 调度失败会令 `_dispatchScheduled` 永久为真 | 代码缺陷（测试基建） | **已关闭** | 认领与排队原子化，排队返回 false 或抛出时回滚标志 |
| 9 | 审计 | canonical trigger parser 的误导性错误消息 | 代码缺陷 | **已关闭** | `TryMatchCanonicalTrigger` 输出 11 类具体原因；14 个 `MalformedTriggerNamesItsSpecificReason` 用例逐条断言原因 |
| 9b | 审计 | parser 对 schema 限定名、双引号 RAISE 消息、非 ASCII 标识符 fail-closed 误拒 | 产品边界 | **保留（有意）** | 三者都不在官方 0004 中；失败方向是拒绝启动而非带着失效防护启动。错误消息现已点明真实原因，不再误称缺少 migration |
| 10 | 审计 | `ReadForeignKeysAsync` 静默丢弃非 `NO ACTION` 外键 | 代码缺陷 | **已关闭** | 读取全部外键并携带其 referential action；非 canonical action、缺失键、额外键各自具名 fail closed；新增 `ExtraForeignKey` 与 `CascadingForeignKey` 变异测试 |
| 11 | 审计 | queue overflow 直接覆盖 `_lastError`，绕过 first-causal-error | 代码缺陷 | **已关闭** | 新增 `ReportConditionCore`；`_lastError` 现只有三处赋值（重置 / 故障 / 条件），由 `LastErrorIsOnlyAssignedThroughTheSharedEntryPoints` 在源码层固定 |
| 12 | 审计 | 带类型过滤的空 catch 缺少解释 | 测试债/可读性 | **已关闭** | 三处均补上说明为何吞掉、以及哪些异常仍会传播 |
| 13 | 审计 | trigger 变异测试断言不具判别力 | 测试债 | **已关闭** | 见 #9；断言从"消息含 trigger 名"升级为"消息含具体原因" |
| 14 | 审计 | `recursive_triggers=OFF` 时 `INSERT OR REPLACE` 可绕过 delete trigger | 代码缺陷 + 产品边界 | **部分关闭** | 应用侧已关闭：写连接启用 `recursive_triggers=ON`；`NoProductionSqlUsesReplaceConflictResolution` 扫描整个 `src` 禁止 REPLACE；`ReplaceOnCommittedEvidenceIsAbortedByTheAppendOnlyGuard` 实际执行 REPLACE 并证明被中止且原证据未变。**外部写入者边界无法从数据库层关闭，保留为已披露 P2**（见下） |

| 15 | 本轮自查 | Live History 的「新查询取消旧查询」并未真正实现：`ViewModelBase.RunSafelyAsync` 的 `IsBusy` 门会**静默丢弃**第二个并发请求 | 代码缺陷 | **已关闭** | `LiveHistoryViewModel` 改为专用的 latest-request-wins 路径（`RequestSlot` + `RequestTicket`），不再复用 `RunSafelyAsync`；新请求取消旧请求，唯一提交点前检查 generation，陈旧请求的异常与取消都不得覆盖较新请求的结果，Dispose 后一律不提交。命令不再因加载中而禁用。5 个确定性用例覆盖「A 阻塞→B 完成并显示→A 最后完成/抛错→UI 仍为 B」「陈旧失败不覆盖较新成功」「Dispose 后完成不更新 UI」「陈旧 raw XML 预览不覆盖新选择」。全部在旧实现下失败 |
| 16 | Phase 2B.4 自查 | readiness 只验证必需 UNIQUE，可能接受额外/partial/表达式 UNIQUE | 代码缺陷 | **已关闭** | 共享 structural validator 现在读取全部非-PK unique index，要求 origin=`u`、非 partial、无表达式并与声明列表精确相等；0005 requirement 覆盖 `live_evidence_id`、两组 session sequence 与 `entry_hash`，并有额外 UNIQUE 变异测试 |

## 3. 当前真实计数

```text
已关闭：2, 4, 5, 7, 8, 9, 10, 11, 12, 13, 15, 16，以及 14 的应用侧
仍然保留：1, 3, 6, 9b, 14（外部写入者）
```

**项目当前 P2 总数 = 5。** 注意这与早期审计报告的 `P2 = 8` 构成完全不同：早期审计项中已有
6 项关闭、1 项部分关闭，剩余名额由此前一直存在的产品级债务占据。

## 3b. Phase 2B 已实现范围

Phase 2B.1–2B.4 均已实现：

- **2B.1**：0004 live evidence identity、raw XML/digest、解析/分类结果和 completion；
- **2B.2.1**：64 条/约 5 秒有界批次刷新与 fault/cancellation/lifecycle 硬化；
- **2B.2.2**：ReadOnly、参数化、分页、按需 Raw XML 的 Live History；
- **2B.3**：按需只读 correlation、delete-session aggregation 和 risk 展示；
- **2B.4**：0005 live-owned canonical projection、独立 epoch/sequence/identity/continuity、幂等事务和真实 WPF 页面。

这些能力保持分离：2B.3 的分析不写回，2B.4 只写 live-owned 表；二者都不伪装成离线导入。没有 placeholder、coming-soon 或无行为按钮。动态验收与数量见 `PHASE_2B_ACCEPTANCE.md`。

## 4. 保留项为何有界

- **#1**：条数上界 63 是常量 `MaxCaptureBatchRecords - 1`，不随流量增长；已在三语用户文案披露。
- **#3**：只影响一条完整性断言，不影响写入正确性；关闭它需要新增 migration。
- **#6**：GitHub 配置，本轮明令禁止修改。
- **#9b**：失败方向为 fail closed，且官方 migration 不受影响。
- **#14 外部写入者**：任何能写数据库文件的进程都可以自选连接设置。这与 SECURITY.md 已声明的
  "SQLite 不是防篡改介质" 是同一条边界，**不能**通过应用层设置关闭，也不得声称已关闭。

## 5. 本文件未覆盖

Phase 2C、Windows Service、USN、ProgramData、签名/外部锚点、安装包和生产部署不在本文件范围内。`v0.1.0-alpha` 仍是冻结的 source-only 快照，未被本分支移动或修改。
