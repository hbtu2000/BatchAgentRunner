# 技术设计文档（TDD）

## AI 批处理任务调度工具（Batch Agent Runner）

---

**文档负责人：** 工程
**状态：** 🟡 初稿（随 M0 验证结论迭代；v1.1 按需求变更「模型池挂账号下」修订）
**最后更新：** 2026-10-15
**版本：** 1.1

**上游文档：**
- 产品需求文档：`docs/PRD-BatchAgentRunner-v2.0.md`（内容版本 v1.11）
- 评审报告（初核 / R2 复审）：`docs/archive/REVIEW-PRD-AI批处理任务调度工具-v1.0.md`、`-r2.md`

**本设计覆盖：** PRD 一期 **P0 全量 + P1 全量**（用户确认范围）
**开发语言 / 平台：** C# / .NET 10，界面 Avalonia 12（跨平台桌面，Windows 优先）

**相关设计文档：**
- 二期扩展设计：`docs/DESIGN-BatchAgentRunner-v2.0.md`（流程模板/多阶段编排、DAG 依赖、质量门禁、场景模板库 S1–S16、REQ-016/017/018、Markdown 原地回写、断点会话恢复）
- 凭证快照与账号切换：`docs/DESIGN-BatchAgentRunner-v2.0.md`（会话恢复方案 b 的 `ICredentialVault` 基础）

---

## 1. 设计目标与约束

### 1.1 设计目标

| 目标 | 来源 | 设计落点 |
|------|------|---------|
| 无人值守跑完一批任务，结果完整回写、可断点续跑 | REQ-001/002/006/008 | 任务文件即状态源 + 原子回写 + 启动前备份 |
| 双维度降级「账号 × 自有候选模型池」应触发必触发（v1.1：模型池挂账号下） | REQ-003/004 | `DegradationMatrix` 各账号 × 自有模型池有序遍历 + 可用性过滤 |
| 免费时段感知（组合级时段：同一模型在不同账号可配不同时段，时段外跳过不计失败、不告警） | REQ-003b | `AvailabilityEvaluator`，时段随账号下模型项配置，时段外为独立不可用原因 |
| 额度重置时间感知（冷却期内不调用、不计尝试） | REQ-004c | `QuotaCooldownRegistry`，恢复时间 = max(重置, 下一时段开始) |
| 断点任务可用兜底模型收尾（每账号独立兜底池） | REQ-004b | `FallbackPolicy`，按「账号顺序 × 账号内兜底池顺序」选取，双重成本上限 |
| 实时可见、随时可停、停了可续 | REQ-007/008 | Avalonia MVVM + 引擎事件流 + 协作式取消 |
| 汇总报告、受控并发、失败即停、日志持久化、自动关机 | REQ-011/012/013/014/015 | P1 模块（本设计一并覆盖） |

### 1.2 硬约束

1. **M0 技术验证未完成**：WorkBuddy / CodeBuddy CLI 的 headless 参数、模型参数、额度错误特征、重置时间格式、用量数据均未实测（PRD 第 13 章 V-1~V-10）。因此适配层必须**契约先行、实现可插拔**，并内置**仿真适配器**让全链路在 M0 前即可验证。
2. **纯本地**：无云端同步、无 외부服务（PRD 第 3 章范围外）。
3. **安全**：凭据不明文落任务文件/日志；危险操作确认**永不自动批准**（REQ-002b）。
4. **状态一致性**：内存任务列表为唯一事实来源；单写者串行回写（REQ-012）。

---

## 2. 技术选型

| 维度 | 选型 | 理由 |
|------|------|------|
| 运行时 | .NET 10（已装 SDK 10.0.401） | 环境就绪；长期支持版本；跨平台 |
| 界面 | **Avalonia 12.1 + Fluent 主题**（用户选定） | 跨平台桌面壳，XAML + MVVM，与 C# 技术栈统一；PRD 第 16 章「Web UI vs 桌面壳」按用户决策取桌面壳 |
| MVVM 框架 | CommunityToolkit.Mvvm 8.4（`ObservableObject`/`RelayCommand`/`ObservableProperty` 源生成器） | 轻量、无侵入；Avalonia 官方模板自带 |
| DI | Microsoft.Extensions.DependencyInjection | 标准容器，便于测试替换与分层解耦 |
| 日志 | Microsoft.Extensions.Logging.Abstractions（Core 仅依赖抽象）+ Infrastructure 文件实现 | Core 不绑定具体实现 |
| 序列化 | System.Text.Json（源生成器友好、UTF-8、无第三方依赖） | 任务文件/配置/冷却记录/报告统一 |
| 测试 | xUnit + FluentAssertions 风格断言（用原生 Assert 亦可） | 标准 |
| 子进程 | System.Diagnostics.Process + 进程树回收 | REQ-008「无僵尸进程残留」 |

