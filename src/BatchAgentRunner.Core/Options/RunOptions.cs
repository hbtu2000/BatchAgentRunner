using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Core.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>
/// 一批任务的执行配置（PRD 第 9 章配置页 9 个配置域）。
/// 默认值全部对齐 PRD 明示值；标注「假设」的需真实任务校准（PRD 第 16 章）。
/// </summary>
public sealed class RunOptions
{
    // ---------- 任务与执行工具 ----------

    [JsonPropertyName("task_file_path")]
    public string TaskFilePath { get; set; } = string.Empty;

    [JsonPropertyName("agent_kind")]
    public AgentKind AgentKind { get; set; } = AgentKind.WorkBuddy;

    /// <summary>CLI 可执行文件路径/命令；为空则使用适配器内置默认。</summary>
    [JsonPropertyName("cli_path")]
    public string? CliPath { get; set; }

    /// <summary>
    /// CLI 参数模板（M0 V-1/V-2 未验证前外置，避免硬编码猜测）。
    /// 占位符：{prompt} {model} {expert} {workspace} {extra}
    /// </summary>
    [JsonPropertyName("cli_arguments_template")]
    public string? CliArgumentsTemplate { get; set; }

    /// <summary>
    /// 执行任务时是否弹出 CLI 原生控制台窗口（默认 <c>false</c> = 后台无窗口运行）。
    /// <para>背景（2026-10-16）：CLI 调用形态为 <c>-p/--print --output-format stream-json</c>，
    /// 后台模式下应用能捕获 stdout 并渲染成实时可读进度（见 <c>StreamJsonRenderer</c>），
    /// 同时自动应答、危险操作拦截、结果回写与鉴权/额度分类全部正常工作。</para>
    /// <para>置 <c>true</c> 仅作排障开关：进程不重定向 stdio、以真实 TTY 运行，窗口里能看到 stream-json 文本（不再是纯黑）；
    /// 但代价是应用侧读不到输出 —— 结果回写/鉴权/额度分类失效、应用内实时区空白，需人工值守窗口。
    /// 不建议在常规批量任务中开启。</para>
    /// </summary>
    [JsonPropertyName("show_cli_window")]
    public bool ShowCliWindow { get; set; }

    // ---------- 工作空间（REQ-003a / REQ-019） ----------

    [JsonPropertyName("workspaces")]
    public List<WorkspaceConfig> Workspaces { get; set; } = new();

    [JsonPropertyName("default_workspace")]
    public string? DefaultWorkspace { get; set; }

    /// <summary>
    /// 命名工作空间数量上限（REQ-019，默认 10）。引擎不设绝对硬上限，以配置值为唯一校验准绳；
    /// 配置页建议对 UI 输入范围作合理限制（如 1–100）以避免不可维护，但允许高级用户经
    /// <c>config.json</c> 设更大值。所有数量校验（新增拦截、预检告警）均以该配置值为准，而非硬编码。
    /// </summary>
    [JsonPropertyName("max_workspaces")]
    public int MaxWorkspaces { get; set; } = 10;

    // ---------- 账号池 × 候选模型（REQ-003） ----------

    [JsonPropertyName("accounts")]
    public List<AccountConfig> Accounts { get; set; } = new();

    /// <summary>
    /// 全局共享候选模型列表（v1.12 起为<b>兼容字段</b>）：仅对未配置自有模型池
    /// （<see cref="AccountConfig.ModelCandidates"/> 为空）的账号生效。
    /// </summary>
    [JsonPropertyName("model_candidates")]
    public List<ModelCandidate> ModelCandidates { get; set; } = new();

    [JsonPropertyName("degradation_strategy")]
    public DegradationStrategy DegradationStrategy { get; set; } = DegradationStrategy.ModelFirst;

    // ---------- 专家（REQ-004a） ----------

    [JsonPropertyName("expert")]
    public ExpertRef Expert { get; set; } = ExpertRef.Default();

    // ---------- 兜底（REQ-004b） ----------

    [JsonPropertyName("fallback")]
    public FallbackConfig Fallback { get; set; } = new();

    // ---------- 重试 / 超时 / 上限 ----------

    /// <summary>普通失败重试次数（REQ-009，默认 2）。</summary>
    [JsonPropertyName("max_retries_per_task")]
    public int MaxRetriesPerTask { get; set; } = 2;

    /// <summary>临时限流退避重试次数（REQ-005，默认 3）。</summary>
    [JsonPropertyName("rate_limit_backoff_retries")]
    public int RateLimitBackoffRetries { get; set; } = 3;

