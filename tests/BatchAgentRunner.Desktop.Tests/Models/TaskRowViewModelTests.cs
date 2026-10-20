using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Models;
using Xunit;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Desktop.Tests.Models;

/// <summary>
/// P1-7：任务列表行 VM 的订阅安全契约。
/// 行 VM 订阅长生命周期 <see cref="TaskItem"/> 的 <see cref="TaskItem.PropertyChanged"/> 以驱动自身刷新，
/// 但 <see cref="TaskItem"/> 由引擎持有贯穿整轮批处理；若不显式退订，被集合移除的旧行 VM 会被永久拽在内存里。
/// 本测试锁死「<see cref="TaskRowViewModel.Dispose"/> 必须解除订阅（即补上缺失的 <c>-=</c>）」这一不变量。
/// </summary>
public class TaskRowViewModelTests
{
    [Fact]
    public void Dispose_取消订阅后_领域对象变更不再驱动本行刷新()
    {
        var item = new TaskItem { Id = "t1", Status = TaskStatus.Pending };
        var row = new TaskRowViewModel(item);

        var rowRaised = false;
        row.PropertyChanged += (_, _) => rowRaised = true;

        // 订阅生效：领域对象变化 → 本行 Refresh() → 本行 PropertyChanged 被触发。
        item.Status = TaskStatus.Running;
        Assert.True(rowRaised);

        // 退订后：领域对象变化不再驱动已处置的本行（即 -= 已生效）。
        rowRaised = false;
        row.Dispose();
        item.Status = TaskStatus.Done;
        Assert.False(rowRaised);
    }

    [Fact]
    public void Dispose_可重复调用_幂等且安全()
    {
        var item = new TaskItem { Id = "t1", Status = TaskStatus.Pending };
        var row = new TaskRowViewModel(item);

        row.Dispose();
        var ex = Record.Exception(() => row.Dispose());
        Assert.Null(ex);

        // 二次退订后再变更领域对象也不应驱动本行。
        var rowRaised = false;
        row.PropertyChanged += (_, _) => rowRaised = true;
        item.Status = TaskStatus.Failed;
        Assert.False(rowRaised);
    }

    [Fact]
    public void 反复重建_旧行退订后_领域对象仅驱动最新行_无订阅累积()
    {
        // 复刻 MonitorViewModel.SyncTasks 的「清空 + 重建」模式：每次重建若不处置旧行，
        // 旧行仍被 item 持有（订阅累积 → 内存泄漏）。此处验证处置后旧行已脱离订阅链。
        var item = new TaskItem { Id = "t1", Status = TaskStatus.Pending };

        var rowV1 = new TaskRowViewModel(item);
        var v1Raised = false;
        rowV1.PropertyChanged += (_, _) => v1Raised = true;

        // 模拟 SyncTasks 清空：先处置旧行，再建新行。
        rowV1.Dispose();
        var rowV2 = new TaskRowViewModel(item);

        var v2Raised = false;
        rowV2.PropertyChanged += (_, _) => v2Raised = true;

        item.Status = TaskStatus.Running;

        // 仅最新行（V2）被驱动；已处置的 V1 不再被驱动 → 无累积订阅。
        Assert.True(v2Raised);
        Assert.False(v1Raised);
    }
}
