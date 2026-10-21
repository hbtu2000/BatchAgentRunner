using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 工作流定义（二期：把「单条指令」升级为「多阶段流程」）。
/// 一个任务可携带 Workflow 取代单条 Instruction；两个阶段之间通过阶段产出变量 `{output:stageId}` 传递。
/// P2-6 偿还：class + 手写 Clone 改 record + <c>with</c> 表达式——新增字段自动进入拷贝，
/// 消除「改字段必漏改 Clone」。
/// </summary>
public partial record WorkflowSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "未命名工作流";

    [JsonPropertyName("stages")]
    public List<WorkflowStage> Stages { get; set; } = new();

    /// <summary>某阶段全失败时整任务的策略（默认停止并标记 NeedsAttention）。</summary>
    [JsonPropertyName("on_failure")]
    public WorkflowOnFailure OnFailure { get; set; } = WorkflowOnFailure.MarkNeedsAttention;

    /// <summary>每阶段独立会话（默认开，省 tokens、防串扰）。</summary>
    [JsonPropertyName("fresh_session_per_stage")]
    public bool FreshSessionPerStage { get; set; } = true;

    /// <summary>阶段级默认门禁修订循环上限（被阶段自身 MaxLoops 覆盖）。</summary>
    [JsonPropertyName("default_max_loops")]
    public int DefaultMaxLoops { get; set; } = 3;
}

/// <summary>整任务级失败策略。</summary>
public enum WorkflowOnFailure
{
    /// <summary>停止后续阶段，任务标记 NeedsAttention（进人工接管，不污染失败率）。</summary>
    MarkNeedsAttention,
    /// <summary>停止后续阶段，任务标记 Failed。</summary>
    Fail,
    /// <summary>跳过失败阶段，继续下一阶段。</summary>
    ContinueNextStage
}

/// <summary>工作流单个阶段。</summary>
public partial record WorkflowStage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>阶段指令模板，支持变量：{output:stageId} / {var:name} / {task.instruction} / {task.field}。</summary>
    [JsonPropertyName("instruction_template")]
    public string InstructionTemplate { get; set; } = string.Empty;

    /// <summary>可选：覆盖该阶段的执行器（模型/账号/专家/工作空间）。</summary>
    [JsonPropertyName("executor")]
    public StageExecutorOverride? Executor { get; set; }

    /// <summary>可选：阶段质量门禁。</summary>
    [JsonPropertyName("gate")]
    public QualityGate? Gate { get; set; }

    /// <summary>该阶段门禁修订循环上限（覆盖工作流默认）。</summary>
    [JsonPropertyName("max_loops")]
    public int? MaxLoops { get; set; }

    /// <summary>门禁失败时的处理（默认进入修订循环）。</summary>
    [JsonPropertyName("on_gate_fail")]
    public StageOnGateFail OnGateFail { get; set; } = StageOnGateFail.FixLoop;

    /// <summary>阶段级依赖（同一工作流内，可选）。</summary>
    [JsonPropertyName("depends_on_stage")]
    public string? DependsOnStage { get; set; }
}

/// <summary>阶段执行器覆盖项（可选字段，缺省继承任务/全局配置）。</summary>
public partial record StageExecutorOverride
{
    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("expert_id")]
    public string? ExpertId { get; set; }

    [JsonPropertyName("workspace")]
    public string? Workspace { get; set; }
}

/// <summary>质量门禁（二期：阶段间卡点）。</summary>
public partial record QualityGate
{
    [JsonPropertyName("kind")]
    public GateKind Kind { get; set; } = GateKind.Command;

    /// <summary>Kind=Command：要执行的检查命令（在 Workspace 目录运行）。</summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>Command 期望退出码（默认 "0"）。</summary>
    [JsonPropertyName("expected_exit_code")]
    public string? ExpectedExitCode { get; set; } = "0";

    /// <summary>Command 额外：对 stdout 做正则匹配判定通过（可选）。</summary>
    [JsonPropertyName("output_match_regex")]
    public string? OutputMatchRegex { get; set; }

    /// <summary>Kind=Review：审查维度 / checklist 文本（交给 agent 审查阶段）。</summary>
    [JsonPropertyName("review_checklist")]
    public string? ReviewChecklist { get; set; }

    /// <summary>Kind=Review：审查所用专家 id（可选）。</summary>
    [JsonPropertyName("review_expert_id")]
    public string? ReviewExpertId { get; set; }

    /// <summary>Kind=Manual：人工卡点（无 UI 确认时由 ManualApprover 决定，默认自动通过）。</summary>
    [JsonPropertyName("manual_approval")]
    public bool ManualApproval { get; set; } = true;

    /// <summary>门禁失败时的遗留问题清单模板（支持 {stage.output} 引用阶段产出）。</summary>
    [JsonPropertyName("fail_message_template")]
    public string? FailMessageTemplate { get; set; }
}

/// <summary>门禁类型。</summary>
public enum GateKind
{
    /// <summary>运行命令（如 dotnet test / npm run lint）按退出码/输出判定。</summary>
    Command,
    /// <summary>以 checklist 交给 agent 审查阶段判定。</summary>
    Review,
    /// <summary>人工卡点（遵循 REQ-002a，危险操作除外）。</summary>
    Manual
}

