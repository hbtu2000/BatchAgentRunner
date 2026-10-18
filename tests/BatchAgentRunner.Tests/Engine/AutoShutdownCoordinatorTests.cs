using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Tests.Engine;

/// <summary>
/// P0-10 不变量：自动关机必须先经过可撤销倒计时，任何「一步到位直接关机」的路径都是缺陷。
/// 这里把 REQ-015 的三条硬约束（仅 Completed 触发 / 倒计时可取消 / 异常不出机）锁成断言。
/// </summary>
[Trait("Category", "Invariant")]
public class AutoShutdownCoordinatorTests
{
    private sealed class FakeShutdownService : IShutdownService
    {
        public bool Supported { get; set; } = true;

        public bool ShutdownResult { get; set; } = true;

        public int ShutdownCalls { get; private set; }

        public bool IsSupported => Supported;

        public Task<bool> TryShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCalls++;
            return Task.FromResult(ShutdownResult);
        }
    }

    private static BatchReport Report(BatchStatus status, int failed = 0) =>
        new() { BatchStatus = status, Failed = failed };

    private static AutoShutdownConfig Config(bool enabled = true) => new() { Enabled = enabled, CountdownSeconds = 60 };

    private static Task<bool> Confirmed(int seconds, CancellationToken ct)
    {
        Assert.Equal(60, seconds); // 倒计时秒数必须按配置透传给窗口
        return Task.FromResult(true);
    }

    [Fact]
    public async Task 倒计时被用户取消时不执行关机()
    {
        var service = new FakeShutdownService();
        var coordinator = new AutoShutdownCoordinator(service);

        var outcome = await coordinator.RunAsync(Config(), Report(BatchStatus.Completed),
            (_, _) => Task.FromResult(false));

        Assert.False(outcome.Executed);
        Assert.Contains("取消", outcome.Reason);
        Assert.Equal(0, service.ShutdownCalls);
    }

    [Fact]
    public async Task 倒计时走完才执行关机()
    {
        var service = new FakeShutdownService();
        var coordinator = new AutoShutdownCoordinator(service);

        var outcome = await coordinator.RunAsync(Config(), Report(BatchStatus.Completed), Confirmed);

        Assert.True(outcome.Executed);
        Assert.Equal(1, service.ShutdownCalls);
    }

    [Theory]
    [InlineData(BatchStatus.Terminated)]
    [InlineData(BatchStatus.Blocked)]
    [InlineData(BatchStatus.Waiting)]
    [InlineData(BatchStatus.Running)]
    public async Task 非完成态一律不触发关机(BatchStatus status)
    {
        var service = new FakeShutdownService();
        var coordinator = new AutoShutdownCoordinator(service);

        var outcome = await coordinator.RunAsync(Config(), Report(status), Confirmed);

        Assert.False(outcome.Executed);
        Assert.Equal(0, service.ShutdownCalls);
    }

    [Fact]
    public async Task 勾选仅零失败且有失败任务时不关机()
    {
        var service = new FakeShutdownService();
        var config = new AutoShutdownConfig { Enabled = true, OnlyWhenZeroFailure = true, CountdownSeconds = 60 };
        var coordinator = new AutoShutdownCoordinator(service);

        var outcome = await coordinator.RunAsync(config, Report(BatchStatus.Completed, failed: 2), Confirmed);

        Assert.False(outcome.Executed);
        Assert.Contains("仅失败率为 0", outcome.Reason);
        Assert.Equal(0, service.ShutdownCalls);
    }

    [Fact]
    public async Task 未启用时不进入关机流程()
    {
        var service = new FakeShutdownService();
        var coordinator = new AutoShutdownCoordinator(service);

        var outcome = await coordinator.RunAsync(Config(enabled: false), Report(BatchStatus.Completed), Confirmed);

        Assert.False(outcome.Executed);
        Assert.Equal(0, service.ShutdownCalls);
    }

    [Fact]
    public async Task 平台不支持时降级为手动关机提示()
    {
        var service = new FakeShutdownService { Supported = false };
        var coordinator = new AutoShutdownCoordinator(service);

        var outcome = await coordinator.RunAsync(Config(), Report(BatchStatus.Completed), Confirmed);

        Assert.False(outcome.Executed);
        Assert.Contains("不支持", outcome.Reason);
        Assert.Equal(0, service.ShutdownCalls);
    }

    [Fact]
    public async Task 倒计时窗口异常时不关机()
    {
        var service = new FakeShutdownService();
        var coordinator = new AutoShutdownCoordinator(service);

        var outcome = await coordinator.RunAsync(Config(), Report(BatchStatus.Completed),
            (_, _) => throw new InvalidOperationException("窗口创建失败"));

        Assert.False(outcome.Executed);
        Assert.Equal(0, service.ShutdownCalls);
    }

    [Fact]
    public async Task 倒计时期间批处理被终止时不关机()
    {
        var service = new FakeShutdownService();
        var coordinator = new AutoShutdownCoordinator(service);

        var outcome = await coordinator.RunAsync(Config(), Report(BatchStatus.Completed),
            (_, _) => throw new OperationCanceledException());

        Assert.False(outcome.Executed);
        Assert.Equal(0, service.ShutdownCalls);
    }

    [Fact]
    public async Task 倒计时秒数为零时按无需等待处理()
    {
        var service = new FakeShutdownService();
        var config = new AutoShutdownConfig { Enabled = true, CountdownSeconds = 0 };
        var coordinator = new AutoShutdownCoordinator(service);
        var passedSeconds = -1;

        // 是否弹窗由调用方（倒计时窗口）按秒数决定；协调器只负责把配置原样透传
        var outcome = await coordinator.RunAsync(config, Report(BatchStatus.Completed), (seconds, _) =>
        {
            passedSeconds = seconds;
            return Task.FromResult(true);
        });

        Assert.Equal(0, passedSeconds);
        Assert.True(outcome.Executed);
        Assert.Equal(1, service.ShutdownCalls);
    }

    [Fact]
    public async Task 关机指令失败时标记未执行并记录降级原因()
    {
        var service = new FakeShutdownService { ShutdownResult = false };
        var coordinator = new AutoShutdownCoordinator(service);

        var outcome = await coordinator.RunAsync(Config(), Report(BatchStatus.Completed), Confirmed);

        Assert.True(outcome.Requested);
        Assert.False(outcome.Executed);
        Assert.Contains("权限不足", outcome.Reason);
    }
}
