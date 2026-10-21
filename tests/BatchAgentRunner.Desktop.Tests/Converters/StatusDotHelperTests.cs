using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Converters;
using BatchAgentRunner.Desktop.Tests.Helpers;
using Xunit;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Desktop.Tests.Converters;

/// <summary>
/// P2-13 偿还守卫：批级状态点着色原按中文字面量（「运行中」/「等待恢复」…）匹配，
/// 改文案即断色；现改绑 <see cref="BatchStatus"/> 枚举（<c>StatusDotHelper.BatchStatus</c> 附加属性），
/// 文案与配色解耦。锁死枚举 → CSS 类映射，防止回落字面量匹配。
/// </summary>
public class StatusDotHelperTests : DesktopHeadlessTestBase
{
    [AvaloniaTheory]
    [InlineData(BatchStatus.Idle, "batch-idle")]
    [InlineData(BatchStatus.Running, "batch-running")]
    [InlineData(BatchStatus.Waiting, "batch-waiting")]
    [InlineData(BatchStatus.Blocked, "batch-blocked")]
    [InlineData(BatchStatus.Terminated, "batch-terminated")]
    [InlineData(BatchStatus.Completed, "batch-completed")]
    public void 批级状态_映射为对应CSS类(BatchStatus status, string expectedClass)
    {
        var dot = new Ellipse();

        StatusDotHelper.SetBatchStatus(dot, status);

        Assert.Contains(expectedClass, dot.Classes);
        Assert.Single(dot.Classes);
    }

    [AvaloniaFact]
    public void 批级状态_运行中改置空_回落idle类()
    {
        var dot = new Ellipse();
        StatusDotHelper.SetBatchStatus(dot, BatchStatus.Running);
        Assert.Contains("batch-running", dot.Classes);

        StatusDotHelper.SetBatchStatus(dot, null);

        Assert.Contains("batch-idle", dot.Classes);
        Assert.Single(dot.Classes);
    }

    [AvaloniaTheory]
    [InlineData(TaskStatus.Pending, "st-pending")]
    [InlineData(TaskStatus.Running, "st-running")]
    [InlineData(TaskStatus.Done, "st-done")]
    [InlineData(TaskStatus.Failed, "st-failed")]
    [InlineData(TaskStatus.Interrupted, "st-interrupted")]
    [InlineData(TaskStatus.NeedsAttention, "st-attention")]
    [InlineData(TaskStatus.Blocked, "st-blocked")]
    public void 任务级状态_映射为对应CSS类(TaskStatus status, string expectedClass)
    {
        var dot = new Ellipse();

        StatusDotHelper.SetTaskStatus(dot, status);

        Assert.Contains(expectedClass, dot.Classes);
        Assert.Single(dot.Classes);
    }
}