/// <summary>门禁失败处理（阶段级）。</summary>
public enum StageOnGateFail
{
    /// <summary>把门禁遗留问题作为补充指令，重跑本阶段（修订循环），直到通过或达 MaxLoops。</summary>
    FixLoop,
    /// <summary>停止本阶段，按 Workflow.OnFailure 处理。</summary>
    StopStage,
    /// <summary>标记 NeedsAttention 并停止本阶段。</summary>
    MarkNeedsAttention
}

/// <summary>阶段执行结果（仅工作流任务落盘到 TaskItem.StageResults）。</summary>
public sealed class StageResult
{
    [JsonPropertyName("stage_id")]
    public string StageId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    /// <summary>阶段产出摘要（完整产出可能很大，仅存摘要）。</summary>
    [JsonPropertyName("output_summary")]
    public string? OutputSummary { get; set; }

    [JsonPropertyName("gate_passed")]
    public bool GatePassed { get; set; }

    [JsonPropertyName("gate_leftover")]
    public string? GateLeftover { get; set; }

    [JsonPropertyName("loop_count")]
    public int LoopCount { get; set; }

    [JsonPropertyName("model_used")]
    public string? ModelUsed { get; set; }

    [JsonPropertyName("account_used")]
    public string? AccountUsed { get; set; }

    [JsonIgnore]
    public bool IsTerminal => Status.IsTerminal();
}

/// <summary>项目级任务关联（二期：任务关联代码仓库 / 分支 / PR）。</summary>
public partial record ProjectRef
{
    [JsonPropertyName("repo_path")]
    public string RepoPath { get; set; } = string.Empty;

    [JsonPropertyName("base_branch")]
    public string? BaseBranch { get; set; }

    [JsonPropertyName("task_branch")]
    public string? TaskBranch { get; set; }

    [JsonPropertyName("pr_target_branch")]
    public string? PrTargetBranch { get; set; }

    [JsonPropertyName("auto_create_branch")]
    public bool AutoCreateBranch { get; set; } = true;

    [JsonPropertyName("auto_open_pr")]
    public bool AutoOpenPr { get; set; }
}

/// <summary>
/// 版本控制全局默认（二期配置域 12）：作为任务级 <see cref="ProjectRef"/> 的兜底。
/// 占位符：分支命名支持 {task_id} / {timestamp} / {date}。
/// </summary>
public sealed class GitConfig
{
    /// <summary>Git 仓库根目录。为空 = 未配置 Git 集成（任务即使带 ProjectRef 也不执行 Git 操作）。</summary>
    [JsonPropertyName("repo_path")]
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>基线分支（创建任务分支的起点）。</summary>
    [JsonPropertyName("base_branch")]
    public string? BaseBranch { get; set; }

    /// <summary>自动建分支命名规则，默认 <c>bar/{task_id}-{timestamp}</c>。</summary>
    [JsonPropertyName("branch_naming")]
    public string BranchNaming { get; set; } = "bar/{task_id}-{timestamp}";

    /// <summary>PR 目标分支（为空则用 <see cref="BaseBranch"/>）。</summary>
    [JsonPropertyName("pr_target_branch")]
    public string? PrTargetBranch { get; set; }

    /// <summary>任务完成后是否自动开 PR（需 gh CLI；不可用时退化为推送分支并提示）。</summary>
    [JsonPropertyName("auto_open_pr")]
    public bool AutoOpenPr { get; set; }
}

/// <summary>模板变量实例（溯源用，不参与执行逻辑）。</summary>
public partial record TemplateVars
{
    [JsonPropertyName("template_id")]
    public string? TemplateId { get; set; }

    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new();
}

/// <summary>任务生成模板（REQ-016：变量批量生成任务清单）。</summary>
public sealed partial record TemplateSpec
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>指令模板，支持 {var:name}。</summary>
    [JsonPropertyName("instruction_template")]
    public string InstructionTemplate { get; set; } = string.Empty;

    [JsonPropertyName("vars")]
    public List<TemplateVarDef> Vars { get; set; } = new();

    /// <summary>可选：按文件清单批量生成（每个文件一任务）。</summary>
    [JsonPropertyName("file_glob")]
    public List<string>? FileGlob { get; set; }

    /// <summary>模板可同时是工作流模板。</summary>
    [JsonPropertyName("workflow")]
    public WorkflowSpec? Workflow { get; set; }

    [JsonPropertyName("default_batch_size")]
    public int? DefaultBatchSize { get; set; }

    [JsonPropertyName("chain_dependency")]
    public bool ChainDependency { get; set; }
}

/// <summary>模板变量定义。</summary>
public sealed record TemplateVarDef
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("default")]
    public string? Default { get; set; }

    [JsonPropertyName("source")]
    public VarSource Source { get; set; } = VarSource.Literal;

    /// <summary>Source=Literal 时的多值（笛卡尔积）；Source=CsvColumn 时的列名。</summary>
    [JsonPropertyName("values")]
    public List<string>? Values { get; set; }
}

