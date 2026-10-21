using System;
using System.Linq;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 每日积分领取流程协作者（P-06 从 <see cref="LoginViewModel"/> 抽离）。
/// 承载「守卫 → 启用账号检查 → 领取中态 → 批量领取 → 逐账号结果行 → 汇总文案」全流程；
/// 独占领取用 <see cref="CancellationTokenSource"/>（CA1001：实现 <see cref="IDisposable"/>，
/// 由 facade <c>Dispose</c> 一并释放）。经 <see cref="ILoginView"/> 读写 UI 状态，不依赖 facade 具体类型。
/// </summary>
internal sealed class CreditsClaimFlow : IDisposable
{
    private readonly ILoginView _view;
    private CancellationTokenSource? _cts;

    public CreditsClaimFlow(ILoginView view)
    {
        _view = view;
    }

    /// <summary>
    /// 逐账号领取每日积分并记录「领取前 / 领取后」积分。领取进度复用本页状态条展示；
    /// 完成后逐账号结果表（含前后积分与变化量）显示在下方。
    /// </summary>
    public async Task RunAsync()
    {
        if (_view.IsBusy || _view.IsClaiming)
        {
            return;
        }

        if (_view.AgentKind == AgentKind.WorkBuddy)
        {
            ShowError("WorkBuddy 的登录文件位置尚未实测确认，暂不支持领取积分。请先切换到 CodeBuddy。");
            return;
        }

        var enabledAccounts = _view.Host.Options.Accounts.Where(a => a.Enabled).ToList();
        if (enabledAccounts.Count == 0)
        {
            ShowError("账号池里还没有启用的账号。先登录一个账号，再来领取积分。");
            return;
        }

        _view.IsBusy = true;
        _view.IsClaiming = true;
        _view.ResultText = string.Empty;
        _view.IsSuccess = false;
        _view.IsError = false;
        _view.ClaimRows.Clear();
        _view.NotifyClaimRowsChanged();
        _view.StatusText = "正在准备领取每日积分…";
        _cts = new CancellationTokenSource();

        try
        {
            var summary = await _view.Host.ClaimDailyCreditsAsync(force: _view.Host.Options.CreditsClaim.Force, _cts.Token).ConfigureAwait(true);

            foreach (var record in summary.Records)
            {
                _view.ClaimRows.Add(new CreditsClaimRow(record));
            }

            _view.NotifyClaimRowsChanged();

            if (summary.FailedCount == 0 && summary.Records.Count > 0)
            {
                _view.IsSuccess = true;
                _view.ResultText = $"每日积分领取完成：成功 {summary.ClaimedCount} 个，" +
                             $"已领取过 {summary.AlreadyClaimedCount} 个，跳过 {summary.SkippedCount} 个。" +
                             ReportPathSuffix();
            }
            else if (summary.Records.Count == 0)
            {
                ShowError("没有可领取的账号（账号池为空）。");
            }
            else
            {
                _view.IsSuccess = true;
                _view.ResultText = $"领取结束：成功 {summary.ClaimedCount}，已领取过 {summary.AlreadyClaimedCount}，" +
                             $"失败 {summary.FailedCount}。失败的账号请检查登录状态后重试。" +
                             ReportPathSuffix();
            }

            _view.StatusText = summary.FailedCount == 0 ? "积分领取全部完成" : "积分领取完成（部分失败）";
        }
        catch (OperationCanceledException)
        {
            _view.StatusText = string.Empty;
            ShowError("已取消领取。台账只保存了已完成账号的结果，随时可以再点一次继续。");
        }
        catch (Exception ex)
        {
            _view.StatusText = string.Empty;
            ShowError("每日积分领取没有完成：" + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _view.IsClaiming = false;
            _view.IsBusy = false;
            _view.Refresh();
        }
    }

    /// <summary>释放未取消完的领取 CTS（CA1001；每次操作结束已主动释放，这里是兜底）。</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>报告路径附注（无报告路径时为空串）。</summary>
    private string ReportPathSuffix() =>
        _view.Host.LastCreditsClaimReportPath is null
            ? string.Empty
            : Environment.NewLine + $"报告已保存：{_view.Host.LastCreditsClaimReportPath}";

    private void ShowError(string message)
    {
        _view.IsSuccess = false;
        _view.IsError = true;
        _view.ResultText = message;
    }
}
