# DeleteAudit 项目规划

## 0. 第一阶段边界

Phase 0 只交付设计、SQLite schema、配置样例和 .NET 8 空载项目骨架。进入 Phase 1A/1B/1C 后，所有实时采集器仍默认禁用；不安装或配置 Sysmon，不修改 Windows 审计策略，不注册 Windows 服务，不访问或写入 D 盘，不执行删除、清理或卷操作。

设计假设：Sysmon 与 Security 审计在未来可能由管理员另行启用。DeleteAudit 只消费已经存在的事件；如果某个来源不可用，程序必须报告覆盖缺口，不能把“没有数据”解释为“没有删除”。

### Phase 1A 实现状态

离线审计核心现已实现：领域模型、Sysmon 1/23/26 与 Security 4663 XML 字符串解析、保守关联、可注入时钟的会话风险、既有 schema 上的参数化 SQLite 仓储，以及内存可测的 JSONL 哈希链。该阶段不包含任何真实事件日志、USN、服务、ProgramData 或系统配置适配器。

SQLite 仓储遵守“不自动修改或迁移 schema”：生产代码只验证所需表并执行 INSERT/SELECT；测试代码负责在内存 SQLite 中显式加载 `db/schema.sql`。低置信路径启发式只出现在 `CorrelationResult`，不会据此回填命令行、父进程、用户名或 SID。

### Phase 1B 实现状态

离线事件导入管线现已实现：显式单文件请求、版本化多事件 XML envelope、导出 EVTX 文件路径适配器、文件 SHA-256 导入身份、结构化逐记录诊断、受控 SQLite 事务、每会话 JSONL/manifest 和纯数据导入报告。

EVTX 适配器只使用 `EventLogQuery(absoluteFilePath, PathType.FilePath)`，不创建实时日志会话、通道订阅或 watcher。所有输入先验证扩展名、普通文件属性、路径组成、大小和哈希，并在读取结束后验证内容、长度与修改时间未变化。管线不递归扫描，也不移动、重命名或修改输入文件。

Phase 1B 的 schema 变化保存在 `db/migrations/0002_phase_1b_offline_import.sql`；运行时代码只验证基线和增量表已经存在，不执行迁移。导入会话、原始记录、可持久化的 Phase 1A 事件图、关联、删除会话和风险在一个事务中提交；JSONL 仅在核心提交后写入，输出失败作为明确的部分失败状态记录。

### Phase 1C 实现状态

离线 WPF 查看器现已实现：Dashboard、手动单文件 XML/EVTX 导入、Import History、Delete Sessions、Delete Events、Diagnostics 和只读 Raw XML。风险、UTC 时间、路径和进程筛选均由只读查询服务转为参数化 SQL；列表使用固定上限的服务端分页并启用 WPF 虚拟化，不一次加载全部数据。Raw XML 页面是只读预览：查询端用参数化 `length(raw_xml)`/`substr(raw_xml, 1, $preview_limit)` 在 SQLite 内截取前 262,144 个字符，不把完整 `raw_xml` 物化到内存；契约返回 `PreviewText`、`OriginalLength`（Int64）、`PreviewLength`、`IsTruncated`、`PreviewLimit`，超限时 UI 醒目提示截断且“复制预览”只复制预览文本，数据库中的原始证据不被修改。

新增的 `DeleteAudit.Application` 保存平台无关的查询契约、展示 DTO 和轻量 MVVM。Viewer 只负责 XAML、文件选择器和 composition root；它不直接持有 SQLite 连接或 SQL。写入仍只通过现有 `OfflineImportPipeline`，`already_imported` 只显示本次操作结果，不伪造新的导入历史。

数据库与导入输出固定在 `<仓库根>\artifacts\viewer-data`（仓库根由 `DELETEAUDIT_REPOSITORY_ROOT` 或向上查找 `DeleteAudit.sln` 解析，解析失败即 fail closed）。查询连接采用 SQLite `ReadOnly` 模式；导入采用不创建数据库的 `ReadWrite` 模式。缺数据库或缺 schema 只产生可见状态/结构化失败，不创建数据库、不执行迁移。Phase 1C 本身不订阅实时 Event Log。

