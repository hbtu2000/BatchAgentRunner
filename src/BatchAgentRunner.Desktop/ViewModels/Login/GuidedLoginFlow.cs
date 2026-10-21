using System;
using System.Linq;
using Avalonia.Threading;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 一键登录全流程协作者（P-06 从 <see cref="LoginViewModel"/> 抽离）。
/// 承载「守卫 → 分配账号 → 忙碌态 → 引导登录 → 入池保存 → 成功收尾」全流程与
/// 编排器进度回调（<see cref="OnProgress"/> → 步骤条）；独占登录用
/// <see cref="CancellationTokenSource"/>（可取消，CA1001：实现 <see cref="IDisposable"/>，
/// 由 facade <c>Dispose</c> 一并释放）。经 <see cref="ILoginView"/> 读写 UI 状态，不依赖 facade 具体类型。
/// </summary>
internal sealed class GuidedLoginFlow : IDisposable
{
    private readonly ILoginView _view;
    private readonly LoginStepsPresenter _steps;
    private CancellationTokenSource? _cts;

    public GuidedLoginFlow(ILoginView view, LoginStepsPresenter steps)
    {
        _view = view;
        _steps = steps;
    }

    /// <summary>由账号行「重新登录」按钮调用（对同一账号重跑一键登录）。</summary>
    public async Task ReLoginAsync(LoginAccountRow row)
    {
        if (_view.IsBusy)
        {
            return;
        }

        _view.NewAccountDisplayName = row.DisplayName;
        await RunAsync(row.Account.Id, row.DisplayName).ConfigureAwait(true);
    }

    /// <summary>一键登录主流程（facade 的 LoginCommand 与账号行重登共用）。</summary>
    public async Task RunAsync(string? accountId, string? displayName)
    {
        if (_view.IsBusy)
        {
            return;
        }

        if (_view.AgentKind == AgentKind.WorkBuddy)
        {
            ShowError("WorkBuddy 的登录文件位置尚未实测确认，暂不支持一键登录。" +
                      "请先用 CodeBuddy（或到「配置向导」里选 CodeBuddy）完成登录。");
            return;
        }

        if (_view.AgentKind == AgentKind.Mock)
        {
            ShowError("Mock 是仿真工具、没有真实登录流程。要登录真实账号请选择 CodeBuddy。");
            return;
        }

        accountId ??= AccountPoolCoordinator.NextAccountId(_view.Host.Options);
        displayName = string.IsNullOrWhiteSpace(displayName)
            ? "账号 " + (_view.Host.Options.Accounts.Count + 1)
            : displayName.Trim();

        _view.IsBusy = true;
        _view.ResultText = string.Empty;
        _view.IsSuccess = false;
        _view.IsError = false;
        _steps.Reset();
        _cts = new CancellationTokenSource();
        _view.StatusText = "正在准备…";

        try
        {
            var account = new AccountConfig
            {
                Id = accountId,
                DisplayName = displayName,
                CredentialReference = "profile:default"
            };

            // 保持配置与页面选择一致，登录成功后随配置一起落盘
            _view.Host.Options.AgentKind = _view.AgentKind;

            var info = await _view.Host.VaultHost
                .GuidedLoginAsync(_view.AgentKind, account, OnProgress, _cts.Token)
                .ConfigureAwait(true);

            // 登录成功 → 自动加入账号池（已存在则补齐凭据引用），保存并重建引擎
            var inPool = _view.Host.Options.Accounts.FirstOrDefault(a =>
                string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (inPool is null)
            {
                _view.Host.Options.Accounts.Add(account);
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

            await _view.Host.SaveConfigAsync().ConfigureAwait(true);
            await _view.Host.RebuildEngineAsync().ConfigureAwait(true);
            _view.RebuildRows();

            _view.IsSuccess = true;
            _view.ResultText =
                $"账号「{displayName}」登录成功！已自动备份（{info.Files.Count} 个文件）并加入账号池。" +
                "直接去「执行监控」页点「开始」就能用了；想再加一个账号，回到上面再点一次「开始登录」即可。";
            _view.StatusText = "全部完成";
            _steps.MarkAllDone();
        }
        catch (OperationCanceledException)
        {
            _steps.Reset();
            _view.StatusText = string.Empty;
            ShowError("已取消等待。别担心，随时可以再点「开始登录」重来一次。");
        }
        catch (Exception ex)
        {
            _steps.MarkFailed();
            _view.StatusText = "登录没有完成";
            ShowError(LoginErrorTranslator.Translate(ex));
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _view.IsBusy = false;
            _view.Refresh();
        }
    }

    /// <summary>取消进行中的登录（无进行中时空转）。</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>释放未取消完的登录 CTS（CA1001；每次操作结束已主动释放，这里是兜底）。</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>编排器进度 → 界面步骤条（编排器回调可能在线程池线程，统一投递到 UI 线程）。</summary>
    private void OnProgress(GuidedLoginProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _view.StatusText = progress.Message;
            _steps.ApplyProgress(progress);
        });
    }

    private void ShowError(string message)
    {
        _view.IsSuccess = false;
        _view.IsError = true;
        _view.ResultText = message;
    }
}
