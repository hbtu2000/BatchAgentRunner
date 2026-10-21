using BatchAgentRunner.Core.Abstractions;

namespace BatchAgentRunner.Tests.Helpers;

/// <summary>
/// 测试用：可推进的假时钟（P2-8 偿还：原驻留生产程序集 <c>Core/Abstractions/IClock.cs</c>——
/// 测试替身随生产程序集分发属「测试代码进生产」，已迁至测试项目；
/// 由 <see cref="LayeringRulesTests"/> 断言生产程序集不得再出现本类型）。
/// </summary>
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