### Phase 2A 实现状态（实时接入预览，已完成）

Phase 2A 的正式定位是：**用户手动开启、当前进程内运行的 Windows Event Log 实时接入预览与会话统计。**

已实现的范围：只读通道探测（区分 available / unavailable / access_denied / disabled / unknown_error，探测在线程池执行，不阻塞 UI 线程）；用户显式点击后在本进程内对已存在的 `Microsoft-Windows-Sysmon/Operational` 与 `Security` 通道建立只读订阅，每个可用通道一个 `EventLogWatcher`，查询用服务端 XPath 限定 Sysmon 1/23/26 与 Security 4663；有界队列（`FullMode=Wait` + 仅 `TryWrite`）承接投递，绝不阻塞回调线程，队列满时计数丢弃并发出限频警告；后台消费者用 Phase 1A 的 `WindowsEventXmlParser` 分类；停止后写入一行会话摘要。

分类计数按语义拆分存储，`delete_fact_count`（Sysmon 23/26）、`process_context_count`（Sysmon 1）、`security_evidence_count`（Security 4663 命中 DELETE/DELETE_CHILD）三者独立；"已分类事件"等于三者之和，且**进程上下文与安全补强永不作为删除事实呈现或合并计数**。平衡关系为 `received = delete_fact + process_context + security_evidence + ignored + error + dropped`，由 DTO 与 0003 的 CHECK 双重约束。停止后到达的迟到事件计入独立的 `late_discarded_count`，不参与该等式，也不会被误报为"队列已满"。

边界与防线：启动前先 `ValidateSchemaAsync`，`db/migrations/0003_phase_2a_live_monitoring.sql` 未应用时 fail closed，不创建 watcher、不读取任何实时事件；单条事件 XML 上限 1,048,576 个 UTF-16 code unit，超限不入队、不解析、不截断冒充完整事件（`EventRecord.ToXml()` 本身仍会先物化一次字符串，该防线约束的是队列驻留与解析内存）；每个会话最多保留前 256 条真实诊断，超出部分只增加 `suppressed_diagnostic_count`，不覆盖已保留诊断，单条消息最多 2048 字符；会话最终状态由锁内的故障标记决定，而不是异步发布的 UI 状态，因此一次真实 watcher 故障即使与用户 Stop 并发也必然落库为 `error`；通道、Provider 与 EventID 不一致时 fail closed 并固定计入 Error；每次 Start 递增一个**仅用于内存生命周期**的 session generation（它不是、也不得被当作取证意义上的 channel epoch），旧 watcher 的迟到回调无法修改新会话计数；source fault 后转入 Error 并在回调线程之外异步停止、drain、只保存一次 `final_state='error'` 的会话摘要，不自动重启。

Phase 2A 的历史边界不再描述当前能力：实时原始 XML、持久化历史、按需关联/聚合/风险分析和独立规范投影均已在 Phase 2B 实现。仍然成立的边界是：实时接入必须由用户手动开始；它不会自动启动、不会安装或配置事件源，也不构成完整或生产级取证。

### Phase 2B 实现状态（实时证据、历史、分析与 live-owned 投影，已完成）

