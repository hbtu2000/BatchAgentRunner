# 技术设计文档（二期）—— AI 批处理任务调度工具（Batch Agent Runner）

---

**文档负责人：** 工程
**状态：** 🟡 初稿（基于一期设计 v1.1 与 PRD v1.11 第 15 章展开）
**最后更新：** 2026-10-17
**版本：** 1.0

**上游文档：**
- 产品需求文档：`docs/PRD-BatchAgentRunner-v2.0.md`（内容版本 v1.11，第 15 章「二期展望」）
- 一期技术设计：`docs/DESIGN-BatchAgentRunner-v2.0.md`（v1.1，本设计为其超集扩展）
- 关联设计：`docs/DESIGN-BatchAgentRunner-v2.0.md`（v1.1，会话恢复依赖其 `ICredentialVault`）、`docs/UI-设计系统-BatchAgentRunner-v1.0.md`（v1.0）

**本设计覆盖：** 二期全部方向（PRD 第 15.4 章 1–7）+ P2 需求 REQ-016/017/018 + 场景模板库（S1–S16）
**开发语言 / 平台：** 沿用一期 C# / .NET 10 + Avalonia 12（跨平台桌面，Windows 优先）

---

## 1. 二期定位与一期基础

### 1.1 定位

一期把「一条指令」无人值守跑完、多账号降级、免费时段/额度感知、实时可视与断点续跑都做扎实了。二期把**任务对象从「一条指令」升级为「一个软件工程流程」**：

| 维度 | 一期 | 二期 |
|------|------|------|
| 任务单元 | 单条 `instruction` | 单条指令 **或** 多阶段工作流 `workflow` |
| 执行模型 | 单次 agent 调用 + 重试/降级 | 阶段有序执行 + 阶段产出传递 + 质量门禁循环 |
| 任务关系 | 扁平列表、顺序/串行 | 支持 `depends_on` 依赖图（DAG 拓扑调度） |
| 结果消费 | 文本结果 + 报告 | 文本结果 + **diff 视图**（代码修改类）+ 报告 |
| 任务来源 | 手写任务文件 | 手写 **或** 模板/变量批量展开（REQ-016） |
| 流程沉淀 | — | **场景模板库 S1–S16**（开箱即用工作流） |
| 无人值守闭环 | 结束/阻塞需人工查看 | 结束/阻塞 **主动通知**（REQ-018） |
| 会话语义 | 切换/兜底后完整指令重发（方案 a） | 若 CLI 支持会话恢复，升级为**断点恢复**（方案 b，兼容回退方案 a） |
| Markdown 契约 | 只读输入 + sidecar 结果 | 支持**原地回写**（可选，向后兼容 sidecar） |

### 1.2 一期为二期预留的接口（已在代码层面落地）

- 任务数据模型预留字段 `workflow` / `depends_on` / `max_loops` / `batch_size`：**一期反序列化已忽略未知字段**，二期直接启用，无需改反序列化器（仅需放开建模）。
- 适配层 `IAgentCliAdapter.ExecuteAsync(AgentRequest, ct)`：**已按「一次任务多次连续调用」设计**；二期新增「每阶段独立会话」「可选会话恢复」能力，不破坏既有签名（通过 `AgentRequest.Session` 可选字段扩展）。

### 1.3 设计原则（沿用一期 + 二期新增）

沿用一期所有硬约束与「预期行为 ≠ 错误」「防误操作优先」「状态永远可见」原则；二期新增：

1. **工作流内失败可局部重试、不连坐**：单阶段失败按该阶段策略处理（重试/降级/门禁循环），不立即终止整批。
2. **阶段产出显式传递、可审计**：阶段间数据通过受控的上下文变量 `{output:stageId}` 引用，禁止隐式共享运行会话状态（防串扰、省 tokens）。
3. **门禁不通过 ≠ 错误消失**：门禁失败产出「遗留问题清单」，任务状态进入 `NeedsAttention`（新增任务级状态，见 §3.1），不污染「失败率」统计但单独可见。
4. **模板与手写等价**：模板展开后的产物与手写任务文件字节级等价，不引入专属运行时格式。

---

## 2. 分层与模块扩展

### 2.1 分层（在一期基础上新增/扩展，依赖方向不变：Desktop → Agents/Infrastructure → Core）

