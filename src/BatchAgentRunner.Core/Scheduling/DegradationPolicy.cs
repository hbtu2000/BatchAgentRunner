using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 组合选择决策（降级矩阵展开 + 临时限流重试同一组合，A-2 / P-03）。
/// 纯函数式：输入「当前组合 / 是否重试同一组合 / 时刻」与矩阵、评估器，输出下一执行组合决策，不持有任何状态、不触发副作用。
/// </summary>
public sealed record ComboSelectionDecision(
    bool HasSelection,
    AgentCombo? NextCombo,
    bool IsRetrySame,
    IReadOnlyList<ComboAvailability> Skipped)
{
    /// <summary>临时限流退避后重试同一组合。</summary>
    /// <param name="combo">需原样重试的组合（限流未耗尽退避额度时沿用）。</param>
    public static ComboSelectionDecision RetrySame(AgentCombo combo) =>
        new(true, combo, true, Array.Empty<ComboAvailability>());

    /// <summary>按降级矩阵展开选中下一组合（附被跳过清单）。</summary>
    /// <param name="combo">矩阵展开选中的下一组合。</param>
    /// <param name="skipped">展开过程中被跳过的组合及原因（时段外 / 冷却）。</param>
    public static ComboSelectionDecision Select(AgentCombo combo, IReadOnlyList<ComboAvailability> skipped) =>
        new(true, combo, false, skipped);

    /// <summary>无可用组合（附被跳过清单，供 blocked 提示）。</summary>
    /// <param name="skipped">全部被跳过的组合及原因，用于拼装不可用说明。</param>
    public static ComboSelectionDecision None(IReadOnlyList<ComboAvailability> skipped) =>
        new(false, null, false, skipped);
}

/// <summary>单任务失败分发后的动作类别（A-2 / P-03）。</summary>
public enum FailureKind
{
    /// <summary>临时限流：退避后重试同一组合。</summary>
    RetrySame,

    /// <summary>额度耗尽 / 限流退避升级：记录冷却并续循环。</summary>
    RecordCooldown,

    /// <summary>凭据失效：停用当前账号并切换下一账号。</summary>
    DisableAccount,

    /// <summary>普通失败：未达重试上限，重试当前任务。</summary>
    RetryTask,

    /// <summary>终态失败（危险操作 / 超时 / 普通超重试上限）：判失败。</summary>
    FailTask,

    /// <summary>终态失败且触发「失败即停」：判失败并暂停批处理。</summary>
    FailTaskStopBatch,

    /// <summary>检测到危险操作确认：拒绝自动批准并中断。</summary>
    FailTaskDangerous
}

/// <summary>
/// 失败分发决策（A-2 / P-03）：纯函数式，输入失败类型与当前计数，输出动作类别与计数器增量。
/// 不触发任何副作用（事件 / 日志 / 落盘），由 <c>TaskRunner</c> 解释决策后执行。
/// </summary>
public sealed record FailureDecision
{
    /// <summary>动作类别。</summary>
    public FailureKind Kind { get; init; }
    /// <summary>限流连击数（退避判定用，随决策回写）。</summary>
    public int? RateLimitStreak { get; init; }
    /// <summary>普通重试计数（随决策回写）。</summary>
    public int? NormalRetries { get; init; }
    /// <summary>终态失败的消息（判失败类决策携带）。</summary>
    public string? Message { get; init; }

    /// <summary>退避后重试同一组合（附新连击数）。</summary>
    /// <param name="streak">累加后的限流连击数，决定退避时长。</param>
    public static FailureDecision RetrySame(int streak) => new() { Kind = FailureKind.RetrySame, RateLimitStreak = streak };
    /// <summary>记录额度冷却并续循环。</summary>
    public static FailureDecision RecordCooldown => new() { Kind = FailureKind.RecordCooldown };
    /// <summary>停用当前账号并切换。</summary>
    public static FailureDecision DisableAccount => new() { Kind = FailureKind.DisableAccount };
    /// <summary>未达重试上限，重试当前任务（附新计数）。</summary>
    /// <param name="normalRetries">累加后的普通重试次数，回写到任务状态。</param>
    public static FailureDecision RetryTask(int normalRetries) => new() { Kind = FailureKind.RetryTask, NormalRetries = normalRetries };
    /// <summary>判任务失败。</summary>
    /// <param name="message">终态失败原因，写入任务 Error 与报告。</param>
    public static FailureDecision FailTask(string message) => new() { Kind = FailureKind.FailTask, Message = message };
    /// <summary>判任务失败并暂停批处理（StopOnFailure）。</summary>
    /// <param name="message">失败原因，同时作为暂停整批的事由。</param>
    public static FailureDecision FailTaskStopBatch(string message) => new() { Kind = FailureKind.FailTaskStopBatch, Message = message };
    /// <summary>危险操作被拒绝，中断任务。</summary>
    /// <param name="message">拒绝自动批准的原因（面向用户的安全提示）。</param>
    public static FailureDecision FailTaskDangerous(string message) => new() { Kind = FailureKind.FailTaskDangerous, Message = message };
}