- **Phase 2B.1 / migration 0004**：每条成功接收的受支持事件保存 `live_evidence_id = live_session_id + received_sequence`、capture 元数据、原始 XML、原始 XML SHA-256、parser identity、分类结果和结构化错误。身份不借用离线 import、输入文件哈希、channel epoch、ingest sequence 或 entry hash。
- **Phase 2B.2.1 有界时间刷新**：批次达到 64 条立即持久化；未满批次从第一条进入空批次起通常约 5 秒后进入持久化，同批后续记录不延长期限。异常退出仍可能丢失最多 63 条未提交记录；调度和 SQLite I/O 使五秒只是调度目标，不是严格完成保证。写入 fault、取消、Stop 和完成记录均使用单一生命周期收口，不自动重试或重启。
- **Phase 2B.2.2 Live History**：SQLite `ReadOnly`、参数化服务端筛选、默认 50/最大 200 分页、按选择延迟读取且数据库端截断的 Raw XML 预览；ViewModel 对并发请求采用 latest-request-wins、取消和 generation stale-result rejection。打开历史页不订阅事件日志，也不轮询。
- **Phase 2B.3 派生分析**：按用户动作只读重新解析所选 capture session，复用 Phase 1A 的 `WindowsEventXmlParser`、`DeleteEventCorrelator`、`DeleteSessionAggregator` 和风险规则，展示关联删除、删除会话和风险信号。结果不写回数据库、不提升为新证据；每次最多分析 5000 条并明确显示截断。
- **Phase 2B.4 / migration 0005**：显式、幂等地把可投影 live evidence 写入独立的 `live_channel_epochs`、`live_projected_records` 和 `live_projection_runs`。每条记录保留来源 `live_evidence_id`、源接收序号、会话内密集 `live_ingest_sequence`、确定性 projection/epoch identity、原始 XML digest、canonical payload digest 与独立 continuity hash。投影事务只写这些 live-owned 表；不写、不冒充、也不连接 `raw_events`、`delete_events`、`delete_sessions`、`channel_epochs`、`import_sessions`、离线 ingest sequence 或离线 hash chain。

`0003`、`0004`、`0005` 都只能由开发者/操作员显式应用；runtime 只用 ReadOnly 连接检查完整的 STRICT table/列/外键/UNIQUE/append-only trigger 结构，绝不自动 migration。缺少或变异 `0005` 时，只有规范投影功能 fail closed 为 unavailable，已有离线、实时预览、Live History 和派生分析仍保持各自边界。

投影连续性 hash 可重算以发现顺序断裂、源证据不一致或意外修改，但没有签名或外部锚点；能写数据库的人可以重建整条链。因此它不是防篡改保证，也不能与离线链或未来外部可信检查点混称。

## 1. 威胁模型

完整仓库级威胁模型见 `THREAT_MODEL.md`。核心安全目标如下：

- 保护审计事件的真实性、顺序、完整性、可追溯性和可用性。
- 将高权限采集面与低权限查看面隔离，查看器永远不能修改数据库、JSONL 或采集器配置。
- 把 Windows 事件 XML、路径、用户名、命令行和 USN 数据全部视为不可信输入。
- 不把 4663 的 DELETE 权限使用误报为已完成删除；不把 USN 记录单独用于执行者归因。
- 明确本机管理员边界：本地哈希链能检测篡改，但只有外部不可变锚点才能可靠抵抗管理员回滚整个日志集。
- 原始 XML、命令行、用户名和 SID 可能包含敏感信息，必须采用最小权限 ACL、受控查询和明确保留策略。

## 2. 架构图（文字版）

```text
┌──────────────────────────── Windows（未来已由管理员配置） ────────────────────────────┐
│ Sysmon 23/26 ─┐                                                                    │
│ Security 4663 ├─> 只读事件适配器 ─┐                                                │
│ Sysmon 1* ────┘                  │                                                │
│ USN Journal ─────> 缺口检测适配器 ├─> 规范化/去重 ─> 证据关联 ─> 删除会话/风险引擎 │
└───────────────────────────────────┘                                                │
                                      │                                               │
                                      ├─> SQLite 结构化索引                           │
                                      ├─> 每日 JSONL 原始审计链                       │
                                      └─> 告警记录（只通知，不拦截）                   │
                                                                                      │
                 C:\ProgramData\DeleteAudit（未来运行数据）                           │
                 ├─ data\deleteaudit.db                                               │
                 ├─ logs\yyyy-MM-dd.jsonl                                             │
                 ├─ manifests\yyyy-MM-dd.manifest.json                                │
                 └─ archive\（仅承接未来 Sysmon 23 的受控存档）                        │
                                      │                                               │
                                      v                                               │
                 本机只读查询边界（优先命名管道；不向 Viewer 授予写权限）              │
                                      │                                               │
                               WPF 日志查看器                                          │
```

