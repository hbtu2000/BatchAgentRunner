using Avalonia;
using Avalonia.Media;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Desktop.Helpers;

/// <summary>
/// 主题画笔单一入口（P2-10 / P2-11 偿还）：原 <c>TaskRowViewModel</c> / <c>Phase2RowModels</c> /
/// <c>DiffViewModel</c> / <c>MonitorViewModel</c> / <c>ConfigHubConverters</c> 各写一份
/// <c>Color.Parse("#…")</c> 调色板，与 <c>AppStyles.xaml</c> 的 token 表并存冲突——
/// 深色主题下这些硬编码色完全失效（P2-10），且两份调色板数值互不一致（P2-11）。
/// 现一律经本类按 token 名取色：运行时有宿主环境 → 主题 token（双主题自动切换）；
/// 无宿主环境（单测 / 无 Application）→ 回落内置浅色值（与 AppStyles 浅色 token 逐字一致，
/// 由 <c>AppBrushesTests</c> 锁死一致性与键存在性）。
/// </summary>
public static class AppBrushes
{
    /// <summary>按 token 键取画笔；宿主不可用或键缺失时回落 <paramref name="fallbackHex"/>（浅色 token 值）。</summary>
    public static IBrush Token(string key, string fallbackHex)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var resource) &&
            resource is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    /// <summary>任务级状态色（状态点 / 行徽标）。</summary>
    public static IBrush ForTaskStatus(TaskStatus status) => Token(status switch
    {
        TaskStatus.Running => "StatusRunningBrush",
        TaskStatus.Done => "StatusDoneBrush",
        TaskStatus.Failed => "StatusFailedBrush",
        TaskStatus.Interrupted => "StatusInterruptedBrush",
        TaskStatus.NeedsAttention => "StatusAttentionBrush",
        TaskStatus.Blocked => "StatusBlockedBrush",
        _ => "StatusPendingBrush"
    }, status switch
    {
        TaskStatus.Running => "#2563EB",
        TaskStatus.Done => "#16A34A",
        TaskStatus.Failed => "#DC2626",
        TaskStatus.Interrupted => "#D97706",
        TaskStatus.NeedsAttention => "#7C3AED",
        TaskStatus.Blocked => "#0E7490",
        _ => "#8A95A3"
    });

    /// <summary>批级状态色（监控页状态徽标）。</summary>
    public static IBrush ForBatchStatus(BatchStatus status) => Token(status switch
    {
        BatchStatus.Running => "BatchRunningBrush",
        BatchStatus.Waiting => "BatchWaitingBrush",
        BatchStatus.Blocked => "BatchBlockedBrush",
        BatchStatus.Completed => "BatchCompletedBrush",
        BatchStatus.Terminated => "BatchTerminatedBrush",
        _ => "BatchIdleBrush"
    }, status switch
    {
        BatchStatus.Running => "#2563EB",
        BatchStatus.Waiting => "#D97706",
        BatchStatus.Blocked => "#DC2626",
        BatchStatus.Completed => "#16A34A",
        BatchStatus.Terminated => "#8A95A3",
        _ => "#8A95A3"
    });

    /// <summary>提示条级别色（danger / warning / 其余 info）。</summary>
    public static IBrush ForLevel(string? level) => level switch
    {
        "danger" => Token("ErrorBrush", "#DC2626"),
        "warning" => Token("WarningBrush", "#D97706"),
        _ => Token("PrimaryBrush", "#2563EB")
    };

    /// <summary>diff 行类型色（新增 / 删除 / 修改）。</summary>
    public static IBrush ForDiffKind(DiffKind kind) => Token(kind switch
    {
        DiffKind.Added => "SuccessBrush",
        DiffKind.Deleted => "ErrorBrush",
        _ => "WarningBrush"
    }, kind switch
    {
        DiffKind.Added => "#16A34A",
        DiffKind.Deleted => "#DC2626",
        _ => "#D97706"
    });

    /// <summary>diff 正文色（主 / 次 / 弱）。</summary>
    public static IBrush DiffText(bool muted) => muted
        ? Token("TextMutedBrush", "#8A95A3")
        : Token("TextSecondaryBrush", "#5A6675");

    /// <summary>配置中心导航/开关前景色（激活主色 / 未激活正文色）。</summary>
    public static IBrush NavActive() => Token("PrimaryBrush", "#2563EB");

    /// <summary>配置中心激活态反白文字（主色背景上的白字，双主题）。</summary>
    public static IBrush NavActiveText() => Token("OnPrimaryBrush", "#FFFFFF");

    public static IBrush NavInactiveText() => Token("TextPrimaryBrush", "#1B2430");

    public static IBrush NavInactiveTrack() => Token("BorderSubtleBrush", "#E3E8EF");
}
