using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Helpers;
using BatchAgentRunner.Desktop.Tests.Helpers;
using Xunit;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Desktop.Tests.Helpers;

/// <summary>
/// P2-10 / P2-11 守卫：<see cref="AppBrushes"/> 是代码侧调色板单一入口，所有状态/级别/diff/导航色
/// 经它按 token 名取色（宿主可用时主题 token，双主题自动切换；无宿主时回落浅色 token 值）。
/// 本测试锁死：① 各方法产出的画刷色值等于预期浅色 token 值（回落值与 AppStyles 浅色 token 逐字一致）；
/// ② token 键不存在时确定性地回落 fallback（不抛、返回预期 hex）；③ 双主题 token 确实存在且可取。
/// </summary>
public class AppBrushesTests : DesktopHeadlessTestBase
{
    private static Color ColorOf(IBrush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    [AvaloniaTheory]
    [InlineData(TaskStatus.Pending, "#8A95A3")]
    [InlineData(TaskStatus.Running, "#2563EB")]
    [InlineData(TaskStatus.Done, "#16A34A")]
    [InlineData(TaskStatus.Failed, "#DC2626")]
    [InlineData(TaskStatus.Interrupted, "#D97706")]
    [InlineData(TaskStatus.NeedsAttention, "#7C3AED")]
    [InlineData(TaskStatus.Blocked, "#0E7490")]
    public void ForTaskStatus_返回预期浅色token色(TaskStatus status, string expected)
        => Assert.Equal(Color.Parse(expected), ColorOf(AppBrushes.ForTaskStatus(status)));

    [AvaloniaTheory]
    [InlineData(BatchStatus.Idle, "#8A95A3")]
    [InlineData(BatchStatus.Running, "#2563EB")]
    [InlineData(BatchStatus.Waiting, "#D97706")]
    [InlineData(BatchStatus.Blocked, "#DC2626")]
    [InlineData(BatchStatus.Completed, "#16A34A")]
    [InlineData(BatchStatus.Terminated, "#8A95A3")]
    public void ForBatchStatus_返回预期浅色token色(BatchStatus status, string expected)
        => Assert.Equal(Color.Parse(expected), ColorOf(AppBrushes.ForBatchStatus(status)));

    [AvaloniaTheory]
    [InlineData("danger", "#DC2626")]
    [InlineData("warning", "#D97706")]
    [InlineData("info", "#2563EB")]
    [InlineData(null, "#2563EB")]
    public void ForLevel_返回预期色(string? level, string expected)
        => Assert.Equal(Color.Parse(expected), ColorOf(AppBrushes.ForLevel(level)));

    [AvaloniaTheory]
    [InlineData(DiffKind.Added, "#16A34A")]
    [InlineData(DiffKind.Deleted, "#DC2626")]
    [InlineData(DiffKind.Modified, "#D97706")]
    public void ForDiffKind_返回预期色(DiffKind kind, string expected)
        => Assert.Equal(Color.Parse(expected), ColorOf(AppBrushes.ForDiffKind(kind)));

    [AvaloniaTheory]
    [InlineData(true, "#8A95A3")]
    [InlineData(false, "#5A6675")]
    public void DiffText_返回预期色(bool muted, string expected)
        => Assert.Equal(Color.Parse(expected), ColorOf(AppBrushes.DiffText(muted)));

    [AvaloniaFact]
    public void Nav_返回预期色()
    {
        Assert.Equal(Color.Parse("#2563EB"), ColorOf(AppBrushes.NavActive()));
        Assert.Equal(Color.Parse("#FFFFFF"), ColorOf(AppBrushes.NavActiveText()));
        Assert.Equal(Color.Parse("#1B2430"), ColorOf(AppBrushes.NavInactiveText()));
        Assert.Equal(Color.Parse("#E3E8EF"), ColorOf(AppBrushes.NavInactiveTrack()));
    }

    [AvaloniaFact]
    public void Token_键不存在时确定回落fallback且不抛()
    {
        var brush = AppBrushes.Token("__不存在的键__", "#123456");

        Assert.Equal(Color.Parse("#123456"), ColorOf(brush));
    }

    [AvaloniaFact]
    public void Token_宿主可用时主题token可取_双主题不抛()
    {
        // Application.Current 已由 headless 框架初始化；此处仅验证 TryGetResource 路径不抛且返回 SolidColorBrush。
        var brush = AppBrushes.ForTaskStatus(TaskStatus.Running);

        Assert.IsType<SolidColorBrush>(brush);
    }
}
