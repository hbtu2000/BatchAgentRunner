using System.Text.RegularExpressions;

namespace BatchAgentRunner.Core.Observability;

/// <summary>
/// 结构化日志事件名的<b>单一事实来源</b>（ARCH P-10 / A-8）。
/// </summary>
/// <remarks>
/// 治理前事件名以裸字符串字面量嵌在各条日志正文里（<c>$"task_started 任务 {id} …"</c>），
/// 人读文本与事件语义绑死：想给采集端一个稳定的 <c>event</c> 字段，只能在正文里再抄一遍名字，
/// 抄错/抄漏无从发现。收口后事件名一律在此登记，正文只留人读部分，
/// 渲染时由 <see cref="RunLogEntry.RenderBody"/> 拼回 <c>{事件名} {正文}</c>，<b>人读文本逐字不变</b>。
/// </remarks>
public static class LogEventNames
{
    public const string RunStarted = "run_started";
    public const string TaskOutput = "task_output";
    public const string TaskStarted = "task_started";
    public const string TaskFinished = "task_finished";
    public const string ModelSwitched = "model_switched";
    public const string WindowSkipped = "model_window_skipped";
    public const string CooldownSkipped = "model_cooldown_skipped";
    public const string QuotaResetRecorded = "quota_reset_recorded";
    public const string FallbackUsed = "fallback_used";
    public const string BatchWaiting = "batch_waiting_window";
    public const string BatchBlocked = "batch_blocked";
    public const string BatchResumed = "batch_resumed";
    public const string BatchTerminated = "batch_terminated";
    public const string DangerousOperation = "dangerous_operation_blocked";
    public const string AutoAnswered = "auto_answered";
    public const string MetricsSnapshot = "metrics_snapshot";

    private static readonly Regex PascalBoundary = new(@"(?<!^)(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>引擎事件枚举 → snake_case 事件名（<c>TaskFinished</c> → <c>task_finished</c>）。</summary>
    public static string FromKind<TKind>(TKind kind) where TKind : struct, Enum =>
        PascalBoundary.Replace(kind.ToString(), "_").ToLowerInvariant();
}