`Sysmon 1*` 只是进程富化来源，不参与“是否发生删除”的判定。Sysmon 23/26 是删除事实主线；4663 补充主体、PID、对象路径和 DELETE/DELETE_CHILD 权限；USN 只检测缺口和补充对象类型/文件引用号。

### 组件职责

- `DeleteAudit.Collector`：未来的 Worker Service 宿主；只读订阅既有事件源，驱动管线和健康状态。
- `DeleteAudit.Domain`：不可变事件、证据、删除会话、风险规则和接口契约，不依赖 Windows/WPF/SQLite。
- `DeleteAudit.Application`：平台无关的只读查询契约、分页 DTO、导入应用契约和 MVVM 展示逻辑。
- `DeleteAudit.Infrastructure`：Windows Event Log、USN、SQLite、JSONL、哈希链和本机 IPC 的适配器。
- `DeleteAudit.Viewer`：WPF 离线导入、只读检索、筛选、分页、会话/事件/诊断和 Raw XML 展示。
- `db/schema.sql`：持久化结构的唯一设计基线；运行时迁移必须版本化且不可静默降级。

## 3. 项目目录结构

```text
<仓库根>\
├─ DeleteAudit.sln
├─ Directory.Build.props
├─ README.md
├─ docs\
│  ├─ PROJECT_PLAN.md
│  ├─ THREAT_MODEL.md
│  ├─ PHASE_1A_ACCEPTANCE.md
│  ├─ PHASE_1B_ACCEPTANCE.md
│  ├─ PHASE_2B_ACCEPTANCE.md
│  └─ PHASE_2B_COMPLETION.md
├─ db\
│  ├─ schema.sql
│  └─ migrations\
│     ├─ 0002_phase_1b_offline_import.sql
│     ├─ 0003_phase_2a_live_monitoring.sql
│     ├─ 0004_phase_2b_live_evidence.sql
│     └─ 0005_phase_2b4_live_projection.sql
├─ config\
│  └─ appsettings.example.json
├─ src\
│  ├─ DeleteAudit.Domain\
│  ├─ DeleteAudit.Application\
│  ├─ DeleteAudit.Collector\
│  ├─ DeleteAudit.Infrastructure\
│  └─ DeleteAudit.Viewer\
└─ tests\
   ├─ DeleteAudit.UnitTests\
   ├─ DeleteAudit.IntegrationTests\
   └─ Fixtures\                 # 仅使用脱敏 XML/EVTX/USN 合成样本
```

当前 Collector 宿主仍为空载；获用户手动启动的实时接入运行在 WPF Viewer 当前进程，不注册服务、不后台自启、不打开卷。离线输出、实时数据库与全部测试数据只写仓库 `artifacts` 目录。

## 4. SQLite schema

完整 DDL 见 `db/schema.sql`。主要实体：

- `channel_epochs`：区分事件日志清空前后的 Record ID 命名空间，避免 ID 重用造成误去重。
- `raw_events`：原始 XML、来源、事件时间、观察时间和逐条哈希链；只追加。
- `process_observations`：来自 Sysmon 1（或未来明确批准的等价来源）的命令行、父进程和进程生命期。
- `delete_sessions`：按进程、用户、路径聚合的 10 秒滚动会话。
- `delete_events`：规范化后的删除事实，保留主证据引用、完整路径、主体、进程、权限、会话、初始风险和缺失字段。
- `event_evidence`：把 23/26、4663、USN 与同一个删除事实关联，并保留评分与理由。
- `session_members`、`risk_assessments`、`alerts`：只追加保存聚合成员、风险演进和告警历史。
- `usn_checkpoints`、`integrity_checkpoints`：记录卷级读取位置、跳号/回卷以及已签名完整性检查点。
- `v_delete_audit`：为 Viewer 提供包含原始 XML 与当前最高风险的只读投影。