**不采用：** Blazor/ASP.NET Core（用户选桌面）；Electron/Tauri（PRD 范围外）；ReactiveUI（模板默认可换，Toolkit 更轻）。

---

## 3. 总体架构

### 3.1 分层

```
┌──────────────────────────────────────────────────────────────┐
│  BatchAgentRunner.Desktop  (Avalonia, MVVM)                  │
│  配置页 / 执行监控页 / 报告页   ← 引擎事件（Observable）驱动    │
└───────────────────────────┬──────────────────────────────────┘
                            │ 引用
┌───────────────────────────▼──────────────────────────────────┐
│  BatchAgentRunner.Agents      │  BatchAgentRunner.Infrastructure│
│  IAgentCliAdapter             │  任务文件加载/回写（原子+备份）  │
│   WorkBuddyAdapter (骨架)     │  配置存储 / 冷却存储 / 运行日志  │
│   CodeBuddyAdapter (骨架)     │  汇总报告 / 自动关机             │
│   MockAdapter (仿真)          │                                 │
│   CliProcessRunner            │                                 │
└───────────────────────────┬──────────────────────────────────┘
                            │ 实现 Core 抽象
┌───────────────────────────▼──────────────────────────────────┐
│  BatchAgentRunner.Core                                        │
│  Domain（实体/枚举/值对象） · Scheduling（可用性/降级矩阵/额度）  │
│  Engine（BatchEngine 主循环 + 批级状态机） · Abstractions（端口）│
└──────────────────────────────────────────────────────────────┘
```

**依赖方向：** Desktop → Agents/Infrastructure → Core。Core **零外部实现依赖**（仅 Logging.Abstractions）。

### 3.2 项目与目录

```
BatchAgentRunner.sln
├─ Directory.Build.props
├─ src/
│  ├─ BatchAgentRunner.Core/
│  │   ├─ Domain/        TaskItem, BatchRunState, AccountConfig（含自有候选/兜底模型池）,
│  │   │                 ModelCandidate, FreeTimeWindow, QuotaCooldownRecord, ExpertRef, Enums
│  │   ├─ Options/       RunOptions（全部可配置参数 + 默认值）
│  │   ├─ Scheduling/    AvailabilityEvaluator, DegradationMatrix,
│  │   │                 QuotaErrorClassifier, ResetTimeParser, RetryPolicy
│  │   ├─ Engine/        BatchEngine, BatchStateMachine, TaskExecutionContext
│  │   ├─ Events/        BatchEvent（任务/切换/等待/阻塞/兜底/终止…）
│  │   └─ Abstractions/  IAgentCliAdapter, ITaskFileStore, ICooldownStore,
│  │                     IRunLogger, IConfigStore, IReportWriter, IShutdownService
│  ├─ BatchAgentRunner.Agents/
│  │   ├─ Adapters/      WorkBuddyAdapter, CodeBuddyAdapter, MockAdapter, AdapterFactory
│  │   └─ Internal/      CliProcessRunner, DangerousOperationGuard, PromptClassifier
│  ├─ BatchAgentRunner.Infrastructure/
│  │   ├─ TaskFiles/     JsonTaskFileStore, MarkdownTaskParser, TaskFileWriter, BackupManager
│  │   ├─ Persistence/   JsonConfigStore, JsonCooldownStore, AtomicFileWriter
│  │   ├─ Logging/       FileRunLogger（运行日志，REQ-014）
│  │   ├─ Reporting/     JsonReportWriter + MarkdownReportWriter（REQ-011）
│  │   └─ System/        ShutdownService（REQ-015）
│  └─ BatchAgentRunner.Desktop/
│      ├─ Views/         MainWindow, ConfigView, MonitorView, ReportView, TaskDetailView
│      ├─ ViewModels/    MainViewModel, ConfigViewModel, MonitorViewModel, ReportViewModel…
│      ├─ Services/      EngineHost（DI 组装 + 事件桥接 UI 线程）
│      └─ Converters/    状态徽标颜色/耗时/倒计时格式化
└─ tests/BatchAgentRunner.Tests/  Core / Scheduling / TaskFiles / Engine
```

