using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
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
    private readonly EngineHost _host;

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
    private bool _forceRerun;
    private bool _stopOnFailure;
    private bool _showCliWindow;
    private string _whitelistText = string.Empty;

    public ConfigViewModel(EngineHost host)
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
        AddWorkspaceCommand = new RelayCommand(() => Workspaces.Add(new WorkspaceConfig { Name = "workspace-new" }));
        RemoveWorkspaceCommand = new RelayCommand<WorkspaceConfig>(w => { if (w is not null) Workspaces.Remove(w); });

        FetchModelsCommand = new AsyncRelayCommand(FetchModelsAsync);
        FetchModelsForAccountCommand = new AsyncRelayCommand<AccountEditor>(FetchModelsForAccountAsync);
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
        ForceRerun = o.ForceRerun;
        StopOnFailure = o.StopOnFailure;
        ShowCliWindow = o.ShowCliWindow;
        WhitelistText = string.Join("\n", o.AutoAnswer.AllowedWhitelist);

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
        set => SetProperty(ref _taskFilePath, value);
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

    public string StatusMessage
    {
        get => _statusMessageField;
        private set => SetProperty(ref _statusMessageField, value);
    }

    // ---------------- 命令 ----------------

    public IAsyncRelayCommand SaveCommand { get; }

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
            FillAvailableModels(AvailableModels, list, Models);

            StatusMessage = list.Count > 0
                ? $"已拉取 {list.Count} 个可用模型（来自当前执行工具），可直接在下拉中选择。"
                : "未能从 CLI 读取模型列表（可能当前 CLI 版本不支持），请直接输入模型名。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"拉取模型失败：{ex.Message}";
        }
        finally
        {
            IsFetchingModels = false;
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
            FillAvailableModels(editor.AvailableModels, list, editor.Models);

            StatusMessage = list.Count > 0
                ? $"已拉取账号「{editor.Account.Label}」的 {list.Count} 个可用模型。"
                : $"未能读取账号「{editor.Account.Label}」的模型列表，请直接输入模型名。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"拉取失败：{ex.Message}";
        }
        finally
        {
            editor.IsFetchingModels = false;
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

    private async Task BrowseTaskFileAsync()
    {
        if (PickTaskFile is null)
        {
            return;
        }

        var picked = await PickTaskFile().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(picked))
        {
            TaskFilePath = picked!;
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
        o.ForceRerun = ForceRerun;
        o.StopOnFailure = StopOnFailure;
        o.ShowCliWindow = ShowCliWindow;
        o.AutoAnswer.AllowedWhitelist = WhitelistText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

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
        _host.RebuildEngine();

        StatusMessage = $"配置已保存（{DateTime.Now:HH:mm:ss}），引擎已按新配置重建";
    }

    private string _statusMessageField = string.Empty;
}
