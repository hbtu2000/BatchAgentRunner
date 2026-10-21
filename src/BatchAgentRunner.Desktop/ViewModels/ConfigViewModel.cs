using System.Collections.ObjectModel;
using System.ComponentModel;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Desktop.Models;
using BatchAgentRunner.Desktop.Services;
using BatchAgentRunner.Desktop.ViewModels.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>模型池的维护方式：共享（所有账号同一份）/ 每账号独立。</summary>
public enum ModelPoolMode
{
    /// <summary>所有账号共用一份候选模型列表（维护简单，推荐新手）。</summary>
    Shared,
    /// <summary>每个账号各自维护候选模型池（不同账号可用不同模型组合）。</summary>
    PerAccount
}

/// <summary>
/// 配置页 façade（P-04 分解收口产物）。
/// <para>
/// 原 <c>ConfigViewModel</c> 是 1000+ 行的上帝类，承载 9 个配置域的双向映射 + 工作空间上限拦截 +
/// 模型池落盘合并 + 准备度计算。P-04 将其按「账号卡 / 降级 / 通知 / Git / 通用」拆为若干子编辑器
/// （<see cref="Config"/> 命名空间下），本类退化为「状态持有 + 绑定转发 + 少量跨域编排」：所有可绑定
/// 属性与命令转发到对应子编辑器，加载/保存跨域编排委托给 <see cref="Config.ConfigSaveCoordinator"/>，
/// 子编辑器的 PropertyChanged 经 <see cref="_Forward"/> 统一透传到本 façade 以保住 Avalonia 反射绑定的
/// 通知契约。对外公开 API（属性名/命令名/语义）逐字节等价于分解前，故既有 headless 契约测试无需改动。
/// </para>
/// </summary>
public sealed class ConfigViewModel : ViewModelBase
{
    private readonly IEngineHost _host;
    private readonly ConfigStatus _status;
    private readonly ModelsAccountSectionViewModel _models;
    private readonly TaskSectionViewModel _task;
    private readonly ScheduleSectionViewModel _schedule;
    private readonly ShutdownSectionViewModel _shutdown;
    private readonly SafetySectionViewModel _safety;
    private readonly ExpertDegradationSectionViewModel _expert;
    private readonly FallbackSectionViewModel _fallback;
    private readonly NotificationSectionViewModel _notif;
    private readonly GitSectionViewModel _git;
    private readonly WorkflowSectionViewModel _workflow;
    private readonly MiscSectionViewModel _misc;
    private readonly ConfigSaveCoordinator _coordinator;

    private LoginViewModel? _login;
    private SetupWizardViewModel? _wizard;
    private string _activeSection = "quickstart";
    private bool _saved;
    private string _readinessSummary = string.Empty;

    public ConfigViewModel(IEngineHost host)
    {
        _host = host;
        _status = new ConfigStatus();

        // 子编辑器：账号卡/模型池依赖 host + 共享状态槽
        _models = new ModelsAccountSectionViewModel(host, _status);
        _task = new TaskSectionViewModel(_status);
        _schedule = new ScheduleSectionViewModel();
        _shutdown = new ShutdownSectionViewModel();
        _safety = new SafetySectionViewModel();
        _expert = new ExpertDegradationSectionViewModel();
        _fallback = new FallbackSectionViewModel();
        _notif = new NotificationSectionViewModel();
        _git = new GitSectionViewModel();
        _workflow = new WorkflowSectionViewModel();
        _misc = new MiscSectionViewModel();

        _coordinator = new ConfigSaveCoordinator(
            _status, _models, _task, _schedule, _shutdown, _safety, _expert, _fallback, _notif, _git, _workflow, _misc);

        // 子编辑器 PropertyChanged 透传到 façade，保住反射绑定契约
        foreach (var section in new ViewModelBase[] { _models, _task, _schedule, _shutdown, _safety, _expert, _fallback, _notif, _git, _workflow, _misc })
        {
            section.PropertyChanged += _Forward;
        }
        _status.PropertyChanged += _ForwardStatus;

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(Reload);
        SectionNavigateCommand = new RelayCommand<string>(SectionNavigate);

        Reload();
    }