---

## 4. 领域模型

### 4.1 任务项 `TaskItem`（对齐 PRD 第 10 章数据模型 + 附录 A）

| 字段 | 类型 | 说明 |
|------|------|------|
| `Id` | string | 稳定 ID（REQ-001a：缺失自动生成 `task-001`，重复/非法字符加载期报错） |
| `Workspace` | string? | 命名空间名或目录路径；三级覆盖：任务 > 本批 > 全局（REQ-003a） |
| `Instruction` | string | 完整指令（Markdown 多行合并，REQ-001） |
| `Status` | `TaskStatus` | Pending / Running / Done / Failed / Interrupted |
| `Result` | string? | 结果；部分完成时前缀「[部分输出]」（REQ-008） |
| `ModelUsed` / `AccountUsed` / `ExpertUsed` | string? | 实际执行者 |
| `FallbackUsed` | bool | 是否兜底完成 |
| `FallbackCost` | decimal? | 兜底花费（实测或估算，`CostIsEstimated` 标注口径） |
| `RetryCount` | int | 重试次数 |
| `Error` | string? | 失败归因（**不允许静默失败**） |
| `StartedAt` / `FinishedAt` | DateTimeOffset? | 时间元数据 |
| `DurationSeconds` | double | 秒（v1.10 定稿） |

二期预留字段（`workflow` / `depends_on` / `max_loops` / `batch_size`）**一期不建模、不落盘**，只在 JSON 反序列化时忽略未知字段以保证前向兼容（PRD 15.4）。

### 4.2 批级运行态 `BatchRunState`

```csharp
enum BatchStatus { Idle, Running, Waiting, Blocked, Terminated, Completed }
```

- `Waiting` 为**独立批级状态**，启动预检等待与运行中等待同一语义（REQ-003b/004c/010）
- 运行态**不落任务文件**，随运行日志落盘；冷却记录单独持久化且重启有效

### 4.3 配置 `RunOptions`（关键默认值对齐 PRD）

| 配置 | 默认 | 来源 |
|------|------|------|
| `AgentKind` | WorkBuddy | REQ-002 |
| `DegradationStrategy` | `ModelFirst` | REQ-004（可切 AccountFirst） |
| `MaxRetriesPerTask`（普通重试） | 2 | REQ-009 |
| `RateLimitBackoffRetries`（临时限流重试） | 3，指数退避 | REQ-005 |
| `TaskTimeoutMinutes` | 10（标注假设） | 边缘情况 4 |
| `MaxAttemptsPerTask`（总尝试上限） | 10 | REQ-009a |
| `MaxTotalMinutesPerTask`（总时长上限） | 60 | REQ-009a |
| `MaxWaitMinutes`（最长可等待时长） | 60 | REQ-003b |
| `DefaultCooldownMinutes`（解析不到重置时间时） | 60 | REQ-004c |
| `Concurrency` | 1（串行） | REQ-012 |
| `FallbackEnabled` | false | REQ-004b |
| `ForceRerun` | false | REQ-008 |
| `StopOnFailure` | false | REQ-013 |
| `AutoShutdownEnabled` / `ShutdownOnlyWhenZeroFailure` / `CountdownSeconds=60` | false / false / 60 | REQ-015 |
| `ResultMaxKB` | 100（超出截断并提示全量日志位置） | 第 8 章 |

### 4.4 账号配置 `AccountConfig`（v1.1：候选/兜底模型池挂账号下）

```csharp
class AccountConfig
{
    string Id;                  // 稳定标识，冷却记录按它落盘
    string DisplayName;
    string CredentialRef;       // 凭据引用（profile:/env:/keychain:），不明文落地
    bool   Enabled = true;
    string? Note;
    List<ModelCandidate>? ModelCandidates;  // 该账号自有候选模型池（有序）；null → 回退全局共享列表
    List<FallbackModel>?  FallbackModels;   // 该账号自有兜底模型池（有序，不参与免费时段过滤）
}

class ModelCandidate
{
    string Name;                        // 账号内唯一；不同账号可同名（时段/单价各自独立）
    FreeTimeWindow? FreeTimeWindow;     // null = 全天可用
    decimal? EstimatedUnitPrice;
}

record FallbackModel(string Name, decimal? EstimatedUnitPrice);
```

