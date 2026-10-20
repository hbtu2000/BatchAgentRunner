using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Desktop.Helpers;
using BatchAgentRunner.Desktop.Models;
using BatchAgentRunner.Desktop.Services;
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

/// <summary>配置页：承载 PRD 第 9 章的 9 个配置域（分组布局）。</summary>
public sealed class ConfigViewModel : ViewModelBase
{
    private readonly IEngineHost _host;

    // ---------------- 模型池模式（单一数据源，消除全局列表与每账号池双写造成的重复） ----------------

    private ModelPoolMode _modelPoolMode = ModelPoolMode.Shared;
    private bool _isFetchingModels;

    private string _taskFilePath = string.Empty;
    private AgentKind _agentKind = AgentKind.WorkBuddy;
    private string _cliPath = string.Empty;
    private string _cliArgumentsTemplate = string.Empty;
    private string _defaultWorkspace = string.Empty;
    private DegradationStrategy _degradationStrategy;
    private ExpertKind _expertKind;
    private string _expertId = string.Empty;
    private string _expertDisplayName = string.Empty;
    private bool _fallbackEnabled;
    private decimal _fallbackMaxPerTask;
    private decimal _fallbackMaxPerBatch;
    private bool _shutdownEnabled;
    private bool _shutdownOnlyZeroFailure;
    private int _shutdownCountdown = 60;
    private int _concurrency = 1;
    private int _maxRetries = 2;
    private int _rateLimitRetries = 3;
    private int _taskTimeoutMinutes = 10;
    private int _maxAttempts = 10;
    private int _maxTotalMinutes = 60;
    private int _maxWaitMinutes = 60;
    private int _defaultCooldownMinutes = 60;
    private int _maxWorkspaces = 10;
    private bool _waitForQuotaReset = true;
    private bool _forceRerun;
    private bool _stopOnFailure;
    private bool _showCliWindow;
    private string _whitelistText = string.Empty;

    // ---------------- 安全与交互 / 其它（对齐 MOCKUP 配置中心重设计） ----------------
    private string _blacklistText = string.Empty;
    private string _defaultAnswerText = "y";
    private int _silenceSeconds = 30;
    private int _choiceDefaultIndex;
    private int _maxResultKb = 100;
    private int _backupRetentionCount = 5;
    private string _activeSection = "quickstart";
    private bool _saved;
    private string _readinessSummary = string.Empty;

    // ---------------- 二期配置域（字段） ----------------
    private int _defaultMaxGateLoops = 3;
    private bool _enableSessionResume;
    private bool _markdownInPlaceWriteback;
    private DependencyFailurePolicy _dependencyFailurePolicy;
    private int _templateBatchExpansionLimit = 50;
    private bool _collectDiffs;
    private string _diffGlobsText = string.Empty;
    private string _gitRepoPath = string.Empty;
    private string _gitBaseBranch = string.Empty;
    private string _gitBranchNaming = "bar/{task_id}-{timestamp}";
    private string _gitPrTargetBranch = string.Empty;
    private bool _gitAutoOpenPr;

    public ConfigViewModel(IEngineHost host)
    {
        _host = host;
        Reload();

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        BrowseTaskFileCommand = new AsyncRelayCommand(BrowseTaskFileAsync);
        AddModelCommand = new RelayCommand(() => Models.Add(new ModelEditor(new ModelCandidate { Name = "model-new" })));
        RemoveModelCommand = new RelayCommand<ModelEditor>(m => { if (m is not null) Models.Remove(m); });
        MoveModelUpCommand = new RelayCommand<ModelEditor>(m =>
        {
            if (m is null) return;
            var i = Models.IndexOf(m);
            if (i > 0) Models.Move(i, i - 1);
        });
        MoveModelDownCommand = new RelayCommand<ModelEditor>(m =>
        {
            if (m is null) return;
            var i = Models.IndexOf(m);
            if (i >= 0 && i < Models.Count - 1) Models.Move(i, i + 1);
        });
        AddWorkspaceCommand = new RelayCommand(AddWorkspace);
        RemoveWorkspaceCommand = new RelayCommand<WorkspaceConfig>(RemoveWorkspace);

        AddNotificationRuleCommand = new RelayCommand(() =>
            NotificationRules.Add(new NotificationRuleEditor(new NotificationRule())));
        RemoveNotificationRuleCommand = new RelayCommand<NotificationRuleEditor>(r =>
        {
            if (r is not null)
            {
                NotificationRules.Remove(r);
            }
        });

        FetchModelsCommand = new AsyncRelayCommand(FetchModelsAsync);
        FetchModelsForAccountCommand = new AsyncRelayCommand<AccountEditor>(FetchModelsForAccountAsync);

        SectionNavigateCommand = new RelayCommand<string>(SectionNavigate);
        CancelCommand = new RelayCommand(Reload);
    }