Phase 1B 增量表：

- `import_sessions`：保存规范化输入路径、大小、修改时间、内容 SHA-256、应用/schema 版本、计数与输出状态；内容哈希唯一。
- `import_records`、`import_diagnostics`：按导入会话和物理序号保留原始 XML 可用性、逐记录结果与结构化诊断。
- `event_correlations`：保存匹配方法、置信度、时间差、证据引用和是否富化身份。
- `risk_assessment_subject_links`：把已有风险评估明确连接到删除会话或删除事件。

Phase 2A / 2B 增量表：

- `0003`：实时会话摘要、通道可用性与有界诊断。
- `0004`：append-only `live_capture_sessions`、`live_capture_records`、`live_capture_completions`；保存 raw XML、digest、parser identity、分类与完成计数。
- `0005`：append-only `live_channel_epochs`、`live_projected_records`、`live_projection_runs`；它们只属于 live path。确定性 projection identity 与独立 continuity chain 从不复用或延长离线身份/序号/链。

所有时间采用两列：UTC 为 RFC 3339 `Z`，本地时间包含数字偏移；另存 Windows 时区 ID 与偏移分钟数。无法从证据可靠得到的必录字段写 `NULL`，并在 `missing_fields_json` 中列明原因，绝不使用伪值。

## 5. 事件关联算法

### 5.1 采集与规范化

1. 为每个事件通道维护 `channel_epoch_id`；检测日志清空、Record ID 倒退或日志实例变化时开启新 epoch，并记录覆盖缺口。
2. 原始 XML 先以固定 UTF-8 编码落入只追加队列，计算 `raw_xml_sha256` 和链式 `entry_hash`，再解析结构化字段。
3. 路径规范化只做确定性字符串转换：统一分隔符、盘符大小写、设备路径到 DOS 卷映射、去除非根路径末尾分隔符。不得跟随重解析点或为了规范化而访问目标文件。
4. 去重键为 `(computer, channel_epoch_id, event_record_id)`；跨来源不直接去重，而是通过证据关联归并。

### 5.2 删除事实判定

- Sysmon 23 或 26 可创建 `confirmed` 删除事实。23 还标记 `archive_expected=true`，仅允许命中配置的高价值目录。
- Security 4663 只有在访问掩码包含 `DELETE` 或 `DELETE_CHILD` 时成为权限证据；它本身不能证明删除最终完成。
- USN 的删除/关闭原因只创建 `gap_candidate` 或补充证据，永远不能单独填充执行者身份。
- 目录类型优先取 USN 文件属性或删除前只读元数据缓存；证据不足时记录 `unknown` 并列入缺失字段。

### 5.3 相关性评分

候选窗口默认以主事件 UTC 时间为中心 `±3 秒`，路径必须相同或能由同一卷文件引用号证明相同对象。建议评分：

| 条件 | 分值 |
| --- | ---: |
| 同一 Process GUID | +60 |
| 同一 PID 且进程生命期重叠 | +35 |
| 同一 SID | +20 |
| 同一规范化完整路径 | +35 |
| 同一卷序列号和文件引用号 | +50 |
| 时间差 ≤1 秒 / ≤3 秒 | +20 / +10 |
| 同一进程路径 | +10 |
| PID 相同但已确认跨进程生命期 | -80 |
| 路径冲突 | -100 |

总分 `>=80` 自动关联，`60–79` 低置信关联并在 Viewer 标记，`<60` 不关联。Process GUID 优先；没有 GUID 时使用 `(boot_id, PID, process_start_utc)`，避免 PID 重用。

### 5.4 会话与批量检测

