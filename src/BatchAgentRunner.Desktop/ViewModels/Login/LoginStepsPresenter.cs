using System.Collections.ObjectModel;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 登录步骤条（P-06 从 <see cref="LoginViewModel"/> 抽离）。
/// 独占六个固定步骤与全部步骤态迁移（Reset / MarkUpTo / MarkAllDone / MarkFailed），
/// 引导登录编排器的进度回调经 <see cref="ApplyProgress"/> 映射到步骤态
/// （Done / Failed 的收尾由流程层统一处理，本类只驱动步骤条）。
/// </summary>
internal sealed class LoginStepsPresenter
{
    private readonly ObservableCollection<LoginStepItem> _items = new()
    {
        new("检查环境（确认没有正在运行的 CLI）"),
        new("清理旧登录状态"),
        new("弹出登录窗口"),
        new("等你在浏览器里完成登录（最长 5 分钟）"),
        new("自动校验登录是否成功"),
        new("自动备份并加入账号池")
    };

    /// <summary>步骤集合（XAML 直接绑定本实例，Count/内容变化经集合通知）。</summary>
    public ObservableCollection<LoginStepItem> Items => _items;

    /// <summary>全部复位为待办。</summary>
    public void Reset()
    {
        foreach (var step in _items)
        {
            step.Set("pending");
        }
    }

    /// <summary>标记到指定进度：之前的步骤完成、该步进行中、之后的待办。</summary>
    public void MarkUpTo(int index)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].Set(i < index ? "done" : i == index ? "active" : "pending");
        }
    }

    /// <summary>全部标记完成。</summary>
    public void MarkAllDone()
    {
        foreach (var step in _items)
        {
            step.Set("done");
        }
    }

    /// <summary>把进行中的步骤标记失败（其余保持原态）。</summary>
    public void MarkFailed()
    {
        foreach (var step in _items.Where(s => s.IsActive))
        {
            step.Set("failed");
        }
    }

    /// <summary>
    /// 编排器进度 → 步骤条：按阶段映射步骤索引（Prepare→0 … BackingUp→5）。
    /// Done / Failed 不在此驱动步骤（成功收尾统一 MarkAllDone、失败统一 MarkFailed，由流程层负责）。
    /// </summary>
    public void ApplyProgress(GuidedLoginProgress progress)
    {
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
            return; // 成功收尾统一在流程层做
        }

        if (progress.Phase == GuidedLoginPhase.Failed)
        {
            return; // 失败统一在流程层做
        }

        if (index.HasValue)
        {
            MarkUpTo(index.Value);
        }
    }
}
