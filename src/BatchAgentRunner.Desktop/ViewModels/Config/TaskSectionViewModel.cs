using System.Collections.ObjectModel;
using System.Threading.Tasks;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「任务与工作空间」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的任务域）：
/// 任务文件 / 执行工具 / CLI 参数 / 默认工作空间 / 命名工作空间列表与上限（REQ-019）。
/// 只做「状态持有 + 绑定 + 本域 Load/Apply 映射」；跨域编排在 <see cref="ConfigSaveCoordinator"/>。
/// </summary>
public sealed class TaskSectionViewModel : ViewModelBase
{
    private readonly ConfigStatus _status;
    private string _taskFilePath = string.Empty;
    private AgentKind _agentKind = AgentKind.WorkBuddy;
    private string _cliPath = string.Empty;
    private string _cliArgumentsTemplate = string.Empty;
    private string _reasoningEffort = ReasoningEfforts.Default;
    private string _defaultWorkspace = string.Empty;
    private bool _showCliWindow;
    private int _maxWorkspaces = 10;

    /// <summary>初始化任务文件选择与工作空间增删命令。</summary>
    public TaskSectionViewModel(ConfigStatus status)
    {
        _status = status;
        BrowseTaskFileCommand = new AsyncRelayCommand(BrowseTaskFileAsync);
        AddWorkspaceCommand = new RelayCommand(AddWorkspace);
        RemoveWorkspaceCommand = new RelayCommand<WorkspaceConfig>(RemoveWorkspace);
    }

    /// <summary>由视图注入的文件选择回调（避免 ViewModel 直接依赖窗口；facade 转发注入）。</summary>
    public Func<Task<string?>>? PickTaskFile { get; set; }

    /// <summary>命名工作空间列表，保存时整体写回配置。</summary>
    public ObservableCollection<WorkspaceConfig> Workspaces { get; private set; } = new();

    /// <summary>执行工具下拉的可选值列表。</summary>
    public IReadOnlyList<AgentKind> AgentKinds { get; } =
        new[]
        {
            AgentKind.WorkBuddy, AgentKind.CodeBuddy, AgentKind.Mock
        };

    /// <summary>点击后弹出文件选择框并回填任务文件路径。</summary>
    public IAsyncRelayCommand BrowseTaskFileCommand { get; }

    /// <summary>点击后追加一个命名工作空间（受上限拦截）。</summary>
    public IRelayCommand AddWorkspaceCommand { get; }

    /// <summary>点击后从列表移除指定的命名工作空间。</summary>
    public IRelayCommand<WorkspaceConfig> RemoveWorkspaceCommand { get; }

    /// <summary>任务文件（tasks.json）路径，变更时通知 HasTaskFile。</summary>
    public string TaskFilePath
    {
        get => _taskFilePath;
        set
        {
            if (SetProperty(ref _taskFilePath, value))
            {
                OnPropertyChanged(nameof(HasTaskFile));
            }
        }
    }

    /// <summary>是否已选择任务文件（准备度 / 快速开始提示用）。</summary>
    public bool HasTaskFile => !string.IsNullOrWhiteSpace(TaskFilePath);

    /// <summary>当前选择的执行工具类型。</summary>
    public AgentKind AgentKind
    {
        get => _agentKind;
        set => SetProperty(ref _agentKind, value);
    }

    /// <summary>CLI 可执行文件路径；空白写回为 null（用默认）。</summary>
    public string CliPath
    {
        get => _cliPath;
        set => SetProperty(ref _cliPath, value);
    }

    /// <summary>传给 CLI 的参数模板；空白写回为 null。</summary>
    public string CliArgumentsTemplate
    {
        get => _cliArgumentsTemplate;
        set => SetProperty(ref _cliArgumentsTemplate, value);
    }

    /// <summary>推理强度下拉可选项（CLI <c>--effort</c> 取值口径见 <see cref="ReasoningEfforts"/>）。</summary>
    public IReadOnlyList<string> ReasoningEffortLevels { get; } = new List<string>(ReasoningEfforts.Levels);

    /// <summary>推理强度（默认 medium：CLI 默认 high 会让单轮往返明显变慢）。</summary>
    public string ReasoningEffort
    {
        get => _reasoningEffort;
        set => SetProperty(ref _reasoningEffort, string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim());
    }

    /// <summary>未显式指定工作空间的任务所使用的默认目录。</summary>
    public string DefaultWorkspace
    {
        get => _defaultWorkspace;
        set => SetProperty(ref _defaultWorkspace, value);
    }

