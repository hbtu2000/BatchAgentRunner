using BatchAgentRunner.Core.Domain;
using Xunit;

namespace BatchAgentRunner.Tests.Core;

/// <summary>
/// P2-5 偿还守卫：<see cref="TaskStatusExtensions.IsTerminal"/> 是全仓任务终态判定的单一事实来源
/// （原 TaskItem.IsTerminal / StageResult.IsTerminal / DagScheduler.IsSettled / TaskDispatcher 内联四份已收敛）。
/// 新增终态状态时本用例失败 = 提醒同步终态集合定义。
/// </summary>
public class TaskStatusExtensionsTests
{
    [Theory]
    [InlineData(TaskStatus.Done)]
    [InlineData(TaskStatus.Failed)]
    [InlineData(TaskStatus.Interrupted)]
    [InlineData(TaskStatus.NeedsAttention)]
    [InlineData(TaskStatus.Blocked)]
    public void 终态状态_判定为终态(TaskStatus status) => Assert.True(status.IsTerminal());

    [Theory]
    [InlineData(TaskStatus.Pending)]
    [InlineData(TaskStatus.Running)]
    public void 非终态状态_判定为非终态(TaskStatus status) => Assert.False(status.IsTerminal());
}
