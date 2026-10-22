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

    /// <summary>创建配置页 facade，装配各子编辑器与保存协调者，并读取一次当前配置。</summary>
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

    /// <summary>区段导航命令，参数为区段 key，点击后切换 rail 激活项。</summary>
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

    /// <summary>保存命令，点击后把各子编辑器的值写回配置、落盘并重建引擎。</summary>
    public IAsyncRelayCommand SaveCommand { get; }
    /// <summary>取消命令，点击后丢弃未保存改动并重新读取配置。</summary>
    public IRelayCommand CancelCommand { get; }
    /// <summary>浏览命令，点击后弹出文件对话框选择任务文件。</summary>
    public IAsyncRelayCommand BrowseTaskFileCommand => _task.BrowseTaskFileCommand;
    /// <summary>新增命令，向候选模型池追加一条空模型。</summary>
    public IRelayCommand AddModelCommand => _models.AddModelCommand;
    /// <summary>删除命令，从候选模型池移除指定模型。</summary>
    public IRelayCommand<ModelEditor> RemoveModelCommand => _models.RemoveModelCommand;
    /// <summary>上移命令，把指定模型在候选池中提前一位。</summary>
    public IRelayCommand<ModelEditor> MoveModelUpCommand => _models.MoveModelUpCommand;
    /// <summary>下移命令，把指定模型在候选池中推后一位。</summary>
    public IRelayCommand<ModelEditor> MoveModelDownCommand => _models.MoveModelDownCommand;
    /// <summary>新增命令，向工作空间列表追加一条空路径。</summary>
    public IRelayCommand AddWorkspaceCommand => _task.AddWorkspaceCommand;
    /// <summary>删除命令，从工作空间列表移除指定项。</summary>
    public IRelayCommand<WorkspaceConfig> RemoveWorkspaceCommand => _task.RemoveWorkspaceCommand;
    /// <summary>拉取命令，点击后从服务端获取可用模型列表。</summary>
    public IAsyncRelayCommand FetchModelsCommand => _models.FetchModelsCommand;
    /// <summary>拉取命令，点击后只为指定账号获取可用模型列表。</summary>
    public IAsyncRelayCommand<AccountEditor> FetchModelsForAccountCommand => _models.FetchModelsForAccountCommand;
    /// <summary>新增命令，向通知规则列表追加一条空规则。</summary>
    public IRelayCommand AddNotificationRuleCommand => _notif.AddNotificationRuleCommand;
    /// <summary>删除命令，移除指定的通知规则。</summary>
    public IRelayCommand<NotificationRuleEditor> RemoveNotificationRuleCommand => _notif.RemoveNotificationRuleCommand;

    /// <summary>由视图注入的文件选择回调（避免 ViewModel 直接依赖窗口）。</summary>
    public Func<Task<string?>>? PickTaskFile
    {
        get => _task.PickTaskFile;
        set => _task.PickTaskFile = value;
    }

    /// <summary>状态提示文案，保存成功或被工作空间上限拦截时写入。</summary>
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

    /// <summary>模型池维护方式：所有账号共享一份，或每账号各维护一份。</summary>
    public ModelPoolMode ModelPoolMode
    {
        get => _models.ModelPoolMode;
        set => _models.ModelPoolMode = value;
    }

    /// <summary>当前是否为共享模型池模式，供单选按钮绑定。</summary>
    public bool IsSharedMode => _models.IsSharedMode;
    /// <summary>当前是否为每账号独立模型池模式。</summary>
    public bool IsPerAccountMode => _models.IsPerAccountMode;
    /// <summary>共享模式开关，置真时把维护方式切为共享。</summary>
    public bool SharedMode { get => _models.SharedMode; set => _models.SharedMode = value; }
    /// <summary>独立模式开关，置真时把维护方式切为每账号独立。</summary>
    public bool PerAccountMode { get => _models.PerAccountMode; set => _models.PerAccountMode = value; }

    /// <summary>共享模式下的候选模型列表。</summary>
    public ObservableCollection<ModelEditor> Models => _models.Models;
    /// <summary>账号列表，每项含该账号自己的模型池与凭据状态。</summary>
    public ObservableCollection<AccountEditor> Accounts => _models.Accounts;
    /// <summary>从服务端拉取到的可选模型，供下拉框挑选。</summary>
    public ObservableCollection<ModelOption> AvailableModels => _models.AvailableModels;
    /// <summary>模型列表是否正在拉取，期间禁用拉取按钮。</summary>
    public bool IsFetchingModels => _models.IsFetchingModels;

    // ---------------- 任务与执行工具 ----------------

    /// <summary>任务文件（JSON 或 Markdown）的路径。</summary>
    public string TaskFilePath { get => _task.TaskFilePath; set => _task.TaskFilePath = value; }
    /// <summary>执行任务所用的智能体内核。</summary>
    public AgentKind AgentKind { get => _task.AgentKind; set => _task.AgentKind = value; }
    /// <summary>CLI 可执行文件路径，留空则按 PATH 查找。</summary>
    public string CliPath { get => _task.CliPath; set => _task.CliPath = value; }
    /// <summary>拼装 CLI 参数的命令行模板，支持 {prompt} {model} 等占位符。</summary>
    public string CliArgumentsTemplate { get => _task.CliArgumentsTemplate; set => _task.CliArgumentsTemplate = value; }
    /// <summary>推理强度档位（low / medium / high），留空则走 CLI 默认。</summary>
    public string ReasoningEffort { get => _task.ReasoningEffort; set => _task.ReasoningEffort = value; }
    /// <summary>推理强度的可选档位，供下拉框绑定。</summary>
    public IReadOnlyList<string> ReasoningEffortLevels => _task.ReasoningEffortLevels;
    /// <summary>任务默认工作空间，任务未单独指定时使用。</summary>
    public string DefaultWorkspace { get => _task.DefaultWorkspace; set => _task.DefaultWorkspace = value; }
    /// <summary>执行时是否弹出 CLI 控制台窗口，仅便于人工排查。</summary>
    public bool ShowCliWindow { get => _task.ShowCliWindow; set => _task.ShowCliWindow = value; }
    /// <summary>工作空间数量上限，超出时新增会被拦截。</summary>
    public int MaxWorkspaces { get => _task.MaxWorkspaces; set => _task.MaxWorkspaces = value; }
    /// <summary>已配置的工作空间列表。</summary>
    public ObservableCollection<WorkspaceConfig> Workspaces => _task.Workspaces;
    /// <summary>是否已配置任务文件，为假时无法开始批处理。</summary>
    public bool HasTaskFile => _task.HasTaskFile;
    /// <summary>是否已达工作空间上限，为真时禁用新增入口。</summary>
    public bool WorkspaceLimitReached => _task.WorkspaceLimitReached;
    /// <summary>达到上限时展示的提示文案。</summary>
    public string WorkspaceLimitHint => _task.WorkspaceLimitHint;
    /// <summary>可选的智能体内核，供下拉框绑定。</summary>
    public IReadOnlyList<AgentKind> AgentKinds => _task.AgentKinds;

    // ---------------- 降级与专家 ----------------

    /// <summary>模型与账号的降级顺序策略：先换模型，或先换账号。</summary>
    public DegradationStrategy DegradationStrategy { get => _expert.DegradationStrategy; set => _expert.DegradationStrategy = value; }
    /// <summary>专家类型：未指定 / 单专家 / 多专家。</summary>
    public ExpertKind ExpertKind { get => _expert.ExpertKind; set => _expert.ExpertKind = value; }
    /// <summary>专家标识，执行时作为 --expert 参数传给 CLI。</summary>
    public string ExpertId { get => _expert.ExpertId; set => _expert.ExpertId = value; }
    /// <summary>专家显示名，仅用于界面展示。</summary>
    public string ExpertDisplayName { get => _expert.ExpertDisplayName; set => _expert.ExpertDisplayName = value; }
    /// <summary>可选降级策略列表，供下拉框绑定。</summary>
    public IReadOnlyList<DegradationStrategy> Strategies => _expert.Strategies;
    /// <summary>可选专家类型列表。</summary>
    public IReadOnlyList<ExpertKind> ExpertKinds => _expert.ExpertKinds;

    // ---------------- 兜底 ----------------

    /// <summary>是否启用兜底模型：主模型不可用时自动切换。</summary>
    public bool FallbackEnabled { get => _fallback.FallbackEnabled; set => _fallback.FallbackEnabled = value; }
    /// <summary>单任务兜底成本上限（元），超出即停止该任务的兜底。</summary>
    public decimal FallbackMaxPerTask { get => _fallback.FallbackMaxPerTask; set => _fallback.FallbackMaxPerTask = value; }
    /// <summary>整批兜底成本上限（元），超出即停用兜底。</summary>
    public decimal FallbackMaxPerBatch { get => _fallback.FallbackMaxPerBatch; set => _fallback.FallbackMaxPerBatch = value; }

    // ---------------- 自动关机 ----------------

    /// <summary>批处理结束后是否自动关机。</summary>
    public bool ShutdownEnabled { get => _shutdown.ShutdownEnabled; set => _shutdown.ShutdownEnabled = value; }
    /// <summary>勾选后仅当没有任何失败任务时才关机。</summary>
    public bool ShutdownOnlyZeroFailure { get => _shutdown.ShutdownOnlyZeroFailure; set => _shutdown.ShutdownOnlyZeroFailure = value; }
    /// <summary>关机前倒计时秒数，倒计时期间可取消。</summary>
    public int ShutdownCountdown { get => _shutdown.ShutdownCountdown; set => _shutdown.ShutdownCountdown = value; }

    // ---------------- 调度与重试 ----------------

    /// <summary>并发执行的任务数，默认 1 即串行。</summary>
    public int Concurrency { get => _schedule.Concurrency; set => _schedule.Concurrency = value; }
    /// <summary>普通失败后的重试次数。</summary>
    public int MaxRetries { get => _schedule.MaxRetries; set => _schedule.MaxRetries = value; }
    /// <summary>命中临时限流后的退避重试次数。</summary>
    public int RateLimitRetries { get => _schedule.RateLimitRetries; set => _schedule.RateLimitRetries = value; }
    /// <summary>单个任务的超时时间（分钟）。</summary>
    public int TaskTimeoutMinutes { get => _schedule.TaskTimeoutMinutes; set => _schedule.TaskTimeoutMinutes = value; }
    /// <summary>单任务总尝试次数上限，含各类重试。</summary>
    public int MaxAttempts { get => _schedule.MaxAttempts; set => _schedule.MaxAttempts = value; }
    /// <summary>单任务累计执行时长上限（分钟）。</summary>
    public int MaxTotalMinutes { get => _schedule.MaxTotalMinutes; set => _schedule.MaxTotalMinutes = value; }
    /// <summary>时段外等待的最长分钟数，超出转为阻塞。</summary>
    public int MaxWaitMinutes { get => _schedule.MaxWaitMinutes; set => _schedule.MaxWaitMinutes = value; }
    /// <summary>账号默认冷却分钟数，额度耗尽后按此等待。</summary>
    public int DefaultCooldownMinutes { get => _schedule.DefaultCooldownMinutes; set => _schedule.DefaultCooldownMinutes = value; }
    /// <summary>额度耗尽时是否无限等待重置，不受最长等待时长限制。</summary>
    public bool WaitForQuotaReset { get => _schedule.WaitForQuotaReset; set => _schedule.WaitForQuotaReset = value; }
    /// <summary>是否忽略续跑台账，已完成的任务也重新执行。</summary>
    public bool ForceRerun { get => _schedule.ForceRerun; set => _schedule.ForceRerun = value; }
    /// <summary>首个任务失败后是否立即停止整批。</summary>
    public bool StopOnFailure { get => _schedule.StopOnFailure; set => _schedule.StopOnFailure = value; }

    // ---------------- 安全与交互 ----------------

    /// <summary>自动应答白名单，每行一条匹配文本。</summary>
    public string WhitelistText { get => _safety.WhitelistText; set => _safety.WhitelistText = value; }
    /// <summary>危险操作黑名单，每行一条，命中即中断该任务。</summary>
    public string BlacklistText { get => _safety.BlacklistText; set => _safety.BlacklistText = value; }
    /// <summary>检测到等待输入时注入的默认应答内容。</summary>
    public string DefaultAnswerText { get => _safety.DefaultAnswerText; set => _safety.DefaultAnswerText = value; }
    /// <summary>无输出超过该秒数即判定为等待输入。</summary>
    public int SilenceSeconds { get => _safety.SilenceSeconds; set => _safety.SilenceSeconds = value; }
    /// <summary>选择型交互默认选中的选项序号（从 0 起）。</summary>
    public int ChoiceDefaultIndex { get => _safety.ChoiceDefaultIndex; set => _safety.ChoiceDefaultIndex = value; }

    // ---------------- 二期：工作流与模板域 ----------------

    /// <summary>质量门禁修订循环的默认上限，可被阶段配置覆盖。</summary>
    public int DefaultMaxGateLoops { get => _workflow.DefaultMaxGateLoops; set => _workflow.DefaultMaxGateLoops = value; }
    /// <summary>断点续跑时是否恢复会话，关闭则重发完整指令。</summary>
    public bool EnableSessionResume { get => _workflow.EnableSessionResume; set => _workflow.EnableSessionResume = value; }
    /// <summary>是否把结果原地回写 Markdown 任务文件，关闭则写 sidecar。</summary>
    public bool MarkdownInPlaceWriteback { get => _workflow.MarkdownInPlaceWriteback; set => _workflow.MarkdownInPlaceWriteback = value; }
    /// <summary>依赖任务失败时的处理策略，如阻塞下游任务。</summary>
    public DependencyFailurePolicy DependencyFailurePolicy { get => _workflow.DependencyFailurePolicy; set => _workflow.DependencyFailurePolicy = value; }
    /// <summary>可选的依赖失败策略列表。</summary>
    public IReadOnlyList<DependencyFailurePolicy> DependencyPolicies => _workflow.DependencyPolicies;
    /// <summary>单批模板展开的任务数上限，超出需人工确认。</summary>
    public int TemplateBatchExpansionLimit { get => _workflow.TemplateBatchExpansionLimit; set => _workflow.TemplateBatchExpansionLimit = value; }
    /// <summary>是否采集任务前后的文件 diff。</summary>
    public bool CollectDiffs { get => _workflow.CollectDiffs; set => _workflow.CollectDiffs = value; }
    /// <summary>diff 采集关注的 glob 列表，每行一条。</summary>
    public string DiffGlobsText { get => _workflow.DiffGlobsText; set => _workflow.DiffGlobsText = value; }

    // ---------------- 二期：通知域 ----------------

    /// <summary>通知规则列表，批处理状态变化时按规则推送。</summary>
    public ObservableCollection<NotificationRuleEditor> NotificationRules => _notif.NotificationRules;

    // ---------------- 二期：版本控制域 ----------------

    /// <summary>Git 仓库根目录，为空表示未启用 Git 集成。</summary>
    public string GitRepoPath { get => _git.GitRepoPath; set => _git.GitRepoPath = value; }
    /// <summary>创建任务分支的基线分支。</summary>
    public string GitBaseBranch { get => _git.GitBaseBranch; set => _git.GitBaseBranch = value; }
    /// <summary>任务分支命名规则，支持 {task_id} {timestamp} 等占位符。</summary>
    public string GitBranchNaming { get => _git.GitBranchNaming; set => _git.GitBranchNaming = value; }
    /// <summary>PR 的目标分支，留空时回退基线分支。</summary>
    public string GitPrTargetBranch { get => _git.GitPrTargetBranch; set => _git.GitPrTargetBranch = value; }
    /// <summary>任务完成后是否自动创建 PR（需 gh CLI）。</summary>
    public bool GitAutoOpenPr { get => _git.GitAutoOpenPr; set => _git.GitAutoOpenPr = value; }

    // ---------------- 二期：结果大小与备份 ----------------

    /// <summary>单任务结果的大小上限（KB），超出即截断并提示全量日志位置。</summary>
    public int MaxResultKb { get => _misc.MaxResultKb; set => _misc.MaxResultKb = value; }
    /// <summary>自动备份快照的保留份数，超出后滚动删除最旧的。</summary>
    public int BackupRetentionCount { get => _misc.BackupRetentionCount; set => _misc.BackupRetentionCount = value; }
}