```
BatchAgentRunner.Core
 ├─ Domain/        新增 WorkflowStage, QualityGate, TaskDependency, ProjectRef,
 │                 TemplateSpec, DiffRecord, NotificationRule, SessionHandle
 ├─ Scheduling/    新增 WorkflowExecutor（阶段循环+门禁）、DagScheduler（拓扑）、
 │                 GateEvaluator、TemplateExpander、SessionResumePolicy
 ├─ Engine/        BatchEngine 接入 DagScheduler + WorkflowExecutor；批级状态机扩展
 └─ Abstractions/  新增 INotifier, IGitIntegration, IAgentSessionStore,
                   IWorkflowTemplateStore, IDiffCollector

BatchAgentRunner.Agents
 └─ Adapters/     AgentRequest 扩展 Session 字段；新增 ResumeAsync 能力探测；
                  MockAdapter 仿真阶段/门禁/恢复

BatchAgentRunner.Infrastructure
 ├─ TaskFiles/    JsonTaskFileStore 扩展 workflow/depends_on 等字段；
 │                新增 MarkdownInPlaceWriter（原地回写）；DiffCollector
 ├─ Reporting/    MarkdownReportWriter 增加工作流阶段时间线 / DAG / 门禁 / diff 章节
 ├─ Notification/ WebhookNotifier / SmtpNotifier / DesktopNotifier
 ├─ Workflow/     FileWorkflowTemplateStore（templates/*.json）
 └─ Git/          LocalGitIntegration（建分支 / 开 PR，可选）

BatchAgentRunner.Desktop
 ├─ Views/        新增 WorkflowView, DagBoardView, GateResultView,
 │                 DiffView, TemplateEditorView, NotificationSettingsView
 └─ ViewModels/   对应 ViewModel + WorkflowRowViewModel / StageRowViewModel
```

### 2.2 配置域扩展（在 9 域基础上新增 3 域 → 共 12 域）

新增配置域（详见 §9 与 CONFIG 文档二期条目）：
- **域 10 工作流与模板**：默认阶段超时、默认门禁最大循环、模板目录、是否启用会话恢复。
- **域 11 通知**：通道（桌面/Webhook/SMTP）、触发事件、安静时段、消息模板。
- **域 12 版本控制集成**：Git 根目录、自动建分支命名、PR 目标分支、是否自动开 PR。

---

## 3. 领域模型扩展

> 约定（沿用一期）：枚举字符串序列化；字段 snake_case；派生/计算属性标 `[JsonIgnore]`；集合声明用具体 `List<T>`（避免 STJ 反序列化接口类型 `NotSupportedException`）；反序列化忽略未知字段保证前向兼容。

### 3.1 任务项 `TaskItem` 扩展

```csharp
class TaskItem
{
    string Id;
    string? Workspace;
    string? Instruction;          // 非工作流任务的单条指令（与 workflow 二选一）
    TaskStatus Status;            // 见下方枚举扩展
    // —— 二期新增（可选）——
    WorkflowSpec? Workflow;       // 工作流定义（取代单条 Instruction 时）
    List<string>? DependsOn;      // DAG 依赖的任务 Id 列表
    ProjectRef? ProjectRef;       // 项目级任务关联的代码仓库/分支
    TemplateVars? TemplateVars;   // 该任务由模板展开得到的变量实例（溯源用）
    int? MaxLoops;                // 该任务门禁/修订循环上限覆盖（全局见 RunOptions）
    int? BatchSize;              // 分批推进时的批大小覆盖
    // —— 结果与元数据（沿用一期，工作流下扩展 stage_results）——
    string? Result;
    List<StageResult>? StageResults;  // 工作流每个阶段的产出/状态（仅工作流任务）
    string? ModelUsed, AccountUsed, ExpertUsed;
    bool FallbackUsed;
    decimal? FallbackCost;
    int RetryCount;
    string? Error;
    // 断点恢复（方案 b）
    SessionHandle? Session;       // 当前会话句柄（CLI 支持时）
    DateTimeOffset? StartedAt, FinishedAt;
    double DurationSeconds;
}
```

**任务级状态枚举扩展**（一期 5 态 + 二期新增 2 态）：

```csharp
enum TaskStatus
{
    Pending, Running, Done, Failed, Interrupted,
    NeedsAttention,   // 二期新增：门禁/修订循环超限，产出遗留问题清单，等待人工介入
    Blocked           // 二期新增：依赖未满足或依赖失败导致本任务阻塞（区别于批级 Blocked）
}
```

> `NeedsAttention` 与 `Blocked` 为任务级状态，不影响批级 `Completed` 判定时**单独统计**（报告区分「真失败」与「待人工」）。

### 3.2 工作流 `WorkflowSpec`

```csharp
class WorkflowSpec
{
    string Name;                       // 模板名或自定义名
    List<WorkflowStage> Stages;        // 有序阶段
    WorkflowOnFailure OnFailure = WorkflowOnFailure.StopTask; // 阶段全失败时的任务策略
    bool FreshSessionPerStage = true;  // 每阶段独立会话（省 tokens，默认开）
}

enum WorkflowOnFailure { StopTask, ContinueNextStage, MarkNeedsAttention }

class WorkflowStage
{
    string Id;                         // 阶段内唯一，供 {output:Id} 引用
    string Name;
    string InstructionTemplate;        // 支持变量：{output:prevStageId} / {var:x} / {task.instruction}
    StageExecutorOverride? Executor;   // 可选：覆盖模型/账号/专家/工作空间
    QualityGate? Gate;                 // 可选质量门禁
    int? MaxLoops;                     // 该阶段门禁修订循环上限（覆盖全局）
    StageOnGateFail OnGateFail = StageOnGateFail.FixLoop; // 门禁失败处理
    string? DependsOnStage;            // 阶段级依赖（同一工作流内，可选）
}

enum StageOnGateFail { FixLoop, StopStage, MarkNeedsAttention }
```

