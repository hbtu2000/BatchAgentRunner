using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>报告页：批处理结束后的汇总视图（REQ-011）。</summary>
public sealed class ReportViewModel : ViewModelBase
{
    private readonly IEngineHost _host;

    /// <summary>创建报告页，初始化各清单集合并立即按上次报告填充一次。</summary>
    public ReportViewModel(IEngineHost host)
    {
        _host = host;
        RefreshCommand = new RelayCommand(Refresh);
        FailedTasks = new ObservableCollection<FailedTaskEntry>();
        Timeline = new ObservableCollection<TimelineEntry>();
        AttentionTasks = new ObservableCollection<AttentionTaskRowViewModel>();
        StageTimeline = new ObservableCollection<StageTimelineRowViewModel>();
        Refresh();
    }

    /// <summary>刷新命令，点击后重读最近报告并重建失败清单与时间线。</summary>
    public IRelayCommand RefreshCommand { get; }

    /// <summary>本次批处理中失败的任务条目，刷新时整体重建。</summary>
    public ObservableCollection<FailedTaskEntry> FailedTasks { get; }

    /// <summary>任务执行时间线（开始 / 结束时刻），用于回看调度顺序。</summary>
    public ObservableCollection<TimelineEntry> Timeline { get; }

    /// <summary>待人工介入 / 依赖阻塞任务清单（二期；不计入失败率）。</summary>
    public ObservableCollection<AttentionTaskRowViewModel> AttentionTasks { get; }

    /// <summary>工作流阶段时间线（二期：跨任务拉平展示阶段状态 / 门禁 / 修订循环）。</summary>
    public ObservableCollection<StageTimelineRowViewModel> StageTimeline { get; }

    /// <summary>宿主缓存的最近一次批处理报告，未运行过时为 null。</summary>
    public BatchReport? Report => _host.LastReport;

    /// <summary>是否存在可展示的报告，为假时显示引导文案。</summary>
    public bool HasReport => _host.LastReport is not null;

    /// <summary>报告文件路径，尚未生成时显示占位文案。</summary>
    public string ReportPath => _host.LastReportPath ?? "（尚未生成）";

    /// <summary>一行汇总：总数 / 成功 / 失败 / 中断 / 未处理，有待人工时追加计数。</summary>
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

    /// <summary>是否有待人工介入或依赖阻塞任务，为假时隐藏该清单。</summary>
    public bool HasAttentionTasks => AttentionTasks.Count > 0;

    /// <summary>是否展示二期区块（门禁统计 / 产物 / diff / 阶段时间线任一有数据）。</summary>
    public bool HasPhase2 => !string.IsNullOrEmpty(Phase2Text) || AttentionTasks.Count > 0 || StageTimeline.Count > 0;

    /// <summary>耗时说明：净执行秒数与含等待的总耗时。</summary>
    public string DurationText => Report is null
        ? "-"
        : $"净执行 {Report.ActiveDurationSeconds:0.#} 秒（总耗时含等待 {Report.DurationSeconds:0.#} 秒）";

    /// <summary>降级模型使用情况：触发次数、模型名、花费及其实测 / 估算来源。</summary>
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

    /// <summary>等待耗时：窗口外等待与额度冷却等待的累计秒数。</summary>
    public string WaitText => Report is null
        ? "-"
        : $"时段外累计等待 {Report.WindowWaitSeconds:0.#} 秒 ｜ 额度冷却累计等待 {Report.CooldownWaitSeconds:0.#} 秒";

    /// <summary>关机动作结果：是否执行及原因，未启用时显示「未启用」。</summary>
    public string ShutdownText => Report?.Shutdown is { Requested: true } s
        ? $"{(s.Executed ? "已执行关机" : "未执行")}（{s.Reason}）"
        : "未启用";

    /// <summary>清空并重建全部清单，同时通知所有派生文案重新求值。</summary>
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
    /// <summary>包装一条任务报告条目，供待人工清单行显示。</summary>
    /// <param name="entry">报告中的任务条目。</param>
    public AttentionTaskRowViewModel(TaskReportEntry entry) => Entry = entry;

    /// <summary>原始任务报告条目，其余展示属性均由其派生。</summary>
    public TaskReportEntry Entry { get; }

    /// <summary>该行对应的任务 ID。</summary>
    public string TaskId => Entry.TaskId;

    /// <summary>任务状态中文文案：待人工介入 / 依赖阻塞，其余回退枚举名。</summary>
    public string StatusText => Entry.Status switch
    {
        TaskStatus.NeedsAttention => "待人工介入",
        TaskStatus.Blocked => "依赖阻塞",
        _ => Entry.Status.ToString()
    };

    /// <summary>阻断来源拼接文案，无阻断来源时为空串。</summary>
    public string BlockedByText => Entry.BlockedBy is { Count: > 0 }
        ? "阻断来源：" + string.Join("、", Entry.BlockedBy)
        : string.Empty;

    /// <summary>是否存在阻断来源，为假时隐藏该行提示。</summary>
    public bool HasBlockedBy => Entry.BlockedBy is { Count: > 0 };

    /// <summary>任务遗留的问题说明，无则为 null。</summary>
    public string? Leftover => Entry.Leftover;

    /// <summary>是否有遗留问题说明，为假时隐藏对应文本块。</summary>
    public bool HasLeftover => !string.IsNullOrWhiteSpace(Entry.Leftover);

    /// <summary>任务失败时的错误摘要，无则为 null。</summary>
    public string? Error => Entry.Error;
}

/// <summary>工作流阶段时间线行（二期报告页：任务 × 阶段拉平）。</summary>
public sealed class StageTimelineRowViewModel : ObservableObject
{
    /// <summary>创建阶段时间线行，记录所属任务 ID 与阶段结果。</summary>
    public StageTimelineRowViewModel(string taskId, StageResult stage)
    {
        _taskId = taskId;
        _stage = stage;
    }

    private readonly string _taskId;
    private readonly StageResult _stage;

    /// <summary>该阶段所属的任务 ID。</summary>
    public string TaskId => _taskId;

    /// <summary>阶段显示名，未配置名称时回退阶段 ID。</summary>
    public string StageText => string.IsNullOrWhiteSpace(_stage.Name) ? _stage.StageId : _stage.Name!;

    /// <summary>阶段状态中文文案（待执行 / 执行中 / 完成 / 失败等）。</summary>
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

    /// <summary>阶段门禁结果：通过 / 未过，未执行阶段显示短横线。</summary>
    public string GateText => _stage.Status == TaskStatus.Pending
        ? "-"
        : _stage.GatePassed ? "通过" : "未过";

    /// <summary>该阶段的修订循环次数，未发生循环时显示短横线。</summary>
    public string LoopText => _stage.LoopCount > 0 ? $"{_stage.LoopCount}" : "-";

    /// <summary>该阶段使用的账号与模型，以「·」拼接后展示。</summary>
    public string ModelText => string.Join(" · ", new[]
    {
        string.IsNullOrEmpty(_stage.AccountUsed) ? null : _stage.AccountUsed,
        string.IsNullOrEmpty(_stage.ModelUsed) ? null : _stage.ModelUsed
    }.Where(x => !string.IsNullOrEmpty(x)));
}
