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

    /// <summary>需人工接管（二期：工作流门禁未过/阶段依赖未满足，不计入失败率）。</summary>
    [JsonPropertyName("needs_attention")]
    public int NeedsAttention { get; set; }

    /// <summary>被依赖失败阻断（二期 DAG，不计入失败率）。</summary>
    [JsonPropertyName("blocked")]
    public int Blocked { get; set; }

    [JsonPropertyName("skipped_already_done")]
    public int SkippedAlreadyDone { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    /// <summary>净执行时长（秒）= 墙钟总时长 − 等待时长。暂停/等待期间不计入（DESIGN-额度耗尽暂停续跑）。</summary>
    [JsonPropertyName("active_duration_seconds")]
    public double ActiveDurationSeconds { get; set; }

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

    /// <summary>结果 diff 采集记录（二期 REQ-017；仅当启用 diff 采集时有内容）。</summary>
    [JsonPropertyName("diffs")]
    public List<DiffRecord> Diffs { get; set; } = new();

    /// <summary>
    /// 任务级明细快照（二期报告章节数据源：工作流时间线 / DAG 概览 / 门禁统计 / 待人工清单）。
    /// 一期报告不使用；为空时报告渲染自动省略对应章节，保持一期输出不变。
    /// </summary>
    [JsonPropertyName("tasks")]
    public List<TaskReportEntry> Tasks { get; set; } = new();
}

/// <summary>报告中的任务级明细（二期：含阶段结果、依赖与阻断来源）。</summary>
public sealed class TaskReportEntry
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    /// <summary>本任务依赖的任务 Id 列表（DAG）。</summary>
    [JsonPropertyName("depends_on")]
    public List<string>? DependsOn { get; set; }

    /// <summary>阻断来源：导致本任务 Blocked 的依赖 Id（失败/阻断/悬空）。</summary>
    [JsonPropertyName("blocked_by")]
    public List<string>? BlockedBy { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>待人工原因：门禁遗留问题或阻断说明。</summary>
    [JsonPropertyName("leftover")]
    public string? Leftover { get; set; }

    /// <summary>工作流阶段结果（仅工作流任务）。</summary>
    [JsonPropertyName("stage_results")]
    public List<StageResult>? StageResults { get; set; }

    /// <summary>来源模板 Id（模板展开产物溯源）。</summary>
    [JsonPropertyName("template_id")]
    public string? TemplateId { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("model_used")]
    public string? ModelUsed { get; set; }

    [JsonPropertyName("account_used")]
    public string? AccountUsed { get; set; }
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
