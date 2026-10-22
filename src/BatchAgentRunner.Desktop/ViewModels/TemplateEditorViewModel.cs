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

    /// <summary>创建模板库页，装配展开 / 加载协调者并初始化输出路径与各集合。</summary>
    /// <param name="host">引擎宿主，提供模板存储、配置目录与默认工作空间。</param>
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

    /// <summary>刷新模板清单命令，导航进入页面时调用并回报加载结果。</summary>
    public IAsyncRelayCommand LoadCommand { get; }

    /// <summary>预览命令，按当前变量取值展开模板并在列表中展示，不写文件。</summary>
    public IRelayCommand PreviewCommand { get; }

    /// <summary>生成命令，展开模板并把任务写入输出路径；未选模板时不可用。</summary>
    public IAsyncRelayCommand GenerateCommand { get; }

    /// <summary>展开 / 收起流程示意。</summary>
    public IRelayCommand ToggleFlowCommand { get; }

    /// <summary>模板清单（内置 S1–S16 + 用户自定义）。</summary>
    public ObservableCollection<TemplateRowViewModel> Templates
    {
        get => _templates;
        private set => SetProperty(ref _templates, value);
    }

    /// <summary>当前模板的可填变量行，切换模板时按变量定义重建。</summary>
    public ObservableCollection<TemplateVarInputViewModel> VarInputs { get; }

    /// <summary>最近一次预览展开出的任务行，切换模板时清空。</summary>
    public ObservableCollection<PreviewTaskRowViewModel> PreviewTasks { get; }

    /// <summary>流程示意节点（自上而下 = 执行顺序；单指令模板为单节点）。</summary>
    public ObservableCollection<TemplateFlowNodeViewModel> FlowNodes { get; }

    /// <summary>是否已构建出流程节点，为假时空态提示占位。</summary>
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

    /// <summary>流程示意折叠按钮的文案，展开时为「收起」，收起时为「展开」。</summary>
    public string FlowToggleText => _isFlowExpanded ? "收起" : "展开";

    /// <summary>模板列表当前选中项，变更后重建变量行与流程示意并清空预览。</summary>
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

    /// <summary>选中模板的指令模板正文，作为说明展示在详情区。</summary>
    public string SelectedDescription => _selectedTemplate?.Spec.InstructionTemplate ?? string.Empty;

    /// <summary>选中模板的形态说明：工作流模板显示阶段数，单指令模板固定文案。</summary>
    public string SelectedWorkflowText => _selectedTemplate?.Spec.Workflow is { } wf
        ? $"工作流模板：{wf.Stages.Count} 个阶段"
        : "单指令模板";

    /// <summary>选中模板的链式依赖开关说明，开启时任务依次挂前一个任务。</summary>
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

    /// <summary>操作结果提示行，加载 / 预览 / 生成结束后写入供界面展示。</summary>
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