**阶段产出变量作用域**：每个阶段执行完毕后，其 `RawOutput` / `Result` 存入工作流上下文 `Dict<string,string>`，键为 `stageId`。后续阶段 `InstructionTemplate` 通过轻量模板引擎（见 §4.1）替换 `{output:stageId}`、`{var:name}`、`{task.field}`。模板引擎**仅做字符串替换 + 长度裁剪**，不做代码执行。

### 3.3 质量门禁 `QualityGate`

```csharp
class QualityGate
{
    GateKind Kind;
    // —— Kind=Command ——
    string? Command;                  // 如 "dotnet test"、"npm run lint"
    string? ExpectedExitCode;         // 默认为 "0"
    string? OutputMatchRegex;         // 可选：对 stdout 做正则匹配判定通过
    // —— Kind=Review ——
    string? ReviewChecklist;          // 审查维度/checklist 文本，交给 agent 审查阶段
    string? ReviewExpertId;           // 审查所用专家
    // —— Kind=Manual ——
    bool ManualApproval = true;       // 人工卡点（遵循 REQ-002a，危险操作除外）
    // 通用
    string? FailMessageTemplate;      // 门禁失败时的遗留问题清单模板
}

enum GateKind { Command, Review, Manual }
```

### 3.4 任务依赖 `TaskDependency`（DAG）

- 以 `TaskItem.DependsOn` 表达；加载期构建有向图。
- **入度调度**：任务仅当其所有依赖处于终态且满足「成功谓词」时入队。成功谓词默认 `Done || NeedsAttention`（即人工接管的不阻塞下游）；可配置 `BlockOnNeedsAttention`。
- **环检测**：加载期 Tarjan/DFS 检测环，命中即 `LoadError` 拒绝启动（不静默）。
- 依赖失败策略（`RunOptions.DependencyFailurePolicy`）：`BlockDependents`（默认，依赖 Failed/Blocked 则下游 Blocked）/ `AbortBatch` / `SkipDependents`。

### 3.5 项目级引用 `ProjectRef`

```csharp
class ProjectRef
{
    string RepoPath;                  // 本地仓库路径（必填）
    string? BaseBranch;               // 基准分支（默认仓库当前分支）
    string? TaskBranch;               // 本任务工作分支（自动生成或指定）
    string? PrTargetBranch;           // 开 PR 的目标分支
    bool AutoCreateBranch = true;     // 启动时按命名规则建分支
    bool AutoOpenPr = false;          // 完成后自动开 PR（依赖 GitIntegration）
}
```

> 项目级任务本身**不强制** Git 集成：无 `IGitIntegration` 或配置关闭时，`ProjectRef` 仅作元数据记录，工作目录仍按 `Workspace` 解析。Git 操作为**尽力而为**，失败记入日志但不阻断任务。

### 3.6 会话句柄 `SessionHandle`（断点恢复方案 b）

```csharp
class SessionHandle
{
    string AdapterKind;               // 仅同类型 adapter 可恢复
    string SessionId;                 // CLI 返回的 session/resume 标识
    string AccountId;                 // 会话所属账号（恢复时需对应凭证）
    DateTimeOffset LastTouchedAt;
}
```

### 3.7 模板 `TemplateSpec`（REQ-016）

```csharp
class TemplateSpec
{
    string Id;
    string Name;
    string InstructionTemplate;       // 支持 {var:name}
    List<TemplateVarDef> Vars;        // 变量定义（名称/来源/默认）
    List<string>? FileGlob;           // 可选：按文件清单批量生成（每个文件一任务）
    WorkflowSpec? Workflow;           // 模板可同时是工作流模板
    int? DefaultBatchSize;
}

class TemplateVarDef { string Name; string? Default; VarSource Source; }
enum VarSource { Literal, FileList, CsvColumn, Expression } // 二期先做 Literal + FileList + CsvColumn
```

### 3.8 Diff 与通知模型（REQ-017/018）

```csharp
class DiffRecord
{
    string TaskId;
    string? StageId;                  // 属哪个阶段（若有）
    string FilePath;
    DiffKind Kind;                    // Added / Modified / Deleted
    string UnifiedDiff;               // 统一 diff 文本
    string? BeforeHash, AfterHash;    // 内容指纹（sha256 前 16 位）
}

class NotificationRule
{
    NotificationChannel Channel;       // Desktop / Webhook / Smtp
    List<NotifyTrigger> Triggers;     // BatchCompleted / BatchBlocked / TaskFailed / NeedsAttention
    string? WebhookUrl;               // Channel=Webhook
    SmtpConfig? Smtp;                 // Channel=Smtp
    TimeRange? QuietHours;            // 安静时段不推送（仍记日志）
}
enum NotificationChannel { Desktop, Webhook, Smtp }
enum NotifyTrigger { BatchCompleted, BatchBlocked, TaskFailed, NeedsAttention, BatchTerminated }
```

---

## 4. 核心算法

### 4.1 阶段模板引擎（轻量）

