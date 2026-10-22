using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>批处理汇总报告（REQ-011）。</summary>
public sealed class BatchReport
{
    /// <summary>报告生成时间（本地时间）。</summary>
    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>批次终态（Completed / Blocked / Terminated…）。</summary>
    [JsonPropertyName("batch_status")]
    public BatchStatus BatchStatus { get; set; }

    /// <summary>任务清单文件路径。</summary>
    [JsonPropertyName("task_file")]
    public string TaskFilePath { get; set; } = string.Empty;

    /// <summary>任务总数。</summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }

    /// <summary>成功完成数。</summary>
    [JsonPropertyName("done")]
    public int Done { get; set; }

    /// <summary>失败数（计入失败率）。</summary>
    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    /// <summary>被人工中断数（终止时尚未完成的任务）。</summary>
    [JsonPropertyName("interrupted")]
    public int Interrupted { get; set; }

    /// <summary>未开始数（阻塞/终止时剩余）。</summary>
    [JsonPropertyName("pending")]
    public int Pending { get; set; }

    /// <summary>需人工接管（二期：工作流门禁未过/阶段依赖未满足，不计入失败率）。</summary>
    [JsonPropertyName("needs_attention")]
    public int NeedsAttention { get; set; }

    /// <summary>被依赖失败阻断（二期 DAG，不计入失败率）。</summary>
    [JsonPropertyName("blocked")]
    public int Blocked { get; set; }

    /// <summary>续跑跳过数（上次已完成，本次未重跑）。</summary>
    [JsonPropertyName("skipped_already_done")]
    public int SkippedAlreadyDone { get; set; }

    /// <summary>批次启动时间。</summary>
    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>批次结束时间。</summary>
    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>墙钟总时长（秒，含暂停/等待）。</summary>
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

    /// <summary>兜底计费汇总（触发次数 / 模型 / 费用 / 是否触顶停用）。</summary>
    [JsonPropertyName("fallback")]
    public FallbackSummary Fallback { get; set; } = new();

    /// <summary>失败任务清单（错误摘要 / 重试次数 / 当时模型账号）。</summary>
    [JsonPropertyName("failed_tasks")]
    public List<FailedTaskEntry> FailedTasks { get; set; } = new();

    /// <summary>时段外累计等待时长（秒）。</summary>
    [JsonPropertyName("window_wait_seconds")]
    public double WindowWaitSeconds { get; set; }

    /// <summary>额度冷却累计等待时长（秒）。</summary>
    [JsonPropertyName("cooldown_wait_seconds")]
    public double CooldownWaitSeconds { get; set; }

    /// <summary>关机动作结果（是否请求 / 是否执行 / 原因）。</summary>
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
    /// <summary>任务 Id。</summary>
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    /// <summary>任务终态。</summary>
    [JsonPropertyName("status")]
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    /// <summary>本任务依赖的任务 Id 列表（DAG）。</summary>
    [JsonPropertyName("depends_on")]
    public List<string>? DependsOn { get; set; }

    /// <summary>阻断来源：导致本任务 Blocked 的依赖 Id（失败/阻断/悬空）。</summary>
    [JsonPropertyName("blocked_by")]
    public List<string>? BlockedBy { get; set; }

    /// <summary>失败错误摘要（失败任务才有）。</summary>
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

    /// <summary>任务耗时（秒，已剔除本任务暂停等待）。</summary>
    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    /// <summary>实际执行所用模型名。</summary>
    [JsonPropertyName("model_used")]
    public string? ModelUsed { get; set; }

    /// <summary>实际执行所用账号 id。</summary>
    [JsonPropertyName("account_used")]
    public string? AccountUsed { get; set; }
}

/// <summary>账号/模型维度用量条目。</summary>
public sealed class UsageEntry
{
    /// <summary>账号 id 或模型名。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>完成任务数。</summary>
    [JsonPropertyName("task_count")]
    public int TaskCount { get; set; }

    /// <summary>费用（CLI 不可得时为 null，由估算口径兜底）。</summary>
    [JsonPropertyName("cost")]
    public decimal? Cost { get; set; }

    /// <summary>费用口径（CLI 实报 / 估算）。</summary>
    [JsonPropertyName("cost_basis")]
    public CostBasis? CostBasis { get; set; }
}

/// <summary>切换/跳过事件时间线条目。</summary>
public sealed class TimelineEntry
{
    /// <summary>事件时间（本地时间）。</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>事件类型（切换 / 降级 / 冷却跳过 / 时段跳过…）。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>事件描述（人读）。</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>关联任务 id（批次级事件为空）。</summary>
    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }

    /// <summary>关联账号 id。</summary>
    [JsonPropertyName("account")]
    public string? Account { get; set; }

    /// <summary>关联模型名。</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>是否为「并发下延后统一切换」（REQ-021）：区分并发延后切换与常规任务边界切换。</summary>
    [JsonPropertyName("deferred_switch")]
    public bool DeferredSwitch { get; set; }
}

/// <summary>兜底计费汇总（REQ-004b）。</summary>
public sealed class FallbackSummary
{
    /// <summary>兜底触发次数。</summary>
    [JsonPropertyName("used_count")]
    public int UsedCount { get; set; }

    /// <summary>兜底所用模型名。</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>兜底累计费用。</summary>
    [JsonPropertyName("total_cost")]
    public decimal TotalCost { get; set; }

    /// <summary>费用口径（CLI 实报 / 估算）。</summary>
    [JsonPropertyName("cost_basis")]
    public CostBasis CostBasis { get; set; } = CostBasis.Estimated;

    /// <summary>是否因触达单任务兜底成本上限而停用兜底。</summary>
    [JsonPropertyName("stopped_by_limit")]
    public bool StoppedByLimit { get; set; }
}

/// <summary>失败任务明细。</summary>
public sealed class FailedTaskEntry
{
    /// <summary>任务 Id。</summary>
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    /// <summary>错误摘要。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>已重试次数。</summary>
    [JsonPropertyName("retry_count")]
    public int RetryCount { get; set; }

    /// <summary>失败时所用模型名。</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>失败时所用账号 id。</summary>
    [JsonPropertyName("account")]
    public string? Account { get; set; }
}

/// <summary>批次结束时的关机动作结果。</summary>
public sealed class ShutdownOutcome
{
    /// <summary>是否请求了关机（自动关机配置命中）。</summary>
    [JsonPropertyName("requested")]
    public bool Requested { get; set; }

    /// <summary>关机是否实际执行。</summary>
    [JsonPropertyName("executed")]
    public bool Executed { get; set; }

    /// <summary>未执行原因（执行了为空）。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
