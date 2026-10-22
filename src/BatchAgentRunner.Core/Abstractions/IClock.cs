namespace BatchAgentRunner.Core.Abstractions;

/// <summary>时钟抽象，便于测试时段/冷却逻辑时注入固定时间。</summary>
public interface IClock
{
    /// <summary>当前时刻。</summary>
    DateTimeOffset Now { get; }

    /// <summary>可取消的等待（测试可用假实现跳过真实等待）。</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>基于系统时钟的默认实现（真实运行环境）。</summary>
public sealed class SystemClock : IClock
{
    /// <summary>当前本地时刻。</summary>
    public DateTimeOffset Now => DateTimeOffset.Now;

    /// <summary>可取消的等待（直接委派 <c>Task.Delay</c>）。</summary>
    /// <param name="delay">等待时长（真实时间轴）。</param>
    /// <param name="cancellationToken">取消令牌，触发时终止等待。</param>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, cancellationToken);
}