```
Render(template, ctx):
  ctx = { "output:<stageId>" : stage.Result,
          "var:<name>"      : task.TemplateVars[name],
          "task:<field>"    : task.<field>.ToString() }
  result = Regex.Replace(template, @"\{(output|var|task):([\w]+)\}", m => ctx.TryGet(m, out v) ? v : "< unresolved:... >")
  return Truncate(result, RunOptions.TemplateMaxKB * 1024)   // 防膨胀
```

- 未解析占位符保留原样并**告警一次**（不阻断），避免静默吞错。
- 渲染结果作为该阶段实际下发给 agent 的 `instruction`。

### 4.2 工作流执行 `WorkflowExecutor`

对每个工作流任务，按 `Stages` 顺序驱动；复用一期单任务执行能力作为「单阶段执行器」`ExecuteStageAsync`：

```
RunWorkflow(task, ct):
  ctx = new WorkflowContext(task)
  for stage in task.Workflow.Stages:
      if stage.DependsOnStage 且 前置阶段未成功: continue with OnGateFail/OnFailure 策略
      loopGuard = 0
      loop:
          render instruction (引用 ctx 中已完成阶段产出)
          // —— 单阶段执行（复用一期重试/降级/额度/冷却逻辑）——
          r = ExecuteStageAsync(task, stage, instruction, ct)
          if r.Failure == None:
              ctx.Set(stage.Id, r.RawOutput)
              if stage.Gate != null:
                  gateResult = GateEvaluator.Evaluate(stage.Gate, r, ct)
                  if gateResult.Passed:
                      record StageResult(stage, Done, r); break loop
                  else:
                      loopGuard++
                      if loopGuard >= (stage.MaxLoops ?? RunOptions.DefaultMaxGateLoops):
                          record StageResult(stage, NeedsAttention, r, gateResult.Leftover)
                          apply stage.OnGateFail → FixLoop超限→MarkNeedsAttention / StopStage / 按 OnFailure
                          break loop
                      else:
                          // 修订循环：把门禁遗留问题作为补充指令，重跑本阶段
                          instruction += "\n\n门禁未通过，遗留问题：\n" + gateResult.Leftover
                          continue loop   // 回到 loop（同阶段，新指令）
          else:
              // 阶段执行失败：走阶段级失败策略（重试/降级已在 ExecuteStageAsync 内消耗）
              apply stage.OnGateFail / Workflow.OnFailure
              if 终止本阶段: record StageResult(stage, Failed/Interrupted); break 或 标记任务终态
  // 全部阶段完成
  task.Result = Aggregate(ctx)              // 汇总各阶段产出
  task.Status = (任一 NeedsAttention) ? NeedsAttention : Done
```

> **关键性质**：单阶段执行器 `ExecuteStageAsync` 与一期「单任务执行器」**代码同构**——区别仅在于入口指令来自模板渲染、出口多一步门禁判定与上下文回写。一期所有降级/额度/冷却/兜底逻辑零改动复用。

### 4.3 质量门禁评估 `GateEvaluator`

| `GateKind` | 评估方式 | 通过判据 |
|------------|---------|---------|
| `Command` | Infrastructure 调 `Process` 执行 `Command`（在 `Workspace` 目录） | 退出码 `== ExpectedExitCode`（默认 0）；若配 `OutputMatchRegex` 需 stdout 命中 |
| `Review` | 以 `ReviewChecklist` 为指令、可选 `ReviewExpertId`，再调一次 agent「审查」阶段 | agent 输出含「通过」判定且无遗留项；遗留项提取为 `Leftover` 清单 |
| `Manual` | 暂停并等待人工在 UI 点「通过/打回」 | 人工通过即通过；打回视为未通过进入修订循环 |

门禁失败统一产出 `LeftoverIssues`（遗留问题清单字符串），写入 `StageResult` 与报告。

### 4.4 DAG 调度 `DagScheduler`

```
BuildGraph(tasks):    // 加载期
  graph = edges from DependsOn
  if HasCycle(graph): throw LoadError("检测到循环依赖: " + cyclePath)
  indegree[id] = DependsOn.Count

ScheduleLoop(readyQueue, ct):
  while pending 非空 且 未终止:
      task = PickNext(readyQueue)            // 仍受账号/模型可用性约束（见一期调度）
      run task (单指令 或 RunWorkflow)
      on task terminal:
          if task.Status == Failed/Blocked 且 policy=BlockDependents:
              mark all dependents Blocked (记录 blocked_by)
          else:
              for dep in dependents(task): indegree[dep]--; if 0 → enqueue
```

- DAG 拓扑与一期「串行/并发」正交：`Concurrency>1` 时允许多条无依赖链并行；有依赖的链严格按拓扑序。
- 依赖等待期间批级状态可为 `Waiting`（复用一期语义），并提供 DAG 看板可视化等待原因。

### 4.5 断点会话恢复（方案 b，兼容方案 a）

```
SessionResumePolicy.Resolve(task, candidateCombo, adapter):
  if !RunOptions.EnableSessionResume:            // 默认关，回退一期行为
      return NoSession                            // 完整指令重发（方案 a）
  if task.Session == null: return NoSession
  if task.Session.AdapterKind != adapter.Kind: return NoSession   // 跨 adapter 不可恢复
  if adapter.CanResume(task.Session): 
      return Resume(task.Session)                // 恢复同一会话，继续当前任务
  else:
      return NoSession                           // 降级方案 a（同等幂等假设）
```