/// <summary>模板变量来源。</summary>
public enum VarSource
{
    Literal,
    FileList,
    CsvColumn
}

/// <summary>diff 记录（REQ-017）。</summary>
public sealed class DiffRecord
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("stage_id")]
    public string? StageId { get; set; }

    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public DiffKind Kind { get; set; }

    [JsonPropertyName("unified_diff")]
    public string? UnifiedDiff { get; set; }

    [JsonIgnore]
    public string? BeforeHash { get; set; }

    [JsonIgnore]
    public string? AfterHash { get; set; }
}

/// <summary>文件变更类型。</summary>
public enum DiffKind
{
    Added,
    Modified,
    Deleted
}

/// <summary>通知规则（REQ-018）。</summary>
public sealed class NotificationRule
{
    [JsonPropertyName("channel")]
    public NotificationChannel Channel { get; set; } = NotificationChannel.Desktop;

    [JsonPropertyName("triggers")]
    public List<NotifyTrigger> Triggers { get; set; } = new();

    [JsonPropertyName("webhook_url")]
    public string? WebhookUrl { get; set; }

    [JsonPropertyName("smtp")]
    public SmtpConfig? Smtp { get; set; }

    /// <summary>安静时段（本地时间，格式 HH:mm-HH:mm），命中则不推送（仍记日志）。</summary>
    [JsonPropertyName("quiet_hours")]
    public string? QuietHours { get; set; }
}

/// <summary>通知通道。</summary>
public enum NotificationChannel
{
    Desktop,
    Webhook,
    Smtp
}

/// <summary>通知触发事件。</summary>
public enum NotifyTrigger
{
    BatchCompleted,
    BatchBlocked,
    BatchTerminated,
    TaskFailed,
    NeedsAttention
}

/// <summary>SMTP 配置（凭据走引用，不明文落地）。</summary>
public sealed class SmtpConfig
{
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = 587;

    [JsonPropertyName("use_ssl")]
    public bool UseSsl { get; set; } = true;

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public List<string> To { get; set; } = new();

    /// <summary>凭据引用（credential_ref 风格），不存明文密码。</summary>
    [JsonPropertyName("credential_ref")]
    public string? CredentialRef { get; set; }
}

/// <summary>断点会话恢复句柄（方案 b；默认关，回退方案 a）。</summary>
public sealed record SessionHandle
{
    [JsonPropertyName("adapter_kind")]
    public string AdapterKind { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("last_touched_at")]
    public DateTimeOffset? LastTouchedAt { get; set; }
}

// ---------------- Clone 辅助（TaskItem.Clone 使用；P2-6：record + with 表达式，新增字段自动进入拷贝） ----------------
// 记录类型保留编译器生成的 Clone 成员名（CS8859 禁止在记录内再声明 Clone），故以扩展方法承载深拷贝。

/// <summary>工作流域模型的深拷贝（P2-6 偿还：原 8 份手写 <c>new() { 逐字段赋值 }</c> 收敛为 with 表达式——新增字段自动进入拷贝）。</summary>
public static class WorkflowTypeClones
{
    /// <summary>工作流深拷贝：阶段列表逐层深拷（编辑副本不得与原模板共享可变节点）。</summary>
    public static WorkflowSpec Clone(this WorkflowSpec spec) =>
        spec with { Stages = spec.Stages.Select(s => s.Clone()).ToList() };

    /// <summary>模板深拷贝：变量列表 / 文件清单 / 内嵌工作流逐层深拷。</summary>
    public static TemplateSpec Clone(this TemplateSpec spec) => spec with
    {
        Vars = spec.Vars.Select(v => v with
        {
            Values = v.Values is null ? null : new List<string>(v.Values)
        }).ToList(),
        FileGlob = spec.FileGlob is null ? null : new List<string>(spec.FileGlob),
        Workflow = spec.Workflow is { } wf ? wf.Clone() : null
    };

    /// <summary>阶段深拷贝：执行器覆盖与质量门禁逐层深拷。</summary>
    public static WorkflowStage Clone(this WorkflowStage stage) => stage with
    {
        Executor = stage.Executor is { } ex ? ex.Clone() : null,
        Gate = stage.Gate is { } g ? g.Clone() : null
    };

    /// <summary>叶子节点深拷贝（字段均为值 / 不可变字符串，with 空修改即完整拷贝）。</summary>
    public static StageExecutorOverride Clone(this StageExecutorOverride executor) => executor with { };

    /// <summary>叶子节点深拷贝。</summary>
    public static QualityGate Clone(this QualityGate gate) => gate with { };

    /// <summary>叶子节点深拷贝。</summary>
    public static ProjectRef Clone(this ProjectRef projectRef) => projectRef with { };

    /// <summary>变量实例深拷贝：字典另行拷贝，避免与原实例共享。</summary>
    public static TemplateVars Clone(this TemplateVars vars) =>
        vars with { Values = new Dictionary<string, string>(vars.Values) };

    /// <summary>叶子节点深拷贝。</summary>
    public static SessionHandle Clone(this SessionHandle handle) => handle with { };
}
