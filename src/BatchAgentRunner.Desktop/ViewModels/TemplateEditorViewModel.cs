using System.Collections.ObjectModel;
using System.IO;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 模板库页（二期 REQ-016 + 场景模板库 S1–S16）：选择模板 → 编辑变量 →
/// 展开预览 → 生成任务文件（JSON，产物与手写任务字节级等价）。
/// <para>
/// P-05 收口：原 674 行上帝类已按「VM → Coordinator/Presenter」拆薄——交互逻辑（模板加载 /
/// 变量展开 / 工作空间解析 / 流程示意构建）下沉到 <c>ViewModels/TemplateEditor/</c> 下的三个协调者，
/// 本类退化为「状态持有 + 绑定转发 + 编排」薄 facade（&lt;400 行，由架构守卫测试锁死）。
/// </para>
/// </summary>
public sealed class TemplateEditorViewModel : ViewModelBase
{
    private readonly TemplateExpansionCoordinator _expansionCoordinator;
    private readonly TemplateLoadCoordinator _loadCoordinator;

    private ObservableCollection<TemplateRowViewModel> _templates = new();
    private TemplateRowViewModel? _selectedTemplate;
    private string _workspace = string.Empty;
    private string _outputPath = string.Empty;
    private string _statusMessage = string.Empty;
    private string _flowSummary = string.Empty;
    private bool _isFlowExpanded = true;

    public TemplateEditorViewModel(IEngineHost host)
    {
        // P1-4/P1-5：模板库实现由组合根装配，UI 只持接口（不再自行 new Infrastructure 具体类型）
        _expansionCoordinator = new TemplateExpansionCoordinator(host);
        _loadCoordinator = new TemplateLoadCoordinator(host.TemplateStore);

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
    /// 并写入每条生成任务（<see cref="BatchAgentRunner.Core.Domain.TaskItem.Workspace"/>），使其可直接运行。
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
    public async System.Threading.Tasks.Task LoadAsync()
    {
        var (rows, status) = await _loadCoordinator.LoadAsync().ConfigureAwait(true);
        Templates = new ObservableCollection<TemplateRowViewModel>(rows);
        StatusMessage = status;
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
            var tasks = _expansionCoordinator.ExpandFromInputs(_selectedTemplate.Spec, VarInputs, _workspace);
            foreach (var t in tasks)
            {
                PreviewTasks.Add(new PreviewTaskRowViewModel(t));
            }

            StatusMessage = $"展开得到 {tasks.Count} 条任务（预览未落盘）";
            OnPropertyChanged(nameof(HasPreviewTasks));
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"展开失败：{ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task GenerateAsync()
    {
        if (_selectedTemplate is null)
        {
            return;
        }

        try
        {
            var tasks = _expansionCoordinator.ExpandFromInputs(_selectedTemplate.Spec, VarInputs, _workspace);
            await TemplateExpansionCoordinator.WriteTasksAsync(tasks, _outputPath).ConfigureAwait(true);
            StatusMessage = $"已生成 {tasks.Count} 条任务 → {_outputPath}";
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"生成失败：{ex.Message}";
        }
    }

    /// <summary>按 selectedTemplate 的 spec 构建流程示意并灌入 <see cref="FlowNodes"/> / <see cref="FlowSummary"/>。</summary>
    private void BuildFlow()
    {
        var result = TemplateFlowCoordinator.BuildFlow(_selectedTemplate?.Spec);
        FlowNodes.Clear();
        foreach (var node in result.Nodes)
        {
            FlowNodes.Add(node);
        }

        FlowSummary = result.Summary;
        OnPropertyChanged(nameof(HasFlow));
    }

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