- 启用会话恢复时，`ExecuteStageAsync` 在首次调用创建 session 并回写 `task.Session`；后续切换/兜底/重试优先 `Resume`。
- 一期 `SessionHandle` 为空时**完全等价于现有行为**，保证向后兼容与可灰度和回退。

---

## 5. 场景模板库（S1–S16）与原语

### 5.1 通用能力原语（PRD 15.1）

| 原语 | 在二期设计中的落点 |
|------|------------------|
| 审查-修订循环 | `WorkflowStage` + `QualityGate(Kind=Review)` + `StageOnGateFail=FixLoop` + `MaxLoops` |
| 分批推进 | `RunOptions.Concurrency` + `BatchSize` + 模板 `FileGlob` 展开为多任务；每批完成即落盘（一期原子回写复用） |
| 独立会话 | `WorkflowSpec.FreshSessionPerStage=true`（默认） |
| 质量门禁 | `QualityGate`（Command/Review/Manual） |
| 人工卡点 | `QualityGate(Kind=Manual)`，遵循 REQ-002a 危险操作除外 |

### 5.2 模板注册与自定义

- 内置模板存于 `templates/builtin/*.json`（S1–S16 各一份，见 §5.3）。
- 用户自定义模板存于 `templates/user/*.json`（用户在 `TemplateEditorView` 基于内置模板微调参数后「另存为」）。
- `IWorkflowTemplateStore` 端口统一加载/保存；模板与手写任务**等价展开**——模板实例化为任务文件后，运行时不再区分来源。
- 模板参数化：批大小 / 循环上限 / 门禁标准 / 审查专家 均为模板顶层可覆盖参数。

### 5.3 内置场景模板定义（S1–S16）

每个模板 = `WorkflowSpec`（阶段序列 + 原语组合 + 默认参数）。以下为各模板的阶段骨架（指令细节由用户填充参数）：

| 编号 | 场景 | 阶段骨架（WorkflowStage 序列） | 门禁 | 关键默认参数 |
|------|------|-------------------------------|------|------------|
| **S1** | 需求文档生成与审查 | 生成需求 → 审查(Review) → 修订(FixLoop) → 再审 | Review，循环至通过 | 审查专家、checklist、MaxLoops=3 |
| **S2** | 设计文档生成与审查 | 生成设计 → 审查(架构/可行性/成本) → 修订 → 再审 | Review | 设计规范引用、MaxLoops=3 |
| **S3** | 自动排期与逐任务推进 | 生成排期(WBS) → 按排期逐任务(FreshSession 每任务) | 无/人工卡点 | 拆解粒度、推进节奏 |
| **S4** | 批量代码审查与分批修复 | 分批审查→问题分级→分批修复→复审 | Command(编译) + Review | 批大小、严重度过滤、MaxLoops=2 |
| **S5** | 批量 E2E 测试与修复 | 分批写/跑 E2E → 汇总失败 → 定位修复 → 重跑失败项 | Command(测试) | 批大小、失败重跑、MaxLoops=2 |
| **S6** | 批量可用性测试与修复 | 分批走查/启发式评估 → 问题清单 → 修复 → 复检 | Review(WCAG/交互) | 评估维度、批大小、MaxLoops=2 |
| **S7** | 批量 Bug 修复流水线 | Bug清单 → 逐个定位(FreshSession) → 修复 → 验证 → 关闭 | Command(验证) | 分批落盘、MaxLoops=2 |
| **S8** | 单元测试批量补齐 | 扫描低覆盖 → 分批生成用例 → 运行 → 修复失败 → 达标 | Command(覆盖率达阈) | 目标覆盖率、MaxLoops=2 |
| **S9** | 依赖升级流水线 | 依赖清单 → 分批升级 → 编译 → 测试 → 兼容修复 | Command(编译+测试) | 全通过即止 |
| **S10** | 代码规范批量整改 | lint 扫描 → 问题分级 → 分批自动修复 → 复扫清零 | Command(lint==0) | 可接受阈值 |
| **S11** | 安全漏洞批量修复 | 扫描报告 → 分级 → 分批修复 → 复扫 → 回归 | Command(复扫) + Review | 高危清零 |
| **S12** | 性能问题批量优化 | profiling → 热点分级 → 分批优化 → 基准对比 | Command(基准) | 性能目标/收益阈值 |
| **S13** | 代码注释/文档批量补全 | 扫描缺注释 → 分批补全(JSDoc/README) → 抽查复审 | Review(抽查通过率) | 通过率门槛 |
| **S14** | 多语言翻译与校对 | 分批翻译 → 审校(术语一致) → 修订 → 再审校 | Review | MaxLoops=3 |
| **S15** | 发布流水线 | 版本号→changelog→发布说明→回归清单→发布检查 | 各步 Manual/Command Gate | 任一步失败阻断 |
| **S16** | UI 走查与视觉回归 | 分批截图对比 → 差异分级 → 修复 → 复检 | Review(差异清零/人工豁免) | 差异阈值 |

