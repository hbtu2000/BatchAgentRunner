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

    /// <summary>创建变更概览页，持有宿主并初始化 diff 行与选中详情集合。</summary>
    public DiffViewModel(IEngineHost host)
    {
        _host = host;
        Diffs = new ObservableCollection<DiffRowViewModel>();
        DiffLines = new ObservableCollection<DiffLineViewModel>();
        RefreshCommand = new RelayCommand(Refresh);
    }

    /// <summary>重新读取最近报告的命令，点击后刷新文件树与汇总。</summary>
    public IRelayCommand RefreshCommand { get; }

    /// <summary>筛选后的变更文件列表，随仅看修改与阶段筛选重建。</summary>
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

    /// <summary>列表中选中的变更记录，切换后重算右侧按行着色内容。</summary>
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

    /// <summary>最近一次报告的路径，刷新时写入；尚无报告显示占位文案。</summary>
    public string ReportPathText
    {
        get => _reportPathText;
        private set => SetProperty(ref _reportPathText, value);
    }

    /// <summary>筛选后是否还有变更记录，为假时显示空态提示。</summary>
    public bool HasDiffs => Diffs.Count > 0;

    /// <summary>变更汇总文案，按新增 / 修改 / 删除分别计数；无报告时给出引导语。</summary>
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
    /// <summary>包装一条 diff 采集记录，暴露给列表行显示。</summary>
    /// <param name="source">报告中的 diff 采集记录。</param>
    public DiffRowViewModel(DiffRecord source) => Source = source;

    /// <summary>原始 diff 采集记录，供筛选时比对与取 unified diff 文本。</summary>
    public DiffRecord Source { get; }

    /// <summary>产生该变更的任务 ID。</summary>
    public string TaskId => Source.TaskId;

    /// <summary>阶段标识文案，未采集到阶段时显示短横线。</summary>
    public string StageText => string.IsNullOrWhiteSpace(Source.StageId) ? "-" : Source.StageId!;

    /// <summary>发生变更的文件路径。</summary>
    public string FilePath => Source.FilePath;

    /// <summary>变更类型中文文案：新增 / 删除 / 修改。</summary>
    public string KindText => Source.Kind switch
    {
        DiffKind.Added => "新增",
        DiffKind.Deleted => "删除",
        _ => "修改"
    };

    /// <summary>按变更类型着色的画刷，列表标签据此显示绿 / 红 / 灰。</summary>
    public IBrush KindBrush => AppBrushes.ForDiffKind(Source.Kind);

    /// <summary>是否采集到 unified diff 正文，为假时右侧不展示行内容。</summary>
    public bool HasContent => !string.IsNullOrWhiteSpace(Source.UnifiedDiff);
}

/// <summary>unified diff 单行（按前缀着色；Classes 不支持绑定，直接给画刷）。</summary>
public sealed class DiffLineViewModel
{
    private DiffLineViewModel(string text, IBrush brush) => (Text, Foreground) = (text, brush);

    /// <summary>该行的原始文本（含 +/-/空 前缀）。</summary>
    public string Text { get; }

    /// <summary>该行的前景画刷，由行前缀在构造时决定。</summary>
    public IBrush Foreground { get; }

    /// <summary>把新增行包装成绿色样式的行对象。</summary>
    /// <param name="text">原始行文本。</param>
    public static DiffLineViewModel Added(string text) =>
        new(text, AppBrushes.ForDiffKind(DiffKind.Added));

    /// <summary>把删除行包装成红色样式的行对象。</summary>
    /// <param name="text">原始行文本。</param>
    public static DiffLineViewModel Removed(string text) =>
        new(text, AppBrushes.ForDiffKind(DiffKind.Deleted));

    /// <summary>把 @@ 块头包装成灰色样式的行对象。</summary>
    /// <param name="text">原始行文本。</param>
    public static DiffLineViewModel Hunk(string text) =>
        new(text, AppBrushes.DiffText(true));

    /// <summary>把上下文行包装成常规文字颜色的行对象。</summary>
    /// <param name="text">原始行文本。</param>
    public static DiffLineViewModel Context(string text) =>
        new(text, AppBrushes.DiffText(false));
}
