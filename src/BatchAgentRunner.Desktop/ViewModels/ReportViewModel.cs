using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>报告页：批处理结束后的汇总视图（REQ-011）。</summary>
public sealed class ReportViewModel : ViewModelBase
{
    private readonly EngineHost _host;

    public ReportViewModel(EngineHost host)
    {
        _host = host;
        RefreshCommand = new RelayCommand(Refresh);
        FailedTasks = new ObservableCollection<FailedTaskEntry>();
        Timeline = new ObservableCollection<TimelineEntry>();
        AttentionTasks = new ObservableCollection<AttentionTaskRowViewModel>();
        StageTimeline = new ObservableCollection<StageTimelineRowViewModel>();
        Refresh();
    }

    public IRelayCommand RefreshCommand { get; }

    public ObservableCollection<FailedTaskEntry> FailedTasks { get; }

    public ObservableCollection<TimelineEntry> Timeline { get; }

    /// <summary>待人工介入 / 依赖阻塞任务清单（二期；不计入失败率）。</summary>
    public ObservableCollection<AttentionTaskRowViewModel> AttentionTasks { get; }

    /// <summary>工作流阶段时间线（二期：跨任务拉平展示阶段状态 / 门禁 / 修订循环）。</summary>
    public ObservableCollection<StageTimelineRowViewModel> StageTimeline { get; }

    public BatchReport? Report => _host.LastReport;

    public bool HasReport => _host.LastReport is not null;

    public string ReportPath => _host.LastReportPath ?? "（尚未生成）";

    public string SummaryText
    {
        get
        {
            var r = Report;
            if (r is null)
            {
                return "尚无报告：请先运行一次批处理。";
            }

            return $"总数 {r.Total} ｜ 成功 {r.Done} ｜ 失败 {r.Failed} ｜ 中断 {r.Interrupted} ｜ 未处理 {r.Pending} ｜ 续跑跳过 {r.SkippedAlreadyDone}"
                + (r.NeedsAttention > 0 || r.Blocked > 0
                    ? $" ｜ 待人工 {r.NeedsAttention} ｜ 依赖阻塞 {r.Blocked}（不计入失败率）"
                    : string.Empty);
        }
    }

    /// <summary>二期概览：门禁统计 / 模板产物 / diff 变更（无数据时为空串）。</summary>
    public string Phase2Text
    {
        get
        {
            var r = Report;
            if (r is null || (r.NeedsAttention == 0 && r.Blocked == 0 && r.Diffs.Count == 0
                && r.Tasks.All(t => t.StageResults is null or { Count: 0 })))
            {
                return string.Empty;
            }

            var gatePassed = r.Tasks.SelectMany(t => t.StageResults ?? new List<StageResult>())
                .Count(s => s.GatePassed);
            var gateFailed = r.Tasks.SelectMany(t => t.StageResults ?? new List<StageResult>())
                .Count(s => !s.GatePassed && s.Status != TaskStatus.Pending);
            var templateCount = r.Tasks.Count(t => !string.IsNullOrEmpty(t.TemplateId));

            return string.Join(" ｜ ", new[]
            {
                r.Tasks.Any(t => t.StageResults is { Count: > 0 })
                    ? $"门禁：通过 {gatePassed} / 未过 {gateFailed}"
                    : null,
                templateCount > 0 ? $"模板展开产物 {templateCount} 条" : null,
                r.Diffs.Count > 0 ? $"diff 变更 {r.Diffs.Count} 个（详见变更概览页）" : null
            }.Where(x => !string.IsNullOrEmpty(x)));
        }
    }

    public bool HasAttentionTasks => AttentionTasks.Count > 0;

    public bool HasPhase2 => !string.IsNullOrEmpty(Phase2Text) || AttentionTasks.Count > 0 || StageTimeline.Count > 0;

    public string DurationText => Report is null
        ? "-"
        : $"净执行 {Report.ActiveDurationSeconds:0.#} 秒（总耗时含等待 {Report.DurationSeconds:0.#} 秒）";

    public string FallbackText
    {
        get
        {
            var f = Report?.Fallback;
            if (f is null)
            {
                return "-";
            }

            return $"触发 {f.UsedCount} 次 ｜ 模型 {f.Model ?? "未配置"} ｜ 总花费 ¥{f.TotalCost:0.####}（{(f.CostBasis == CostBasis.Measured ? "实测" : "估算")}）" +
                   (f.StoppedByLimit ? " ｜ 已因成本上限停用" : string.Empty);
        }
    }