> 模板的「通用性」：S1/S2 的「生成→审查→修订」循环可复用于任意文档类型（测试方案、运维手册等），模板仅参数化实例。

---

## 6. Markdown 原地回写规范（二期新增，兼容一期 sidecar）

一期：Markdown 只读 + `<name>.md.result.json` sidecar。二期提供**可选**原地回写，默认关闭（保持一期行为），开启后：

### 6.1 原地回写格式

在 `## <task-id>` 标题下、指令内容之后，插入一个受控的元数据区块（以唯一哨兵注释包裹，便于解析与幂等更新）：

```markdown
## task-001
- instruction: 将 src/utils.ts 补充 JSDoc 注释

<!-- batch-agent-runner:meta task-001 -->
- status: done
- model_used: hy3
- account_used: acct-a
- expert_used: ""
- fallback_used: false
- started_at: 2026-10-17T01:00:00+08:00
- finished_at: 2026-10-17T01:03:12+08:00
- duration: 192
- result: |
  已为 12 个函数补充 JSDoc。
<!-- /batch-agent-runner:meta -->
```

- 哨兵注释 `<!-- batch-agent-runner:meta <id> -->` 与 `<!-- /batch-agent-runner:meta -->` 之间为工具托管区；**用户手写内容（instruction 及任何其他文本）永远不被改写**。
- 多阶段任务：在 meta 区块内追加 `stages:` 子列表（每阶段 status/result 摘要），正文仍只写最终汇总 `result`。
- 更新策略：解析时定位哨兵区间整体替换；区间不存在则插入到该任务标题块末尾（下一条 `##` 之前）。

### 6.2 兼容与迁移

- 开启原地回写后，仍**同时**维护 sidecar（两份并存，sidecar 为权威备份），避免解析失败丢数据。
- 提供一次性迁移命令：将既有 sidecar 结果回填进 `.md` 原地 meta 区块。
- 工作流任务的 Markdown 回写：仅回写最终汇总与阶段状态摘要（完整阶段产出仍在任务文件/报告的 `stage_results`）。

---

## 7. REQ-016 任务模板 / 变量批量生成

### 7.1 展开机制 `TemplateExpander`

```
Expand(template, source):
  instances =
    source.Vars == Literal   → 笛卡尔积（每变量取值列表）→ N 个变量组合
    source.Vars == FileList  → 遍历 FileGlob 命中文件 → 每文件一个实例（{var:file}=路径）
    source.Vars == CsvColumn → 读 CSV，每行一个实例
  for inst in instances:
      task = new TaskItem {
          Id = template.Id + "-" + index (或 CSV 主键),
          Instruction = Render(template.InstructionTemplate, inst),
          TemplateVars = inst,
          Workflow = template.Workflow?.CloneAndRender(inst),  // 工作流模板同样展开变量
          DependsOn = template.DependsOn (可选链式: 上一条完成后下一条)
      }
  return tasks   // 与手写任务文件字节级等价
```

- 展开产物写入**新的任务文件**（如 `generated-<timestamp>.json`），不污染源模板；用户可审阅后再运行。
- 链式依赖：模板可声明「展开后任务间顺序依赖」（如 S3 排期推进），`Expand` 自动补 `depends_on` 链。

### 7.2 UI（TemplateEditorView）

- 选择内置/自定义模板 → 编辑变量值（多值换行即多实例）→ 预览展开后的任务列表（条数、首条指令预览）→ 「生成任务文件」落地。

---

## 8. REQ-017 结果 diff 视图

### 8.1 采集 `IDiffCollector`

- 工作流/单任务**开始前**，对 `Workspace` 下「受关注文件集」做快照（默认：任务显式声明的文件路径 + 阶段执行后 agent 报告的改动文件；或配置 `diff_targets` glob）。
- 快照方式：记录文件 sha256 前 16 位 + 内容（或仅 hash，diff 时按需读取原内容——一期不持久原内容，故默认存 unified diff 快照）。
- 任务/阶段**结束后**再算一次，产出 `List<DiffRecord>`（Added/Modified/Deleted + unified diff 文本）。
- 大文件/二进制：仅记录 kind + size 变化，不生成文本 diff（防膨胀）。

### 8.2 落盘与展示

- `DiffRecord` 存入任务的 `stage_results` 或独立 sidecar `<name>.diff.json`（按任务聚合）。
- 报告页新增「变更概览」章节（改动文件数、按阶段分布）。
- `DiffView`：左侧文件树（按任务/阶段分组）、右侧 unified diff 高亮；支持「仅看 Modified」「按阶段筛选」。

---

## 9. REQ-018 通知推送

### 9.1 通道与触发

- `INotifier` 端口；内置 `DesktopNotifier`（复用一期 toast 机制，跨平台系统通知）、`WebhookNotifier`（POST JSON 到用户配置的 Webhook URL，如 企业微信/Slack/Discord 通用 inbound）、`SmtpNotifier`（SMTP 发邮件）。
- 触发事件：`BatchCompleted` / `BatchBlocked` / `BatchTerminated` / `TaskFailed` / `NeedsAttention`。
- 安静时段 `QuietHours`：命中则不推送通道消息（仍写运行日志），避免深夜打扰。

