using System.Collections.ObjectModel;
using Avalonia.Threading;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 傻瓜式登录页 facade（P-06 分解收口产物）。
/// <para>
/// 原 <c>LoginViewModel</c> 是 751 行的上帝类，承载「一键登录全流程 + 每日积分领取 + 账号池一键操作 +
/// 步骤条 / 结果横幅 / 账号行 / 积分行四类 UI 状态」。P-06 按「状态持有 + 绑定转发 + 流程编排」
/// 将其下沉为 Login 命名空间下的协作者：<see cref="GuidedLoginFlow"/>（一键登录全流程）、
/// <see cref="CreditsClaimFlow"/>（每日积分领取）、<see cref="AccountPoolCoordinator"/>（账号行重建 /
/// 切换 / 两段式删除）、<see cref="LoginStepsPresenter"/>（步骤条）、<see cref="LoginErrorTranslator"/>（异常人话翻译），
/// 行视图模型拆为 <see cref="LoginStepItem"/> / <see cref="LoginAccountRow"/> / <see cref="CreditsClaimRow"/> 独立文件。
/// 本类退化为 facade：持有可绑定状态（经 <see cref="ILoginView"/> 缝向协作者暴露）、转发公开属性与命令。
/// 对外公开 API（属性名 / 命令名 / 语义）逐字节等价于分解前，故既有契约测试无需改动。
/// </para>
/// <remarks>CA1001（P0-12）：协作者持有 <see cref="CancellationTokenSource"/>（登录/领积分可取消操作），
/// 由本类 <see cref="Dispose"/> 一并释放。</remarks>
/// </summary>
public sealed class LoginViewModel : ViewModelBase, ILoginView, IDisposable
{
    private readonly IEngineHost _host;
    private readonly LoginStepsPresenter _steps;
    private readonly GuidedLoginFlow _loginFlow;
    private readonly CreditsClaimFlow _claimFlow;
    private readonly AccountPoolCoordinator _accounts;

    private AgentKind _agentKind;
    private string _newAccountDisplayName = string.Empty;
    private bool _isBusy;
    private bool _isClaiming;
    private string _statusText = string.Empty;
    private string _resultText = string.Empty;
    private bool _isSuccess;
    private bool _isError;

    /// <summary>创建登录页 facade，装配登录 / 领积分 / 账号池协作者并订阅领取进度。</summary>
    public LoginViewModel(IEngineHost host)
    {
        _host = host;
        _agentKind = host.Options.AgentKind;
        _steps = new LoginStepsPresenter();
        _accounts = new AccountPoolCoordinator(this);
        _loginFlow = new GuidedLoginFlow(this, _steps);
        _claimFlow = new CreditsClaimFlow(this);

        LoginCommand = new AsyncRelayCommand(() => _loginFlow.RunAsync(accountId: null, displayName: NewAccountDisplayName));
        CancelLoginCommand = new RelayCommand(() => _loginFlow.Cancel());
        RefreshCommand = new RelayCommand(Refresh);
        ClaimCreditsCommand = new AsyncRelayCommand(() => _claimFlow.RunAsync(), () => IsClaimIdle);
        // 领取进度（编排器回调可能在线程池线程）→ 状态条
        _host.CreditsClaimProgressRaised += (_, p) => Dispatcher.UIThread.Post(() =>
        {
            if (IsClaiming && !string.IsNullOrWhiteSpace(p.Message))
            {
                StatusText = p.Message;
            }
        });
        Refresh();
    }

    /// <summary>导航进入本页时刷新账号列表（外部改了配置也能看到最新值）。</summary>
    public void Refresh()
    {
        AgentKind = _host.Options.AgentKind;
        OnPropertyChanged(nameof(ForceClaim));
        RebuildRows();
    }

    /// <summary>一键登录的步骤条条目，由登录编排者按阶段推进。</summary>
    public ObservableCollection<LoginStepItem> Steps => _steps.Items;

    /// <summary>账号池中的账号行，切换 / 删除 / 登录成功后重建。</summary>
    public ObservableCollection<LoginAccountRow> Rows => _accounts.Rows;

    /// <summary>可选的智能体内核枚举值，供下拉框绑定。</summary>
    public AgentKind[] AgentKinds { get; } = Enum.GetValues<AgentKind>();

    /// <summary>当前选择的智能体内核，来自配置并在进入页面时同步。</summary>
    public AgentKind AgentKind
    {
        get => _agentKind;
        set => SetProperty(ref _agentKind, value);
    }

    /// <summary>新账号显示名（可留空，自动编号）。</summary>
    public string NewAccountDisplayName
    {
        get => _newAccountDisplayName;
        set => SetProperty(ref _newAccountDisplayName, value);
    }

    /// <summary>
    /// 强制领取（允许重复领取）：绑定到配置 <c>credits_claim.force</c>。
    /// 勾选 = 默认行为，忽略台账去重、每次都去调领取接口；取消勾选恢复「同账号每日一次」去重跳过。
    /// 变更即时写入配置。
    /// </summary>
    public bool ForceClaim
    {
        get => _host.Options.CreditsClaim.Force;
        set
        {
            if (_host.Options.CreditsClaim.Force == value) return;
            _host.Options.CreditsClaim.Force = value;
            OnPropertyChanged(nameof(ForceClaim));
            _ = _host.SaveConfigAsync();
        }
    }

    /// <summary>一键登录进行中，期间禁用登录按钮并联动空闲判定。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsClaimIdle));
                ClaimCreditsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>当前没有登录在跑，界面据此启用登录入口。</summary>
    public bool IsIdle => !_isBusy;

