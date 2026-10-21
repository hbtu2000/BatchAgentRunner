using System;
using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Desktop.Models;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 事件提示板（P-06 从 <see cref="MonitorViewModel"/> 抽离）。
/// 独占 <see cref="Toasts"/> 集合与两级写入：引擎事件 <see cref="Add"/>（按事件级别映射 +
/// 二期关注类消息关键词升级告警）与操作失败 <see cref="AddIssue"/>（状态栏 + 提示板双写，
/// 避免「点了按钮似乎没反应」——状态栏在窗口最底部极易被忽略，2026-10-18 现场）。
/// 上限 100 条，超出从尾部淘汰。
/// </summary>
internal sealed class EventToastBoard
{
    private readonly ObservableCollection<ToastItem> _toasts = new();

    /// <summary>事件提示集合（XAML 直接绑定本实例，Count 变化经集合通知）。</summary>
    public ObservableCollection<ToastItem> Items => _toasts;

    /// <summary>引擎事件 → 提示行（按事件类型定级，二期关注类消息按关键词升级 warning）。</summary>
    public void Add(BatchEvent ev)
    {
        var level = ev.Kind switch
        {
            BatchEventKind.DangerousOperationBlocked => "danger",
            BatchEventKind.BatchBlocked or BatchEventKind.ModelSwitched or BatchEventKind.AccountSwitched
                or BatchEventKind.FallbackUsed or BatchEventKind.BatchWaiting or BatchEventKind.QuotaResetRecorded
                or BatchEventKind.Warning or BatchEventKind.BatchTerminated => "warning",
            _ => "info"
        };

        // 二期事件走 Info/Warning 通道，按消息关键词升级告警级别（门禁/依赖/会话/通知/模板）。
        if (level == "info" && IsPhase2Attention(ev.Message))
        {
            level = "warning";
        }

        Insert(level, ev.Message, ev.At);
    }

    /// <summary>需要用户注意的失败提示：与 <see cref="Add"/> 同一块提示板，调用方负责同步状态栏。</summary>
    public void AddIssue(string message, string level)
    {
        Insert(level, message, DateTimeOffset.Now);
    }

    /// <summary>二期关注类消息关键词：模板展开 / 门禁修订 / 依赖阻塞 / 会话恢复 / 通知发送失败。</summary>
    private static bool IsPhase2Attention(string? message) => message is not null && (
        message.Contains("门禁", StringComparison.Ordinal) ||
        message.Contains("依赖", StringComparison.Ordinal) ||
        message.Contains("模板", StringComparison.Ordinal) ||
        message.Contains("会话恢复", StringComparison.Ordinal) ||
        message.Contains("通知", StringComparison.Ordinal) ||
        message.Contains("待人工", StringComparison.Ordinal));

    private void Insert(string level, string? message, DateTimeOffset at)
    {
        _toasts.Insert(0, new ToastItem(level, message ?? string.Empty, at));
        while (_toasts.Count > 100)
        {
            _toasts.RemoveAt(_toasts.Count - 1);
        }
    }
}