### 9.2 消息模板

- 默认消息含：批名称、触发事件、成功/失败/待人工计数、耗时、报告路径、下一恢复时间点（若为 Blocked/Waiting）。
- 支持 `{batch.name}` `{stats.done}` 等占位符，用户可在配置域 11 自定义。

### 9.3 安全

- Webhook URL / SMTP 密码走凭据引用（同 `credential_ref` 机制），不明文落配置；SmtpNotifier 凭据经 `ICredentialVault` 取用。

---

## 10. 适配层与持久化扩展

### 10.1 `AgentRequest` 扩展

```csharp
record AgentRequest(
    string Instruction, AccountConfig Account, ModelCandidate Model,
    string? ExpertId, string WorkingDirectory, TimeSpan Timeout,
    AutoAnswerPolicy AutoAnswer, Action<string>? OnOutputChunk,
    SessionHandle? Session = null);          // 二期新增：非空则尝试恢复会话

// IAgentCliAdapter 新增能力探测
bool CanResume(SessionHandle handle);          // CLI 是否支持 session/resume
```

- 一期既有字段与行为不变；`Session` 为可选，null 时完全等价原逻辑。
- `MockAdapter` 仿真：阶段成功/门禁失败/会话恢复/断点续跑，支撑全链路自测（无需 M0 即可验证二期逻辑）。

### 10.2 文件契约更新

| 内容 | 位置 | 二期变更 |
|------|------|---------|
| 任务文件 JSON | `.json` | 新增 `workflow` / `depends_on` / `project_ref` / `template_vars` / `max_loops` / `batch_size` / `stage_results` / `session` 字段；未知字段忽略（前向兼容） |
| Markdown 任务 | `.md` | 可选原地回写（§6）；未开启时仍 sidecar |
| 工作流模板 | `templates/builtin/*.json`、`templates/user/*.json` | 新增 |
| Diff 快照 | `<name>.diff.json`（或随 stage_results） | 新增 |
| 通知配置 | `config.json` 域 11 | 新增 |
| 冷却/运行日志/报告 | 沿用一期 | 报告增加工作流阶段时间线、DAG、门禁、diff 章节 |

### 10.3 报告扩展（REQ-011 二期增强）

Markdown/JSON 报告新增章节：
- **工作流阶段时间线**：每任务各阶段的起止、门禁结果、循环次数。
- **DAG 概览**：任务依赖关系、阻塞原因（`blocked_by`）。
- **门禁统计**：通过/未通过/遗留问题数，按场景模板归类。
- **变更概览（diff）**：改动文件数、按阶段分布（引用 DiffView）。
- **待人工介入清单**：所有 `NeedsAttention` / `Blocked` 任务及其遗留问题，单独成表（不计入「真失败率」）。

---

## 11. 界面设计扩展（Avalonia，沿用 UI 设计系统 v1.0 双主题/令牌）

在三大页面（配置/监控/报告）基础上新增视图与状态，复用既有设计令牌与状态色速查表。

### 11.1 配置页新增（域 10/11/12）

- **工作流与模板域**：默认阶段超时、默认门禁最大循环、模板目录、会话恢复开关。
- **通知域**：通道开关 + Webhook URL / SMTP 配置 + 触发事件多选 + 安静时段。
- **版本控制域**：Git 根目录、自动建分支命名规则、PR 目标、是否自动开 PR。

### 11.2 监控页新增视图

1. **WorkflowView（工作流树）**：每个任务展开为阶段列表（阶段状态徽标、门禁结果、循环次数、当前阶段实时输出）；沿用「状态永远可见」原则。
2. **DagBoardView（依赖看板）**：节点=任务，有向边=依赖；颜色区分 Done/Running/Blocked/NeedsAttention；悬停显示 `blocked_by` 与等待原因；支持缩放到全批。
3. **GateResultView（门禁结果）**：门禁通过/未通过、遗留问题清单、修订循环次数。
4. **Toast 扩展**：模板展开完成、门禁未通过进入修订循环、依赖阻塞、会话恢复成功/降级、通知已发送。

### 11.3 报告页新增

- 工作流阶段时间线图、DAG 缩略图、门禁统计、变更概览入口（跳转 DiffView）。

### 11.4 新增独立视图

- **DiffView**：文件树 + unified diff（§8.2）。
- **TemplateEditorView**：模板选择/参数编辑/展开预览/生成任务文件（§7.2）。

### 11.5 新增任务级状态视图

`NeedsAttention`（中性色 + 「查看遗留问题」按钮，不报红）、`Blocked`（提示 `blocked_by` 与依赖恢复条件）。两者均**不计入批级失败率**，但批级 `Completed` 判定时单独可见（影响自动关机条件：默认允许带 NeedsAttention 关机，勾选「仅零待人工」时阻止）。

---

## 12. 测试策略

