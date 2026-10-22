using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Options;

/// <summary>⑤ 重试 / 退避 / 超时 / 冷却（REQ-004c / REQ-005 / REQ-009 / REQ-009a / REQ-003b）。</summary>
public sealed partial class RunOptions
{
    /// <summary>普通失败重试次数（REQ-009，默认 2）。</summary>
    [JsonPropertyName("max_retries_per_task")]
    public int MaxRetriesPerTask { get; set; } = 2;

    /// <summary>临时限流退避重试次数（REQ-005，默认 3）。</summary>
    [JsonPropertyName("rate_limit_backoff_retries")]
    public int RateLimitBackoffRetries { get; set; } = 3;

    /// <summary>临时限流退避的初始间隔（秒，默认 5；后续按指数退避递增，上限见重试策略）。</summary>
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
    /// <para>当所有启用账号的所有候选模型组合<b>因额度耗尽</b>（<c>WaitKind.QuotaReset</c>）同时不可用时，
    /// 引擎进入 <c>Waiting</c> 暂停并等待最早恢复的组合额度重置后自动续跑，不再受 <see cref="MaxWaitMinutes"/> 上限约束
    /// （额度每日必然重置，属于确定可恢复场景）。暂停时长不计入批处理与各任务的时长。</para>
    /// <para>关闭后退回旧行为：额度耗尽等待同样受 <see cref="MaxWaitMinutes"/> 约束，超出则转 <c>Blocked</c>。</para>
    /// </summary>
    [JsonPropertyName("wait_for_quota_reset")]
    public bool WaitForQuotaReset { get; set; } = true;

    /// <summary>CLI 未返回可解析重置时间时的默认冷却时长（分钟，REQ-004c，默认 60，标注为假设）。</summary>
    [JsonPropertyName("default_cooldown_minutes")]
    public int DefaultCooldownMinutes { get; set; } = 60;
}