    [JsonPropertyName("rate_limit_initial_backoff_seconds")]
    public int RateLimitInitialBackoffSeconds { get; set; } = 5;

    /// <summary>单任务超时（分钟，默认 10，标注为假设）。</summary>
    [JsonPropertyName("task_timeout_minutes")]
    public int TaskTimeoutMinutes { get; set; } = 10;

    /// <summary>单任务总尝试次数上限（REQ-009a，默认 10）。</summary>
    [JsonPropertyName("max_attempts_per_task")]
    public int MaxAttemptsPerTask { get; set; } = 10;

    /// <summary>单任务总时长上限（分钟，REQ-009a，默认 60）。</summary>
    [JsonPropertyName("max_total_minutes_per_task")]
    public int MaxTotalMinutesPerTask { get; set; } = 60;

    /// <summary>最长可等待时长（分钟，REQ-003b，默认 60）。仅对「免费时段外」类等待生效；额度耗尽等待不受此限（见 <see cref="WaitForQuotaReset"/>）。</summary>
    [JsonPropertyName("max_wait_minutes")]
    public int MaxWaitMinutes { get; set; } = 60;

    /// <summary>
    /// 额度耗尽时无限等待重置（默认 <c>true</c>）。
    /// <para>当所有启用账号的所有候选模型组合<b>因额度耗尽</b>（<see cref="WaitKind.QuotaReset"/>）同时不可用时，
    /// 引擎进入 <c>Waiting</c> 暂停并等待最早恢复的组合额度重置后自动续跑，不再受 <see cref="MaxWaitMinutes"/> 上限约束
    /// （额度每日必然重置，属于确定可恢复场景）。暂停时长不计入批处理与各任务的时长。</para>
    /// <para>关闭后退回旧行为：额度耗尽等待同样受 <see cref="MaxWaitMinutes"/> 约束，超出则转 <c>Blocked</c>。</para>
    /// </summary>
    [JsonPropertyName("wait_for_quota_reset")]
    public bool WaitForQuotaReset { get; set; } = true;

    /// <summary>CLI 未返回可解析重置时间时的默认冷却时长（分钟，REQ-004c，默认 60，标注为假设）。</summary>
    [JsonPropertyName("default_cooldown_minutes")]
    public int DefaultCooldownMinutes { get; set; } = 60;

    // ---------- 并发 / 续跑 / 失败即停（P1） ----------

    [JsonPropertyName("concurrency")]
    public int Concurrency { get; set; } = 1;

    [JsonPropertyName("force_rerun")]
    public bool ForceRerun { get; set; }

    [JsonPropertyName("stop_on_failure")]
    public bool StopOnFailure { get; set; }

    /// <summary>终止时是否等待当前任务完成后再终止（PRD 用例 4，默认立即中断）。</summary>
    [JsonPropertyName("terminate_after_current_task")]
    public bool TerminateAfterCurrentTask { get; set; }

    // ---------- 自动关机（REQ-015，P1） ----------

    [JsonPropertyName("auto_shutdown")]
    public AutoShutdownConfig AutoShutdown { get; set; } = new();

    // ---------- 每日积分领取 ----------

    [JsonPropertyName("credits_claim")]
    public CreditsClaimConfig CreditsClaim { get; set; } = new();

    // ---------- 交互自动应答（REQ-002a / 002b） ----------

    [JsonPropertyName("auto_answer")]
    public AutoAnswerPolicy AutoAnswer { get; set; } = AutoAnswerPolicy.CreateDefault();

    // ---------- 二期配置域 ----------

    /// <summary>通知规则（REQ-018）。默认空（不推送）。</summary>
    [JsonPropertyName("notifications")]
    public List<NotificationRule> Notifications { get; set; } = new();

    /// <summary>启用断点会话恢复（方案 b，PRD 15.4.6）。默认关，回退一期方案 a（完整指令重发）。</summary>
    [JsonPropertyName("enable_session_resume")]
    public bool EnableSessionResume { get; set; }

    /// <summary>质量门禁修订循环默认上限（二期）。被阶段 MaxLoops 覆盖。</summary>
    [JsonPropertyName("default_max_gate_loops")]
    public int DefaultMaxGateLoops { get; set; } = 3;

    /// <summary>Markdown 任务文件原地回写（二期）。默认关，保持一期 sidecar 行为。</summary>
    [JsonPropertyName("markdown_in_place_writeback")]
    public bool MarkdownInPlaceWriteback { get; set; }

