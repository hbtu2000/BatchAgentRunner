using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BatchAgentRunner.Desktop.Services;
using BatchAgentRunner.Desktop.Tests.Helpers;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Services;

/// <summary>
/// P1-19：线程收敛运行时契约（P0-6 / P0-7 的最终验证）。
/// 引擎事件在后台线程触发、VM 的 <c>ConfigureAwait(false)</c> 后续链路不在 UI 线程，凡触碰 UI 状态（集合 / 绑定属性）
/// 的写入必须经 <see cref="UiThread"/> 收敛到 UI 线程。本测试在 headless（<c>Application.Current</c> 已初始化、
/// UI 线程消息循环运行中）下验证 <see cref="UiThread"/> 确实把动作 marshal 到 UI 线程执行。
/// 无 Application 时的同步降级分支由既有非 headless VM 测试（如 ConfigViewModelTests.SaveAsync）锁死——
/// 那些测试在 Application.Current 为空时仍断言属性/配置正确回写，证明降级路径不崩且语义正确。
/// </summary>
public class UiThreadContractTests : DesktopHeadlessTestBase
{
    [AvaloniaFact]
    public async Task InvokeAsync_动作在UI线程执行()
    {
        // 测试体本身运行在 headless UI 线程上
        Assert.True(Dispatcher.UIThread.CheckAccess());

        var ranOnUiThread = await UiThread.InvokeAsync(async () => Dispatcher.UIThread.CheckAccess());
        Assert.True(ranOnUiThread);
    }

    [AvaloniaFact]
    public async Task Post_后台线程调用的动作被调度到UI线程执行()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(() =>
        {
            // 后台线程调用 Post：必须被 marshal 回 UI 线程执行（而非在调用线程同步执行）
            UiThread.Post(() => tcs.TrySetResult(Dispatcher.UIThread.CheckAccess()));
        });

        var ranOnUiThread = await tcs.Task;
        Assert.True(ranOnUiThread);
    }
}