要点（对齐 REQ-003/003b/004b，v1.12）：
- **模型池是账号的子配置**：降级矩阵 = 各启用账号 × 其自有候选池（不再是「账号池 × 全局列表」笛卡尔积）
- **免费时段天然是「账号 × 模型」组合级**——同一模型名（如 Hy4）在 A 账号可设 0:00–8:00、B 账号设 0:00–24:00
- 兼容策略：全局 `ModelCandidates` 保留为共享默认池，仅对未配置自有池的账号生效；`Fallback.Enabled / MaxCostPerTask / MaxCostPerBatch` 保持全局，约束所有账号的兜底行为
- 实现约束沿用项目约定：集合声明用具体 `List<T>`（非接口类型，避免 STJ 反序列化 `NotSupportedException`）；字段 snake_case；派生属性标 `[JsonIgnore]`

### 4.5 免费时段 `FreeTimeWindow`

```csharp
record FreeTimeWindow(TimeOnly Start, TimeOnly End, IReadOnlySet<DayOfWeek>? Days);
```

判定规则（REQ-003b，`days` 按**时段覆盖日**）：
- 未配置 → 全天可用
- `Start == End` → 全天
- 不跨零点（`Start < End`）：当日 `Start ≤ now.TimeOfDay ≤ End`（**按分钟闭区间**，写 `End` 即包含该整分钟，下一分钟才离开时段）且 `now.DayOfWeek ∈ Days`
- 跨零点（`Start > End`，如 22:00–06:00 或 23:00 → 次日 07:59）：区间拆为「当日 Start → 次日 End」；`now` 落在任一半段即视为在时段内（同样按分钟闭区间，如 `23:00 → 07:59` 覆盖到次日 08:00 之前）；`Days` 检查**开始日与结束日**（覆盖任一日命中即生效，支持「周日 22:00 → 周一 06:00」）
- 下一可用开始时间 `NextStartAfter(now)`：用于 waiting 倒计时

### 4.6 冷却记录 `QuotaCooldownRecord`

```csharp
record QuotaCooldownRecord(string AccountId, string ModelName,
                           DateTimeOffset ResetAt, CooldownSource Source);
enum CooldownSource { CliParsed, DefaultFallback }
```

- 键 = `账号 × 模型`；恢复后再次耗尽 → 新记录覆盖
- 不可靠重置时间（早于当前 / 无法解析）→ 降级默认冷却并标注 `DefaultFallback`（边缘情况 13）

---

## 5. 核心算法

### 5.1 组合可用性评估 `AvailabilityEvaluator`

对组合 `(account, model)` 在时刻 `now` 求值（时段窗口取该账号下模型项自己的配置，v1.1）：

```
Evaluate(account, model, now):
   windowOut   = combo.Window != null && !combo.Window.Contains(now)   // combo.Window = account.ModelCandidates[i].FreeTimeWindow
   nextWindow  = windowOut ? model.Window.NextStartAfter(now) : null
   cooldown    = registry.TryGet(account, model, out resetAt) && resetAt > now
   resetAt     = cooldown ? resetAt : null

   available   = !windowOut && !cooldown
   restoreAt   = available ? now
                           : max(nextWindow ?? now, resetAt ?? now)   // 取较晚者（REQ-004c）
   reason      = available ? None
                           : (cooldown && resetAt >= (nextWindow ?? now) ? Cooldown : OutsideFreeWindow)
```

> **两种不可用原因严格分离**（PRD 亮点）：`OutsideFreeWindow`（预期调度行为）与 `Cooldown`（额度维度）分别导致 `model_window_skipped` / `model_cooldown_skipped` 两个不同事件，均**不计失败、不触发切换告警、不污染误切换率**。

### 5.2 降级矩阵 `DegradationMatrix`

组合序列在引擎启动时一次性展开（配置变更即重建，REQ-004，v1.1 语义）：
- **ModelFirst（默认）**：`foreach account in 账号池（启用且有序）: foreach model in 该账号自有候选池（有序；未配置自有池则回退全局共享列表）`
- **AccountFirst**：按「模型名」聚合——`foreach model name（按各账号候选池中的首次出现顺序）: foreach account（仅取配置了该模型名的启用账号，按账号顺序）`

> 组合总数 = Σ 各账号自有候选池大小；同名模型在不同账号下视为**不同组合**（时段/单价/冷却均独立，冷却键仍为 `账号 × 模型`）。

选点逻辑 `NextAvailable(from, now)`：
1. 从 `from` 在序列中的位置往后找第一个 `Evaluate(...).Available` 的组合
2. 找不到则从头到 `from` 之前再找一轮（循环一次，避免过早 blocked）
3. 仍无 → 返回 `null`，由引擎进入「兜底 / waiting / blocked」判定

