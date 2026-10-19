using System.Collections.ObjectModel;
using System.IO;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Desktop.Services;
using BatchAgentRunner.Infrastructure.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 模板库页（二期 REQ-016 + 场景模板库 S1–S16）：选择模板 → 编辑变量 →
/// 展开预览 → 生成任务文件（JSON，产物与手写任务字节级等价）。
/// </summary>
public sealed class TemplateEditorViewModel : ViewModelBase
{
    /// <summary>变量多值输入分隔符（CA1861：常量数组提为 static readonly）。</summary>
    private static readonly char[] VarValueSeparators = { ',', '\n', ';' };

    private readonly IEngineHost _host;
    private readonly FileWorkflowTemplateStore _store;

    private ObservableCollection<TemplateRowViewModel> _templates = new();
    private TemplateRowViewModel? _selectedTemplate;
    private string _workspace = string.Empty;
    private string _outputPath = string.Empty;
    private string _statusMessage = string.Empty;
    private string _flowSummary = string.Empty;
    private bool _isFlowExpanded = true;

    public TemplateEditorViewModel(IEngineHost host)
    {
        _host = host;
        _store = new FileWorkflowTemplateStore();
        _workspace = host.Options.DefaultWorkspace ?? Directory.GetCurrentDirectory();
        var generatedDir = Path.Combine(host.ConfigDirectory, "generated");
        _outputPath = Path.Combine(generatedDir, "template-output.tasks.json");
        PreviewTasks = new ObservableCollection<PreviewTaskRowViewModel>();
        VarInputs = new ObservableCollection<TemplateVarInputViewModel>();
        FlowNodes = new ObservableCollection<TemplateFlowNodeViewModel>();

        LoadCommand = new AsyncRelayCommand(LoadAsync);
        PreviewCommand = new RelayCommand(Preview);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => _selectedTemplate is not null);
        ToggleFlowCommand = new RelayCommand(() => IsFlowExpanded = !IsFlowExpanded);
    }

    public IAsyncRelayCommand LoadCommand { get; }

    public IRelayCommand PreviewCommand { get; }

    public IAsyncRelayCommand GenerateCommand { get; }

    /// <summary>展开 / 收起流程示意。</summary>
    public IRelayCommand ToggleFlowCommand { get; }

    /// <summary>模板清单（内置 S1–S16 + 用户自定义）。</summary>
    public ObservableCollection<TemplateRowViewModel> Templates
    {
        get => _templates;
        private set => SetProperty(ref _templates, value);
    }

    public ObservableCollection<TemplateVarInputViewModel> VarInputs { get; }

    public ObservableCollection<PreviewTaskRowViewModel> PreviewTasks { get; }

    /// <summary>流程示意节点（自上而下 = 执行顺序；单指令模板为单节点）。</summary>
    public ObservableCollection<TemplateFlowNodeViewModel> FlowNodes { get; }

    public bool HasFlow => FlowNodes.Count > 0;

    /// <summary>是否已有展开预览结果（用于空态提示）。</summary>
    public bool HasPreviewTasks => PreviewTasks.Count > 0;

    /// <summary>标题旁的节点计数（如「4 个阶段」/「单指令」）。</summary>
    public string FlowNodeCountText => _selectedTemplate?.Spec.Workflow is { Stages.Count: > 0 } wf
        ? $"{wf.Stages.Count} 个阶段"
        : "单指令";

    /// <summary>流程示意顶部的一句话摘要（工作流配置摘要 / 单指令说明）。</summary>
    public string FlowSummary
    {
        get => _flowSummary;
        private set => SetProperty(ref _flowSummary, value);
    }

    /// <summary>流程示意是否展开（收起后仅保留标题行，点击可再展开）。</summary>
    public bool IsFlowExpanded
    {
        get => _isFlowExpanded;
        set
        {
            if (SetProperty(ref _isFlowExpanded, value))
            {
                OnPropertyChanged(nameof(FlowToggleText));
            }
        }
    }

    public string FlowToggleText => _isFlowExpanded ? "收起" : "展开";

    public TemplateRowViewModel? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
            {
                OnPropertyChanged(nameof(SelectedDescription));
                OnPropertyChanged(nameof(SelectedWorkflowText));
                OnPropertyChanged(nameof(SelectedChainText));
                OnPropertyChanged(nameof(FlowNodeCountText));
                IsFlowExpanded = true;
                BuildFlow();
                LoadVarInputs();
                PreviewTasks.Clear();
                OnPropertyChanged(nameof(HasPreviewTasks));
                GenerateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedDescription => _selectedTemplate?.Spec.InstructionTemplate ?? string.Empty;

    public string SelectedWorkflowText => _selectedTemplate?.Spec.Workflow is { } wf
        ? $"工作流模板：{wf.Stages.Count} 个阶段"
        : "单指令模板";

    public string SelectedChainText => _selectedTemplate?.Spec.ChainDependency == true
        ? "链式依赖：开启（任务依次 depends_on 前一个）"
        : "链式依赖：关闭";

    /// <summary>
    /// 工作空间（REQ-003a）：命名空间名或目录路径。作为变量文件 glob / CSV 的相对路径解析根，
    /// 并写入每条生成任务（<see cref="TaskItem.Workspace"/>），使其可直接运行。
    /// </summary>
    public string Workspace
    {
        get => _workspace;
        set => SetProperty(ref _workspace, value);
    }

    /// <summary>生成任务文件的输出路径（JSON 数组，可直接在高级配置中选用）。</summary>
    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>导航进入时刷新模板清单。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var specs = await _store.ListAsync().ConfigureAwait(true);
            Templates = new ObservableCollection<TemplateRowViewModel>(
                specs.Select(s => new TemplateRowViewModel(s)));
            StatusMessage = $"已加载 {Templates.Count} 个模板";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载模板失败：{ex.Message}";
        }
    }

    /// <summary>用当前变量取值展开模板（不落盘）。</summary>
    public void Preview()
    {
        PreviewTasks.Clear();
        OnPropertyChanged(nameof(HasPreviewTasks));
        if (_selectedTemplate is null)
        {
            return;
        }

        try
        {
            var tasks = TemplateExpander.Expand(BuildSpec(), ResolveWorkspaceRoot(_workspace), NormalizeWorkspaceForTask(_workspace));
            foreach (var t in tasks)
            {
                PreviewTasks.Add(new PreviewTaskRowViewModel(t));
            }

            StatusMessage = $"展开得到 {tasks.Count} 条任务（预览未落盘）";
            OnPropertyChanged(nameof(HasPreviewTasks));
        }
        catch (Exception ex)
        {
            StatusMessage = $"展开失败：{ex.Message}";
        }
    }

    private async Task GenerateAsync()
    {
        if (_selectedTemplate is null)
        {
            return;
        }

        try
        {
            var tasks = TemplateExpander.Expand(BuildSpec(), ResolveWorkspaceRoot(_workspace), NormalizeWorkspaceForTask(_workspace));
            var dir = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(_outputPath, JsonDefaults.Serialize(tasks)).ConfigureAwait(true);
            StatusMessage = $"已生成 {tasks.Count} 条任务 → {_outputPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"生成失败：{ex.Message}";
        }
    }

    /// <summary>把界面变量输入写回模板副本（不改动仓库里的模板定义）。</summary>
    private TemplateSpec BuildSpec()
    {
        var spec = _selectedTemplate!.Spec.Clone();
        foreach (var input in VarInputs)
        {
            var def = spec.Vars.FirstOrDefault(v => string.Equals(v.Name, input.Name, StringComparison.Ordinal));
            if (def is null)
            {
                continue;
            }

            def.Default = input.Value;
            def.Values = input.ValuesText
                .Split(VarValueSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return spec;
    }

    /// <summary>
    /// 把界面填写的「工作空间」（命名空间名或目录路径）解析为变量文件 glob / CSV 的实际目录根。
    /// 命中命名工作空间则用其 Path；否则按目录路径处理（相对路径就地展开为绝对路径）；
    /// 为空时退回当前目录（仅用于文件解析，任务级 workspace 仍按 <see cref="NormalizeWorkspaceForTask"/> 处理）。
    /// </summary>
    private string? ResolveWorkspaceRoot(string? ws)
    {
        if (string.IsNullOrWhiteSpace(ws))
        {
            return Directory.GetCurrentDirectory();
        }

        var named = _host.Options.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, ws, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return named.Path;
        }

        return Path.IsPathRooted(ws) ? ws : Path.GetFullPath(ws);
    }

    /// <summary>
    /// 规整为可直接落盘到任务的工作空间值：命中命名工作空间则存其 Name（便于随配置漂移重新解析），
    /// 否则存绝对目录路径（保证任务可直接运行）；空 = 不写，运行时由引擎套用默认 / 全局默认工作空间。
    /// </summary>
    private string? NormalizeWorkspaceForTask(string? ws)
    {
        if (string.IsNullOrWhiteSpace(ws))
        {
            return null;
        }

        var named = _host.Options.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, ws, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return named.Name;
        }

        return Path.IsPathRooted(ws) ? ws : Path.GetFullPath(ws);
    }

    /// <summary>
    /// 构建流程示意：按阶段依赖（depends_on_stage）做拓扑排序，生成自上而下的执行顺序；
    /// 单指令模板退化为单节点。节点上标注门禁 / 修订循环 / 执行器覆盖 / 被引用的产出。
    /// </summary>
    private void BuildFlow()
    {
        FlowNodes.Clear();
        var spec = _selectedTemplate?.Spec;
        if (spec is null)
        {
            FlowSummary = string.Empty;
            OnPropertyChanged(nameof(HasFlow));
            return;
        }

        if (spec.Workflow is not { Stages.Count: > 0 } wf)
        {
            FlowNodes.Add(new TemplateFlowNodeViewModel(
                index: 1,
                stage: null,
                spec: spec,
                consumedBy: Array.Empty<string>(),
                isLast: true));
            FlowSummary = spec.FileGlob is { Count: > 0 }
                ? "单指令模板：每命中一个文件生成一条任务，独立执行、无阶段编排"
                : "单指令模板：一条指令即一个任务，无阶段编排";
            OnPropertyChanged(nameof(HasFlow));
            return;
        }

        var ordered = TopoSortStages(wf.Stages);
        var consumers = BuildOutputConsumers(ordered);

        for (var i = 0; i < ordered.Count; i++)
        {
            FlowNodes.Add(new TemplateFlowNodeViewModel(
                index: i + 1,
                stage: ordered[i],
                spec: spec,
                consumedBy: consumers.TryGetValue(ordered[i].Id, out var by) ? by : Array.Empty<string>(),
                isLast: i == ordered.Count - 1));
        }

        FlowSummary =
            $"{wf.Name}：{ordered.Count} 个阶段顺序执行（{DescribeOnFailure(wf.OnFailure)}，门禁默认最多修订 {wf.DefaultMaxLoops} 轮{(wf.FreshSessionPerStage ? "，每阶段独立会话" : string.Empty)}）";
        OnPropertyChanged(nameof(HasFlow));
    }

    /// <summary>按 depends_on_stage 做稳定拓扑排序；存在环或悬空依赖时退化为原始顺序。</summary>
    private static List<WorkflowStage> TopoSortStages(List<WorkflowStage> stages)
    {
        var result = new List<WorkflowStage>(stages.Count);
        var known = new HashSet<string>(stages.Select(s => s.Id), StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);

        var remaining = stages.ToList();
        var guard = stages.Count + 1;
        while (remaining.Count > 0 && guard-- > 0)
        {
            var progressed = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var dep = remaining[i].DependsOnStage;
                var ready = string.IsNullOrWhiteSpace(dep)
                    || !known.Contains(dep)
                    || placed.Contains(dep);
                if (!ready)
                {
                    continue;
                }

                result.Add(remaining[i]);
                placed.Add(remaining[i].Id);
                remaining.RemoveAt(i);
                progressed = true;
                i--;
            }

            if (!progressed)
            {
                // 有环：按原顺序补齐，保证界面仍能展示完整流程
                result.AddRange(remaining);
                break;
            }
        }

        return result;
    }

    /// <summary>统计每个阶段的产出被哪些后续阶段以 {output:stageId} 引用。</summary>
    private static Dictionary<string, List<string>> BuildOutputConsumers(List<WorkflowStage> stages)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var producer in stages)
        {
            foreach (var consumer in stages)
            {
                if (string.Equals(producer.Id, consumer.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!consumer.InstructionTemplate.Contains($"{{output:{producer.Id}}}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!map.TryGetValue(producer.Id, out var list))
                {
                    list = new List<string>();
                    map[producer.Id] = list;
                }

                list.Add(consumer.Id);
            }
        }

        return map;
    }

    private static string DescribeOnFailure(WorkflowOnFailure onFailure) => onFailure switch
    {
        WorkflowOnFailure.MarkNeedsAttention => "阶段失败即停并标记待人工接管",
        WorkflowOnFailure.Fail => "阶段失败即停并标记失败",
        WorkflowOnFailure.ContinueNextStage => "阶段失败也继续下一阶段",
        _ => onFailure.ToString()
    };

    private void LoadVarInputs()
    {
        VarInputs.Clear();
        if (_selectedTemplate is null)
        {
            return;
        }

        foreach (var def in _selectedTemplate.Spec.Vars)
        {
            VarInputs.Add(new TemplateVarInputViewModel(def));
        }
    }
}

/// <summary>模板清单行。</summary>
public sealed class TemplateRowViewModel : ObservableObject
{
    public TemplateRowViewModel(TemplateSpec spec) => Spec = spec;

    public TemplateSpec Spec { get; }

    public string Id => Spec.Id;

    public string Name => Spec.Name;

    public string VarSummary => Spec.Vars.Count == 0 ? "无变量" : $"{Spec.Vars.Count} 个变量";

    public bool IsWorkflow => Spec.Workflow is { Stages.Count: > 0 };

    public string KindText => IsWorkflow ? "工作流" : "单指令";
}

/// <summary>模板变量输入行（默认值 + 笛卡尔积多值）。</summary>
public sealed class TemplateVarInputViewModel : ObservableObject
{
    private string _value;
    private string _valuesText;

    public TemplateVarInputViewModel(TemplateVarDef def)
    {
        Def = def;
        _value = def.Default ?? string.Empty;
        _valuesText = def.Values is { Count: > 0 } ? string.Join(",", def.Values) : string.Empty;
    }

    public TemplateVarDef Def { get; }

    public string Name => Def.Name;

    public string SourceText => Def.Source switch
    {
        VarSource.Literal => "字面量",
        VarSource.FileList => "文件清单",
        VarSource.CsvColumn => "CSV 列",
        _ => Def.Source.ToString()
    };

    /// <summary>变量取值（写入 def.Default）。</summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>多值（逗号分隔，Source=Literal 时做笛卡尔积；写入 def.Values）。</summary>
    public string ValuesText
    {
        get => _valuesText;
        set => SetProperty(ref _valuesText, value);
    }
}

/// <summary>
/// 流程示意节点（二期 UI）：一个节点 = 一个执行阶段（单指令模板则为一个虚拟节点）。
/// 节点之间的连线由视图根据 <see cref="IsLast"/> 绘制成箭头。
/// </summary>
public sealed class TemplateFlowNodeViewModel
{
    private static readonly System.Text.RegularExpressions.Regex PlaceholderRx = new(
        @"\{(output:[^}]+|var:[^}]+|task\.[^}]+)\}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public TemplateFlowNodeViewModel(
        int index,
        WorkflowStage? stage,
        TemplateSpec spec,
        IReadOnlyList<string> consumedBy,
        bool isLast)
    {
        Index = index;
        Stage = stage;
        IsLast = isLast;
        IsSingleStep = stage is null;

        var template = stage?.InstructionTemplate ?? spec.InstructionTemplate;
        Title = stage is null
            ? spec.Name
            : string.IsNullOrWhiteSpace(stage.Name) ? stage.Id : stage.Name;
        Template = string.IsNullOrWhiteSpace(template) ? "（未填写指令模板）" : template;
        Placeholders = PlaceholderRx.Matches(Template).Select(m => m.Value).Distinct().ToArray();
        ExecutorText = BuildExecutorText(stage);
        GateText = BuildGateText(stage);
        LoopText = stage is null
            ? string.Empty
            : $"最多 {stage.MaxLoops ?? (spec.Workflow?.DefaultMaxLoops ?? 3)} 轮修订（{DescribeStageGateFail(stage.OnGateFail)}）";
        DependsText = string.IsNullOrWhiteSpace(stage?.DependsOnStage)
            ? string.Empty
            : $"依赖阶段：{stage!.DependsOnStage}";
        ConsumedByText = consumedBy.Count == 0
            ? string.Empty
            : $"产出被引用：{string.Join("、", consumedBy)}";
    }

    public int Index { get; }

    public WorkflowStage? Stage { get; }

    public bool IsLast { get; }

    /// <summary>单指令模板的虚拟节点（无门禁/执行器概念）。</summary>
    public bool IsSingleStep { get; }

    public string IndexText => Index.ToString();

    public string Title { get; }

    public string Template { get; }

    public IReadOnlyList<string> Placeholders { get; }

    public string ExecutorText { get; }

    public string GateText { get; }

    public string LoopText { get; }

    public string DependsText { get; }

    public string ConsumedByText { get; }

    public bool HasPlaceholders => Placeholders.Count > 0;

    public bool HasDepends => DependsText.Length > 0;

    public bool HasConsumedBy => ConsumedByText.Length > 0;

    public bool HasLoop => LoopText.Length > 0;

    public bool HasConnector => !IsLast;

    public bool HasExecutor => ExecutorText.Length > 0;

    public bool HasGate => GateText.Length > 0;

    private static string BuildExecutorText(WorkflowStage? stage)
    {
        var ex = stage?.Executor;
        if (ex is null)
        {
            return stage is null ? string.Empty : "执行器：继承全局配置";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ex.AccountId))
        {
            parts.Add($"账号 {ex.AccountId}");
        }

        if (!string.IsNullOrWhiteSpace(ex.Model))
        {
            parts.Add($"模型 {ex.Model}");
        }

        if (!string.IsNullOrWhiteSpace(ex.ExpertId))
        {
            parts.Add($"专家 {ex.ExpertId}");
        }

        if (!string.IsNullOrWhiteSpace(ex.Workspace))
        {
            parts.Add($"工作空间 {ex.Workspace}");
        }

        return parts.Count == 0 ? string.Empty : "执行器：" + string.Join(" · ", parts);
    }

    private static string BuildGateText(WorkflowStage? stage)
    {
        if (stage?.Gate is not { } gate)
        {
            return stage is null ? string.Empty : "门禁：无";
        }

        var kind = gate.Kind switch
        {
            GateKind.Command => "命令",
            GateKind.Review => "审查",
            GateKind.Manual => "人工",
            _ => gate.Kind.ToString()
        };

        var detail = gate.Kind switch
        {
            GateKind.Command when !string.IsNullOrWhiteSpace(gate.Command) =>
                $"{gate.Command}（期望退出码 {gate.ExpectedExitCode ?? "0"}）",
            GateKind.Review when !string.IsNullOrWhiteSpace(gate.ReviewChecklist) =>
                Shorten(gate.ReviewChecklist, 60),
            GateKind.Manual => gate.ManualApproval ? "需人工确认" : "自动通过",
            _ => string.Empty
        };

        return string.IsNullOrEmpty(detail) ? $"门禁：{kind}" : $"门禁：{kind} · {detail}";
    }

    private static string DescribeStageGateFail(StageOnGateFail onGateFail) => onGateFail switch
    {
        StageOnGateFail.FixLoop => "不通过则修订重跑",
        StageOnGateFail.StopStage => "不通过则停止本阶段",
        StageOnGateFail.MarkNeedsAttention => "不通过则标记待人工接管",
        _ => onGateFail.ToString()
    };

    private static string Shorten(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}

/// <summary>展开预览任务行。</summary>
public sealed class PreviewTaskRowViewModel : ObservableObject
{
    public PreviewTaskRowViewModel(TaskItem task) => Task = task;

    public TaskItem Task { get; }

    public string Id => Task.Id;

    public string Instruction => Task.Instruction;

    public string DependsText => Task.DependsOn is { Count: > 0 }
        ? "依赖：" + string.Join("、", Task.DependsOn)
        : string.Empty;

    public bool HasDepends => Task.DependsOn is { Count: > 0 };

    public string StageText => Task.IsWorkflow ? $"{Task.Workflow!.Stages.Count} 阶段" : "单指令";

    public string WorkspaceText => string.IsNullOrWhiteSpace(Task.Workspace)
        ? "工作空间：（默认）"
        : $"工作空间：{Task.Workspace}";

    public bool HasWorkspace => !string.IsNullOrWhiteSpace(Task.Workspace);
}
