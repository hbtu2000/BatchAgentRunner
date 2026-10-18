using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 报告聚合协作者（P1-1 拆分产物 ⑤，§3.4 <c>IReportBuilder</c>）：从 <see cref="BatchEngine"/>
/// 抽出用量统计 / 时间线 / 等待累计 / 报告组装。状态随实例走，Build 只读快照，
/// 并发统计经内部 <c>_statsLock</c> 保护（与原引擎同一把锁的语义）。
/// 引擎态（任务列表 / 状态 / diffs / 兜底汇总）经构造期提供函数读取，保证 Build 时点一致。
/// </summary>
public interface IBatchReportBuilder
{
    /// <summary>本批跳过的已完成任务数（ForceRerun 时为 0）。</summary>
    int SkippedDoneCount { get; }

    void SetSkippedDoneCount(int count);

    /// <summary>累计账号/模型维度用量（成本按最后一次 Basis 覆盖展示值，任务数累加）。</summary>
    void TrackUsage(string account, string model, AgentUsage? usage);

    /// <summary>追加时间线条目（切换 / 跳过 / 阻塞等）。</summary>
    void AddTimeline(string kind, string message, string? taskId, string? account, string? model);

    /// <summary>累计等待时长（时段窗口 / 冷却，用于报告的「有效时长」剔除）。</summary>
    void AddWaitSeconds(WaitKind kind, double seconds);

    /// <summary>组装批处理报告（只读当前各项状态的快照）。</summary>
    BatchReport Build(DateTimeOffset startedAt);
}

/// <summary>默认实现：独占原 BatchEngine 的 _timeline/_accountUsage/_modelUsage/_statsLock 与等待累计。</summary>
public sealed class BatchReportBuilder : IBatchReportBuilder
{
    private readonly IClock _clock;
    private readonly Func<RunOptions> _options;
    private readonly Func<IReadOnlyList<TaskItem>> _tasks;
    private readonly Func<BatchStatus> _status;
    private readonly Func<IReadOnlyList<DiffRecord>> _diffs;
    private readonly Func<FallbackSummary> _fallbackSummary;
    private readonly object _statsLock = new();
    private readonly List<TimelineEntry> _timeline = new();
    private readonly Dictionary<string, UsageEntry> _accountUsage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageEntry> _modelUsage = new(StringComparer.OrdinalIgnoreCase);
    private double _windowWaitSeconds;
    private double _cooldownWaitSeconds;
    private int _skippedDoneCount;

    public BatchReportBuilder(
        IClock clock,
        Func<RunOptions> options,
        Func<IReadOnlyList<TaskItem>> tasks,
        Func<BatchStatus> status,
        Func<IReadOnlyList<DiffRecord>> diffs,
        Func<FallbackSummary> fallbackSummary)
    {
        _clock = clock;
        _options = options;
        _tasks = tasks;
        _status = status;
        _diffs = diffs;
        _fallbackSummary = fallbackSummary;
    }

    public int SkippedDoneCount
    {
        get { lock (_statsLock) { return _skippedDoneCount; } }
    }

    public void SetSkippedDoneCount(int count)
    {
        lock (_statsLock)
        {
            _skippedDoneCount = count;
        }
    }

    public void TrackUsage(string account, string model, AgentUsage? usage)
    {
        lock (_statsLock)
        {
            TrackUsageCore(account, model, usage);
        }
    }

    public void AddTimeline(string kind, string message, string? taskId, string? account, string? model)
    {
        var entry = new TimelineEntry
        {
            At = _clock.Now,
            Kind = kind,
            Message = message,
            TaskId = taskId,
            Account = account,
            Model = model
        };
        lock (_statsLock)
        {
            _timeline.Add(entry);
        }
    }

    public void AddWaitSeconds(WaitKind kind, double seconds)
    {
        lock (_statsLock)
        {
            if (kind is WaitKind.FreeWindow or WaitKind.Mixed)
            {
                _windowWaitSeconds += seconds;
            }
            else
            {
                _cooldownWaitSeconds += seconds;
            }
        }
    }

