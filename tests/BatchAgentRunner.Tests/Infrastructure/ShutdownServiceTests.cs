using BatchAgentRunner.Infrastructure.Platform;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// P1-15 高危盲区：<see cref="ShutdownService"/> 是自动关机（REQ-015）的唯一执行点，
/// 直接拉起系统 shutdown/osascript 进程。其最易错的「平台判定」逻辑必须经断言锁死：
/// ① Windows/macOS 视为受支持、其它平台不受支持；② 不受支持平台 TryShutdownAsync 直接
/// 返回 false 且不触达任何关机命令（受支持平台跳过本用例，避免测试误关机）。
/// 注意：受支持平台下 TryShutdownAsync 会真正请求关机，绝不在此处构造可触发的用例。
/// </summary>
public class ShutdownServiceTests
{
    [Fact]
    public void IsSupported_与平台判定一致()
    {
        var svc = new ShutdownService();

        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), svc.IsSupported);
    }

    [Fact]
    public async Task TryShutdownAsync_不受支持的平台_返回false且不触达关机()
    {
        // 受支持平台（Windows/macOS）会真正请求关机，跳过以免误关机。
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return;
        }

        var svc = new ShutdownService();

        Assert.False(svc.IsSupported);
        var result = await svc.TryShutdownAsync();
        Assert.False(result);
    }
}