被跳过的组合**实时**记录 `SkipReason` 并发出事件（供界面 toast 与报告统计）。

### 5.3 额度错误识别与重置时间解析

`QuotaErrorClassifier`（适配层归一化后的 `AgentFailure` 输入）：

| 类别 | 判据（模式库，可按 CLI 扩展） | 处理 |
|------|--------------------------|------|
| `QuotaExhausted` | quota / quota_exceeded / 额度已用尽 / out of credits / insufficient balance / billing hard limit / 402 … | 记录冷却 + 切换组合 |
| `RateLimited` | 429 / rate limit / too many requests / try again later | 指数退避 3 次重试**同一组合**，仍失败才升级切换 |
| `DangerousOperation` | 交互点命中危险黑名单 | 立即失败该任务，记录交互点 |
| `AuthExpired` | 未登录/凭据过期/401/403（非额度） | 停用该账号，切换下一账号 |
| `TaskError` | 其余非零退出 | 普通重试 2 次 → failed |
| `Timeout` | 超过 `TaskTimeoutMinutes` | 按失败处理，保留已产出部分输出 |

`ResetTimeParser` 归一化三类输入（REQ-004c / V-10）：
1. ISO 8601 绝对时间戳
2. 相对时长（`3h24m`、`in 2 hours`、`90s`、`120 分钟`）
3. HTTP `Retry-After`（秒数或 HTTP-date）

解析结果 `< now` 或超长异常 → `DefaultFallback`（默认冷却时长）。

### 5.4 引擎主循环 `BatchEngine`

```
Start():
  预检（REQ-010）→ 加载任务（跳过 done，除非 ForceRerun）→ ID 快照校验（REQ-001a）
  → 备份任务文件 → Status = Running → 调度循环

调度循环（单任务视角，串行；并发见 §9）：
  while 有待处理任务 && 未终止:
      task = 下一个 pending/interrupted 任务
      ctx  = new TaskExecutionContext(task)   // 总尝试/总时长上限计时起点
      loop:
         受总上限约束检查（REQ-009a）→ 超限则 failed
         combo = matrix.NextAvailable(current, now)

         if combo == null:                       // 免费组合矩阵当前无可用
             if 任务未完成 && FallbackEnabled && 未超成本上限:
                 fallbackCombo = 第一个可用兜底组合   // 按「账号顺序 × 该账号兜底池顺序」遍历（v1.1）
                 if fallbackCombo != null:
                     用它执行（不受时段过滤）→ 成功则 done 并记 fallback_cost → 下一条
             earliest = 全部组合中最早 restoreAt
             if earliest <= now + MaxWaitMinutes:
                 Status = Waiting(earliest) → 倒计时（可取消；配置变更即重评提前退出）
                 到点 → 回到 loop；取消 → Blocked，当前任务 interrupted
             else:
                 Status = Blocked → 当前任务 interrupted（保留部分输出）→ 退出循环

         r = adapter.ExecuteAsync(task.Instruction, combo, expert, workspace, ct)
         switch r.Failure:
            None              → 写回 done → 下一条
            RateLimited       → 退避重试同一组合（≤3）→ 仍失败按 QuotaExhausted 升级
            QuotaExhausted    → 记录冷却（解析/默认）→ combo 前进 → 继续 loop（**同一任务，完整指令重发，不继承上下文**）
            AuthExpired       → 停用账号 → combo 前进
            DangerousOperation→ task failed（注明命中点）→ 下一条
            其它              → 重试（≤2）→ 仍失败 failed → 下一条
```

**终止（REQ-008）**：协作式 `CancellationToken` → adapter 回收子进程树 → 当前任务 `Interrupted`（**不写成功**）→ 全部落盘 → `Terminated`。支持「等待当前任务完成后再终止」。

**续跑**：以任务文件/sidecar 的 `status` 为准，`done` 跳过并计数「已跳过 N 条」；`Interrupted` 重跑时清除旧「部分输出」（旧值留运行日志）。

**批级终态**：全部任务到达终态 → `Completed`；存在 blocked/waiting 则不触发完成（影响自动关机条件，REQ-015）。

### 5.5 重试与上限（REQ-009a）

单任务维护 `Attempts`（实际发起的调用次数，冷却跳过的组合**不计**）与 `TotalElapsed`；任一超限 → `failed`（错误信息注明超限类型）。退避等待计入总时长。