    private void _Forward(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    private void _ForwardStatus(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Message")
        {
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    // ---------------- 区段导航 + 准备度（MOCKUP 配置中心重设计） ----------------

    /// <summary>当前激活的区段 key（rail 导航）。</summary>
    public string ActiveSection
    {
        get => _activeSection;
        set => SetProperty(ref _activeSection, value);
    }

    public IRelayCommand<string> SectionNavigateCommand { get; }

    /// <summary>rail 区段导航：切换激活区段并刷新准备度。</summary>
    public void SectionNavigate(string? section)
    {
        if (!string.IsNullOrEmpty(section))
        {
            ActiveSection = section;
        }

        RefreshReadiness();
    }

    /// <summary>登录页 ViewModel（账号与凭据区段内嵌登录 / 凭据管理）。</summary>
    public LoginViewModel? Login
    {
        get => _login;
        set => SetProperty(ref _login, value);
    }

    /// <summary>配置向导 ViewModel（快速开始区段可重跑引导）。</summary>
    public SetupWizardViewModel? Wizard
    {
        get => _wizard;
        set => SetProperty(ref _wizard, value);
    }

    /// <summary>配置准备度步骤（顶部向导进度条）。</summary>
    public ObservableCollection<ReadinessStep> ReadinessSteps { get; } = new();

    /// <summary>准备度文案，如「完成 4 / 6 步，可随时运行」。</summary>
    public string ReadinessSummary
    {
        get => _readinessSummary;
        private set => SetProperty(ref _readinessSummary, value);
    }

    // ---------------- 命令 ----------------

    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand BrowseTaskFileCommand => _task.BrowseTaskFileCommand;
    public IRelayCommand AddModelCommand => _models.AddModelCommand;
    public IRelayCommand<ModelEditor> RemoveModelCommand => _models.RemoveModelCommand;
    public IRelayCommand<ModelEditor> MoveModelUpCommand => _models.MoveModelUpCommand;
    public IRelayCommand<ModelEditor> MoveModelDownCommand => _models.MoveModelDownCommand;
    public IRelayCommand AddWorkspaceCommand => _task.AddWorkspaceCommand;
    public IRelayCommand<WorkspaceConfig> RemoveWorkspaceCommand => _task.RemoveWorkspaceCommand;
    public IAsyncRelayCommand FetchModelsCommand => _models.FetchModelsCommand;
    public IAsyncRelayCommand<AccountEditor> FetchModelsForAccountCommand => _models.FetchModelsForAccountCommand;
    public IRelayCommand AddNotificationRuleCommand => _notif.AddNotificationRuleCommand;
    public IRelayCommand<NotificationRuleEditor> RemoveNotificationRuleCommand => _notif.RemoveNotificationRuleCommand;

    /// <summary>由视图注入的文件选择回调（避免 ViewModel 直接依赖窗口）。</summary>
    public Func<Task<string?>>? PickTaskFile
    {
        get => _task.PickTaskFile;
        set => _task.PickTaskFile = value;
    }

    public string StatusMessage
    {
        get => _status.Message;
        private set => _status.Show(value);
    }

    // ---------------- 加载 / 保存 / 快照 ----------------

    /// <summary>从引擎宿主重新读取配置（向导保存或手改 config.json 后刷新页面）。</summary>
    public void Reload()
    {
        _coordinator.Load(_host.Options);
        RefreshReadiness();
    }

    private async Task SaveAsync()
    {
        if (!_coordinator.Apply(_host.Options))
        {
            return; // 被工作空间上限拦截：不改写、不落盘、不重建
        }

        await _host.SaveConfigAsync().ConfigureAwait(false);
        await _host.RebuildEngineAsync().ConfigureAwait(false);

        UiThread.Post(() =>
        {
            _saved = true;
            StatusMessage = $"配置已保存（{DateTime.Now:HH:mm:ss}），引擎已按新配置重建";
            RefreshReadiness();
        });
    }

    /// <summary>刷新各账号卡的 Vault 快照状态（DESIGN §7「快照状态」）。</summary>
    public void RefreshSnapshotStatuses()
    {
        foreach (var editor in _models.Accounts)
        {
            editor.SnapshotStatus = _host.VaultHost.DescribeSnapshot(_task.AgentKind, editor.Account.Id);
        }
    }

    /// <summary>刷新配置准备度（顶部向导进度条）。需在 UI 线程执行（会修改 ReadinessSteps 集合）。</summary>
    public void RefreshReadiness()
    {
        bool doneTool = true; // 执行工具总有默认选型
        bool doneAccount = (Login is { HasAccounts: true }) || _models.Accounts.Count > 0;
        bool doneModel = _models.Models.Count > 0 || _models.Accounts.Any(a => a.Models.Count > 0);
        bool doneTask = _task.HasTaskFile;
        bool doneWorkspace = _task.Workspaces.Count > 0;
        bool doneSave = _saved;

        var steps = new (int Num, string Title, bool Done)[]
        {
            (1, "执行工具", doneTool),
            (2, "账号", doneAccount),
            (3, "模型", doneModel),
            (4, "任务文件", doneTask),
            (5, "工作空间", doneWorkspace),
            (6, "保存", doneSave),
        };

        int currentNum = Array.FindIndex(steps, s => !s.Done) is var idx && idx >= 0 ? steps[idx].Num : 0;

        if (ReadinessSteps.Count != steps.Length)
        {
            ReadinessSteps.Clear();
            foreach (var s in steps)
            {
                ReadinessSteps.Add(new ReadinessStep(s.Num, s.Title));
            }
        }

        int doneCount = 0;
        for (int i = 0; i < steps.Length; i++)
        {
            var target = ReadinessSteps[i];
            target.State = steps[i].Done ? "done" : (steps[i].Num == currentNum ? "current" : "pending");
            if (steps[i].Done)
            {
                doneCount++;
            }
        }

        ReadinessSummary = $"完成 {doneCount} / {steps.Length} 步，可随时运行";
    }

    // ---------------- 模型池模式（单一数据源） ----------------

    public ModelPoolMode ModelPoolMode
    {
        get => _models.ModelPoolMode;
        set => _models.ModelPoolMode = value;
    }

    public bool IsSharedMode => _models.IsSharedMode;
    public bool IsPerAccountMode => _models.IsPerAccountMode;
    public bool SharedMode { get => _models.SharedMode; set => _models.SharedMode = value; }
    public bool PerAccountMode { get => _models.PerAccountMode; set => _models.PerAccountMode = value; }

    public ObservableCollection<ModelEditor> Models => _models.Models;
    public ObservableCollection<AccountEditor> Accounts => _models.Accounts;
    public ObservableCollection<ModelOption> AvailableModels => _models.AvailableModels;
    public bool IsFetchingModels => _models.IsFetchingModels;

    // ---------------- 任务与执行工具 ----------------

    public string TaskFilePath { get => _task.TaskFilePath; set => _task.TaskFilePath = value; }
    public AgentKind AgentKind { get => _task.AgentKind; set => _task.AgentKind = value; }
    public string CliPath { get => _task.CliPath; set => _task.CliPath = value; }
    public string CliArgumentsTemplate { get => _task.CliArgumentsTemplate; set => _task.CliArgumentsTemplate = value; }
    public string ReasoningEffort { get => _task.ReasoningEffort; set => _task.ReasoningEffort = value; }
    public IReadOnlyList<string> ReasoningEffortLevels => _task.ReasoningEffortLevels;
    public string DefaultWorkspace { get => _task.DefaultWorkspace; set => _task.DefaultWorkspace = value; }
    public bool ShowCliWindow { get => _task.ShowCliWindow; set => _task.ShowCliWindow = value; }
    public int MaxWorkspaces { get => _task.MaxWorkspaces; set => _task.MaxWorkspaces = value; }
    public ObservableCollection<WorkspaceConfig> Workspaces => _task.Workspaces;
    public bool HasTaskFile => _task.HasTaskFile;
    public bool WorkspaceLimitReached => _task.WorkspaceLimitReached;
    public string WorkspaceLimitHint => _task.WorkspaceLimitHint;
    public IReadOnlyList<AgentKind> AgentKinds => _task.AgentKinds;

    // ---------------- 降级与专家 ----------------

    public DegradationStrategy DegradationStrategy { get => _expert.DegradationStrategy; set => _expert.DegradationStrategy = value; }
    public ExpertKind ExpertKind { get => _expert.ExpertKind; set => _expert.ExpertKind = value; }
    public string ExpertId { get => _expert.ExpertId; set => _expert.ExpertId = value; }
    public string ExpertDisplayName { get => _expert.ExpertDisplayName; set => _expert.ExpertDisplayName = value; }
    public IReadOnlyList<DegradationStrategy> Strategies => _expert.Strategies;
    public IReadOnlyList<ExpertKind> ExpertKinds => _expert.ExpertKinds;

    // ---------------- 兜底 ----------------

    public bool FallbackEnabled { get => _fallback.FallbackEnabled; set => _fallback.FallbackEnabled = value; }
    public decimal FallbackMaxPerTask { get => _fallback.FallbackMaxPerTask; set => _fallback.FallbackMaxPerTask = value; }
    public decimal FallbackMaxPerBatch { get => _fallback.FallbackMaxPerBatch; set => _fallback.FallbackMaxPerBatch = value; }

    // ---------------- 自动关机 ----------------

    public bool ShutdownEnabled { get => _shutdown.ShutdownEnabled; set => _shutdown.ShutdownEnabled = value; }
    public bool ShutdownOnlyZeroFailure { get => _shutdown.ShutdownOnlyZeroFailure; set => _shutdown.ShutdownOnlyZeroFailure = value; }
    public int ShutdownCountdown { get => _shutdown.ShutdownCountdown; set => _shutdown.ShutdownCountdown = value; }

    // ---------------- 调度与重试 ----------------

    public int Concurrency { get => _schedule.Concurrency; set => _schedule.Concurrency = value; }
    public int MaxRetries { get => _schedule.MaxRetries; set => _schedule.MaxRetries = value; }
    public int RateLimitRetries { get => _schedule.RateLimitRetries; set => _schedule.RateLimitRetries = value; }
    public int TaskTimeoutMinutes { get => _schedule.TaskTimeoutMinutes; set => _schedule.TaskTimeoutMinutes = value; }
    public int MaxAttempts { get => _schedule.MaxAttempts; set => _schedule.MaxAttempts = value; }
    public int MaxTotalMinutes { get => _schedule.MaxTotalMinutes; set => _schedule.MaxTotalMinutes = value; }
    public int MaxWaitMinutes { get => _schedule.MaxWaitMinutes; set => _schedule.MaxWaitMinutes = value; }
    public int DefaultCooldownMinutes { get => _schedule.DefaultCooldownMinutes; set => _schedule.DefaultCooldownMinutes = value; }
    public bool WaitForQuotaReset { get => _schedule.WaitForQuotaReset; set => _schedule.WaitForQuotaReset = value; }
    public bool ForceRerun { get => _schedule.ForceRerun; set => _schedule.ForceRerun = value; }
    public bool StopOnFailure { get => _schedule.StopOnFailure; set => _schedule.StopOnFailure = value; }

    // ---------------- 安全与交互 ----------------

    public string WhitelistText { get => _safety.WhitelistText; set => _safety.WhitelistText = value; }
    public string BlacklistText { get => _safety.BlacklistText; set => _safety.BlacklistText = value; }
    public string DefaultAnswerText { get => _safety.DefaultAnswerText; set => _safety.DefaultAnswerText = value; }
    public int SilenceSeconds { get => _safety.SilenceSeconds; set => _safety.SilenceSeconds = value; }
    public int ChoiceDefaultIndex { get => _safety.ChoiceDefaultIndex; set => _safety.ChoiceDefaultIndex = value; }

    // ---------------- 二期：工作流与模板域 ----------------

    public int DefaultMaxGateLoops { get => _workflow.DefaultMaxGateLoops; set => _workflow.DefaultMaxGateLoops = value; }
    public bool EnableSessionResume { get => _workflow.EnableSessionResume; set => _workflow.EnableSessionResume = value; }
    public bool MarkdownInPlaceWriteback { get => _workflow.MarkdownInPlaceWriteback; set => _workflow.MarkdownInPlaceWriteback = value; }
    public DependencyFailurePolicy DependencyFailurePolicy { get => _workflow.DependencyFailurePolicy; set => _workflow.DependencyFailurePolicy = value; }
    public IReadOnlyList<DependencyFailurePolicy> DependencyPolicies => _workflow.DependencyPolicies;
    public int TemplateBatchExpansionLimit { get => _workflow.TemplateBatchExpansionLimit; set => _workflow.TemplateBatchExpansionLimit = value; }
    public bool CollectDiffs { get => _workflow.CollectDiffs; set => _workflow.CollectDiffs = value; }
    public string DiffGlobsText { get => _workflow.DiffGlobsText; set => _workflow.DiffGlobsText = value; }

    // ---------------- 二期：通知域 ----------------

    public ObservableCollection<NotificationRuleEditor> NotificationRules => _notif.NotificationRules;

    // ---------------- 二期：版本控制域 ----------------

    public string GitRepoPath { get => _git.GitRepoPath; set => _git.GitRepoPath = value; }
    public string GitBaseBranch { get => _git.GitBaseBranch; set => _git.GitBaseBranch = value; }
    public string GitBranchNaming { get => _git.GitBranchNaming; set => _git.GitBranchNaming = value; }
    public string GitPrTargetBranch { get => _git.GitPrTargetBranch; set => _git.GitPrTargetBranch = value; }
    public bool GitAutoOpenPr { get => _git.GitAutoOpenPr; set => _git.GitAutoOpenPr = value; }

    // ---------------- 二期：结果大小与备份 ----------------

    public int MaxResultKb { get => _misc.MaxResultKb; set => _misc.MaxResultKb = value; }
    public int BackupRetentionCount { get => _misc.BackupRetentionCount; set => _misc.BackupRetentionCount = value; }
}
