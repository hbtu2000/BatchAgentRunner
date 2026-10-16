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
        Refresh();
    }

    public IRelayCommand RefreshCommand { get; }

    public ObservableCollection<FailedTaskEntry> FailedTasks { get; }

    public ObservableCollection<TimelineEntry> Timeline { get; }

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

            return $"总数 {r.Total} ｜ 成功 {r.Done} ｜ 失败 {r.Failed} ｜ 中断 {r.Interrupted} ｜ 未处理 {r.Pending} ｜ 续跑跳过 {r.SkippedAlreadyDone}";
        }
    }

    public string DurationText => Report is null ? "-" : $"{Report.DurationSeconds:0.#} 秒";

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
        }

        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(ReportPath));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(FallbackText));
        OnPropertyChanged(nameof(WaitText));
        OnPropertyChanged(nameof(ShutdownText));
    }
}
