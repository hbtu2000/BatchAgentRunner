using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>批处理汇总报告（REQ-011）。</summary>
public sealed class BatchReport
{
    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; }

    [JsonPropertyName("batch_status")]
    public BatchStatus BatchStatus { get; set; }

    [JsonPropertyName("task_file")]
    public string TaskFilePath { get; set; } = string.Empty;

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("done")]
    public int Done { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("interrupted")]
    public int Interrupted { get; set; }

    [JsonPropertyName("pending")]
    public int Pending { get; set; }

    [JsonPropertyName("skipped_already_done")]
    public int SkippedAlreadyDone { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    /// <summary>各账号使用量。</summary>
    [JsonPropertyName("account_usage")]
    public List<UsageEntry> AccountUsage { get; set; } = new();

    /// <summary>各模型使用量。</summary>
    [JsonPropertyName("model_usage")]
    public List<UsageEntry> ModelUsage { get; set; } = new();

    /// <summary>切换事件时间线（原因四段区分：额度耗尽 / 临时限流 / 时段外跳过 / 冷却跳过）。</summary>
    [JsonPropertyName("timeline")]
    public List<TimelineEntry> Timeline { get; set; } = new();

    [JsonPropertyName("fallback")]
    public FallbackSummary Fallback { get; set; } = new();

    [JsonPropertyName("failed_tasks")]
    public List<FailedTaskEntry> FailedTasks { get; set; } = new();

    /// <summary>时段外累计等待时长（秒）。</summary>
    [JsonPropertyName("window_wait_seconds")]
    public double WindowWaitSeconds { get; set; }

    /// <summary>额度冷却累计等待时长（秒）。</summary>
    [JsonPropertyName("cooldown_wait_seconds")]
    public double CooldownWaitSeconds { get; set; }

    [JsonPropertyName("shutdown")]
    public ShutdownOutcome Shutdown { get; set; } = new();
}

public sealed class UsageEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("task_count")]
    public int TaskCount { get; set; }

    [JsonPropertyName("cost")]
    public decimal? Cost { get; set; }

    [JsonPropertyName("cost_basis")]
    public CostBasis? CostBasis { get; set; }
}

public sealed class TimelineEntry
{
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }

    [JsonPropertyName("account")]
    public string? Account { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

public sealed class FallbackSummary
{
    [JsonPropertyName("used_count")]
    public int UsedCount { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("total_cost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("cost_basis")]
    public CostBasis CostBasis { get; set; } = CostBasis.Estimated;

    [JsonPropertyName("stopped_by_limit")]
    public bool StoppedByLimit { get; set; }
}

public sealed class FailedTaskEntry
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("retry_count")]
    public int RetryCount { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("account")]
    public string? Account { get; set; }
}

public sealed class ShutdownOutcome
{
    [JsonPropertyName("requested")]
    public bool Requested { get; set; }

    [JsonPropertyName("executed")]
    public bool Executed { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