---

## 6. Agent 适配层契约

```csharp
public interface IAgentCliAdapter
{
    AgentKind Kind { get; }
    Task<PreflightResult> ValidateAsync(AccountConfig account, CancellationToken ct); // REQ-010 极简调用
    Task<AgentExecutionResult> ExecuteAsync(AgentRequest request, CancellationToken ct);
}

public record AgentRequest(
    string Instruction, AccountConfig Account, ModelCandidate Model,
    string? ExpertId, string WorkingDirectory, TimeSpan Timeout,
    AutoAnswerPolicy AutoAnswer,        // 默认选项映射 + 危险黑名单/白名单
    Action<string>? OnOutputChunk);      // 实时输出流（REQ-007）
```

`AgentExecutionResult`：`Succeeded` / `RawOutput` / `Failure(AgentFailureKind, Message, ResetAt?, Usage?)`。

**`CliProcessRunner`（Agents/Internal）职责：**
- 以 `RedirectStandardOutput/Input/Error` 启动子进程，`WorkingDirectory` = 任务工作空间（REQ-003a）
- 增量转发 stdout → `OnOutputChunk`（界面流式追加）
- **零人工介入（REQ-002a）**：优先 CLI 官方无人值守参数；检测「无输出增长超阈值」判定 stdin 等待；普通确认注入 `default_answer`，选择型交互（含编号选项）按 `default_choice_index` 自动选择对应选项；无法注入 → 超时失败，不挂起
- **危险操作防护（REQ-002b）**：`DangerousOperationGuard` 维护黑名单（删除文件 / 系统配置修改 / `git push --force` / `rm -rf` / 格式化 …）与白名单；**命中黑名单永不自动批准**，该任务标记 failed 并醒目标记日志（REQ-010b）
- **进程树回收**：终止/超时/崩溃时 kill 子进程树，无残留（第 8 章可靠性）

**`MockAdapter`（仿真）**：按脚本返回成功 / 限流 / 额度耗尽（可带重置时间）/ 危险操作 / 超时，用于端到端验证全部降级与等待逻辑，支撑 M0 前的开发自测。

**WorkBuddy/CodeBuddy Adapter 骨架**：参数拼装集中在 `CliCommandBuilder`（`-p/--prompt`、`--model`、`--output-format`、无人值守开关等），**真实参数与错误特征待 M0 实测后填充**，现阶段以配置项 `CliArgumentsTemplate` 外置，避免硬编码猜测。

---

## 7. 持久化与文件契约

| 内容 | 位置 | 说明 |
|------|------|------|
| JSON 任务文件 | 用户指定 `.json` | **原地原子回写**（临时文件 + `File.Move(overwrite:true)`） |
| Markdown 任务文件 | 用户指定 `.md` | **只读输入**；状态/结果写 sidecar `<name>.md.result.json`；续跑以 sidecar 为准 |
| 备份 | `<name>.backup-<yyyyMMddHHmmss>` | 每次启动前，保留最近 5 份（REQ-006） |
| 冷却记录 | `%LOCALAPPDATA%/BatchAgentRunner/cooldowns.json` | 重启有效（REQ-004c） |
| 配置 | `%LOCALAPPDATA%/BatchAgentRunner/config.json` | 凭据**不明文落地**为设计约束：账号仅存标识 + 凭据引用（profile/env/keychain 引用），日志脱敏 |
| 运行日志 | `<任务文件目录>/runs/run-<时间戳>.log` | task_started/finished、model_switched、account_switched、model_window_skipped、quota_reset_recorded、model_cooldown_skipped、fallback_used、batch_blocked/resumed/terminated（第 11 章） |
| 汇总报告 | `<任务文件目录>/report-<时间戳>.json/.md` | REQ-011：成败统计、账号/模型使用量、切换时间线、兜底花费、失败清单；切换原因四段区分 + 时段外/冷却累计等待时长 |

**原子写（`AtomicFileWriter`）**：写同目录临时文件 → `Flush(true)` → `File.Move(tmp, target, overwrite: true)`；失败保留临时文件不破坏原文件。

---

## 8. 界面设计（Avalonia）

### 8.1 页面

