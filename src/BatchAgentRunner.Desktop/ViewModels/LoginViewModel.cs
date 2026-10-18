using System.Collections.ObjectModel;
using Avalonia.Threading;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Services;
using BatchAgentRunner.Infrastructure.CredentialVault;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>每日积分领取结果行（界面上逐账号展示「领取前 → 领取后」）。</summary>
public sealed class CreditsClaimRow
{
    public CreditsClaimRow(CreditsClaimRecord record)
    {
        AccountName = string.IsNullOrWhiteSpace(record.DisplayName) ? record.AccountId : record.DisplayName;
        Status = record.Status switch
        {
            CreditsClaimStatus.Claimed => "领取成功",
            CreditsClaimStatus.AlreadyClaimed => "今日已领取过",
            CreditsClaimStatus.Failed => "失败",
            CreditsClaimStatus.Skipped => "跳过",
            _ => record.Status.ToString()
        };
        Before = Format(record.BeforeCredits);
        After = Format(record.AfterCredits);
        Delta = record.DeltaCredits.HasValue
            ? (record.DeltaCredits.Value >= 0 ? "+" : "") + record.DeltaCredits.Value.ToString("0.##")
            : "-";
        Detail = record.Message ?? record.Error ?? string.Empty;
        IsFailed = record.Status == CreditsClaimStatus.Failed;
        IsClaimed = record.Status == CreditsClaimStatus.Claimed;
        IsNeutral = !IsFailed && !IsClaimed;
    }

    public string AccountName { get; }
    public string Status { get; }
    public string Before { get; }
    public string After { get; }
    public string Delta { get; }
    public string Detail { get; }
    public bool IsFailed { get; }
    public bool IsClaimed { get; }
    public bool IsNeutral { get; }

    private static string Format(decimal? value) => value.HasValue ? value.Value.ToString("0.##") : "未知";
}

/// <summary>登录进度条目：pending（待办）/ active（进行中）/ done（完成）/ failed（失败）。</summary>
public sealed class LoginStepItem : ObservableObject
{
    private string _state = "pending";

    public LoginStepItem(string title) => Title = title;

    public string Title { get; }

    private string State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    public bool IsPending => State == "pending";
    public bool IsActive => State == "active";
    public bool IsDone => State == "done";
    public bool IsFailed => State == "failed";

    public void Set(string state) => State = state;
}

/// <summary>已登录账号行（账号池条目 + Vault 快照状态 + 一键操作）。</summary>
public sealed class LoginAccountRow : ObservableObject
{
    private readonly LoginViewModel _owner;
    private string _snapshotStatus;
    private bool _isConfirmingDelete;

    public LoginAccountRow(AccountConfig account, string snapshotStatus, bool isCurrentLive, LoginViewModel owner)
    {
        Account = account;
        _snapshotStatus = snapshotStatus;
        IsCurrentLive = isCurrentLive;
        _owner = owner;
        SwitchCommand = new AsyncRelayCommand(() => owner.SwitchToAsync(this));
        ReloginCommand = new AsyncRelayCommand(() => owner.ReLoginAsync(this));
        DeleteCommand = new AsyncRelayCommand(() => owner.DeleteAsync(this));
        CancelDeleteCommand = new RelayCommand(() => owner.CancelDelete(this));
    }

    public AccountConfig Account { get; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Account.DisplayName) ? Account.Id : Account.DisplayName;

    public string Id => Account.Id;

    public string SnapshotStatus
    {
        get => _snapshotStatus;
        set => SetProperty(ref _snapshotStatus, value);
    }

    public bool IsCurrentLive { get; }

