namespace BatchAgentRunner.Core.Abstractions;

/// <summary>时钟抽象，便于测试时段/冷却逻辑时注入固定时间。</summary>
public interface IClock
{
    DateTimeOffset Now { get; }

    /// <summary>可取消的等待（测试可用假实现跳过真实等待）。</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, cancellationToken);
}

/// <summary>测试用：可推进的假时钟。</summary>
public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset? now = null) => _now = now ?? DateTimeOffset.Now;

    public DateTimeOffset Now => _now;

    public void Advance(TimeSpan delta) => _now += delta;

    public void Set(DateTimeOffset now) => _now = now;

    /// <summary>
    /// <see cref="DelayAsync"/> 是否自动推进时间（默认 true，向后兼容既有测试）。
    /// 置 false 时延迟任务永不完成（除非取消）——「冻结时间」模式：
    /// 等待循环会一直挂起，测试可确定性地观察/取消等待态，再手动 <see cref="Advance"/> 放行。
    /// </summary>
    public bool AutoAdvance { get; init; } = true;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (AutoAdvance)
        {
            Advance(delay);
            return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.CanBeCanceled)
        {
            // 注册随 CTS 生命周期保留；取消时完成延迟任务（引擎等待循环另有 _wake 唤醒路径，双保险）
            cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs);
        }

        return tcs.Task;
    }
}
