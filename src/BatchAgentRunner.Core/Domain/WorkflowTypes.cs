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
    /// <summary>工作流名称（展示用，不参与执行逻辑）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "未命名工作流";

    /// <summary>有序阶段列表（按声明序执行；阶段间依赖见 <see cref="WorkflowStage.DependsOnStage"/>）。</summary>
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
    /// <summary>阶段标识（阶段内引用：{output:stageId} / depends_on_stage）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>阶段名称（展示用）。</summary>
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
    /// <summary>覆盖账号（账号 id；缺省继承任务/全局配置）。</summary>
    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    /// <summary>覆盖模型名。</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>覆盖专家 id。</summary>
    [JsonPropertyName("expert_id")]
    public string? ExpertId { get; set; }

    /// <summary>覆盖工作空间目录。</summary>
    [JsonPropertyName("workspace")]
    public string? Workspace { get; set; }
}

/// <summary>质量门禁（二期：阶段间卡点）。</summary>
public partial record QualityGate
{
    /// <summary>门禁类型（Command 跑命令 / Review 交 agent 审查 / Manual 人工卡点）。</summary>
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

/// <summary>
/// 阶段执行结果（仅工作流任务落盘到 <c>TaskItem.StageResults</c>）。
/// ARCH P-08：改为记录类型以复用 <c>with</c> 表达式深拷贝（<see cref="WorkflowTypeClones.Clone(StageResult)"/>），
/// 使 <c>TaskItem.Clone()</c> 的阶段结果逐层拷贝——快照与原型不得共享可变节点。
/// </summary>
public sealed record StageResult
{
    /// <summary>阶段标识（对应 <see cref="WorkflowStage.Id"/>）。</summary>
    [JsonPropertyName("stage_id")]
    public string StageId { get; set; } = string.Empty;

    /// <summary>阶段名称（结果快照，防阶段改名后无从追溯）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>阶段终态（Pending / Completed / Failed / Skipped…）。</summary>
    [JsonPropertyName("status")]
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    /// <summary>阶段产出摘要（完整产出可能很大，仅存摘要）。</summary>
    [JsonPropertyName("output_summary")]
    public string? OutputSummary { get; set; }

    /// <summary>门禁是否通过（无门禁阶段为 true）。</summary>
    [JsonPropertyName("gate_passed")]
    public bool GatePassed { get; set; }

    /// <summary>门禁未通过时的遗留问题清单（交给修订循环或人工）。</summary>
    [JsonPropertyName("gate_leftover")]
    public string? GateLeftover { get; set; }

    /// <summary>门禁修订循环已执行次数（达 <see cref="WorkflowStage.MaxLoops"/> 即停）。</summary>
    [JsonPropertyName("loop_count")]
    public int LoopCount { get; set; }

    /// <summary>实际执行该阶段的模型名。</summary>
    [JsonPropertyName("model_used")]
    public string? ModelUsed { get; set; }

    /// <summary>实际执行该阶段的账号 id。</summary>
    [JsonPropertyName("account_used")]
    public string? AccountUsed { get; set; }

    /// <summary>阶段是否已终态（派生：<see cref="TaskStatusExtensions.IsTerminal"/>，不落盘）。</summary>
    [JsonIgnore]
    public bool IsTerminal => Status.IsTerminal();
}

/// <summary>项目级任务关联（二期：任务关联代码仓库 / 分支 / PR）。</summary>
public partial record ProjectRef
{
    /// <summary>Git 仓库根目录。</summary>
    [JsonPropertyName("repo_path")]
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>基线分支（创建任务分支的起点）。</summary>
    [JsonPropertyName("base_branch")]
    public string? BaseBranch { get; set; }

    /// <summary>任务分支名（为空时按 <see cref="GitConfig.BranchNaming"/> 规则生成）。</summary>
    [JsonPropertyName("task_branch")]
    public string? TaskBranch { get; set; }

    /// <summary>PR 目标分支（为空则用 <see cref="BaseBranch"/>）。</summary>
    [JsonPropertyName("pr_target_branch")]
    public string? PrTargetBranch { get; set; }

    /// <summary>是否自动创建任务分支。</summary>
    [JsonPropertyName("auto_create_branch")]
    public bool AutoCreateBranch { get; set; } = true;

    /// <summary>任务完成后是否自动开 PR（需 gh CLI；不可用时退化为推送分支并提示）。</summary>
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
    /// <summary>来源模板 id（溯源用）。</summary>
    [JsonPropertyName("template_id")]
    public string? TemplateId { get; set; }

    /// <summary>变量名 → 取值（展开后的最终值，溯源用）。</summary>
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new();
}

/// <summary>任务生成模板（REQ-016：变量批量生成任务清单）。</summary>
public sealed partial record TemplateSpec
{
    /// <summary>模板唯一 id。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>模板名称（展示用）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>指令模板，支持 {var:name}。</summary>
    [JsonPropertyName("instruction_template")]
    public string InstructionTemplate { get; set; } = string.Empty;