    public BatchReport Build(DateTimeOffset startedAt)
    {
        var options = _options();
        var tasks = _tasks();
        var finishedAt = _clock.Now;
        return new BatchReport
        {
            GeneratedAt = finishedAt,
            BatchStatus = _status(),
            TaskFilePath = options.TaskFilePath,
            Total = tasks.Count,
            Done = tasks.Count(t => t.Status == TaskStatus.Done),
            Failed = tasks.Count(t => t.Status == TaskStatus.Failed),
            Interrupted = tasks.Count(t => t.Status == TaskStatus.Interrupted),
            Pending = tasks.Count(t => t.Status == TaskStatus.Pending),
            NeedsAttention = tasks.Count(t => t.Status == TaskStatus.NeedsAttention),
            Blocked = tasks.Count(t => t.Status == TaskStatus.Blocked),
            SkippedAlreadyDone = SkippedDoneCount,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            DurationSeconds = Math.Round((finishedAt - startedAt).TotalSeconds, 1),
            ActiveDurationSeconds = Math.Round(
                Math.Max(0, (finishedAt - startedAt).TotalSeconds - WindowWaitSeconds - CooldownWaitSeconds), 1),
            AccountUsage = AccountUsage,
            ModelUsage = ModelUsage,
            Timeline = Timeline,
            Fallback = _fallbackSummary(),
            FailedTasks = tasks.Where(t => t.Status == TaskStatus.Failed)
                .Select(t => new FailedTaskEntry
                {
                    TaskId = t.Id,
                    Error = t.Error,
                    RetryCount = t.RetryCount,
                    Model = t.ModelUsed,
                    Account = t.AccountUsed
                }).ToList(),
            WindowWaitSeconds = Math.Round(WindowWaitSeconds, 1),
            CooldownWaitSeconds = Math.Round(CooldownWaitSeconds, 1),
            Diffs = _diffs().ToList(),
            Tasks = tasks.Select(t => new TaskReportEntry
            {
                TaskId = t.Id,
                Status = t.Status,
                DependsOn = t.DependsOn is null ? null : new List<string>(t.DependsOn),
                BlockedBy = ComputeBlockedBy(t),
                Error = t.Error,
                Leftover = ComputeLeftover(t),
                StageResults = t.StageResults is null ? null : new List<StageResult>(t.StageResults),
                TemplateId = t.TemplateVars?.TemplateId,
                DurationSeconds = t.DurationSeconds,
                ModelUsed = t.ModelUsed,
                AccountUsed = t.AccountUsed
            }).ToList()
        };
    }

    private double WindowWaitSeconds
    {
        get { lock (_statsLock) { return _windowWaitSeconds; } }
    }

    private double CooldownWaitSeconds
    {
        get { lock (_statsLock) { return _cooldownWaitSeconds; } }
    }

    private List<UsageEntry> AccountUsage
    {
        get { lock (_statsLock) { return _accountUsage.Values.ToList(); } }
    }

    private List<UsageEntry> ModelUsage
    {
        get { lock (_statsLock) { return _modelUsage.Values.ToList(); } }
    }

    private List<TimelineEntry> Timeline
    {
        get { lock (_statsLock) { return _timeline.ToList(); } }
    }

    private void TrackUsageCore(string account, string model, AgentUsage? usage)
    {
        if (!_accountUsage.TryGetValue(account, out var au))
        {
            au = new UsageEntry { Name = account };
            _accountUsage[account] = au;
        }

        if (!_modelUsage.TryGetValue(model, out var mu))
        {
            mu = new UsageEntry { Name = model };
            _modelUsage[model] = mu;
        }

        au.TaskCount++;
        mu.TaskCount++;
        if (usage?.Cost is { } cost)
        {
            au.Cost = (au.Cost ?? 0m) + cost;
            mu.Cost = (mu.Cost ?? 0m) + cost;
            au.CostBasis = usage.Basis;
            mu.CostBasis = usage.Basis;
        }
    }

    /// <summary>阻断来源：依赖失败（Failed/Blocked）或悬空（不存在）的依赖 Id。</summary>
    private List<string>? ComputeBlockedBy(TaskItem task)
    {
        if (task.Status != TaskStatus.Blocked || task.DependsOn is not { Count: > 0 })
        {
            return null;
        }

        var byId = _tasks().ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        foreach (var dep in task.DependsOn)
        {
            if (!byId.TryGetValue(dep, out var depTask))
            {
                sources.Add($"{dep}（不存在）"); // 悬空依赖
            }
            else if (depTask.Status is TaskStatus.Failed or TaskStatus.Blocked)
            {
                sources.Add(dep);
            }
        }

        return sources.Count > 0 ? sources : null;
    }

    /// <summary>待人工原因：任务错误，或首个未完成阶段的门禁遗留/产出摘要。</summary>
    private static string? ComputeLeftover(TaskItem task)
    {
        if (!string.IsNullOrWhiteSpace(task.Error))
        {
            return task.Error;
        }

        if (task.StageResults is null)
        {
            return null;
        }

        var pending = task.StageResults.FirstOrDefault(s => s.Status != TaskStatus.Done);
        if (pending is null)
        {
            return null;
        }

        return pending.GateLeftover ?? pending.OutputSummary ?? $"阶段 {pending.Name ?? pending.StageId} 未完成";
    }
}
