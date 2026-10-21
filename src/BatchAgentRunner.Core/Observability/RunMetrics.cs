using System.Globalization;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Observability;

/// <summary>
/// 运行级指标采集器（ARCH P-10 / A-8：指标与 <c>BatchReport</c> 互补——报告给用户看，指标给运维看）。
/// </summary>
/// <remarks>
/// 治理前「降级次数 / 兜底触发 / 冷却跳过 / 误切换」这些运维最关心的量<b>只能从日志文本里人肉数</b>：
/// 引擎会把切换与跳过写进运行日志与时间线，但没有任何一处把它们累计成可采集的数字。
/// 收口后：埋点固定在状态机的少量决策点（任务开始 / 终态 / 切换 / 跳过 / 兜底 / 高危拒绝），
/// 由本采集器统一累计，批次结束时输出快照（结构化日志条目 + <c>metrics-*.json</c> 落盘）。
///
/// 口径（与 <c>BatchReport</c> 一致，避免两套数字打架）：
/// <list type="bullet">
///   <item><b>失败率</b> = 失败任务 / 终态任务；待人工（NeedsAttention）与依赖阻断（Blocked）<b>不计失败</b>；</item>
///   <item><b>误切换率</b> = 切换后本次尝试仍因额度耗尽/凭据失效而失败的切换次数 / 切换总次数；</item>
///   <item><b>吞吐</b> = 完成任务数 / 批次已运行小时数；<b>平均耗时</b> = 终态任务耗时累计 / 终态任务数。</item>
/// </list>
/// 并发安全：派发为受控并发（REQ-012），计数与耗时累计一律在锁内完成。
/// </remarks>
public sealed class RunMetrics
{
    private readonly object _sync = new();
    private int _tasksStarted;
    private int _tasksCompleted;
    private int _tasksFailed;
    private int _tasksInterrupted;
    private int _tasksNeedsAttention;
    private int _tasksBlocked;
    private int _degradationSwitches;
    private int _misswitches;
    private int _fallbackTriggered;
    private int _cooldownSkips;
    private int _windowSkips;
    private int _dangerousBlocked;
    private double _totalDurationSeconds;
    private DateTimeOffset? _startedAt;

    public RunMetrics(string runId, DateTimeOffset? startedAt = null)
    {
        RunId = runId;
        _startedAt = startedAt;
    }

    /// <summary>本批关联标识（与运行日志同一 RunId）。</summary>
    public string RunId { get; }

    /// <summary>批次真正开始运行的时刻（引擎构造可能远早于启动，故由生命周期显式标记）。</summary>
    public void MarkBatchStarted(DateTimeOffset now)
    {
        lock (_sync)
        {
            _startedAt ??= now;
        }
    }

    public void RecordTaskStarted()
    {
        lock (_sync)
        {
            _tasksStarted++;
        }
    }

    /// <summary>任务到达终态（吞吐、失败率、平均耗时的数据源）。</summary>
    public void RecordTaskFinished(TaskStatus status, double durationSeconds)
    {
        lock (_sync)
        {
            switch (status)
            {
                case TaskStatus.Done:
                    _tasksCompleted++;
                    break;
                case TaskStatus.Failed:
                    _tasksFailed++;
                    break;
                case TaskStatus.Interrupted:
                    _tasksInterrupted++;
                    break;
                case TaskStatus.NeedsAttention:
                    _tasksNeedsAttention++;
                    break;
                case TaskStatus.Blocked:
                    _tasksBlocked++;
                    break;
            }

            _totalDurationSeconds += durationSeconds;
        }
    }

    /// <summary>降级/账号切换（误切换率的分母）。</summary>
    public void RecordSwitch()
    {
        lock (_sync)
        {
            _degradationSwitches++;
        }
    }

    /// <summary>
    /// 误切换：刚切换完的这次尝试仍因额度耗尽 / 凭据失效而失败，说明这次切换没换来可用组合。
    /// 时段外跳过与冷却跳过属于<b>预期调度行为</b>（设计已定义"不计失败"），不计入误切换。
    /// </summary>
    public void RecordMisswitch()
    {
        lock (_sync)
        {
            _misswitches++;
        }
    }

    public void RecordFallback()
    {
        lock (_sync)
        {
            _fallbackTriggered++;
        }
    }

    /// <summary>额度冷却期跳过组合（预期行为，不计失败）。</summary>
    public void RecordCooldownSkip()
    {
        lock (_sync)
        {
            _cooldownSkips++;
        }
    }

    /// <summary>免费时段外跳过组合（预期行为，不计失败）。</summary>
    public void RecordWindowSkip()
    {
        lock (_sync)
        {
            _windowSkips++;
        }
    }

    public void RecordDangerousBlock()
    {
        lock (_sync)
        {
            _dangerousBlocked++;
        }
    }

    public RunMetricsSnapshot Snapshot(DateTimeOffset capturedAt)
    {
        lock (_sync)
        {
            return new RunMetricsSnapshot(
                RunId,
                _startedAt ?? capturedAt,
                capturedAt,
                _tasksStarted,
                _tasksCompleted,
                _tasksFailed,
                _tasksInterrupted,
                _tasksNeedsAttention,
                _tasksBlocked,
                _degradationSwitches,
                _misswitches,
                _fallbackTriggered,
                _cooldownSkips,
                _windowSkips,
                _dangerousBlocked,
                _totalDurationSeconds);
        }
    }