    /// <summary>登录过程中的当前进度说明（取自编排器的人话消息）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>登录结束后的结果横幅文案，成功或失败时由编排者写入。</summary>
    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    /// <summary>上次登录是否成功，为真时结果横幅按成功样式渲染。</summary>
    public bool IsSuccess
    {
        get => _isSuccess;
        private set
        {
            if (SetProperty(ref _isSuccess, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    /// <summary>上次登录是否失败，为真时结果横幅按失败样式渲染。</summary>
    public bool IsError
    {
        get => _isError;
        private set
        {
            if (SetProperty(ref _isError, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    /// <summary>是否已有登录结果可展示（成功或失败任一成立）。</summary>
    public bool HasResult => _isSuccess || _isError;

    /// <summary>一键登录命令，点击后按引导流程完成登录并回填账号池。</summary>
    public IAsyncRelayCommand LoginCommand { get; }

    /// <summary>取消命令，点击后中止正在执行的一键登录。</summary>
    public IRelayCommand CancelLoginCommand { get; }

    /// <summary>刷新命令，重新读取配置并重建账号行。</summary>
    public IRelayCommand RefreshCommand { get; }

    /// <summary>一键领取所有启用账号的今日积分。</summary>
    public IAsyncRelayCommand ClaimCreditsCommand { get; }

    /// <summary>本次领取的逐账号结果（领取前 / 领取后 / 变化量）。</summary>
    public ObservableCollection<CreditsClaimRow> ClaimRows { get; } = new();

    /// <summary>是否已有逐账号领取结果，为假时隐藏领取结果表。</summary>
    public bool HasClaimRows => ClaimRows.Count > 0;

    /// <summary>领取进行中（与登录流程互斥：同一时刻 CLI 只有一个 Live 身份）。</summary>
    public bool IsClaiming
    {
        get => _isClaiming;
        private set
        {
            if (SetProperty(ref _isClaiming, value))
            {
                OnPropertyChanged(nameof(IsClaimIdle));
                ClaimCreditsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>当前可发起积分领取（既未登录也未领取），驱动按钮可用性。</summary>
    public bool IsClaimIdle => !_isClaiming && !_isBusy;

    // ---------------- 账号行操作（委托 AccountPoolCoordinator） ----------------

    /// <summary>由账号行「重新登录」按钮调用（对同一账号重跑一键登录）。</summary>
    /// <param name="row">要重新登录的账号行。</param>
    public Task ReLoginAsync(LoginAccountRow row) => _loginFlow.ReLoginAsync(row);

    /// <summary>由账号行「切换到这个账号」按钮调用（Vault 快照一键回填 Live）。</summary>
    /// <param name="row">要切换到的目标账号行。</param>
    public Task SwitchToAsync(LoginAccountRow row) => _accounts.SwitchAsync(row);

    /// <summary>由账号行「取消」按钮调用：撤销删除确认状态。</summary>
    /// <param name="row">正在等待删除确认的账号行。</param>
    public void CancelDelete(LoginAccountRow row) => _accounts.CancelDelete(row);

    /// <summary>由账号行「删除」按钮调用：清掉 Vault 快照并从账号池移除（两段式确认）。</summary>
    /// <param name="row">要删除的账号行。</param>
    public Task DeleteAsync(LoginAccountRow row) => _accounts.DeleteAsync(row);

    /// <summary>账号池是否非空，为假时显示「尚无账号」空态。</summary>
    public bool HasAccounts => Rows.Count > 0;

    /// <summary>按账号池 + Vault 快照重建账号行（切换 / 删除 / 登录成功 / 全量刷新共用）。</summary>
    internal void RebuildRows()
    {
        _accounts.Rebuild();
        OnPropertyChanged(nameof(HasAccounts));
    }

    /// <summary>释放未取消完的登录/领积分 CTS（CA1001；每次操作结束已主动释放，这里是兜底）。</summary>
    public void Dispose()
    {
        _loginFlow.Dispose();
        _claimFlow.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---------------- ILoginView（协作者状态缝，显式实现不占公开面） ----------------

    IEngineHost ILoginView.Host => _host;
    AgentKind ILoginView.AgentKind => _agentKind;

    string ILoginView.NewAccountDisplayName
    {
        get => NewAccountDisplayName;
        set => NewAccountDisplayName = value;
    }

    bool ILoginView.IsBusy
    {
        get => IsBusy;
        set => IsBusy = value;
    }

    bool ILoginView.IsClaiming
    {
        get => IsClaiming;
        set => IsClaiming = value;
    }

    string ILoginView.StatusText
    {
        get => StatusText;
        set => StatusText = value;
    }

    string ILoginView.ResultText
    {
        get => ResultText;
        set => ResultText = value;
    }

    bool ILoginView.IsSuccess
    {
        get => IsSuccess;
        set => IsSuccess = value;
    }

    bool ILoginView.IsError
    {
        get => IsError;
        set => IsError = value;
    }

    ObservableCollection<LoginAccountRow> ILoginView.Rows => Rows;
    ObservableCollection<CreditsClaimRow> ILoginView.ClaimRows => ClaimRows;
    LoginStepsPresenter ILoginView.Steps => _steps;

    void ILoginView.NotifyClaimRowsChanged() => OnPropertyChanged(nameof(HasClaimRows));
    void ILoginView.RebuildRows() => RebuildRows();
    void ILoginView.Refresh() => Refresh();
}