- 会话聚合键：`process_identity + user_sid + protected_root/路径作用域`。同一键相邻事件间隔不超过 10 秒则加入当前会话，否则新建 GUID 会话。
- 计数只统计唯一 `confirmed` 删除事实，不重复计算同一事实的 23/26、4663 和 USN 证据。
- 任意滚动 10 秒窗口达到 30 项，追加 `warning` 风险评估与告警；达到 100 项，追加 `critical` 并抑制同一会话的重复 warning 通知。
- 命中保护目录的首个 confirmed 删除立即追加 `critical` 告警，不等待窗口结束，但只通知、不阻断。
- 迟到或乱序事件触发受限窗口重算；风险历史只追加，已发出的更高等级不回退。会话静默 10 秒后封存。

### 5.5 字段富化优先级

1. 进程：Sysmon 事件内 Process GUID/PID/Image。
2. 用户：4663 SID 优先，Sysmon User 作为显示名补充；发生冲突时都保留为证据并降低置信度。
3. 命令行与父进程：按 Process GUID 关联 Sysmon Event ID 1。若未来未启用该来源，则字段为 NULL 并报告 `process_enrichment_unavailable`。
4. 删除权限：从 4663 的 AccessList/AccessMask 解析 `DELETE`、`DELETE_CHILD` 或两者；Sysmon-only 事件记为 `not_observed`，不推测。

## 6. 日志防篡改方案

### 6.1 单条与每日链

- 每条记录保存 `raw_xml_sha256 = SHA-256(原始 XML 的固定 UTF-8 字节)`。
- 采用版本化、确定性字段顺序生成 JSONL；`entry_hash = SHA-256(format_version || previous_entry_hash || canonical_record_bytes)`。
- 每日第一条记录引用上一日最后一条哈希，形成跨日链；数据库与 JSONL 保存相同 `event_id/content_hash/entry_hash`，可双向核对。
- 每日 manifest 保存日期、记录数、首尾哈希、JSONL 整文件哈希、前一日 manifest 哈希和 collector build 标识。

### 6.2 签名与锚定

- 未来运行阶段使用 Windows CNG 的不可导出 ECDSA P-256 密钥签署小时/每日检查点；优先使用 TPM-backed key，并把密钥 ACL 限制到专用服务 SID。
- 至少把已签名检查点定期复制到本机之外的追加写存储，才能抵抗本地管理员删除或整体回滚 DB、JSONL 和密钥状态。
- 没有外部锚点时，产品必须把保证表述为“可检测常规篡改和不一致”，不得宣称能抵抗本机管理员。

### 6.3 本机权限与恢复

- `C:\ProgramData\DeleteAudit` 继承关闭后使用显式 ACL：服务 SID 与 SYSTEM 可写；Viewer 用户只通过受限查询 IPC 读取；普通用户无目录遍历权。
- SQLite 使用 WAL、`synchronous=FULL`、外键和 `trusted_schema=OFF`。原始事件、证据、风险历史和检查点表通过触发器禁止 UPDATE/DELETE；可变会话只允许在封存前更新。
- 启动时验证最后检查点、JSONL 尾部和数据库链；任何断链、回滚、日志清空、USN Journal ID 变化或写入失败都生成显式健康告警。
- 保留、归档和磁盘额度策略必须另行批准；Collector 不实现自动删除或清理。

## 7. 测试计划

所有测试使用内存对象、SQLite `:memory:`、内存流和脱敏事件夹具；不通过真实删除制造事件，不打开 D 盘，不更改审计策略。

### 单元测试

- 23/26/4663/Sysmon 1 XML 解析：缺失字段、十六进制 PID/AccessMask、时区、畸形 XML、超长路径和 Unicode。
- Windows 路径规范化：盘符、UNC、设备路径、尾部分隔符、大小写、重解析点文本；验证不触发文件系统访问。
- 相关性评分：PID 重用、GUID 冲突、时间边界、路径冲突、多证据去重。
- 10 秒滚动窗口：29/30/99/100 边界、乱序、迟到、重复事件、保护目录首项告警。
- 哈希链：确定性序列化、跨日链接、单字节篡改、截断、插入、重排和回滚检测。