    /// <summary>依赖失败策略（DAG，二期）。</summary>
    [JsonPropertyName("dependency_failure_policy")]
    public DependencyFailurePolicy DependencyFailurePolicy { get; set; } = DependencyFailurePolicy.BlockDependents;

    /// <summary>模板展开单批上限（REQ-016）：超过则要求确认。默认 50（对齐一期每批 ≥50 条验证规模）。</summary>
    [JsonPropertyName("template_batch_expansion_limit")]
    public int TemplateBatchExpansionLimit { get; set; } = 50;

    /// <summary>是否采集结果 diff（REQ-017）。默认关（由引擎可选注入 IDiffCollector 时生效）。</summary>
    [JsonPropertyName("collect_diffs")]
    public bool CollectDiffs { get; set; }

    /// <summary>diff 采集的受关注文件 glob（相对于工作空间）。</summary>
    [JsonPropertyName("diff_target_globs")]
    public List<string> DiffTargetGlobs { get; set; } = new();

    /// <summary>
    /// 版本控制全局默认（二期项目级任务，配置域 12）。
    /// 作为任务级 <see cref="ProjectRef"/> 的兜底：任务未填的字段用这里的值，
    /// 任务已填的字段以任务为准。仓库路径为空 = 未配置 Git 集成。
    /// </summary>
    [JsonPropertyName("git")]
    public GitConfig Git { get; set; } = new();

    // ---------- 其它 ----------

    /// <summary>单任务结果大小上限（KB，超出截断并提示全量日志位置，PRD 第 8 章）。</summary>
    [JsonPropertyName("result_max_kb")]
    public int ResultMaxKB { get; set; } = 100;

    [JsonPropertyName("backup_retention")]
    public int BackupRetention { get; set; } = 5;

    /// <summary>单任务超时（派生自 <see cref="TaskTimeoutMinutes"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public TimeSpan TaskTimeout => TimeSpan.FromMinutes(Math.Max(1, TaskTimeoutMinutes));

    /// <summary>单任务总时长上限（派生自 <see cref="MaxTotalMinutesPerTask"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public TimeSpan MaxTaskDuration => TimeSpan.FromMinutes(Math.Max(1, MaxTotalMinutesPerTask));

    /// <summary>最长可等待时长（派生自 <see cref="MaxWaitMinutes"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public TimeSpan MaxWait => TimeSpan.FromMinutes(Math.Max(0, MaxWaitMinutes));

    /// <summary>额度耗尽是否无限等待重置（派生自 <see cref="WaitForQuotaReset"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public bool WaitForQuotaResetEnabled => WaitForQuotaReset;

    /// <summary>默认冷却时长（派生自 <see cref="DefaultCooldownMinutes"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public TimeSpan DefaultCooldown => TimeSpan.FromMinutes(Math.Max(1, DefaultCooldownMinutes));
}

/// <summary>
/// 特殊模型兜底配置（REQ-004b）。默认关闭。
/// v1.12：兜底模型池挂在账号下（<see cref="AccountConfig.FallbackModels"/>），
/// 本类只保留全局开关与成本上限；旧字段 <c>fallback.model</c> / <c>fallback.estimated_unit_price</c> 已废弃
/// （反序列化时忽略，配置请迁移到各账号的 fallback_models）。
/// </summary>
public sealed class FallbackConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("max_cost_per_task")]
    public decimal MaxCostPerTask { get; set; } = 1.0m;

    [JsonPropertyName("max_cost_per_batch")]
    public decimal MaxCostPerBatch { get; set; } = 10.0m;
}

/// <summary>
/// 每日积分领取配置。
/// 端点 2026-10-16 已抓包实测确认（契约见 <see cref="Infrastructure.Http.CodeBuddyCreditsApiClient"/>）：
/// 全部为 POST + 空 JSON 体 + Bearer accessToken 鉴权；服务端如调整接口仅需改这里的路径。
/// </summary>
public sealed class CreditsClaimConfig
{
    /// <summary>总开关：关闭后「领取每日积分」入口不可用。默认开启。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>每次批量任务开始前自动为所有账号领取（默认关闭，只提供手动一键入口）。</summary>
    [JsonPropertyName("auto_before_batch")]
    public bool AutoBeforeBatch { get; set; }

    /// <summary>
    /// 强制领取（默认 <c>true</c>）：true 时忽略台账去重，每次都去调领取接口（即「允许重复领取」；
    /// 服务端若判定今日已领过会回「今日已领取过」，但不会在本地因去重而跳过）。
    /// false 时恢复「同账号每日一次」去重语义：今日已领过的账号会被跳过。
    /// </summary>
    [JsonPropertyName("force")]
    public bool Force { get; set; } = true;

