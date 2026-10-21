using System;
using System.Collections.ObjectModel;
using System.Linq;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Desktop.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 实时输出流协作者（P-06 从 <see cref="MonitorViewModel"/> 抽离）。
/// 独占「全局输出缓冲 + 跟随过滤视图 + 跟随模式状态」：原始 stdout 按行拆分分类
/// （分类规则在 <see cref="OutputTagClassifier"/>）、引擎调度类事件并入流内，
/// 并按「随任务联动 / 全部任务」两种模式重建 <see cref="DisplayedOutputLines"/>。
/// 任务清单经 ctor 的 <c>taskItems</c> 委托按需读取（跟随标题展示目标任务），不持引用。
/// </summary>
internal sealed class OutputStreamCoordinator : ViewModelBase
{
    private readonly Func<IReadOnlyList<TaskItem>> _taskItems;

    private readonly ObservableCollection<OutputLineItem> _outputLines = new();
    private readonly ObservableCollection<OutputLineItem> _displayedOutput = new();

    private string? _followTaskId;
    private bool _followPinned;
    private OutputFollowMode _followMode = OutputFollowMode.Selected;

    private string _outputFollowTitle = "实时输出流";
    private bool _hasDisplayedOutput;
    private string _outputEmptyHint = "选择左侧任务可查看其输出；运行中将自动跟随当前执行任务";

    public OutputStreamCoordinator(Func<IReadOnlyList<TaskItem>> taskItems)
    {
        _taskItems = taskItems;
    }

    /// <summary>全局输出缓冲（保留全部任务历史，上限 500 行）。</summary>
    public ObservableCollection<OutputLineItem> OutputLines => _outputLines;

    /// <summary>
    /// 实时输出流 ListBox 的实际数据源：按「联动任务」过滤后的行（随任务联动模式），
    /// 或「全部任务」混合行（全部模式）。解决并发任务输出混在一起的问题。
    /// </summary>
    public ObservableCollection<OutputLineItem> DisplayedOutputLines => _displayedOutput;

    /// <summary>是否显示全部任务的混合输出（false = 随任务联动，仅显示联动任务，默认）。</summary>
    public bool ShowAllOutput
    {
        get => _followMode == OutputFollowMode.All;
        set
        {
            var next = value ? OutputFollowMode.All : OutputFollowMode.Selected;
            if (_followMode == next)
            {
                return;
            }

            _followMode = next;
            RebuildDisplayedOutput();
            UpdateOutputFollowTitle();
            OnPropertyChanged();
        }
    }

    /// <summary>输出流标题：展示当前联动目标任务（如「实时输出流 · T3」）。</summary>
    public string OutputFollowTitle
    {
        get => _outputFollowTitle;
        private set => SetProperty(ref _outputFollowTitle, value);
    }

    /// <summary>当前是否有可展示的输出行（用于切换空状态提示）。</summary>
    public bool HasDisplayedOutput
    {
        get => _hasDisplayedOutput;
        private set => SetProperty(ref _hasDisplayedOutput, value);
    }

    /// <summary>无输出时的提示文案（随选择/跟随状态变化）。</summary>
    public string OutputEmptyHint
    {
        get => _outputEmptyHint;
        private set => SetProperty(ref _outputEmptyHint, value);
    }