/// <summary>
/// 降级决策策略对象（A-2 / P-03）：封装「降级矩阵展开」与「单任务失败路由」两类纯判定。
/// 与 <see cref="DegradationMatrix"/> / <see cref="AvailabilityEvaluator"/> 协作但不重复其遍历逻辑，
/// 仅负责「在何种情境下选下一组合 / 失败后应做什么」的策略判定，可独立单测（§3.3）。
/// </summary>
public sealed class DegradationPolicy
{
    // 矩阵以延迟委托持有：引擎会在 NotifyConfigurationChanged() 后重建矩阵，
    // 若在此处捕获实例则会读到陈旧矩阵（P-03 复测发现：配置变更补充可用组合后任务误判 Blocked）。
    // 沿用 BatchEngine.BuildTaskRunnerContext 的 MatrixProvider 懒解析约定，每次判定现取现用。
    private readonly Func<DegradationMatrix> _matrixProvider;
    private readonly AvailabilityEvaluator _evaluator;
    private readonly RunOptions _options;

    /// <summary>
    /// 构造降级策略。矩阵以 <paramref name="matrixProvider"/> 延迟持有（引擎配置变更会重建矩阵，
    /// 捕获实例会读到陈旧矩阵——P-03 复测教训：每次判定现取现用）。
    /// </summary>
    public DegradationPolicy(Func<DegradationMatrix> matrixProvider, AvailabilityEvaluator evaluator, RunOptions options)
    {
        _matrixProvider = matrixProvider ?? throw new ArgumentNullException(nameof(matrixProvider));
        _evaluator = evaluator;
        _options = options;
    }

    /// <summary>
    /// 选择下一执行组合：临时限流退避后重试同一组合（REQ-005），否则按降级矩阵展开下一个可用组合。
    /// </summary>
    public ComboSelectionDecision DecideNext(AgentCombo? current, bool retrySameCombo, DateTimeOffset now)
    {
        if (retrySameCombo && current is not null && _evaluator.Evaluate(current, now).Available)
        {
            return ComboSelectionDecision.RetrySame(current);
        }

        var selection = _matrixProvider().SelectNext(current, now, _evaluator);
        return selection.Found
            ? ComboSelectionDecision.Select(selection.Combo!, selection.Skipped)
            : ComboSelectionDecision.None(selection.Skipped);
    }

    /// <summary>
    /// 单任务失败路由判定：退化/退避/降级/超时/终止，输出动作类别与计数器增量（不触发副作用）。
    /// </summary>
    public FailureDecision DecideFailure(AgentFailure failure, int rateLimitStreak, int normalRetries)
    {
        switch (failure.Kind)
        {
            case AgentFailureKind.RateLimited:
                var newStreak = rateLimitStreak + 1;
                return newStreak <= _options.RateLimitBackoffRetries
                    ? FailureDecision.RetrySame(newStreak)
                    : FailureDecision.RecordCooldown;

            case AgentFailureKind.QuotaExhausted:
                return FailureDecision.RecordCooldown;

            case AgentFailureKind.AuthExpired:
                return FailureDecision.DisableAccount;

            case AgentFailureKind.DangerousOperation:
                return FailureDecision.FailTaskDangerous(failure.Message);

            case AgentFailureKind.Timeout:
                return FailureDecision.FailTask($"单任务执行超时（{_options.TaskTimeoutMinutes} 分钟），已保留部分输出");

            default:
                var newNormal = normalRetries + 1;
                if (newNormal <= _options.MaxRetriesPerTask)
                {
                    return FailureDecision.RetryTask(newNormal);
                }

                return _options.StopOnFailure
                    ? FailureDecision.FailTaskStopBatch(failure.Message)
                    : FailureDecision.FailTask(failure.Message);
        }
    }
}