    /// <summary>rail 区段导航：切换激活区段并刷新准备度。</summary>
    public void SectionNavigate(string? section)
    {
        if (!string.IsNullOrEmpty(section))
        {
            ActiveSection = section;
        }

        RefreshReadiness();
    }

    /// <summary>
    /// 从引擎宿主重新读取配置（向导保存或手改 config.json 后刷新页面）。
    /// </summary>
    public void Reload()
    {
        var o = _host.Options;
        TaskFilePath = o.TaskFilePath;
        AgentKind = o.AgentKind;
        CliPath = o.CliPath ?? string.Empty;
        CliArgumentsTemplate = o.CliArgumentsTemplate ?? string.Empty;
        DefaultWorkspace = o.DefaultWorkspace ?? string.Empty;
        DegradationStrategy = o.DegradationStrategy;
        ExpertKind = o.Expert.Kind;
        ExpertId = o.Expert.Id ?? string.Empty;
        ExpertDisplayName = o.Expert.DisplayName ?? string.Empty;
        FallbackEnabled = o.Fallback.Enabled;
        FallbackMaxPerTask = o.Fallback.MaxCostPerTask;
        FallbackMaxPerBatch = o.Fallback.MaxCostPerBatch;
        ShutdownEnabled = o.AutoShutdown.Enabled;
        ShutdownOnlyZeroFailure = o.AutoShutdown.OnlyWhenZeroFailure;
        ShutdownCountdown = o.AutoShutdown.CountdownSeconds;
        Concurrency = o.Concurrency;
        MaxRetries = o.MaxRetriesPerTask;
        RateLimitRetries = o.RateLimitBackoffRetries;
        TaskTimeoutMinutes = o.TaskTimeoutMinutes;
        MaxAttempts = o.MaxAttemptsPerTask;
        MaxTotalMinutes = o.MaxTotalMinutesPerTask;
        MaxWaitMinutes = o.MaxWaitMinutes;
        DefaultCooldownMinutes = o.DefaultCooldownMinutes;
        WaitForQuotaReset = o.WaitForQuotaReset;
        MaxWorkspaces = o.MaxWorkspaces;
        ForceRerun = o.ForceRerun;
        StopOnFailure = o.StopOnFailure;
        ShowCliWindow = o.ShowCliWindow;
        WhitelistText = MultiValueText.Join(o.AutoAnswer.AllowedWhitelist);
        BlacklistText = MultiValueText.Join(o.AutoAnswer.DangerousBlacklist);
        DefaultAnswerText = o.AutoAnswer.DefaultAnswer ?? "y";
        SilenceSeconds = o.AutoAnswer.StdinIdleSeconds;
        ChoiceDefaultIndex = o.AutoAnswer.DefaultChoiceIndex;
        MaxResultKb = o.ResultMaxKB;
        BackupRetentionCount = o.BackupRetention;

        // 二期配置域
        DefaultMaxGateLoops = o.DefaultMaxGateLoops;
        EnableSessionResume = o.EnableSessionResume;
        MarkdownInPlaceWriteback = o.MarkdownInPlaceWriteback;
        DependencyFailurePolicy = o.DependencyFailurePolicy;
        TemplateBatchExpansionLimit = o.TemplateBatchExpansionLimit;
        CollectDiffs = o.CollectDiffs;
        DiffGlobsText = MultiValueText.Join(o.DiffTargetGlobs);
        GitRepoPath = o.Git.RepoPath ?? string.Empty;
        GitBaseBranch = o.Git.BaseBranch ?? string.Empty;
        GitBranchNaming = o.Git.BranchNaming ?? "bar/{task_id}-{timestamp}";
        GitPrTargetBranch = o.Git.PrTargetBranch ?? string.Empty;
        GitAutoOpenPr = o.Git.AutoOpenPr;

        NotificationRules = new ObservableCollection<NotificationRuleEditor>(
            o.Notifications.Select(r => new NotificationRuleEditor(r)));
        OnPropertyChanged(nameof(NotificationRules));

        Models = new ObservableCollection<ModelEditor>(o.ModelCandidates.Select(m => new ModelEditor(m)));
        Accounts = new ObservableCollection<AccountEditor>(o.Accounts.Select(a => new AccountEditor(a)));
        Workspaces = new ObservableCollection<WorkspaceConfig>(o.Workspaces);

        // 模型池模式：只要任一账号配置了自有池 → 视为「每账号独立」，否则「共享」
        ModelPoolMode = o.Accounts.Any(a => a.ModelCandidates is { Count: > 0 })
            ? ModelPoolMode.PerAccount
            : ModelPoolMode.Shared;

        RefreshSnapshotStatuses();
        OnPropertyChanged(nameof(Models));
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(Workspaces));
        OnPropertyChanged(nameof(WorkspaceLimitReached));
        RefreshReadiness();
    }

    /// <summary>模型池模式切换时做安全的单向迁移，避免误切导致数据丢失或双份并存。</summary>
    private void MigrateModelPool(ModelPoolMode mode)
    {
        if (mode == ModelPoolMode.PerAccount)
        {
            // 共享 → 独立：把当前共享列表复制到尚未配置自有池的账号，使其继承既有配置后再各自发散
            foreach (var acc in Accounts)
            {
                if (acc.Models.Count == 0)
                {
                    foreach (var m in Models)
                    {
                        acc.Models.Add(new ModelEditor(new ModelCandidate
                        {
                            Name = m.Name,
                            FreeTimeWindow = m.Model.FreeTimeWindow,
                            EstimatedUnitPrice = m.Model.EstimatedUnitPrice,
                            Discount = m.Model.Discount
                        }));
                    }
                }
            }
        }
        else
        {
            // 独立 → 共享：把各账号模型按名去重合并为一份共享列表，并清空账号自有池（单一数据源）
            var union = new List<ModelEditor>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var acc in Accounts)
            {
                foreach (var m in acc.Models)
                {
                    if (seen.Add(m.Name.Trim()))
                    {
                        union.Add(new ModelEditor(new ModelCandidate { Name = m.Name }));
                    }
                }
            }

            Models = new ObservableCollection<ModelEditor>(union);
            OnPropertyChanged(nameof(Models));
            foreach (var acc in Accounts)
            {
                acc.Models.Clear();
            }
        }
    }

    /// <summary>刷新各账号卡的 Vault 快照状态（DESIGN §7「快照状态」）。</summary>
    public void RefreshSnapshotStatuses()
    {
        foreach (var editor in Accounts)
        {
            editor.SnapshotStatus = _host.VaultHost.DescribeSnapshot(AgentKind, editor.Account.Id);
        }
    }

    /// <summary>由视图注入的文件选择回调（避免 ViewModel 直接依赖窗口）。</summary>
    public Func<Task<string?>>? PickTaskFile { get; set; }

    public ObservableCollection<ModelEditor> Models { get; private set; } = new();

    public ObservableCollection<AccountEditor> Accounts { get; private set; } = new();

    public ObservableCollection<WorkspaceConfig> Workspaces { get; private set; } = new();

    // ---------------- 区段导航 + 准备度（MOCKUP 配置中心重设计） ----------------

    /// <summary>当前激活的区段 key（rail 导航：quickstart / accounts / models / tasks / expert / fallback / schedule / safety / shutdown / misc）。</summary>
    public string ActiveSection
    {
        get => _activeSection;
        set => SetProperty(ref _activeSection, value);
    }

    public IRelayCommand<string> SectionNavigateCommand { get; }

    /// <summary>登录页 ViewModel（账号与凭据区段内嵌登录 / 凭据管理）。</summary>
    public LoginViewModel? Login { get; set; }

    /// <summary>配置向导 ViewModel（快速开始区段可重跑引导）。</summary>
    public SetupWizardViewModel? Wizard { get; set; }

    /// <summary>配置准备度步骤（顶部向导进度条）。</summary>
    public ObservableCollection<ReadinessStep> ReadinessSteps { get; } = new();

    /// <summary>准备度文案，如「完成 4 / 6 步，可随时运行」。</summary>
    public string ReadinessSummary
    {
        get => _readinessSummary;
        private set => SetProperty(ref _readinessSummary, value);
    }

    // ---------------- 模型池模式 + 实时可用模型 ----------------

    /// <summary>模型池维护方式（共享 / 每账号独立）。切换时自动安全迁移。</summary>
    public ModelPoolMode ModelPoolMode
    {
        get => _modelPoolMode;
        set
        {
            if (SetProperty(ref _modelPoolMode, value))
            {
                OnPropertyChanged(nameof(IsSharedMode));
                OnPropertyChanged(nameof(IsPerAccountMode));
                OnPropertyChanged(nameof(SharedMode));
                OnPropertyChanged(nameof(PerAccountMode));
                MigrateModelPool(value);
            }
        }
    }

    public bool IsSharedMode => _modelPoolMode == ModelPoolMode.Shared;
    public bool IsPerAccountMode => _modelPoolMode == ModelPoolMode.PerAccount;

    /// <summary>RadioButton 双向绑定用：选中 = 切到共享模式。</summary>
    public bool SharedMode
    {
        get => IsSharedMode;
        set { if (value) ModelPoolMode = ModelPoolMode.Shared; }
    }

    /// <summary>RadioButton 双向绑定用：选中 = 切到每账号独立模式。</summary>
    public bool PerAccountMode
    {
        get => IsPerAccountMode;
        set { if (value) ModelPoolMode = ModelPoolMode.PerAccount; }
    }

    /// <summary>共享模式下，全局模型行下拉可选用的「可用模型名」（由「拉取可用模型」填充；含已配置折扣的展示）。</summary>
    public ObservableCollection<ModelOption> AvailableModels { get; } = new();

    public bool IsFetchingModels
    {
        get => _isFetchingModels;
        private set => SetProperty(ref _isFetchingModels, value);
    }

    public IReadOnlyList<AgentKind> AgentKinds { get; } =
        new[]
        {
            global::BatchAgentRunner.Core.Domain.AgentKind.WorkBuddy, global::BatchAgentRunner.Core.Domain.AgentKind.CodeBuddy, global::BatchAgentRunner.Core.Domain.AgentKind.Mock
        };

    public IReadOnlyList<DegradationStrategy> Strategies { get; } =
        new[]
        {
            global::BatchAgentRunner.Core.Domain.DegradationStrategy.ModelFirst, global::BatchAgentRunner.Core.Domain.DegradationStrategy.AccountFirst
        };

    public IReadOnlyList<ExpertKind> ExpertKinds { get; } =
        new[] { global::BatchAgentRunner.Core.Domain.ExpertKind.None, global::BatchAgentRunner.Core.Domain.ExpertKind.Single, global::BatchAgentRunner.Core.Domain.ExpertKind.Team };

    // ---------------- 任务与执行工具 ----------------

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

    public AgentKind AgentKind
    {
        get => _agentKind;
        set => SetProperty(ref _agentKind, value);
    }

    public string CliPath
    {
        get => _cliPath;
        set => SetProperty(ref _cliPath, value);
    }

    public string CliArgumentsTemplate
    {
        get => _cliArgumentsTemplate;
        set => SetProperty(ref _cliArgumentsTemplate, value);
    }

    public string DefaultWorkspace
    {
        get => _defaultWorkspace;
        set => SetProperty(ref _defaultWorkspace, value);
    }

    public DegradationStrategy DegradationStrategy
    {
        get => _degradationStrategy;
        set => SetProperty(ref _degradationStrategy, value);
    }

    // ---------------- 专家 ----------------

    public ExpertKind ExpertKind
    {
        get => _expertKind;
        set => SetProperty(ref _expertKind, value);
    }

    public string ExpertId
    {
        get => _expertId;
        set => SetProperty(ref _expertId, value);
    }

    public string ExpertDisplayName
    {
        get => _expertDisplayName;
        set => SetProperty(ref _expertDisplayName, value);
    }

    // ---------------- 兜底 ----------------

    public bool FallbackEnabled
    {
        get => _fallbackEnabled;
        set => SetProperty(ref _fallbackEnabled, value);
    }

    public decimal FallbackMaxPerTask
    {
        get => _fallbackMaxPerTask;
        set => SetProperty(ref _fallbackMaxPerTask, value);
    }

    public decimal FallbackMaxPerBatch
    {
        get => _fallbackMaxPerBatch;
        set => SetProperty(ref _fallbackMaxPerBatch, value);
    }

    // ---------------- 自动关机 ----------------

    public bool ShutdownEnabled
    {
        get => _shutdownEnabled;
        set => SetProperty(ref _shutdownEnabled, value);
    }

    public bool ShutdownOnlyZeroFailure
    {
        get => _shutdownOnlyZeroFailure;
        set => SetProperty(ref _shutdownOnlyZeroFailure, value);
    }

    public int ShutdownCountdown
    {
        get => _shutdownCountdown;
        set => SetProperty(ref _shutdownCountdown, value);
    }

    // ---------------- 重试 / 超时 / 上限 ----------------

    public int Concurrency
    {
        get => _concurrency;
        set => SetProperty(ref _concurrency, value);
    }

    public int MaxRetries
    {
        get => _maxRetries;
        set => SetProperty(ref _maxRetries, value);
    }

    public int RateLimitRetries
    {
        get => _rateLimitRetries;
        set => SetProperty(ref _rateLimitRetries, value);
    }

    public int TaskTimeoutMinutes
    {
        get => _taskTimeoutMinutes;
        set => SetProperty(ref _taskTimeoutMinutes, value);
    }

    public int MaxAttempts
    {
        get => _maxAttempts;
        set => SetProperty(ref _maxAttempts, value);
    }

    public int MaxTotalMinutes
    {
        get => _maxTotalMinutes;
        set => SetProperty(ref _maxTotalMinutes, value);
    }

    public int MaxWaitMinutes
    {
        get => _maxWaitMinutes;
        set => SetProperty(ref _maxWaitMinutes, value);
    }

    public int DefaultCooldownMinutes
    {
        get => _defaultCooldownMinutes;
        set => SetProperty(ref _defaultCooldownMinutes, value);
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

    /// <summary>额度耗尽时无限等待重置（暂停不计入时长）。</summary>
    public bool WaitForQuotaReset
    {
        get => _waitForQuotaReset;
        set => SetProperty(ref _waitForQuotaReset, value);
    }

    public bool ForceRerun
    {
        get => _forceRerun;
        set => SetProperty(ref _forceRerun, value);
    }

    public bool StopOnFailure
    {
        get => _stopOnFailure;
        set => SetProperty(ref _stopOnFailure, value);
    }

    public bool ShowCliWindow
    {
        get => _showCliWindow;
        set => SetProperty(ref _showCliWindow, value);
    }

    public string WhitelistText
    {
        get => _whitelistText;
        set => SetProperty(ref _whitelistText, value);
    }

    /// <summary>危险操作黑名单（命中即拒绝自动批准并终止任务，PRD 第 8 章）。</summary>
    public string BlacklistText
    {
        get => _blacklistText;
        set => SetProperty(ref _blacklistText, value);
    }

    /// <summary>普通确认型交互注入 stdin 的默认内容。</summary>
    public string DefaultAnswerText
    {
        get => _defaultAnswerText;
        set => SetProperty(ref _defaultAnswerText, value);
    }

    /// <summary>静默多少秒判定为「在等确认」。</summary>
    public int SilenceSeconds
    {
        get => _silenceSeconds;
        set => SetProperty(ref _silenceSeconds, value);
    }

    /// <summary>选择型交互默认选择的选项索引（0-based）。</summary>
    public int ChoiceDefaultIndex
    {
        get => _choiceDefaultIndex;
        set => SetProperty(ref _choiceDefaultIndex, value);
    }

    /// <summary>单任务结果大小上限（KB，超出截断并提示全量日志）。</summary>
    public int MaxResultKb
    {
        get => _maxResultKb;
        set => SetProperty(ref _maxResultKb, value);
    }

    /// <summary>每次启动前备份份数（0 = 不备份）。</summary>
    public int BackupRetentionCount
    {
        get => _backupRetentionCount;
        set => SetProperty(ref _backupRetentionCount, value);
    }

    /// <summary>是否已选择任务文件（准备度 / 快速开始提示用）。</summary>
    public bool HasTaskFile => !string.IsNullOrWhiteSpace(TaskFilePath);

    // ---------------- 二期：工作流与模板域 ----------------

    /// <summary>质量门禁修订循环默认上限（被阶段 MaxLoops 覆盖）。</summary>
    public int DefaultMaxGateLoops
    {
        get => _defaultMaxGateLoops;
        set => SetProperty(ref _defaultMaxGateLoops, value);
    }

    /// <summary>启用断点会话恢复（方案 b；不支持时回退一期方案 a）。</summary>
    public bool EnableSessionResume
    {
        get => _enableSessionResume;
        set => SetProperty(ref _enableSessionResume, value);
    }

    /// <summary>Markdown 任务文件原地回写（默认 sidecar）。</summary>
    public bool MarkdownInPlaceWriteback
    {
        get => _markdownInPlaceWriteback;
        set => SetProperty(ref _markdownInPlaceWriteback, value);
    }

    /// <summary>依赖失败策略（DAG）。</summary>
    public DependencyFailurePolicy DependencyFailurePolicy
    {
        get => _dependencyFailurePolicy;
        set => SetProperty(ref _dependencyFailurePolicy, value);
    }

    public IReadOnlyList<DependencyFailurePolicy> DependencyPolicies { get; } =
        new[]
        {
            global::BatchAgentRunner.Core.Scheduling.DependencyFailurePolicy.BlockDependents,
            global::BatchAgentRunner.Core.Scheduling.DependencyFailurePolicy.AbortBatch,
            global::BatchAgentRunner.Core.Scheduling.DependencyFailurePolicy.SkipDependents
        };

    /// <summary>模板展开单批上限（超过则要求确认）。</summary>
    public int TemplateBatchExpansionLimit
    {
        get => _templateBatchExpansionLimit;
        set => SetProperty(ref _templateBatchExpansionLimit, value);
    }

    /// <summary>是否采集结果 diff（REQ-017）。</summary>
    public bool CollectDiffs
    {
        get => _collectDiffs;
        set => SetProperty(ref _collectDiffs, value);
    }

    /// <summary>diff 采集的受关注文件 glob（每行一条）。</summary>
    public string DiffGlobsText
    {
        get => _diffGlobsText;
        set => SetProperty(ref _diffGlobsText, value);
    }

    // ---------------- 二期：通知域 ----------------

    /// <summary>通知规则列表（空 = 不推送）。</summary>
    public ObservableCollection<NotificationRuleEditor> NotificationRules { get; private set; } = new();

    public IRelayCommand AddNotificationRuleCommand { get; }

    public IRelayCommand<NotificationRuleEditor> RemoveNotificationRuleCommand { get; }

    // ---------------- 二期：版本控制域 ----------------

    public string GitRepoPath
    {
        get => _gitRepoPath;
        set => SetProperty(ref _gitRepoPath, value);
    }

    public string GitBaseBranch
    {
        get => _gitBaseBranch;
        set => SetProperty(ref _gitBaseBranch, value);
    }

    /// <summary>自动建分支命名规则（支持 {task_id} / {timestamp} / {date}）。</summary>
    public string GitBranchNaming
    {
        get => _gitBranchNaming;
        set => SetProperty(ref _gitBranchNaming, value);
    }

    public string GitPrTargetBranch
    {
        get => _gitPrTargetBranch;
        set => SetProperty(ref _gitPrTargetBranch, value);
    }

    /// <summary>任务完成后是否自动开 PR（需 gh CLI，不可用时退化为推送分支）。</summary>
    public bool GitAutoOpenPr
    {
        get => _gitAutoOpenPr;
        set => SetProperty(ref _gitAutoOpenPr, value);
    }

    public string StatusMessage
    {
        get => _statusMessageField;
        private set => SetProperty(ref _statusMessageField, value);
    }

    // ---------------- 命令 ----------------

    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>取消：放弃未保存的修改并重新从磁盘加载配置。</summary>
    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand BrowseTaskFileCommand { get; }

    public IRelayCommand AddModelCommand { get; }

    public IRelayCommand<ModelEditor> RemoveModelCommand { get; }

    public IRelayCommand<ModelEditor> MoveModelUpCommand { get; }

    public IRelayCommand<ModelEditor> MoveModelDownCommand { get; }

    public IRelayCommand AddWorkspaceCommand { get; }

    public IRelayCommand<WorkspaceConfig> RemoveWorkspaceCommand { get; }

    /// <summary>拉取当前执行工具支持的模型清单（共享模式用，填充全局下拉）。</summary>
    public IAsyncRelayCommand FetchModelsCommand { get; }

    /// <summary>拉取指定账号可用的模型清单（每账号独立模式用，填充该账号卡内下拉）。</summary>
    public IAsyncRelayCommand<AccountEditor> FetchModelsForAccountCommand { get; }

    /// <summary>实时拉取当前执行工具支持的模型清单（共享模式），填充全局下拉，避免手填模型名。</summary>
    private async Task FetchModelsAsync()
    {
        if (IsFetchingModels)
        {
            return;
        }

        IsFetchingModels = true;
        try
        {
            var list = await _host.FetchModelsAsync(null).ConfigureAwait(false);

            // P0-6：ConfigureAwait(false) 之后已在后台线程，填充 ObservableCollection 必须回到 UI 线程
            UiThread.Post(() =>
            {
                FillAvailableModels(AvailableModels, list, Models);
                StatusMessage = list.Count > 0
                    ? $"已拉取 {list.Count} 个可用模型（来自当前执行工具），可直接在下拉中选择。"
                    : "未能从 CLI 读取模型列表（可能当前 CLI 版本不支持），请直接输入模型名。";
            });
        }
        catch (Exception ex)
        {
            UiThread.Post(() => StatusMessage = $"拉取模型失败：{ex.Message}");
        }
        finally
        {
            UiThread.Post(() => IsFetchingModels = false);
        }
    }

    /// <summary>实时按账号拉取可用模型清单，填充该账号卡内的下拉。</summary>
    private async Task FetchModelsForAccountAsync(AccountEditor? editor)
    {
        if (editor is null || editor.IsFetchingModels)
        {
            return;
        }

        editor.IsFetchingModels = true;
        try
        {
            var list = await _host.FetchModelsAsync(editor.Account).ConfigureAwait(false);

            // P0-6：同上，回到 UI 线程再改 UI 集合
            UiThread.Post(() =>
            {
                FillAvailableModels(editor.AvailableModels, list, editor.Models);
                StatusMessage = list.Count > 0
                    ? $"已拉取账号「{editor.Account.Label}」的 {list.Count} 个可用模型。"
                    : $"未能读取账号「{editor.Account.Label}」的模型列表，请直接输入模型名。";
            });
        }
        catch (Exception ex)
        {
            UiThread.Post(() => StatusMessage = $"拉取失败：{ex.Message}");
        }
        finally
        {
            UiThread.Post(() => editor.IsFetchingModels = false);
        }
    }

    /// <summary>
    /// 把模型目录填充进下拉：倍率优先用官方产品配置里的 credits（如 0.79 → 0.79x），
    /// 官方没有的模型再回退用候选模型行里手工配置的折扣；已配折扣但目录没有的模型追加到尾部。
    /// </summary>
    private static void FillAvailableModels(
        ObservableCollection<ModelOption> target, IReadOnlyList<ModelCatalogEntry> entries, IEnumerable<ModelEditor> configured)
    {
        var configuredDiscount = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var editor in configured)
        {
            var key = editor.Name.Trim();
            if (key.Length == 0 || configuredDiscount.ContainsKey(key))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(editor.DiscountDisplay))
            {
                configuredDiscount[key] = editor.DiscountDisplay;
            }
        }

        target.Clear();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = entry.Name.Trim();
            if (key.Length == 0 || !present.Add(key))
            {
                continue;
            }

            var display = entry.CreditsMultiplier is { } multiplier
                ? $"{multiplier:0.00}x"
                : configuredDiscount.TryGetValue(key, out var discount) ? discount : string.Empty;
            target.Add(new ModelOption(entry.Name, display));
        }

        foreach (var (name, discount) in configuredDiscount)
        {
            if (present.Add(name))
            {
                target.Add(new ModelOption(name, discount));
            }
        }
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
            StatusMessage = $"工作空间数量已达上限（{limit} 个），无法继续新增；如需更多请先在上方调大「工作空间上限」";
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

    private async Task SaveAsync()
    {
        var o = _host.Options;
        o.TaskFilePath = TaskFilePath;
        o.AgentKind = AgentKind;
        o.CliPath = string.IsNullOrWhiteSpace(CliPath) ? null : CliPath;
        o.CliArgumentsTemplate = string.IsNullOrWhiteSpace(CliArgumentsTemplate) ? null : CliArgumentsTemplate;
        o.DefaultWorkspace = string.IsNullOrWhiteSpace(DefaultWorkspace) ? null : DefaultWorkspace;
        o.DegradationStrategy = DegradationStrategy;
        o.Workspaces = Workspaces.ToList();

        o.Expert.Kind = ExpertKind;
        o.Expert.Id = string.IsNullOrWhiteSpace(ExpertId) ? null : ExpertId;
        o.Expert.DisplayName = string.IsNullOrWhiteSpace(ExpertDisplayName) ? null : ExpertDisplayName;

        o.Fallback.Enabled = FallbackEnabled;
        o.Fallback.MaxCostPerTask = FallbackMaxPerTask;
        o.Fallback.MaxCostPerBatch = FallbackMaxPerBatch;

        o.AutoShutdown.Enabled = ShutdownEnabled;
        o.AutoShutdown.OnlyWhenZeroFailure = ShutdownOnlyZeroFailure;
        o.AutoShutdown.CountdownSeconds = ShutdownCountdown;

        o.Concurrency = Math.Max(1, Concurrency);
        o.MaxRetriesPerTask = Math.Max(0, MaxRetries);
        o.RateLimitBackoffRetries = Math.Max(0, RateLimitRetries);
        o.TaskTimeoutMinutes = Math.Max(1, TaskTimeoutMinutes);
        o.MaxAttemptsPerTask = Math.Max(1, MaxAttempts);
        o.MaxTotalMinutesPerTask = Math.Max(1, MaxTotalMinutes);
        o.MaxWaitMinutes = Math.Max(0, MaxWaitMinutes);
        o.DefaultCooldownMinutes = Math.Max(1, DefaultCooldownMinutes);
        o.WaitForQuotaReset = WaitForQuotaReset;
        o.MaxWorkspaces = Math.Max(1, MaxWorkspaces);

        // REQ-019：保存前以配置上限拦截，杜绝静默超出（提示中的 N 取配置值）
        if (Workspaces.Count > o.MaxWorkspaces)
        {
            StatusMessage = $"保存被拦截：工作空间数量（{Workspaces.Count}）已超过上限（{o.MaxWorkspaces}），请精简后再保存";
            return;
        }
        o.ForceRerun = ForceRerun;
        o.StopOnFailure = StopOnFailure;
        o.ShowCliWindow = ShowCliWindow;
        o.AutoAnswer.AllowedWhitelist = MultiValueText.Split(WhitelistText, '\n').ToList();
        o.AutoAnswer.DangerousBlacklist = MultiValueText.Split(BlacklistText, '\n').ToList();
        o.AutoAnswer.DefaultAnswer = string.IsNullOrWhiteSpace(DefaultAnswerText) ? "y" : DefaultAnswerText;
        o.AutoAnswer.StdinIdleSeconds = Math.Max(0, SilenceSeconds);
        o.AutoAnswer.DefaultChoiceIndex = Math.Max(0, ChoiceDefaultIndex);
        o.ResultMaxKB = Math.Max(0, MaxResultKb);
        o.BackupRetention = Math.Max(0, BackupRetentionCount);

        // 二期配置域
        o.DefaultMaxGateLoops = Math.Max(1, DefaultMaxGateLoops);
        o.EnableSessionResume = EnableSessionResume;
        o.MarkdownInPlaceWriteback = MarkdownInPlaceWriteback;
        o.DependencyFailurePolicy = DependencyFailurePolicy;
        o.TemplateBatchExpansionLimit = Math.Max(1, TemplateBatchExpansionLimit);
        o.CollectDiffs = CollectDiffs;
        o.DiffTargetGlobs = MultiValueText.Split(DiffGlobsText, '\n').ToList();
        o.Git.RepoPath = string.IsNullOrWhiteSpace(GitRepoPath) ? string.Empty : GitRepoPath.Trim();
        o.Git.BaseBranch = string.IsNullOrWhiteSpace(GitBaseBranch) ? null : GitBaseBranch.Trim();
        o.Git.BranchNaming = string.IsNullOrWhiteSpace(GitBranchNaming) ? "bar/{task_id}-{timestamp}" : GitBranchNaming.Trim();
        o.Git.PrTargetBranch = string.IsNullOrWhiteSpace(GitPrTargetBranch) ? null : GitPrTargetBranch.Trim();
        o.Git.AutoOpenPr = GitAutoOpenPr;
        o.Notifications = NotificationRules.Select(r => r.Apply()).ToList();

        o.ModelCandidates.Clear();
        o.Accounts.Clear();

        if (ModelPoolMode == ModelPoolMode.Shared)
        {
            // 单一数据源：全局共享列表；各账号自有池清空，使其回退到共享列表（避免双份并存）
            foreach (var editor in Models)
            {
                editor.Apply();
                o.ModelCandidates.Add(editor.Model);
            }

            foreach (var editor in Accounts)
            {
                editor.Apply();
                editor.Account.ModelCandidates = null;
                o.Accounts.Add(editor.Account);
            }
        }
        else
        {
            // 每账号独立：全局列表清空；各账号写入各自的自有候选模型池
            foreach (var editor in Accounts)
            {
                editor.Apply();
                o.Accounts.Add(editor.Account);
            }
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

    /// <summary>刷新配置准备度（顶部向导进度条）。在 Reload / 保存后调用。需在 UI 线程执行（会修改 ReadinessSteps 集合）。</summary>
    public void RefreshReadiness()
    {
        bool doneTool = true; // 执行工具总有默认选型
        bool doneAccount = (Login is { HasAccounts: true }) || Accounts.Count > 0;
        bool doneModel = Models.Count > 0 || Accounts.Any(a => a.Models.Count > 0);
        bool doneTask = HasTaskFile;
        bool doneWorkspace = Workspaces.Count > 0;
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

        // 第一个未完成的步骤即「当前步」
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

    private string _statusMessageField = string.Empty;
}