1. **配置页**（分组，PRD 第 9 章 9 个配置域）：任务文件与执行工具 / 工作空间（多空间 CRUD + 全局默认 + 本批默认）/ 账号池（增删排序启停 + **每账号编排自有候选模型池与兜底模型池**：增删、拖拽排序、免费时段配置，跨零点以「周日 22:00 → 周一 06:00」预览；同一模型在不同账号可配不同时段）/ 全局兼容候选模型列表（仅对未配置自有池的账号生效）/ 专家或专家团 / 兜底全局开关与成本上限（单任务 + 单批，默认关）/ 危险操作白名单 / 并发·重试·超时·总上限·降级策略 / 自动关机（二次确认 + 倒计时说明）
2. **执行监控页（主页面）**：顶部总进度条 + 状态统计卡（pending/running/done/failed/interrupted + 批级状态 + 已跳过计数）；中部任务列表（状态徽标、当前账号/模型/专家/工作空间、兜底标识）；底部当前任务实时输出流；右侧/抽屉任务详情（指令 + 输出 + 结果 + 错误）；toast 区（切换/兜底/时段/冷却/危险操作提示）
3. **报告页**：汇总视图（P1，REQ-011）

### 8.2 六种批级状态视图

`Idle`（空状态引导）· `Running` · `Waiting`（倒计时 + 取消等待，取消转 Blocked）· `Blocked`（提示下一恢复时间点，可续跑）· `Terminated`（可续跑 + 强制重跑）· `Completed`（含失败清单入口）

### 8.3 数据流

```
BatchEngine ──(BatchEvent)──▶ EngineHost(订阅)
                                  │ Dispatcher.UIThread.Post
                                  ▼
                         MonitorViewModel (ObservableCollection<TaskRowViewModel>)
                                  │ 绑定
                                  ▼
                         Avalonia View（≤2s 刷新：事件驱动，非轮询）
```
终止 / 续跑 / 取消等待 / 强制重跑 → ViewModel 命令 → Engine API。

---

## 9. 受控并发（P1，REQ-012）

- 默认 `Concurrency = 1`（串行）；>1 时并行派发
- **额度/冷却/时段状态共享**：`AvailabilityEvaluator` 与 `QuotaCooldownRegistry` 为线程安全单例（`lock` + 不可变快照），任一任务触发耗尽/时段边界后立即对所有任务生效
- **单写者**：`TaskFileWriter` 串行队列执行回写，内存状态为事实来源
- **终止顺序**：停止派发 → 逐一回收子进程 → 全部落盘 → 反馈已终止
- 界面：「当前任务」展示全部 running 卡片；账号/模型按任务分别展示

---

## 10. 自动关机（P1，REQ-015）

- 触发条件：批级 `Completed`（全部任务到达终态，无 blocked/waiting）且未被终止；默认允许带 failed 关机，勾选「仅失败率为 0」时存在 failed 则阻止
- 二次确认 + **60 秒可撤销倒计时**（界面「取消关机」按钮）
- 顺序硬约束：**先全部落盘并回收子进程 → 再关机**
- Windows：`shutdown /s /t 0`（权限不足 → 降级提示手动关机并记录）；macOS：AppleScript `shut down`
- 未触发时在界面与报告标注原因（边缘情况 7）
- **实现状态（2026-10-17 补齐，技术债务 P0-10）**：原实现判定条件后直接执行关机指令，跳过可撤销倒计时。
  现在策略下沉到 `Core/Engine/AutoShutdownCoordinator`（条件判定 + 倒计时委托），
  Desktop 以 `Views/ShutdownCountdownWindow` 提供倒计时窗口（剩余秒数 +「立即关机」/「取消关机」），
  只有「倒计时走完未被取消」才调用 `IShutdownService.TryShutdownAsync()`。
  倒计时在应用内计时而非 OS 级延时：进程不在时默认**不关机**（失效即保护），因此系统命令仍是 `/t 0` 立即执行。
  任何异常/被取消/窗口不可用 → `ShutdownOutcome.Executed = false` 并把原因写进报告。

---

## 11. M0 验证项与本设计的接口映射