    /// <summary>把一段原始 stdout 按行拆分并逐行分类写入输出流（taskId 用于按任务联动过滤）。</summary>
    public void AppendChunk(DateTimeOffset at, string? chunk, string? taskId)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        foreach (var raw in chunk.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var (kind, tag, dim) = OutputTagClassifier.ClassifyLine(line);
            AppendOutputLine(at, kind, tag, line, dim, taskId);
        }
    }

    /// <summary>把引擎调度类事件（切换 / 额度 / 错误 / 提示）也并入输出流，使流内可见降级轨迹（对齐 mockup）。</summary>
    public void AppendEvent(BatchEvent ev, string? taskId)
    {
        var (kind, tag) = OutputTagClassifier.ClassifyEvent(ev.Kind);

        if (ev.Kind == BatchEventKind.AutoAnswered)
        {
            // 自动应答算成功类，去强调
            AppendOutputLine(ev.At, OutputTagKind.Ok, "应答", ev.Message, dim: true, taskId: taskId);
            return;
        }

        AppendOutputLine(ev.At, kind, tag, ev.Message, dim: false, taskId: taskId);
    }

    /// <summary>
    /// 随任务联动：未手动固定跟随目标时，自动跟随新启动的运行中任务（避免并发任务输出混在一起）。
    /// </summary>
    public void OnTaskStarted(string? taskId)
    {
        if (!_followPinned && _followMode == OutputFollowMode.Selected && !string.IsNullOrEmpty(taskId))
        {
            _followTaskId = taskId;
            RebuildDisplayedOutput();
            UpdateOutputFollowTitle();
        }
    }

    /// <summary>
    /// 选中任务即固定为输出流跟随目标；取消选中则解除固定（回归自动跟随运行中任务）。
    /// </summary>
    public void SetSelectedTask(string? id)
    {
        if (id is not null)
        {
            _followTaskId = id;
            _followPinned = true;
        }
        else
        {
            _followPinned = false;
            _followTaskId = null;
        }

        if (_followMode == OutputFollowMode.Selected)
        {
            RebuildDisplayedOutput();
            UpdateOutputFollowTitle();
        }
    }

    /// <summary>刷新输出流标题与空状态提示（反映当前联动目标任务）。</summary>
    public void RefreshFollowTitle() => UpdateOutputFollowTitle();

    /// <summary>
    /// 写入一行实时输出：始终进入全局缓冲 <see cref="OutputLines"/>（保留全部任务历史），
    /// 并依据跟随模式追加到 <see cref="DisplayedOutputLines"/>（仅联动任务或全部）。
    /// </summary>
    private void AppendOutputLine(DateTimeOffset at, OutputTagKind kind, string tag, string message, bool dim, string? taskId = null)
    {
        var item = new OutputLineItem(at, kind, tag, message, dim, taskId);
        OutputLines.Add(item);
        if (OutputLines.Count > 500)
        {
            OutputLines.RemoveAt(0);
        }

        if (ShouldDisplay(taskId))
        {
            _displayedOutput.Add(item);
            if (_displayedOutput.Count > 500)
            {
                _displayedOutput.RemoveAt(0);
            }

            HasDisplayedOutput = true;
        }
    }

    /// <summary>判断某行是否应出现在当前输出流视图中（随任务联动 → 仅联动任务；全部 → 任意）。</summary>
    private bool ShouldDisplay(string? taskId) =>
        _followMode == OutputFollowMode.All ||
        (!string.IsNullOrEmpty(taskId) && taskId == _followTaskId);

    /// <summary>按当前跟随规则重建输出流视图（切换选中任务 / 切换跟随模式 / 自动跟随变更时调用）。</summary>
    private void RebuildDisplayedOutput()
    {
        _displayedOutput.Clear();
        foreach (var item in OutputLines)
        {
            if (ShouldDisplay(item.TaskId))
            {
                _displayedOutput.Add(item);
            }
        }

        HasDisplayedOutput = _displayedOutput.Count > 0;
    }

    private void UpdateOutputFollowTitle()
    {
        if (_followMode == OutputFollowMode.All)
        {
            OutputFollowTitle = "实时输出流 · 全部任务";
            OutputEmptyHint = "（暂无输出）";
            return;
        }

        if (_followTaskId is null)
        {
            OutputFollowTitle = "实时输出流 · 未选择任务";
            OutputEmptyHint = "选择左侧任务可查看其输出；运行中将自动跟随当前执行任务";
            return;
        }

        var target = _taskItems().FirstOrDefault(t => t.Id == _followTaskId);
        if (target is null)
        {
            OutputFollowTitle = "实时输出流";
            OutputEmptyHint = "（该任务暂无实时输出）";
            return;
        }

        OutputFollowTitle = $"实时输出流 · {target.Id}";
        OutputEmptyHint = $"任务 {target.Id} 暂无实时输出";
    }
}
