using BatchAgentRunner.Core.Engine;
using Xunit;

namespace BatchAgentRunner.Tests.Scheduling;

/// <summary>
/// 并行调度协调器单元测试：锁定 REQ-020（按工作空间串行 / 并行度上界）与
/// REQ-021（并行下统一切换：冻结 + 派发暂停 + 统一切换触发）的核心不变量。
/// 协调器为纯内存态、线程安全，不依赖引擎或适配器，可确定性强。
/// </summary>
[Trait("Category", "Invariant")]
public class DispatchCoordinatorTests
{
    // ---------------- REQ-020：按工作空间串行 ----------------

    [Fact]
    public void CanDispatch_同工作空间已有在飞_拒绝第二个()
    {
        var c = new DispatchCoordinator();
        c.RegisterInFlight("t1", "ws-a");

        Assert.False(c.CanDispatch("ws-a", distinctPendingWorkspaceCount: 2, concurrency: 2));
        Assert.True(c.CanDispatch("ws-b", distinctPendingWorkspaceCount: 2, concurrency: 2));
    }

    [Fact]
    public void CanDispatch_并行度上界取并发与不同工作空间数较小者()
    {
        var c = new DispatchCoordinator();
        c.RegisterInFlight("t1", "ws-a");
        // 仅剩 ws-a 一个待派发工作空间：cap = min(2, 1) = 1，已达上限 → 即便不同工作空间也不派发
        Assert.Equal(1, c.InFlightWorkspaceCount);
        Assert.False(c.CanDispatch("ws-b", distinctPendingWorkspaceCount: 1, concurrency: 2));
    }

    [Fact]
    public void CanDispatch_并发为1_整体串行()
    {
        var c = new DispatchCoordinator();
        c.RegisterInFlight("t1", "ws-a");
        // concurrency = 1 → cap = min(1, 2) = 1，即便不同工作空间也不并行
        Assert.False(c.CanDispatch("ws-b", distinctPendingWorkspaceCount: 2, concurrency: 1));
    }

    [Fact]
    public void RegisterInFlight_ReleaseInFlight_同工作空间最后一个结束后释放槽位()
    {
        var c = new DispatchCoordinator();
        c.RegisterInFlight("t1", "ws-a");
        c.RegisterInFlight("t2", "ws-a");
        Assert.Equal(1, c.InFlightWorkspaceCount);

        c.ReleaseInFlight("t1");
        Assert.Equal(1, c.InFlightWorkspaceCount); // t2 仍在 ws-a → 槽位保留
        c.ReleaseInFlight("t2");
        Assert.Equal(0, c.InFlightWorkspaceCount);
    }

    [Fact]
    public void CanDispatch_空字符串与默认工作空间桶视为同一桶()
    {
        var c = new DispatchCoordinator();
        c.RegisterInFlight("t1", "(default)");
        // 未指定工作空间的任务归一化为 "(default)"，与显式空串/默认桶互斥，保证默认桶串行
        Assert.False(c.CanDispatch("", distinctPendingWorkspaceCount: 1, concurrency: 2));
        Assert.False(c.CanDispatch("(default)", distinctPendingWorkspaceCount: 1, concurrency: 2));
    }

    // ---------------- REQ-021：并行下统一切换 ----------------

    [Fact]
    public void RequestDeferredSwitch_冻结槽位并进入派发暂停且记录目标()
    {
        var c = new DispatchCoordinator();
        c.RequestDeferredSwitch("t1", "acc2");

        Assert.True(c.DispatchHalted);
        Assert.Equal("acc2", c.PendingSwitchTarget);
        Assert.Contains("t1", c.FrozenSlots);
        // 派发暂停期间一切新派发被拒
        Assert.False(c.CanDispatch("ws-a", distinctPendingWorkspaceCount: 2, concurrency: 2));
    }

    [Fact]
    public void RequestDeferredSwitch_多任务冻结仅保留首个目标()
    {
        var c = new DispatchCoordinator();
        c.RequestDeferredSwitch("t1", "acc2");
        c.RequestDeferredSwitch("t2", "acc3"); // 不同账号 → 唯一性论证：仅首个目标生效

        Assert.Equal("acc2", c.PendingSwitchTarget);
    }

    [Fact]
    public void ReleaseInFlight_在飞降为0时返回统一切换目标并清除暂停()
    {
        var c = new DispatchCoordinator();
        c.RegisterInFlight("t1", "ws-a");
        c.RequestDeferredSwitch("t1", "acc2");

        var target = c.ReleaseInFlight("t1");

        Assert.Equal("acc2", target);
        Assert.False(c.DispatchHalted);
        Assert.Null(c.PendingSwitchTarget);
        Assert.Empty(c.FrozenSlots);
    }

    [Fact]
    public void ReleaseInFlight_多冻结槽位仅在最后一个结束后触发一次统一切换()
    {
        var c = new DispatchCoordinator();
        c.RegisterInFlight("t1", "ws-a");
        c.RegisterInFlight("t2", "ws-b");
        c.RequestDeferredSwitch("t1", "acc2");
        c.RequestDeferredSwitch("t2", "acc2");

        // 先释放 t1：t2 仍在飞 → 不触发
        Assert.Null(c.ReleaseInFlight("t1"));
        Assert.True(c.DispatchHalted);

        // 再释放 t2：在飞降为 0 → 仅触发一次，返回目标并解除暂停
        Assert.Equal("acc2", c.ReleaseInFlight("t2"));
        Assert.False(c.DispatchHalted);
        Assert.Empty(c.FrozenSlots);

        // 解除后派发恢复
        Assert.True(c.CanDispatch("ws-c", distinctPendingWorkspaceCount: 1, concurrency: 2));
    }
}
