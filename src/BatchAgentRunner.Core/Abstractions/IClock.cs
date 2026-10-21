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
