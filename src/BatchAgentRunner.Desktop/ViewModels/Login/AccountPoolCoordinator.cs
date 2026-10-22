using System;
using System.Collections.ObjectModel;
using System.Linq;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 账号池协作者（P-06 从 <see cref="LoginViewModel"/> 抽离）。
/// 独占 <see cref="Rows"/> 集合与账号行三类操作：整表重建（按账号池 + Vault 快照状态 +
/// Live 记账）、一键切换（Vault 快照回填 CLI 当前身份）、两段式删除（清 Vault 快照 + 出池 +
/// 保存 + 重建引擎）。ctor 取 facade 具体类型：账号行构造需要 owner（facade）承接行命令路由。
/// </summary>
internal sealed class AccountPoolCoordinator
{
    private readonly LoginViewModel _vm;
    private readonly ILoginView _view;
    private readonly ObservableCollection<LoginAccountRow> _rows = new();

    /// <summary>以登录页 facade 初始化，并从其读取宿主与 UI 状态。</summary>
    public AccountPoolCoordinator(LoginViewModel vm)
    {
        _vm = vm;
        _view = vm;
    }

    /// <summary>账号行集合（LoginView.axaml 直接绑定本实例）。</summary>
    public ObservableCollection<LoginAccountRow> Rows => _rows;

    /// <summary>
    /// 按账号池重建账号行：逐行带 Vault 快照描述与 Live 标记。
    /// Live 记账缺失不影响列表展示（try/catch 吞掉，与分解前一致）。
    /// </summary>
    public void Rebuild()
    {
        string? currentLive = null;
        try
        {
            currentLive = _view.Host.VaultHost.Vault.CurrentLiveAccount(_view.AgentKind);
        }
        catch
        {
            // Live 记账缺失不影响列表展示
        }

        _rows.Clear();
        foreach (var account in _view.Host.Options.Accounts)
        {
            _rows.Add(new LoginAccountRow(
                account,
                _view.Host.VaultHost.DescribeSnapshot(_view.AgentKind, account.Id),
                string.Equals(currentLive, account.Id, StringComparison.OrdinalIgnoreCase),
                _vm));
        }
    }

    /// <summary>一键切换：把该账号的 Vault 快照回填为 CLI 当前身份。</summary>
    public async Task SwitchAsync(LoginAccountRow row)
    {
        try
        {
            _view.StatusText = $"正在切换到「{row.DisplayName}」…";
            await _view.Host.VaultHost.SwitchAsync(_view.AgentKind, row.Id).ConfigureAwait(true);
            _view.StatusText = $"已切换：CLI 现在用的就是「{row.DisplayName}」的身份。";
            _view.RebuildRows();
        }
        catch (Exception ex)
        {
            _view.StatusText = string.Empty;
            ShowError(LoginErrorTranslator.Translate(ex));
        }
    }

    /// <summary>由账号行「取消」按钮调用：撤销删除确认状态。</summary>
    public void CancelDelete(LoginAccountRow row)
    {
        row.IsConfirmingDelete = false;
        _view.StatusText = string.Empty;
    }

    /// <summary>由账号行「删除」按钮调用：清掉 Vault 快照并从账号池移除（两段式确认）。</summary>
    public async Task DeleteAsync(LoginAccountRow row)
    {
        if (_view.IsBusy)
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

            await _view.Host.VaultHost.DeleteSnapshotAsync(_view.AgentKind, row.Id).ConfigureAwait(true);

            var inPool = _view.Host.Options.Accounts.FirstOrDefault(a =>
                string.Equals(a.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (inPool is not null)
            {
                _view.Host.Options.Accounts.Remove(inPool);
                await _view.Host.SaveConfigAsync().ConfigureAwait(true);
            }

            await _view.Host.RebuildEngineAsync().ConfigureAwait(true);
            _view.RebuildRows();

            _view.IsError = false;
            _view.IsSuccess = true;
            _view.ResultText = $"账号「{row.DisplayName}」已删除：备份凭证一并清掉，账号池也已同步更新。";
        }
        catch (Exception ex)
        {
            _view.StatusText = string.Empty;
            ShowError(LoginErrorTranslator.Translate(ex));
        }
        finally
        {
            row.IsConfirmingDelete = false;
        }
    }

    /// <summary>生成不与现有账号冲突的 acc-N。</summary>
    public static string NextAccountId(RunOptions options)
    {
        var used = new HashSet<string>(
            options.Accounts.Select(a => a.Id),
            StringComparer.OrdinalIgnoreCase);

        var index = used.Count + 1;
        while (used.Contains("acc-" + index))
        {
            index++;
        }

        return "acc-" + index;
    }

    private void ShowError(string message)
    {
        _view.IsSuccess = false;
        _view.IsError = true;
        _view.ResultText = message;
    }
}
