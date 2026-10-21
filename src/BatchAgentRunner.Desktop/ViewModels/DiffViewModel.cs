using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Helpers;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 变更概览页（二期 REQ-017 diff 视图）：从最近一次报告读取 diff 采集记录，
/// 按任务/阶段分组展示文件树，unified diff 按行着色（增绿 / 删红 / 块头灰）。
/// </summary>
public sealed class DiffViewModel : ViewModelBase
{
    private static readonly Regex HunkHeader = new(@"^@@\s+-\d+(?:,\d+)?\s+\+\d+(?:,\d+)?\s@@", RegexOptions.Compiled);

    private readonly IEngineHost _host;

    private bool _onlyModified;
    private string _stageFilter = string.Empty;
    private DiffRowViewModel? _selectedDiff;
    private string _reportPathText = string.Empty;

    public DiffViewModel(IEngineHost host)
    {
        _host = host;
        Diffs = new ObservableCollection<DiffRowViewModel>();
        DiffLines = new ObservableCollection<DiffLineViewModel>();
        RefreshCommand = new RelayCommand(Refresh);
    }

    public IRelayCommand RefreshCommand { get; }

    public ObservableCollection<DiffRowViewModel> Diffs { get; }

    /// <summary>选中 diff 的按行着色内容。</summary>
    public ObservableCollection<DiffLineViewModel> DiffLines { get; }

    /// <summary>仅看 Modified（隐藏 Added/Deleted）。</summary>
    public bool OnlyModified
    {
        get => _onlyModified;
        set
        {
            if (SetProperty(ref _onlyModified, value))
            {
                Rebuild();
            }
        }
    }

    /// <summary>阶段筛选（空 = 全部阶段；不区分大小写包含匹配）。</summary>
    public string StageFilter
    {
        get => _stageFilter;
        set
        {
            if (SetProperty(ref _stageFilter, value))
            {
                Rebuild();
            }
        }
    }

    public DiffRowViewModel? SelectedDiff
    {
        get => _selectedDiff;
        set
        {
            if (SetProperty(ref _selectedDiff, value))
            {
                RebuildLines();
            }
        }
    }

    public string ReportPathText
    {
        get => _reportPathText;
        private set => SetProperty(ref _reportPathText, value);
    }

    public bool HasDiffs => Diffs.Count > 0;

    public string SummaryText
    {
        get
        {
            var r = _host.LastReport;
            if (r is null)
            {
                return "尚无报告：请先运行一次批处理（需在配置中启用 diff 采集）。";
            }

            var modified = r.Diffs.Count(d => d.Kind == DiffKind.Modified);
            var added = r.Diffs.Count(d => d.Kind == DiffKind.Added);
            var deleted = r.Diffs.Count(d => d.Kind == DiffKind.Deleted);
            return $"共 {r.Diffs.Count} 个变更：修改 {modified} ／ 新增 {added} ／ 删除 {deleted}";
        }
    }

    /// <summary>导航进入时刷新（MainViewModel.Navigate 调用）。</summary>
    public void Refresh()
    {
        ReportPathText = _host.LastReportPath ?? "（尚未生成报告）";
        Rebuild();
        OnPropertyChanged(nameof(SummaryText));
    }

    private void Rebuild()
    {
        Diffs.Clear();
        var diffs = _host.LastReport?.Diffs ?? new List<DiffRecord>();
        foreach (var d in diffs)
        {
            if (_onlyModified && d.Kind != DiffKind.Modified)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(_stageFilter) &&
                !(d.StageId ?? string.Empty).Contains(_stageFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Diffs.Add(new DiffRowViewModel(d));
        }

        if (SelectedDiff is not null && Diffs.All(d => !ReferenceEquals(d.Source, SelectedDiff.Source)))
        {
            SelectedDiff = Diffs.FirstOrDefault();
        }
        else if (SelectedDiff is null && Diffs.Count > 0)
        {
            SelectedDiff = Diffs[0];
        }

        OnPropertyChanged(nameof(HasDiffs));
        OnPropertyChanged(nameof(SummaryText));
        RebuildLines();
    }

    private void RebuildLines()
    {
        DiffLines.Clear();
        var diff = SelectedDiff?.Source;
        if (diff?.UnifiedDiff is null)
        {
            return;
        }

        foreach (var line in diff.UnifiedDiff.Split('\n'))
        {
            DiffLines.Add(HunkHeader.IsMatch(line)
                ? DiffLineViewModel.Hunk(line)
                : line.StartsWith('+') ? DiffLineViewModel.Added(line)
                : line.StartsWith('-') ? DiffLineViewModel.Removed(line)
                : DiffLineViewModel.Context(line));
        }
    }
}

/// <summary>diff 文件行：一条采集记录的展示属性。</summary>
public sealed class DiffRowViewModel : ObservableObject
{
    public DiffRowViewModel(DiffRecord source) => Source = source;

    public DiffRecord Source { get; }

    public string TaskId => Source.TaskId;

    public string StageText => string.IsNullOrWhiteSpace(Source.StageId) ? "-" : Source.StageId!;

    public string FilePath => Source.FilePath;

    public string KindText => Source.Kind switch
    {
        DiffKind.Added => "新增",
        DiffKind.Deleted => "删除",
        _ => "修改"
    };

    public IBrush KindBrush => AppBrushes.ForDiffKind(Source.Kind);

    public bool HasContent => !string.IsNullOrWhiteSpace(Source.UnifiedDiff);
}

/// <summary>unified diff 单行（按前缀着色；Classes 不支持绑定，直接给画刷）。</summary>
public sealed class DiffLineViewModel
{
    private DiffLineViewModel(string text, IBrush brush) => (Text, Foreground) = (text, brush);

    public string Text { get; }

    public IBrush Foreground { get; }

    public static DiffLineViewModel Added(string text) =>
        new(text, AppBrushes.ForDiffKind(DiffKind.Added));

    public static DiffLineViewModel Removed(string text) =>
        new(text, AppBrushes.ForDiffKind(DiffKind.Deleted));

    public static DiffLineViewModel Hunk(string text) =>
        new(text, AppBrushes.DiffText(true));

    public static DiffLineViewModel Context(string text) =>
        new(text, AppBrushes.DiffText(false));
}
