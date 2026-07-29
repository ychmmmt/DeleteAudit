# DeleteAudit Phase 2B 完成度与 P2 统一台账

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
| 2 | 项目级 | 没有 Live History 只读界面 | 产品边界 | **已关闭** | Phase 2B.2.2 已实现：`ILiveHistoryQueryService` + `SqliteLiveHistoryQueryService`（全部 ReadOnly、参数化、分页上限 200、raw XML 按需并在 SQLite 内截断）、`LiveHistoryViewModel`、以及 MainWindow 中真实可用的「实时接入历史」页。15 个集成用例 + 13 个 ViewModel 用例，其中包含「浏览后数据库逐字节不变」与注入/通配符转义 |
| 3 | 项目级 | `live_capture_completions.stopped_utc` 无跨表先后约束 | 代码缺陷（schema） | **保留** | SQLite 的 CHECK 不能跨表；唯一手段是触发器 + 子查询，必须新增 migration |
| 4 | 项目级 | cancellation 测试相邻断言债 | 测试债 | **保留** | 本轮 A7 未执行 |
| 5 | 项目级 | `docs/PHASE_*` 未记录 Phase 2B.1 / 2B.2.1 | 文档债 | **保留** | 本文件只覆盖台账，未补阶段验收文档 |
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

## 3. 当前真实计数

```text
本轮关闭：2, 7, 8, 9, 10, 11, 12, 13，以及 14 的应用侧
仍然保留：1, 3, 4, 5, 6, 9b, 14（外部写入者）
```

**项目当前 P2 总数 = 7。** 注意这与审计报告的 `P2 = 8` 构成完全不同：审计那 8 项中已有
6 项关闭、1 项部分关闭，剩余名额由此前一直存在的产品级债务占据。

## 3b. 尚未实现的 Phase 2B 范围

以下仍未实现，**不计入上面的 P2 台账**，因为它们是尚未开始的阶段目标而不是已交付代码的缺陷：

- **Phase 2B.3**：rolling correlation、delete-session aggregation、风险展示及其只读界面；
- **Phase 2B.4**：核心表投影、channel epoch、ingest sequence、entry hash chain、幂等投影与
  对应的增量 migration；
- Phase 2B.3 / 2B.4 的阶段验收文档。

实时接入历史页面因此目前只展示"接收到了什么"，**不**展示关联结果、删除会话或风险等级；页面
文案与本文件对此保持一致，界面上不存在指向未实现功能的入口或占位按钮。

## 4. 保留项为何有界

- **#1**：条数上界 63 是常量 `MaxCaptureBatchRecords - 1`，不随流量增长；已在三语用户文案披露。
- **#2**：缺少的是查看界面，不是数据——证据已经落库，可直接查询数据库。
- **#3**：只影响一条完整性断言，不影响写入正确性；关闭它需要新增 migration。
- **#4 / #5**：纯测试与文档债，不改变运行时行为。
- **#6**：GitHub 配置，本轮明令禁止修改。
- **#9b**：失败方向为 fail closed，且官方 migration 不受影响。
- **#14 外部写入者**：任何能写数据库文件的进程都可以自选连接设置。这与 SECURITY.md 已声明的
  "SQLite 不是防篡改介质" 是同一条边界，**不能**通过应用层设置关闭，也不得声称已关闭。

## 5. 本文件未覆盖

Phase 2B.2.2（Live History）、2B.3（关联/聚合/风险）、2B.4（核心表投影与 channel epoch）的
实现与阶段验收文档尚未完成，不在本文件范围内。`v0.1.0-alpha` 仍是冻结的 source-only 快照，
未被本分支移动或修改。