    /// <summary>两段式删除确认：第一次点只亮确认按钮，第二次点才真正删除。</summary>
    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set
        {
            if (SetProperty(ref _isConfirmingDelete, value))
            {
                OnPropertyChanged(nameof(IsNotConfirmingDelete));
            }
        }
    }

    public bool IsNotConfirmingDelete => !_isConfirmingDelete;

    /// <summary>一键切换：把该账号的 Vault 快照回填为 CLI 当前身份。</summary>
    public IAsyncRelayCommand SwitchCommand { get; }

    /// <summary>一键重登：对同一个账号重新走一遍登录流程（额度/过期场景）。</summary>
    public IAsyncRelayCommand ReloginCommand { get; }

    /// <summary>删除账号：清掉 Vault 备份快照并从账号池移除。</summary>
    public IAsyncRelayCommand DeleteCommand { get; }

    /// <summary>撤销删除确认（回到普通状态）。</summary>
    public IRelayCommand CancelDeleteCommand { get; }
}

/// <summary>
/// 傻瓜式登录页：把「引导登录全流程 + 加入账号池」收成一个大按钮，
/// 用户只需要在弹出的浏览器/终端里完成登录，其余（清场、等待、校验、备份、入池、保存）全自动。
/// </summary>
/// <remarks>CA1001（P0-12）：持有 <see cref="CancellationTokenSource"/>（登录/领积分可取消操作），实现 <see cref="IDisposable"/>。</remarks>
public sealed class LoginViewModel : ViewModelBase, IDisposable
{
    private readonly IEngineHost _host;

    private AgentKind _agentKind;
    private string _newAccountDisplayName = string.Empty;
    private bool _isBusy;
    private bool _isClaiming;
    private string _statusText = string.Empty;
    private string _resultText = string.Empty;
    private bool _isSuccess;
    private bool _isError;
    private CancellationTokenSource? _cts;

    public LoginViewModel(IEngineHost host)
    {
        _host = host;
        _agentKind = host.Options.AgentKind;

        Steps = new ObservableCollection<LoginStepItem>
        {
            new("检查环境（确认没有正在运行的 CLI）"),
            new("清理旧登录状态"),
            new("弹出登录窗口"),
            new("等你在浏览器里完成登录（最长 5 分钟）"),
            new("自动校验登录是否成功"),
            new("自动备份并加入账号池")
        };

        LoginCommand = new AsyncRelayCommand(() => RunLoginAsync(accountId: null, displayName: NewAccountDisplayName));
        CancelLoginCommand = new RelayCommand(() => _cts?.Cancel());
        RefreshCommand = new RelayCommand(Refresh);
        ClaimCreditsCommand = new AsyncRelayCommand(RunClaimCreditsAsync, () => IsClaimIdle);
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
        RebuildRows();
    }

    public ObservableCollection<LoginStepItem> Steps { get; }

    public ObservableCollection<LoginAccountRow> Rows { get; } = new();

    public AgentKind[] AgentKinds { get; } = Enum.GetValues<AgentKind>();

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

    public bool IsIdle => !_isBusy;

    /// <summary>登录过程中的当前进度说明（取自编排器的人话消息）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

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

    public bool HasResult => _isSuccess || _isError;

    public IAsyncRelayCommand LoginCommand { get; }

    public IRelayCommand CancelLoginCommand { get; }

    public IRelayCommand RefreshCommand { get; }

    /// <summary>一键领取所有启用账号的今日积分。</summary>
    public IAsyncRelayCommand ClaimCreditsCommand { get; }

    /// <summary>本次领取的逐账号结果（领取前 / 领取后 / 变化量）。</summary>
    public ObservableCollection<CreditsClaimRow> ClaimRows { get; } = new();

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

    public bool IsClaimIdle => !_isClaiming && !_isBusy;

    // ---------------- 一键登录 ----------------

    /// <summary>由账号行「重新登录」按钮调用（对同一账号重跑一键登录）。</summary>
    public async Task ReLoginAsync(LoginAccountRow row)
    {
        if (IsBusy)
        {
            return;
        }

        NewAccountDisplayName = row.DisplayName;
        await RunLoginAsync(row.Account.Id, row.DisplayName).ConfigureAwait(true);
    }