    /// <summary>变量定义清单（与 <see cref="FileGlob"/> 做笛卡尔积批量生成任务）。</summary>
    [JsonPropertyName("vars")]
    public List<TemplateVarDef> Vars { get; set; } = new();

    /// <summary>可选：按文件清单批量生成（每个文件一任务）。</summary>
    [JsonPropertyName("file_glob")]
    public List<string>? FileGlob { get; set; }

    /// <summary>模板可同时是工作流模板。</summary>
    [JsonPropertyName("workflow")]
    public WorkflowSpec? Workflow { get; set; }

    /// <summary>展开任务的默认批大小（为空用全局批大小）。</summary>
    [JsonPropertyName("default_batch_size")]
    public int? DefaultBatchSize { get; set; }

    /// <summary>是否串行链式依赖（前一任务完成才跑下一个；DAG 之外的简单串行模式）。</summary>
    [JsonPropertyName("chain_dependency")]
    public bool ChainDependency { get; set; }
}

/// <summary>模板变量定义。</summary>
public sealed record TemplateVarDef
{
    /// <summary>变量名（指令模板里以 {var:name} 引用）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>缺省取值（用户未填时使用）。</summary>
    [JsonPropertyName("default")]
    public string? Default { get; set; }

    /// <summary>变量来源（字面值 / 文件清单 / CSV 列）。</summary>
    [JsonPropertyName("source")]
    public VarSource Source { get; set; } = VarSource.Literal;

    /// <summary>Source=Literal 时的多值（笛卡尔积）；Source=CsvColumn 时的列名。</summary>
    [JsonPropertyName("values")]
    public List<string>? Values { get; set; }
}

/// <summary>模板变量来源。</summary>
public enum VarSource
{
    /// <summary>字面值（Values 即全部取值，缺省走 Default）。</summary>
    Literal,
    /// <summary>来自文件清单（Values 为 glob 模式匹配到的文件集）。</summary>
    FileList,
    /// <summary>来自 CSV 列（Values 为列名，按行取值）。</summary>
    CsvColumn
}

/// <summary>diff 记录（REQ-017）。</summary>
public sealed class DiffRecord
{
    /// <summary>产生该 diff 的任务 id。</summary>
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    /// <summary>产生该 diff 的工作流阶段 id（单指令任务为空）。</summary>
    [JsonPropertyName("stage_id")]
    public string? StageId { get; set; }

    /// <summary>变更文件路径（相对工作区）。</summary>
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>变更类型（新增 / 修改 / 删除）。</summary>
    [JsonPropertyName("kind")]
    public DiffKind Kind { get; set; }

    /// <summary>统一格式 diff 正文（新增/删除文件可能为空）。</summary>
    [JsonPropertyName("unified_diff")]
    public string? UnifiedDiff { get; set; }

    /// <summary>变更前内容哈希（判「是否真被改过」，不落盘）。</summary>
    [JsonIgnore]
    public string? BeforeHash { get; set; }

    /// <summary>变更后内容哈希（不落盘）。</summary>
    [JsonIgnore]
    public string? AfterHash { get; set; }
}

/// <summary>文件变更类型。</summary>
public enum DiffKind
{
    /// <summary>新增文件。</summary>
    Added,
    /// <summary>修改已有文件。</summary>
    Modified,
    /// <summary>删除文件。</summary>
    Deleted
}

/// <summary>通知规则（REQ-018）。</summary>
public sealed class NotificationRule
{
    /// <summary>通知通道（桌面提示 / Webhook / SMTP 邮件）。</summary>
    [JsonPropertyName("channel")]
    public NotificationChannel Channel { get; set; } = NotificationChannel.Desktop;

    /// <summary>触发事件集合（批次完成/阻塞/终止、任务失败、需人工）。</summary>
    [JsonPropertyName("triggers")]
    public List<NotifyTrigger> Triggers { get; set; } = new();

    /// <summary>Channel=Webhook 时的推送地址（凭据不放这里）。</summary>
    [JsonPropertyName("webhook_url")]
    public string? WebhookUrl { get; set; }

    /// <summary>Channel=Smtp 时的邮件配置（凭据走引用）。</summary>
    [JsonPropertyName("smtp")]
    public SmtpConfig? Smtp { get; set; }

