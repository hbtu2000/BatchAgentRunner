using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 批次统计可绑定状态（P-06 分解产物）。
/// 原 <c>MonitorViewModel</c> 的统计属性（Total / Done / … / EtaText / Current*Text）由
/// <c>Recalculate</c> 一处写入；分解后由本类型持有（facade 的 <see cref="MonitorViewModel"/>
/// 同名属性转发到此），<see cref="BatchStatistics"/> 的计算结果经 <see cref="Apply"/> 一次性落入。
/// </summary>
internal sealed class BatchStatsState : ViewModelBase
{
    private int _total;
    private int _done;
    private int _failed;
    private int _running;
    private int _interrupted;
    private int _pending;
    private int _needsAttention;
    private int _blocked;
    private string _etaText = "—";
    private string _currentTaskText = "无";
    private string _currentComboText = "-";
    private string _currentWorkspaceText = "-";

    /// <summary>分解前 MonitorViewModel._skipped 字段从未被赋值（恒 0），此处保留同语义。</summary>
    private readonly int _skipped;

    /// <summary>创建统计状态，各计数从 0 起，等待首次 Apply 覆盖。</summary>
    public BatchStatsState()
    {
        _skipped = 0;
    }

    /// <summary>参与统计的任务总数，由快照一次性写入。</summary>
    public int Total { get => _total; private set => SetProperty(ref _total, value); }

    /// <summary>已成功完成的任务数。</summary>
    public int Done { get => _done; private set => SetProperty(ref _done, value); }

    /// <summary>执行失败的任务数。</summary>
    public int Failed { get => _failed; private set => SetProperty(ref _failed, value); }

    /// <summary>当前正在执行的任务数。</summary>
    public int Running { get => _running; private set => SetProperty(ref _running, value); }

    /// <summary>被中断的任务数。</summary>
    public int Interrupted { get => _interrupted; private set => SetProperty(ref _interrupted, value); }

    /// <summary>尚未开始的任务数。</summary>
    public int Pending { get => _pending; private set => SetProperty(ref _pending, value); }

    /// <summary>跳过计数（领域无 TaskStatus.Skipped，监控页该卡位沿用分解前语义：恒为 0，不计入失败率）。</summary>
    public int Skipped => _skipped;

    /// <summary>待人工介入任务数（二期：门禁超限等，不计入失败率）。</summary>
    public int NeedsAttention { get => _needsAttention; private set => SetProperty(ref _needsAttention, value); }

    /// <summary>依赖阻塞任务数（二期：DAG 上游失败导致）。</summary>
    public int Blocked { get => _blocked; private set => SetProperty(ref _blocked, value); }

    /// <summary>预计剩余时间（基于已完成的均值 × 待处理数粗略估算，无数据时为「—」）。</summary>
    public string EtaText { get => _etaText; private set => SetProperty(ref _etaText, value); }

    /// <summary>当前执行任务的标识与指令摘要，无运行任务时为「无」。</summary>
    public string CurrentTaskText { get => _currentTaskText; private set => SetProperty(ref _currentTaskText, value); }

    /// <summary>当前任务使用的账号与模型，以「+」拼接。</summary>
    public string CurrentComboText { get => _currentComboText; private set => SetProperty(ref _currentComboText, value); }

    /// <summary>当前任务的工作空间路径，未指定时回退默认工作空间。</summary>
    public string CurrentWorkspaceText { get => _currentWorkspaceText; private set => SetProperty(ref _currentWorkspaceText, value); }

    /// <summary>落入一批统计快照（逐项 SetProperty，变更项才发通知）。</summary>
    public void Apply(BatchStatistics.Snapshot s)
    {
        Total = s.Total;
        Done = s.Done;
        Failed = s.Failed;
        Running = s.Running;
        Interrupted = s.Interrupted;
        Pending = s.Pending;
        NeedsAttention = s.NeedsAttention;
        Blocked = s.Blocked;
        EtaText = s.EtaText;
        CurrentTaskText = s.CurrentTaskText;
        CurrentComboText = s.CurrentComboText;
        CurrentWorkspaceText = s.CurrentWorkspaceText;
    }
}