    private async Task RunLoginAsync(string? accountId, string? displayName)
    {
        if (IsBusy)
        {
            return;
        }

        if (_agentKind == AgentKind.WorkBuddy)
        {
            ShowError("WorkBuddy 的登录文件位置尚未实测确认，暂不支持一键登录。" +
                      "请先用 CodeBuddy（或到「配置向导」里选 CodeBuddy）完成登录。");
            return;
        }

        if (_agentKind == AgentKind.Mock)
        {
            ShowError("Mock 是仿真工具、没有真实登录流程。要登录真实账号请选择 CodeBuddy。");
            return;
        }

        accountId ??= NextAccountId();
        displayName = string.IsNullOrWhiteSpace(displayName)
            ? "账号 " + (_host.Options.Accounts.Count + 1)
            : displayName.Trim();

        IsBusy = true;
        ResultText = string.Empty;
        IsSuccess = IsError = false;
        ResetSteps();
        _cts = new CancellationTokenSource();
        StatusText = "正在准备…";

        try
        {
            var account = new AccountConfig
            {
                Id = accountId,
                DisplayName = displayName,
                CredentialReference = "profile:default"
            };

            // 保持配置与页面选择一致，登录成功后随配置一起落盘
            _host.Options.AgentKind = _agentKind;

            var info = await _host.VaultHost
                .GuidedLoginAsync(_agentKind, account, OnProgress, _cts.Token)
                .ConfigureAwait(true);

            // 登录成功 → 自动加入账号池（已存在则补齐凭据引用），保存并重建引擎
            var inPool = _host.Options.Accounts.FirstOrDefault(a =>
                string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (inPool is null)
            {
                _host.Options.Accounts.Add(account);
            }
            else
            {
                inPool.DisplayName = displayName;
                if (string.IsNullOrWhiteSpace(inPool.CredentialReference))
                {
                    inPool.CredentialReference = "profile:default";
                }
                inPool.Enabled = true;
            }

            await _host.SaveConfigAsync().ConfigureAwait(true);
            _host.RebuildEngine();
            RebuildRows();

            IsSuccess = true;
            ResultText =
                $"账号「{displayName}」登录成功！已自动备份（{info.Files.Count} 个文件）并加入账号池。" +
                "直接去「执行监控」页点「开始」就能用了；想再加一个账号，回到上面再点一次「开始登录」即可。";
            StatusText = "全部完成";
            MarkAllDone();
        }
        catch (OperationCanceledException)
        {
            ResetSteps();
            StatusText = string.Empty;
            ShowError("已取消等待。别担心，随时可以再点「开始登录」重来一次。");
        }
        catch (Exception ex)
        {
            MarkFailed();
            StatusText = "登录没有完成";
            ShowError(TranslateError(ex));
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>编排器进度 → 界面步骤条（编排器回调可能在线程池线程，统一投递到 UI 线程）。</summary>
    private void OnProgress(GuidedLoginProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = progress.Message;
            var index = progress.Phase switch
            {
                GuidedLoginPhase.Prepare => 0,
                GuidedLoginPhase.Clearing => 1,
                GuidedLoginPhase.Launching => 2,
                GuidedLoginPhase.Waiting => 3,
                GuidedLoginPhase.Verifying => 4,
                GuidedLoginPhase.BackingUp => 5,
                _ => (int?)null
            };

            if (progress.Phase == GuidedLoginPhase.Done)
            {
                return; // 成功收尾统一在 RunLoginAsync 里做
            }

            if (progress.Phase == GuidedLoginPhase.Failed)
            {
                return; // 失败统一在 catch 里做
            }

            if (index.HasValue)
            {
                MarkUpTo(index.Value);
            }
        });
    }

    private void ResetSteps()
    {
        foreach (var step in Steps)
        {
            step.Set("pending");
        }
    }

    private void MarkUpTo(int index)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Set(i < index ? "done" : i == index ? "active" : "pending");
        }
    }

    private void MarkAllDone()
    {
        foreach (var step in Steps)
        {
            step.Set("done");
        }
    }

    private void MarkFailed()
    {
        foreach (var step in Steps.Where(s => s.IsActive))
        {
            step.Set("failed");
        }
    }

    private void ShowError(string message)
    {
        IsSuccess = false;
        IsError = true;
        ResultText = message;
    }

    private static string TranslateError(Exception ex)
    {
        var raw = ex.Message;
        if (raw.Contains("超时"))
        {
            return "等了 5 分钟还没检测到登录完成——可能是浏览器里的登录没有点完。" +
                   "点「开始登录」重来一次就好；如果反复失败，先在终端里手动运行 codebuddy 确认能正常登录。" +
                   Environment.NewLine + $"（细节：{raw}）";
        }

        if (raw.Contains("进程仍在运行") || raw.Contains("进程互斥"))
        {
            return "有一个 CLI 窗口还开着，请先把它整个关掉，再点「开始登录」。" +
                   Environment.NewLine + $"（细节：{raw}）";
        }

        if (raw.Contains("未找到"))
        {
            return "电脑上没找到 codebuddy 命令。先安装：npm install -g @tencent-ai/codebuddy-code，装完再点「开始登录」。" +
                   Environment.NewLine + $"（细节：{raw}）";
        }

        return raw;
    }

    // ---------------- 一键领取每日积分 ----------------

    /// <summary>
    /// 逐账号领取每日积分并记录「领取前 / 领取后」积分。领取进度复用本页状态条展示；
    /// 完成后逐账号结果表（含前后积分与变化量）显示在下方。
    /// </summary>
    private async Task RunClaimCreditsAsync()
    {
        if (IsBusy || IsClaiming)
        {
            return;
        }

        if (_agentKind == AgentKind.WorkBuddy)
        {
            ShowError("WorkBuddy 的登录文件位置尚未实测确认，暂不支持领取积分。请先切换到 CodeBuddy。");
            return;
        }

        var enabledAccounts = _host.Options.Accounts.Where(a => a.Enabled).ToList();
        if (enabledAccounts.Count == 0)
        {
            ShowError("账号池里还没有启用的账号。先登录一个账号，再来领取积分。");
            return;
        }

        IsBusy = true;
        IsClaiming = true;
        ResultText = string.Empty;
        IsSuccess = IsError = false;
        ClaimRows.Clear();
        OnPropertyChanged(nameof(HasClaimRows));
        StatusText = "正在准备领取每日积分…";
        _cts = new CancellationTokenSource();

        try
        {
            var summary = await _host.ClaimDailyCreditsAsync(force: false, _cts.Token).ConfigureAwait(true);

            foreach (var record in summary.Records)
            {
                ClaimRows.Add(new CreditsClaimRow(record));
            }

            OnPropertyChanged(nameof(HasClaimRows));

            if (summary.FailedCount == 0 && summary.Records.Count > 0)
            {
                IsSuccess = true;
                ResultText = $"每日积分领取完成：成功 {summary.ClaimedCount} 个，" +
                             $"已领取过 {summary.AlreadyClaimedCount} 个，跳过 {summary.SkippedCount} 个。" +
                             (_host.LastCreditsClaimReportPath is null
                                 ? string.Empty
                                 : Environment.NewLine + $"报告已保存：{_host.LastCreditsClaimReportPath}");
            }
            else if (summary.Records.Count == 0)
            {
                ShowError("没有可领取的账号（账号池为空）。");
            }
            else
            {
                IsSuccess = true;
                ResultText = $"领取结束：成功 {summary.ClaimedCount}，已领取过 {summary.AlreadyClaimedCount}，" +
                             $"失败 {summary.FailedCount}。失败的账号请检查登录状态后重试。" +
                             (_host.LastCreditsClaimReportPath is null
                                 ? string.Empty
                                 : Environment.NewLine + $"报告已保存：{_host.LastCreditsClaimReportPath}");
            }

            StatusText = summary.FailedCount == 0 ? "积分领取全部完成" : "积分领取完成（部分失败）";
        }
        catch (OperationCanceledException)
        {
            StatusText = string.Empty;
            ShowError("已取消领取。台账只保存了已完成账号的结果，随时可以再点一次继续。");
        }
        catch (Exception ex)
        {
            StatusText = string.Empty;
            ShowError("每日积分领取没有完成：" + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsClaiming = false;
            IsBusy = false;
            Refresh();
        }
    }

    // ---------------- 已登录账号（一键切换 / 重登） ----------------

    /// <summary>由账号行「切换到这个账号」按钮调用（Vault 快照一键回填 Live）。</summary>
    public async Task SwitchToAsync(LoginAccountRow row)
    {
        try
        {
            StatusText = $"正在切换到「{row.DisplayName}」…";
            await _host.VaultHost.SwitchAsync(_agentKind, row.Id).ConfigureAwait(true);
            StatusText = $"已切换：CLI 现在用的就是「{row.DisplayName}」的身份。";
            RebuildRows();
        }
        catch (Exception ex)
        {
            StatusText = string.Empty;
            ShowError(TranslateError(ex));
        }
    }

    /// <summary>由账号行「取消」按钮调用：撤销删除确认状态。</summary>
    public void CancelDelete(LoginAccountRow row)
    {
        row.IsConfirmingDelete = false;
        StatusText = string.Empty;
    }

    /// <summary>由账号行「删除」按钮调用：清掉 Vault 快照并从账号池移除（两段式确认）。</summary>
    public async Task DeleteAsync(LoginAccountRow row)
    {
        if (IsBusy)
        {
            return;
        }

        if (!row.IsConfirmingDelete)
        {
            row.IsConfirmingDelete = true;
            return;
        }

        try
        {
            if (row.IsCurrentLive)
            {
                ShowError($"「{row.DisplayName}」是 CLI 当前正在使用的账号，不能直接删除。" +
                          "先切换到别的账号，再删除它。");
                return;
            }

            await _host.VaultHost.DeleteSnapshotAsync(_agentKind, row.Id).ConfigureAwait(true);

            var inPool = _host.Options.Accounts.FirstOrDefault(a =>
                string.Equals(a.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (inPool is not null)
            {
                _host.Options.Accounts.Remove(inPool);
                await _host.SaveConfigAsync().ConfigureAwait(true);
            }

            _host.RebuildEngine();
            RebuildRows();

            IsError = false;
            IsSuccess = true;
            ResultText = $"账号「{row.DisplayName}」已删除：备份凭证一并清掉，账号池也已同步更新。";
        }
        catch (Exception ex)
        {
            StatusText = string.Empty;
            ShowError(TranslateError(ex));
        }
        finally
        {
            row.IsConfirmingDelete = false;
        }
    }

    private void RebuildRows()
    {
        string? currentLive = null;
        try
        {
            currentLive = _host.VaultHost.Vault.CurrentLiveAccount(_agentKind);
        }
        catch
        {
            // Live 记账缺失不影响列表展示
        }

        Rows.Clear();
        foreach (var account in _host.Options.Accounts)
        {
            Rows.Add(new LoginAccountRow(
                account,
                _host.VaultHost.DescribeSnapshot(_agentKind, account.Id),
                string.Equals(currentLive, account.Id, StringComparison.OrdinalIgnoreCase),
                this));
        }

        OnPropertyChanged(nameof(HasAccounts));
    }

    public bool HasAccounts => Rows.Count > 0;

    // ---------------- 小工具 ----------------

    /// <summary>生成不与现有账号冲突的 acc-N。</summary>
    private string NextAccountId()
    {
        var used = new HashSet<string>(
            _host.Options.Accounts.Select(a => a.Id),
            StringComparer.OrdinalIgnoreCase);

        var index = used.Count + 1;
        while (used.Contains("acc-" + index))
        {
            index++;
        }

        return "acc-" + index;
    }

    /// <summary>释放未取消完的登录/领积分 CTS（CA1001；每次操作结束已主动释放，这里是兜底）。</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
