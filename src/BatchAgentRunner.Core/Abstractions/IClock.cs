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

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        Advance(delay);
        return Task.CompletedTask;
    }
}
