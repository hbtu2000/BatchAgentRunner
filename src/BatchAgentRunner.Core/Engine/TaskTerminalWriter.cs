using System.Threading;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Text;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 任务终态写入协作器（A-2 / P-03）：从 <see cref="TaskRunner"/> 下沉的「终态落盘」纯写操作。
/// 所有方法经 <see cref="TaskRunnerContext"/> 注入端口，不持有状态；与落盘序列化互斥（P0-4）由
/// <see cref="TaskRunnerContext.EnterWriteGate"/> 保证，事件广播保持在锁外。
/// </summary>
internal static class TaskTerminalWriter
{
    public static async Task CompleteTaskAsync(TaskRunnerContext ctx, TaskItem task, AgentExecutionResult result)
    {
        var combo = result.Request;
        using (await ctx.EnterWriteGate().ConfigureAwait(false))
        {
            task.Status = TaskStatus.Done;
            task.Result = TextTruncate.Result(result.Output, ctx.Options.ResultMaxKB * 1024);
            task.ModelUsed = combo.Model.Name;
            task.AccountUsed = combo.Account.Label;
            task.ExpertUsed = ctx.Options.Expert.ToString();
            task.FallbackUsed = false;
            task.Error = null;
            task.Finish(TaskStatus.Done, ctx.Clock.Now);
            ApplyPausedExclusion(ctx, task);
            ctx.Report.TrackUsage(combo.Account.Label, combo.Model.Name, result.Usage);
            // P-10 / A-8：吞吐与平均耗时的数据源（耗时已剔除本任务自身的暂停等待）
            ctx.Metrics.RecordTaskFinished(TaskStatus.Done, task.DurationSeconds);
        }

        ctx.Raise(new BatchEvent(BatchEventKind.TaskFinished, ctx.Clock.Now,
            $"任务 {task.Id} 完成（{task.DurationSeconds:0.#}s，{combo.Account.Label} + {combo.Model.Name}）",
            task.Id, combo.Account.Label, combo.Model.Name)
        { Task = task });
        ctx.Logger.WriteTaskFinished(task.Id, TaskStatus.Done, task.DurationSeconds, task.RetryCount);
        ctx.RaiseTaskChanged(task);
        await ctx.Persist(CancellationToken.None).ConfigureAwait(false);
    }

    public static async Task FailTaskAsync(TaskRunnerContext ctx, TaskItem task, string error)
    {
        using (await ctx.EnterWriteGate().ConfigureAwait(false))
        {
            task.Status = TaskStatus.Failed;
            task.Error = error;
            task.Finish(TaskStatus.Failed, ctx.Clock.Now);
            ApplyPausedExclusion(ctx, task);
            ctx.Metrics.RecordTaskFinished(TaskStatus.Failed, task.DurationSeconds);
        }

        ctx.Raise(new BatchEvent(BatchEventKind.TaskFinished, ctx.Clock.Now,
            $"任务 {task.Id} 失败（重试 {task.RetryCount} 次）：{error}", task.Id)
        { Task = task });
        ctx.Logger.WriteTaskFinished(task.Id, TaskStatus.Failed, task.DurationSeconds, task.RetryCount);
        ctx.RaiseTaskChanged(task);
        await ctx.Persist(CancellationToken.None).ConfigureAwait(false);

        // 二期：任务失败通知（REQ-018；按规则触发，失败不阻断）
        await ctx.Notifier.DispatchAsync(NotifyTrigger.TaskFailed, null, error).ConfigureAwait(false);
    }

    public static async Task MarkInterruptedAsync(TaskRunnerContext ctx, TaskItem task, string reason)
    {
        using (await ctx.EnterWriteGate().ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(task.Result) && !task.HasPartialResult)
            {
                task.Result = TaskItem.PartialResultPrefix + task.Result;
            }

            task.Status = TaskStatus.Interrupted;
            task.Error = reason;
            task.Finish(TaskStatus.Interrupted, ctx.Clock.Now);
            ApplyPausedExclusion(ctx, task);
            ctx.Metrics.RecordTaskFinished(TaskStatus.Interrupted, task.DurationSeconds);
        }

        ctx.Raise(new BatchEvent(BatchEventKind.TaskFinished, ctx.Clock.Now,
            $"任务 {task.Id} 已中断：{reason}", task.Id)
        { Task = task });
        ctx.Logger.WriteTaskFinished(task.Id, TaskStatus.Interrupted, task.DurationSeconds, task.RetryCount);
        ctx.RaiseTaskChanged(task);
        await ctx.Persist(CancellationToken.None).ConfigureAwait(false);
    }

    public static void AppendPartialOutput(TaskRunnerContext ctx, TaskItem task, string output)
    {
        var merged = string.IsNullOrWhiteSpace(task.Result) ? output : task.Result + "\n" + output;
        task.Result = TextTruncate.Result(merged, ctx.Options.ResultMaxKB * 1024);
    }

    /// <summary>
    /// 从任务时长中剔除其自身经历的暂停等待（DESIGN-额度耗尽暂停续跑：暂停时间不计入时长）。
    /// 仅在终止阶段调用，等待只发生在循环内，故本任务必有对应累计。
    /// </summary>
    public static void ApplyPausedExclusion(TaskRunnerContext ctx, TaskItem task)
    {
        double waited;
        lock (ctx.StatsLock)
        {
            if (!ctx.TaskWaitSeconds.TryGetValue(task.Id, out waited) || waited <= 0)
            {
                return;
            }

            ctx.TaskWaitSeconds.Remove(task.Id);
        }

        task.DurationSeconds = Math.Max(0, Math.Round(task.DurationSeconds - waited, 3));
    }
}