| M0 项 | 影响的本设计落点 | 未验证时的设计兜底 |
|-------|----------------|-------------------|
| V-1 headless 调用与输出捕获 | `CliCommandBuilder` / `CliProcessRunner` | 参数模板外置为配置，不硬编码 |
| V-2 模型参数 | `CliCommandBuilder` | 配置 `CliArgumentsTemplate` 占位 |
| V-3 额度错误特征 | `QuotaErrorClassifier` 模式库 | 模式库配置化 + 手动标记额度耗尽入口 |
| V-4 多账号切换 | `AccountConfig` 凭据策略 | 账号仅存标识 + 凭据引用，切换策略可替换；**凭据备份/切换专项设计见 `docs/DESIGN-BatchAgentRunner-v2.0.md`（凭证快照 Vault 方案）** |
| V-5 专家参数 | `AgentRequest.ExpertId` | 不支持时降级为 prompt 注入角色描述 |
| V-6 无人值守交互覆盖 | `CliProcessRunner` + `PromptClassifier` | stdin 等待检测；确认型注入默认应答，选择型按索引自动选默认项 |
| V-7 用量/费用数据 | `AgentUsage` / `FallbackCost` | 不可得 → 调用次数 × 预估单价，报告标注「估算」 |
| V-8 会话恢复 | 一期不依赖 | 一律「完整指令重新执行」（方案 a） |
| V-9 危险操作默认项 | `DangerousOperationGuard` 黑白名单 | 黑名单命中一律拒绝自动批准（保守默认） |
| V-10 重置时间格式 | `ResetTimeParser` | 不可得 → 默认冷却时长（可配）并标注来源 |

---

## 12. 测试策略

| 层 | 用例 |
|----|------|
| `FreeTimeWindow` | 跨零点（22:00–06:00）边界、星期覆盖日（周日 22:00 → 周一 06:00 命中 mon）、未配置=全天、`NextStartAfter` |
| `QuotaCooldownRegistry` | 恢复时间 = max(重置, 下一时段开始)；恢复后覆盖；持久化往返 |
| `DegradationMatrix` | ModelFirst / AccountFirst 遍历顺序（含账号自有池展开与全局列表回退）；同名模型不同账号不同时段分别评估；时段外与冷却组合被跳过；冷却跳过不计尝试 |
| `FallbackPolicy` | 按「账号顺序 × 账号内兜底池顺序」选取第一个可用兜底组合；全局开关/成本上限约束所有账号兜底池；兜底组合不参与时段过滤 |
| `ResetTimeParser` | ISO / 相对时长 / Retry-After / 不可靠值降级 |
| `QuotaErrorClassifier` | 额度 vs 限流 vs 认证 vs 危险操作 |
| `MarkdownTaskParser` | `## id` + `- instruction:` 多行合并至下一二级标题；缺失 ID 生成；重复 ID 报错定位 |
| `TaskFileWriter` | 原子写、备份保留 5 份、sidecar 回写不改动 md 原文 |
| `BatchEngine`（MockAdapter） | 端到端：额度耗尽自动切换、全矩阵耗尽→兜底、全冷却→waiting→恢复续跑、终止→interrupted→续跑跳过 done、危险操作→failed 不阻塞 |

---

## 13. 里程碑

| 里程碑 | 内容 | 依赖 |
|-------|------|------|
| D0 | 本设计 + 项目骨架 + Core 领域/调度算法 + 单元测试 | — |
| D1 | Agents 适配层（含 Mock）+ 引擎主循环贯通 | D0 |
| D2 | Infrastructure 持久化（回写/备份/冷却/日志/报告/关机） | D0 |
| D3 | Avalonia 界面三页 + 六种状态 | D1/D2 |
| D4 | M0 实测 CLI → 填充真实 adapter 参数与错误模式库 → 自用验证（3 批 × ≥50 条） | M0 |

---

## 14. 风险与开放问题

| 风险 | 设计缓解 |
|------|---------|
| CLI 能力未知导致返工 | 适配层契约先行 + 仿真适配器 + 参数/错误模式库外置配置 |
| 额度误判（限流当耗尽） | 限流先退避 3 次；模式库可迭代；误切换率可在报告中统计 |
| 自动批准放大破坏 | 危险黑名单命中即失败；全量自动应答日志（REQ-010b） |
| 子进程残留 | 进程树管理 + 超时兜底 + 终止流程统一回收 |
| 回写冲突 | 单写者串行；外部修改仅提示不合并 |
| 系统时间跳变导致时段误判 | 以 wall-clock 为准，恢复/唤醒后立即重评；时段外跳过不计失败 |
| 界面刷新延迟 | 事件驱动（非轮询），UI 线程批量合并刷新 |

**开放问题（待 M0 或用户确认）：** 单任务超时/总上限/最长等待/默认冷却四个默认值需真实任务校准（PRD 第 16 章）；跨零点时段+星期过滤的极端组合（如 `days` 只含某天且跨零点）以「覆盖任一日命中」为准已在 §4.5 定义。