### 集成测试

- 将固定 XML/USN 夹具送入完整管线，核对 `v_delete_audit` 的必录字段、缺失原因和风险历史。
- 在 SQLite `:memory:` 验证 schema、外键、唯一键、只追加触发器、会话封存约束和事务回滚。
- 通过内存 JSONL sink 验证数据库/JSONL 一致性、故障注入、重复投递和崩溃恢复语义。
- 对只读查询接口做授权矩阵测试，证明 Viewer 不能调用写入、配置或系统控制操作。

### 性能与可靠性

- 用合成内存事件流进行 10 万条突发、1000 个并发会话、队列背压和慢存储测试。
- 明确 SLO：正常负载下 P95 入库延迟、保护目录告警延迟、恢复点目标、最大可接受事件缺口。
- 以固定时钟做跨午夜、夏令时切换、系统时间回拨和重启测试；所有关联以 UTC 为准。

### 验收测试（未来、需单独批准）

- 仅在隔离 VM 中消费预置 EVTX 和模拟 USN 适配器；不得在真实工作站创建或删除测试文件。
- Sysmon/4663 不可用时，UI 必须显示 `degraded` 覆盖状态，且不会把不完整数据标为完整审计。

## 8. 实现阶段拆分

### 阶段 0：设计与空载骨架（已完成）

- 固化威胁模型、schema、事件契约、目录结构、配置样例和测试边界。
- 骨架不得读取事件日志、USN 或 ProgramData；默认不产生后台活动。
- 退出条件：八项设计可评审，禁止事项可由静态检查确认。

### 阶段 1：领域模型与离线解析

- 实现不可变领域类型、XML 解析、路径规范化、字段缺失模型和夹具测试。
- 仅处理仓库内脱敏夹具；无 Windows 系统调用。

### 阶段 2：持久化与完整性

- 实现 SQLite 仓储、内存/文件 JSONL sink、哈希链、manifest 和启动验证。
- 文件 sink 先仅在明确指定的测试目录启用；不实现保留清理。

### 阶段 3：关联、会话与风险引擎

- 实现证据评分、PID 生命期、滚动窗口、保护目录和只追加风险历史。
- 用合成数据完成正确性与性能门槛。

### 阶段 4：只读 Windows 采集适配器（Phase 2A / 2B 已完成当前进程模式）

- 用户显式启动后接入已经存在的 Sysmon/Security 事件通道；USN 适配器仍未实现。
- 不安装 Sysmon、不启用审计策略、不注册服务；权限不足时清晰降级。

### 阶段 5：WPF Viewer（离线与 Phase 2B 页面已实现）

- Phase 1C 已实现离线导入/查询；Phase 2B 已增加实时预览、Live History、派生分析和 live-owned 规范投影页面。
- 未来生产数据仍须经受限本机查询 IPC；覆盖健康和完整性检查点页面尚未实现。
- 默认隐藏敏感命令行细节；按显式授权显示。

### 阶段 6：安全加固与运维验证

- 设计专用服务 SID、ProgramData ACL、CNG/TPM 签名、外部锚定、备份与恢复演练。
- 任何 ACL、证书或系统配置变更都需要新的明确批准。

### 阶段 7：部署（不属于当前授权）

- Windows Service 注册、Sysmon 配置、4663 审计策略、安装包和生产保留策略分别评审、审批和回滚演练。
- 产品始终保持“审计与告警”，不演变为删除拦截、进程终止或内核驱动。

## 关键未决策项

- 保护目录的精确清单及 Sysmon 23 存档额度，需要管理员在部署阶段确认。
- 外部不可变锚点的目标系统和网络失败策略尚未选择。
- 命令行可能含秘密；显示脱敏、访问授权和保留期限需要数据治理决定。
- USN Journal 访问权限与性能预算必须在隔离 VM 验证，不能在当前阶段假设可用。
