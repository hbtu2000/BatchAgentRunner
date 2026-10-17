using Avalonia.Threading;

namespace BatchAgentRunner.Desktop.Services;

/// <summary>
/// UI 线程收敛入口（技术债务 P0-6 / P0-7）。
/// 引擎事件在后台线程触发、VM 的 <c>ConfigureAwait(false)</c> 后续链路也不在 UI 线程上，
/// 过去由「订阅方自行切线程」的约定保证安全（7 处各写一遍 <c>Dispatcher.UIThread.Post</c>），
/// 必漏写必踩坑。现在统一要求：凡是从后台线程触碰 UI 状态（集合 / 绑定属性），一律经本类，
/// 不再依赖调用方记得 Marshal。
/// </summary>
public static class UiThread
{
    /// <summary>
    /// 在 UI 线程执行 <paramref name="action"/>：已在 UI 线程则同步执行（保持调用处的顺序语义），
    /// 否则投递到 UI 线程队列后立即返回。
    /// </summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    /// <summary>切到 UI 线程执行异步操作（如模态对话框），等待其内部 Task 完成。</summary>
    public static Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> asyncAction)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);

        if (Dispatcher.UIThread.CheckAccess())
        {
            return asyncAction();
        }

        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.SetResult(await asyncAction().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return completion.Task;
    }
}