    /// <summary>弹出 CLI 原生控制台窗口（默认不勾选：后台运行并实时回显进度）。</summary>
    public bool ShowCliWindow
    {
        get => _showCliWindow;
        set => SetProperty(ref _showCliWindow, value);
    }

    /// <summary>命名工作空间数量上限（REQ-019，默认 10）。引擎与预检均以该值为唯一校验准绳，非硬编码。</summary>
    public int MaxWorkspaces
    {
        get => _maxWorkspaces;
        set
        {
            if (SetProperty(ref _maxWorkspaces, value))
            {
                OnPropertyChanged(nameof(WorkspaceLimitReached));
                OnPropertyChanged(nameof(WorkspaceLimitHint));
            }
        }
    }

    /// <summary>工作空间数量是否已抵达配置上限（REQ-019）：用于置灰「新增工作空间」按钮。</summary>
    public bool WorkspaceLimitReached => Workspaces.Count >= Math.Max(1, MaxWorkspaces);

    /// <summary>工作空间数量提示文本（REQ-019），形如「当前 3 / 上限 10」，供配置页实时展示。</summary>
    public string WorkspaceLimitHint => $"当前 {Workspaces.Count} / 上限 {Math.Max(1, MaxWorkspaces)}";

    /// <summary>从运行配置加载本域状态（Reload / 取消时由 <see cref="ConfigSaveCoordinator.Load"/> 调用）。</summary>
    public void LoadFrom(RunOptions o)
    {
        TaskFilePath = o.TaskFilePath;
        AgentKind = o.AgentKind;
        CliPath = o.CliPath ?? string.Empty;
        CliArgumentsTemplate = o.CliArgumentsTemplate ?? string.Empty;
        ReasoningEffort = string.IsNullOrWhiteSpace(o.ReasoningEffort) ? string.Empty : o.ReasoningEffort!;
        DefaultWorkspace = o.DefaultWorkspace ?? string.Empty;
        ShowCliWindow = o.ShowCliWindow;
        MaxWorkspaces = o.MaxWorkspaces;
        Workspaces = new ObservableCollection<WorkspaceConfig>(o.Workspaces);
        OnPropertyChanged(nameof(Workspaces));
        OnPropertyChanged(nameof(WorkspaceLimitReached));
        OnPropertyChanged(nameof(WorkspaceLimitHint));
    }

    /// <summary>把本域状态写回运行配置（保存时由 <see cref="ConfigSaveCoordinator"/> 调用）。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.TaskFilePath = TaskFilePath;
        o.AgentKind = AgentKind;
        o.CliPath = ConfigFieldRules.NullIfBlank(CliPath);
        o.CliArgumentsTemplate = ConfigFieldRules.NullIfBlank(CliArgumentsTemplate);
        o.ReasoningEffort = ConfigFieldRules.NullIfBlank(ReasoningEffort);
        o.DefaultWorkspace = ConfigFieldRules.NullIfBlank(DefaultWorkspace);
        o.ShowCliWindow = ShowCliWindow;
        o.Workspaces = Workspaces.ToList();
        o.MaxWorkspaces = ConfigFieldRules.AtLeastOne(MaxWorkspaces);
    }

    /// <summary>
    /// 新增工作空间（REQ-019）：已达配置上限时拦截并给出动态提示（提示中的 N 取配置值，非硬编码），
    /// 不创建超出上限的工作空间。
    /// </summary>
    private void AddWorkspace()
    {
        var limit = Math.Max(1, MaxWorkspaces);
        if (Workspaces.Count >= limit)
        {
            _status.Show($"工作空间数量已达上限（{limit} 个），无法继续新增；如需更多请先在上方调大「工作空间上限」");
            return;
        }

        Workspaces.Add(new WorkspaceConfig { Name = $"workspace-new-{Workspaces.Count + 1}" });
        OnPropertyChanged(nameof(WorkspaceLimitReached));
        OnPropertyChanged(nameof(WorkspaceLimitHint));
    }

    /// <summary>
    /// 删除工作空间（REQ-019）：移除后刷新上限相关 UI（解除新增按钮置灰、更新数量提示）。
    /// </summary>
    private void RemoveWorkspace(WorkspaceConfig? w)
    {
        if (w is null)
        {
            return;
        }

        Workspaces.Remove(w);
        OnPropertyChanged(nameof(WorkspaceLimitReached));
        OnPropertyChanged(nameof(WorkspaceLimitHint));
    }

    private async Task BrowseTaskFileAsync()
    {
        if (PickTaskFile is null)
        {
            return;
        }

        var picked = await PickTaskFile().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(picked))
        {
            UiThread.Post(() => TaskFilePath = picked!);
        }
    }
}