    /// <summary>
    /// 指标快照条目（批次结束时写入运行日志）：数值进 <c>Fields</c> 供采集，
    /// 正文给一行人读摘要，便于 <c>tail</c> 排障时直接看到"这批跑得怎么样"。
    /// </summary>
    public RunLogEntry ToLogEntry(DateTimeOffset at)
    {
        var snapshot = Snapshot(at);
        return new RunLogEntry(RunLogLevels.Info, "metrics_snapshot", snapshot.Describe())
        {
            At = at,
            RunId = RunId,
            Fields = snapshot.ToFields()
        };
    }
}

/// <summary>
/// 运行级指标快照（不可变；由 <see cref="RunMetrics.Snapshot"/> 产出，落盘 <c>metrics-*.json</c>）。
/// 派生量（失败率 / 误切换率 / 平均耗时 / 吞吐）随快照一同落盘，避免采集端各算一套。
/// </summary>
public sealed record RunMetricsSnapshot(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset CapturedAt,
    int TasksStarted,
    int TasksCompleted,
    int TasksFailed,
    int TasksInterrupted,
    int TasksNeedsAttention,
    int TasksBlocked,
    int DegradationSwitches,
    int Misswitches,
    int FallbackTriggered,
    int CooldownSkips,
    int WindowSkips,
    int DangerousBlocked,
    double TotalDurationSeconds)
{
    /// <summary>终态任务数（失败率分母；待人工与依赖阻断计入终态但不计失败）。</summary>
    public int TasksTerminal =>
        TasksCompleted + TasksFailed + TasksInterrupted + TasksNeedsAttention + TasksBlocked;

    public double ElapsedSeconds => Math.Max(0, (CapturedAt - StartedAt).TotalSeconds);

    /// <summary>失败率：失败 / 终态（待人工与阻断不计失败）。</summary>
    public double FailureRate =>
        TasksTerminal == 0 ? 0 : Math.Round((double)TasksFailed / TasksTerminal, 4);

    /// <summary>误切换率：切换后仍未换来可用组合的切换 / 切换总数。</summary>
    public double MisswitchRate =>
        DegradationSwitches == 0 ? 0 : Math.Round((double)Misswitches / DegradationSwitches, 4);

    /// <summary>平均任务耗时（秒）：终态任务耗时累计 / 终态任务数。</summary>
    public double AverageDurationSeconds =>
        TasksTerminal == 0 ? 0 : Math.Round(TotalDurationSeconds / TasksTerminal, 3);

    /// <summary>吞吐（任务/小时）：完成任务数 / 批次已运行小时数。</summary>
    public double ThroughputPerHour
    {
        get
        {
            var hours = ElapsedSeconds / 3600d;
            return hours <= 0 ? 0 : Math.Round(TasksCompleted / hours, 3);
        }
    }

    /// <summary>一行人读摘要（进运行日志正文）。</summary>
    public string Describe() => string.Format(
        CultureInfo.InvariantCulture,
        "批次指标：完成 {0} / 失败 {1} / 中断 {2}（失败率 {3:P1}）｜降级切换 {4}（误切换 {5}，{6:P1}）｜兜底 {7}｜冷却跳过 {8}｜时段跳过 {9}｜高危拒绝 {10}｜平均耗时 {11:0.#}s｜吞吐 {12:0.#}/h",
        TasksCompleted,
        TasksFailed,
        TasksInterrupted,
        FailureRate,
        DegradationSwitches,
        Misswitches,
        MisswitchRate,
        FallbackTriggered,
        CooldownSkips,
        WindowSkips,
        DangerousBlocked,
        AverageDurationSeconds,
        ThroughputPerHour);

    /// <summary>结构化字段（供 JSONL / 采集端使用；数值统一不变文化字符串）。</summary>
    public IReadOnlyDictionary<string, string> ToFields() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["run_id"] = RunId,
        ["elapsed_seconds"] = RunLogEntry.Num(ElapsedSeconds),
        ["tasks_started"] = TasksStarted.ToString(CultureInfo.InvariantCulture),
        ["tasks_completed"] = TasksCompleted.ToString(CultureInfo.InvariantCulture),
        ["tasks_failed"] = TasksFailed.ToString(CultureInfo.InvariantCulture),
        ["tasks_interrupted"] = TasksInterrupted.ToString(CultureInfo.InvariantCulture),
        ["tasks_terminal"] = TasksTerminal.ToString(CultureInfo.InvariantCulture),
        ["failure_rate"] = RunLogEntry.Num(FailureRate),
        ["degradation_switches"] = DegradationSwitches.ToString(CultureInfo.InvariantCulture),
        ["misswitches"] = Misswitches.ToString(CultureInfo.InvariantCulture),
        ["misswitch_rate"] = RunLogEntry.Num(MisswitchRate),
        ["fallback_triggered"] = FallbackTriggered.ToString(CultureInfo.InvariantCulture),
        ["cooldown_skips"] = CooldownSkips.ToString(CultureInfo.InvariantCulture),
        ["window_skips"] = WindowSkips.ToString(CultureInfo.InvariantCulture),
        ["dangerous_blocked"] = DangerousBlocked.ToString(CultureInfo.InvariantCulture),
        ["total_duration_seconds"] = RunLogEntry.Num(TotalDurationSeconds),
        ["average_duration_seconds"] = RunLogEntry.Num(AverageDurationSeconds),
        ["throughput_per_hour"] = RunLogEntry.Num(ThroughputPerHour)
    };
}
