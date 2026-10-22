using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 自动关机协调器（REQ-015；技术债务 P0-10）。
/// 原实现在 <c>EngineHost.MaybeShutdownAsync</c> 里判定触发条件后直接调用系统关机指令，
/// 跳过 REQ-015 要求的「60 秒可撤销倒计时」，用户在途工作会被强关。
/// 收敛为一处的收益：① 触发条件与倒计时/取消判定下沉到 Core，可被单测锁定；
/// ② Desktop 只负责倒计时窗口的界面呈现，不再承担策略判定。
/// 安全取向：任何异常、任何不确定性（含倒计时窗口不可用）一律**不关机**。
/// </summary>
public sealed class AutoShutdownCoordinator
{
    private readonly IShutdownService _shutdownService;

    /// <summary>构造自动关机协调者（关机能力经端口注入，平台不支持时优雅降级）。</summary>
    /// <param name="shutdownService">平台关机能力端口，不可用时协调器降级为不关机。</param>
    public AutoShutdownCoordinator(IShutdownService shutdownService) => _shutdownService = shutdownService;

    /// <summary>
    /// 按 REQ-015 执行「条件判定 → 可撤销倒计时 → 关机」，产出写入报告的 <see cref="ShutdownOutcome"/>。
    /// </summary>
    /// <param name="config">自动关机配置（是否启用、仅零失败、倒计时秒数等）。</param>
    /// <param name="report">已完成的批次报告——批次状态与失败数是触发条件的数据源。</param>
    /// <param name="confirmAsync">倒计时确认：入参为倒计时秒数（0 = 无需等待）。返回 true = 倒计时走完且未被用户取消；false = 用户取消。</param>
    /// <param name="cancellationToken">批次取消令牌。</param>
    /// <returns>本次关机决策结果（已关机 / 已跳过及原因 / 用户取消）。</returns>
    public async Task<ShutdownOutcome> RunAsync(AutoShutdownConfig config, BatchReport report,
        Func<int, CancellationToken, Task<bool>> confirmAsync, CancellationToken cancellationToken = default)
    {
        if (!config.Enabled)
        {
            return Skipped("未开启自动关机");
        }

        // 只有「正常完成」才触发：blocked / waiting / terminated / interrupted 均把机器留给使用者
        if (report.BatchStatus != BatchStatus.Completed)
        {
            return Skipped($"批级状态为 {report.BatchStatus}，不满足关机触发条件");
        }

        if (config.OnlyWhenZeroFailure && report.Failed > 0)
        {
            return Skipped($"存在 {report.Failed} 条失败任务，已勾选「仅失败率为 0 时关机」");
        }

        if (!_shutdownService.IsSupported)
        {
            return Skipped("当前平台不支持自动关机，请手动关机");
        }

        var countdown = config.CountdownSeconds > 0 ? config.CountdownSeconds : 0;

        bool proceed;
        try
        {
            proceed = await confirmAsync(countdown, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Skipped("关机倒计时已被批处理终止操作取消");
        }
        catch (Exception ex)
        {
            // 倒计时窗口自身出错时保守处理：宁可不关机，也不能在用户无法撤销的情况下关机
            return Skipped($"倒计时窗口异常，已放弃关机：{ex.Message}");
        }

        if (!proceed)
        {
            return Skipped("用户在倒计时内取消了关机");
        }

        var executed = await _shutdownService.TryShutdownAsync(cancellationToken).ConfigureAwait(false);
        return new ShutdownOutcome
        {
            Requested = true,
            Executed = executed,
            Reason = executed
                ? "批处理正常完成，倒计时结束且未被取消，已触发关机"
                : "权限不足，已降级为提示手动关机"
        };
    }

    private static ShutdownOutcome Skipped(string reason) =>
        new() { Requested = true, Executed = false, Reason = reason };
}