    /// <summary>积分服务端 API 基址（实测值，见 <see cref="CodeBuddyApiConstants.ProductionApiBase"/>）。</summary>
    [JsonPropertyName("api_base")]
    public string ApiBase { get; set; } = CodeBuddyApiConstants.ProductionApiBase;

    /// <summary>余额查询路径（POST，资源包剩余 credits 求和；实测值，见 <see cref="CodeBuddyApiConstants.BalancePath"/>）。</summary>
    [JsonPropertyName("balance_path")]
    public string BalancePath { get; set; } = CodeBuddyApiConstants.BalancePath;

    /// <summary>每日领取路径（POST；实测值，见 <see cref="CodeBuddyApiConstants.ClaimPath"/>）。</summary>
    [JsonPropertyName("claim_path")]
    public string ClaimPath { get; set; } = CodeBuddyApiConstants.ClaimPath;

    /// <summary>单次 HTTP 调用超时（秒）。</summary>
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>自动关机配置（REQ-015）。默认关闭。</summary>
public sealed class AutoShutdownConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>勾选后：存在 failed 任务则不关机。默认 false（允许带 failed 关机）。</summary>
    [JsonPropertyName("only_when_zero_failure")]
    public bool OnlyWhenZeroFailure { get; set; }

    [JsonPropertyName("countdown_seconds")]
    public int CountdownSeconds { get; set; } = 60;
}

/// <summary>危险操作防护与自动应答策略（REQ-002a / REQ-002b）。</summary>
public sealed class AutoAnswerPolicy
{
    /// <summary>命中即拒绝自动批准并中断该任务。</summary>
    [JsonPropertyName("dangerous_blacklist")]
    public List<string> DangerousBlacklist { get; set; } = new();

    /// <summary>白名单内的确认按默认应答执行。</summary>
    [JsonPropertyName("allowed_whitelist")]
    public List<string> AllowedWhitelist { get; set; } = new();

    /// <summary>默认应答注入内容（检测到 stdin 等待时写入）。</summary>
    [JsonPropertyName("default_answer")]
    public string DefaultAnswer { get; set; } = "y";

    /// <summary>无输出增长超过该秒数即判定为 stdin 等待。</summary>
    [JsonPropertyName("stdin_idle_seconds")]
    public int StdinIdleSeconds { get; set; } = 30;

    /// <summary>
    /// 识别「选择型」交互提示的关键词。提示文本包含任一关键词且能提取出编号选项时，
    /// 自动按 <see cref="DefaultChoiceIndex"/> 选择对应选项，而不是注入 <see cref="DefaultAnswer"/>。
    /// </summary>
    [JsonPropertyName("choice_prompt_patterns")]
    public List<string> ChoicePromptPatterns { get; set; } = new();

    /// <summary>
    /// 匹配选项行的正则。必须包含捕获组：第 1 组为选项编号/键，第 2 组为选项文本。
    /// 默认匹配 "1. xxx" / "1、xxx" / "1) xxx" / "[1] xxx" 等常见形态。
    /// </summary>
    [JsonPropertyName("choice_option_pattern")]
    public string ChoiceOptionPattern { get; set; } =
        @"^\s*\[?(\d+)\]?[\.、,\)\]\s]+(.+)$";

    /// <summary>选择型交互默认选择的选项索引（0-based）。0 表示第一个选项。</summary>
    [JsonPropertyName("default_choice_index")]
    public int DefaultChoiceIndex { get; set; } = 0;

    public static AutoAnswerPolicy CreateDefault() => new()
    {
        DangerousBlacklist = new List<string>
        {
            "rm -rf", "rm -r /", "del /f", "format ", "mkfs",
            "git push --force", "git push -f", "git reset --hard", "git clean -fd",
            "删除文件", "删除目录", "格式化", "强制推送",
            "修改系统配置", "注册表", "registry", "shutdown", "重启系统"
        },
        AllowedWhitelist = new List<string>
        {
            "读取文件", "写入文件", "创建文件", "运行测试", "运行命令", "读取目录"
        },
        DefaultAnswer = "y",
        StdinIdleSeconds = 30,
        ChoicePromptPatterns = new List<string>
        {
            "选择", "请选择", "选项", "scheme", "select", "choose", "please choose"
        },
        ChoiceOptionPattern = @"^\s*\[?(\d+)\]?[\.、,\)\]\s]+(.+)$",
        DefaultChoiceIndex = 0
    };
}