| 层 | 用例（二期新增） |
|----|----------------|
| `WorkflowExecutor`(MockAdapter) | 多阶段顺序执行 + 阶段产出传递；门禁通过即止；门禁失败进入 FixLoop 直至通过；MaxLoops 超限 → NeedsAttention；`OnFailure=ContinueNextStage` 容错；FreshSessionPerStage 每阶段独立会话 |
| `GateEvaluator` | Command 退出码/正则判定；Review 遗留问题提取；Manual 卡点通过/打回；门禁失败产出 Leftover |
| `DagScheduler` | 拓扑序执行；环检测拒绝启动；依赖 Failed → 下游 Blocked（policy=BlockDependents）；并发下无依赖链并行、有依赖严格有序 |
| `TemplateExpander` | Literal 笛卡尔积、FileList 多文件、CsvColumn 多行；链式 `depends_on` 自动生成；展开产物与手写等价 |
| `MarkdownInPlaceWriter` | 首次插入 meta、二次幂等更新、不改写用户 instruction、无 meta 区块时插入正确位置、迁移 sidecar |
| `DiffCollector` | 文件新增/修改/删除判定、unified diff 生成、大文件仅记 kind |
| `SessionResumePolicy` | 未启用→方案 a；跨 adapter/无 session→方案 a；支持→Resume；恢复失败降级方案 a |
| `Notifier` | Desktop/Webhook/Smtp 触发；安静时段抑制；消息模板渲染 |
| `FileWorkflowTemplateStore` | 内置 S1–S16 加载、用户自定义保存/读取、模板实例化等价性 |
| 报告 | 工作流时间线/门禁/diff 章节生成正确 |

---

## 13. 里程碑与风险

### 13.1 里程碑（建议在 M4 打磨发布后启动）

| 里程碑 | 内容 | 依赖 |
|-------|------|------|
| E0 | 二期设计（本文档）+ 领域模型扩展 + 单元测试（不含 UI） | 一期 D0–D4 完成 |
| E1 | 工作流执行引擎 + 门禁 + DAG 调度贯通（MockAdapter 全链路） | E0 |
| E2 | 模板库 S1–S16 + TemplateExpander + TemplateEditorView | E1 |
| E3 | REQ-017 diff + REQ-018 通知 + Markdown 原地回写 | E1 |
| E4 | 会话恢复（方案 b，依赖 M0 V-8 实测；不支持则保持方案 a） | M0 V-8 |
| E5 | UI 扩展（Workflow/DAG/Gate/Diff/Template/Notification 视图）+ 报告增强 | E1–E3 |
| E6 | 真实任务验证（场景模板跑 ≥3 批真实工程任务） | E1–E5 |

### 13.2 风险与缓解

| 风险 | 缓解 |
|------|------|
| CLI 不支持会话恢复（V-8） | 方案 b 默认关，`SessionHandle` 为空时完全回退方案 a（一期行为）；不阻塞其它二期能力 |
| 阶段产出过大导致上下文膨胀 | 模板引擎默认长度裁剪（TemplateMaxKB）；FreshSessionPerStage 默认开；阶段间仅传字符串变量 |
| 门禁误判（如 lint 噪声） | 门禁标准参数化、可配正则白名单；MaxLoops 封顶；超限转 NeedsAttention 而非失败 |
| DAG 死锁/环 | 加载期环检测拒绝启动；依赖失败策略可配，默认阻塞下游而非 abort |
| 模板展开爆炸（笛卡尔积） | 展开预览先显示条数并强制确认；单批上限 `MaxBatchExpansion` 配置 |
| Git 集成副作用（误建分支/开 PR） | 默认关闭自动开 PR；Git 操作尽力而为，失败仅记日志不阻断；危险操作仍走黑名单防护 |
| Markdown 原地回写破坏用户内容 | 哨兵注释包裹托管区 + 同时维护 sidecar 备份；提供迁移与回滚 |

---

## 14. 开放问题（待用户/M0 确认）

1. **会话恢复是否启用**：取决于 M0 V-8。二期设计已将其作为**可选、默认关**的能力，不影响主线。
2. **门禁默认 MaxLoops**：S1/S2 建议 3，S4–S13 建议 2，是否统一为单一全局默认 + 模板覆盖？待真实工程任务校准。
3. **模板展开单批上限**：默认多少条触发「确认」弹窗（建议 50，对齐一期每批 ≥50 条验证规模）？
4. **Markdown 原地回写默认**：一期用户已习惯 sidecar，二期默认仍关，还是随升级默认开？建议默认关 + 首次开启引导。
5. **项目级任务的 Git 集成深度**：二期先做「建分支 + 记录 PR 链接」，自动开 PR（含 push）是否放入二期或留待三期？
6. **diff 采集的「受关注文件」范围**：默认全仓快照开销大，是否仅采集 agent 显式报告改动的文件（依赖 adapter 返回改动清单）？需 M0 V-1/V-7 实测输出结构确认。
7. **通知通道优先级**：桌面通知是否足以覆盖无人值守场景？Webhook（企业微信等）是否作为二期默认推荐通道？

---

> 本文档与一期设计 `docs/DESIGN-BatchAgentRunner-v2.0.md` 共同构成完整技术设计。一期为「单指令无人值守跑批」，二期为「软件工程流程自动化」。两者共享同一引擎内核、领域模型与界面体系，二期通过**可选字段 + 端口扩展**叠加，不破坏一期已交付行为。