    public string WaitText => Report is null
        ? "-"
        : $"时段外累计等待 {Report.WindowWaitSeconds:0.#} 秒 ｜ 额度冷却累计等待 {Report.CooldownWaitSeconds:0.#} 秒";

    public string ShutdownText => Report?.Shutdown is { Requested: true } s
        ? $"{(s.Executed ? "已执行关机" : "未执行")}（{s.Reason}）"
        : "未启用";

    public void Refresh()
    {
        FailedTasks.Clear();
        Timeline.Clear();
        AttentionTasks.Clear();
        StageTimeline.Clear();

        if (Report is { } r)
        {
            foreach (var f in r.FailedTasks)
            {
                FailedTasks.Add(f);
            }

            foreach (var t in r.Timeline)
            {
                Timeline.Add(t);
            }

            // 二期：待人工介入 / 依赖阻塞清单（blocked_by 与遗留问题一并展示）
            foreach (var t in r.Tasks)
            {
                if (t.Status is TaskStatus.NeedsAttention or TaskStatus.Blocked)
                {
                    AttentionTasks.Add(new AttentionTaskRowViewModel(t));
                }

                if (t.StageResults is { Count: > 0 })
                {
                    foreach (var s in t.StageResults)
                    {
                        StageTimeline.Add(new StageTimelineRowViewModel(t.TaskId, s));
                    }
                }
            }
        }

        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(ReportPath));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(FallbackText));
        OnPropertyChanged(nameof(WaitText));
        OnPropertyChanged(nameof(ShutdownText));
        OnPropertyChanged(nameof(Phase2Text));
        OnPropertyChanged(nameof(HasAttentionTasks));
        OnPropertyChanged(nameof(HasPhase2));
    }
}

/// <summary>待人工介入 / 依赖阻塞任务行（二期报告页）。</summary>
public sealed class AttentionTaskRowViewModel : ObservableObject
{
    public AttentionTaskRowViewModel(TaskReportEntry entry) => Entry = entry;

    public TaskReportEntry Entry { get; }

    public string TaskId => Entry.TaskId;

    public string StatusText => Entry.Status switch
    {
        TaskStatus.NeedsAttention => "待人工介入",
        TaskStatus.Blocked => "依赖阻塞",
        _ => Entry.Status.ToString()
    };

    public string BlockedByText => Entry.BlockedBy is { Count: > 0 }
        ? "阻断来源：" + string.Join("、", Entry.BlockedBy)
        : string.Empty;

    public bool HasBlockedBy => Entry.BlockedBy is { Count: > 0 };

    public string? Leftover => Entry.Leftover;

    public bool HasLeftover => !string.IsNullOrWhiteSpace(Entry.Leftover);

    public string? Error => Entry.Error;
}

/// <summary>工作流阶段时间线行（二期报告页：任务 × 阶段拉平）。</summary>
public sealed class StageTimelineRowViewModel : ObservableObject
{
    public StageTimelineRowViewModel(string taskId, StageResult stage)
    {
        _taskId = taskId;
        _stage = stage;
    }

    private readonly string _taskId;
    private readonly StageResult _stage;

    public string TaskId => _taskId;

    public string StageText => string.IsNullOrWhiteSpace(_stage.Name) ? _stage.StageId : _stage.Name!;

    public string StatusText => _stage.Status switch
    {
        TaskStatus.Pending => "待执行",
        TaskStatus.Running => "执行中",
        TaskStatus.Done => "完成",
        TaskStatus.Failed => "失败",
        TaskStatus.Interrupted => "已中断",
        TaskStatus.NeedsAttention => "待人工介入",
        TaskStatus.Blocked => "已阻塞",
        _ => _stage.Status.ToString()
    };

    public string GateText => _stage.Status == TaskStatus.Pending
        ? "-"
        : _stage.GatePassed ? "通过" : "未过";

    public string LoopText => _stage.LoopCount > 0 ? $"{_stage.LoopCount}" : "-";

    public string ModelText => string.Join(" · ", new[]
    {
        string.IsNullOrEmpty(_stage.AccountUsed) ? null : _stage.AccountUsed,
        string.IsNullOrEmpty(_stage.ModelUsed) ? null : _stage.ModelUsed
    }.Where(x => !string.IsNullOrEmpty(x)));
}