    /// <summary>安静时段（本地时间，格式 HH:mm-HH:mm），命中则不推送（仍记日志）。</summary>
    [JsonPropertyName("quiet_hours")]
    public string? QuietHours { get; set; }
}

/// <summary>通知通道。</summary>
public enum NotificationChannel
{
    /// <summary>桌面提示（应用内 Toast）。</summary>
    Desktop,
    /// <summary>HTTP Webhook 推送。</summary>
    Webhook,
    /// <summary>SMTP 邮件。</summary>
    Smtp
}

/// <summary>通知触发事件。</summary>
public enum NotifyTrigger
{
    /// <summary>批次正常完成。</summary>
    BatchCompleted,
    /// <summary>批次阻塞（无可用组合且不满足自动等待）。</summary>
    BatchBlocked,
    /// <summary>批次被人工终止。</summary>
    BatchTerminated,
    /// <summary>任务失败。</summary>
    TaskFailed,
    /// <summary>任务需人工介入（门禁超限 / 遗留问题清单）。</summary>
    NeedsAttention
}

/// <summary>SMTP 配置（凭据走引用，不明文落地）。</summary>
public sealed class SmtpConfig
{
    /// <summary>SMTP 服务器地址。</summary>
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    /// <summary>SMTP 端口（协议默认 587）。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 587;

    /// <summary>是否启用 SSL/TLS。</summary>
    [JsonPropertyName("use_ssl")]
    public bool UseSsl { get; set; } = true;

    /// <summary>发件人地址。</summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }

    /// <summary>收件人地址清单。</summary>
    [JsonPropertyName("to")]
    public List<string> To { get; set; } = new();

    /// <summary>凭据引用（credential_ref 风格），不存明文密码。</summary>
    [JsonPropertyName("credential_ref")]
    public string? CredentialRef { get; set; }
}

/// <summary>断点会话恢复句柄（方案 b；默认关，回退方案 a）。</summary>
public sealed record SessionHandle
{
    /// <summary>适配器类型名（恢复时按此选回同一 CLI）。</summary>
    [JsonPropertyName("adapter_kind")]
    public string AdapterKind { get; set; } = string.Empty;

    /// <summary>CLI 侧会话 id。</summary>
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>建立该会话的账号 id。</summary>
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    /// <summary>最近一次活动时间（过期句柄不恢复）。</summary>
    [JsonPropertyName("last_touched_at")]
    public DateTimeOffset? LastTouchedAt { get; set; }
}

// ---------------- Clone 辅助（TaskItem.Clone 使用；P2-6：record + with 表达式，新增字段自动进入拷贝） ----------------
// 记录类型保留编译器生成的 Clone 成员名（CS8859 禁止在记录内再声明 Clone），故以扩展方法承载深拷贝。

/// <summary>工作流域模型的深拷贝（P2-6 偿还：原 8 份手写 <c>new() { 逐字段赋值 }</c> 收敛为 with 表达式——新增字段自动进入拷贝）。</summary>
public static class WorkflowTypeClones
{
    /// <summary>工作流深拷贝：阶段列表逐层深拷（编辑副本不得与原模板共享可变节点）。</summary>
    /// <param name="spec">待拷贝的工作流定义，阶段列表逐层深拷。</param>
    public static WorkflowSpec Clone(this WorkflowSpec spec) =>
        spec with { Stages = spec.Stages.Select(s => s.Clone()).ToList() };

    /// <summary>模板深拷贝：变量列表 / 文件清单 / 内嵌工作流逐层深拷。</summary>
    /// <param name="spec">待拷贝的模板定义，变量/文件清单/内嵌工作流均深拷。</param>
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
    /// <param name="stage">待拷贝的阶段定义，执行器覆盖与门禁深拷。</param>
    public static WorkflowStage Clone(this WorkflowStage stage) => stage with
    {
        Executor = stage.Executor is { } ex ? ex.Clone() : null,
        Gate = stage.Gate is { } g ? g.Clone() : null
    };

    /// <summary>叶子节点深拷贝（字段均为值 / 不可变字符串，with 空修改即完整拷贝）。</summary>
    /// <param name="executor">待拷贝的执行器覆盖配置。</param>
    public static StageExecutorOverride Clone(this StageExecutorOverride executor) => executor with { };

    /// <summary>叶子节点深拷贝。</summary>
    /// <param name="gate">待拷贝的质量门禁配置。</param>
    public static QualityGate Clone(this QualityGate gate) => gate with { };

    /// <summary>叶子节点深拷贝。</summary>
    /// <param name="projectRef">待拷贝的项目引用（路径与分支信息）。</param>
    public static ProjectRef Clone(this ProjectRef projectRef) => projectRef with { };

    /// <summary>变量实例深拷贝：字典另行拷贝，避免与原实例共享。</summary>
    /// <param name="vars">待拷贝的变量实例，字典另建副本。</param>
    public static TemplateVars Clone(this TemplateVars vars) =>
        vars with { Values = new Dictionary<string, string>(vars.Values) };

    /// <summary>叶子节点深拷贝。</summary>
    /// <param name="handle">待拷贝的断点会话句柄。</param>
    public static SessionHandle Clone(this SessionHandle handle) => handle with { };

    /// <summary>阶段结果深拷贝（ARCH P-08：<c>TaskItem.Clone()</c> 的阶段结果列表逐元素拷贝）。</summary>
    /// <param name="result">待拷贝的阶段执行结果。</param>
    public static StageResult Clone(this StageResult result) => result with { };
}
