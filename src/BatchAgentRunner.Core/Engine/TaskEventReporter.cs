using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 任务事件上报协作器（A-2 / P-03）：从 <see cref="TaskRunner"/> 下沉的「切换 / 跳过」事件与日志埋点。
/// 纯上报，不持有状态，文案与埋点与原实现逐字节一致。
/// </summary>
internal static class TaskEventReporter
{
    public static void RaiseSkipEvent(TaskRunnerContext ctx, ComboAvailability skipped)
    {
        switch (skipped.Reason)
        {
            case ComboSkipReason.OutsideFreeWindow:
                ctx.Raise(new BatchEvent(BatchEventKind.ModelWindowSkipped, ctx.Clock.Now, skipped.DescribeSkip(),
                    null, skipped.Combo.Account.Label, skipped.Combo.Model.Name));
                ctx.Logger.WriteWindowSkipped(skipped.Combo.Model.Name, skipped.NextWindowStart);
                // P-10 / A-8：免费时段外跳过是预期调度行为——计「跳过」不计失败
                ctx.Metrics.RecordWindowSkip();
                ctx.Report.AddTimeline("时段外跳过", skipped.DescribeSkip(), null, null, skipped.Combo.Model.Name);
                break;
            case ComboSkipReason.Cooldown:
                ctx.Raise(new BatchEvent(BatchEventKind.ModelCooldownSkipped, ctx.Clock.Now, skipped.DescribeSkip(),
                    null, skipped.Combo.Account.Label, skipped.Combo.Model.Name));
                if (skipped.ResetAt.HasValue)
                {
                    ctx.Logger.WriteCooldownSkipped(skipped.Combo.Account.Id, skipped.Combo.Model.Name,
                        skipped.ResetAt.Value);
                }

                // P-10 / A-8：额度冷却期跳过同为预期行为——计「跳过」不计失败
                ctx.Metrics.RecordCooldownSkip();

                ctx.Report.AddTimeline("冷却跳过", skipped.DescribeSkip(), null, skipped.Combo.Account.Label,
                    skipped.Combo.Model.Name);
                break;
        }
    }

    public static void RaiseSwitchEvents(TaskRunnerContext ctx, AgentCombo from, AgentCombo to, string? taskId)
    {
        // P-10 / A-8：降级切换计数（误切换率的分母）。账号切换与模型切换各计一次真实切换；
        // 同一组合不变时不计数（降级矩阵未产出新组合，不构成切换）。
        var switched = !string.Equals(from.Account.Id, to.Account.Id, StringComparison.OrdinalIgnoreCase) ||
                       !string.Equals(from.Model.Name, to.Model.Name, StringComparison.OrdinalIgnoreCase);
        if (switched)
        {
            ctx.Metrics.RecordSwitch();
        }

        if (!string.Equals(from.Account.Id, to.Account.Id, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Raise(new BatchEvent(BatchEventKind.AccountSwitched, ctx.Clock.Now,
                $"账号额度耗尽，已切换：{from.Account.Label} → {to.Account.Label}",
                taskId, to.Account.Label, to.Model.Name));
            ctx.Logger.WriteModelSwitched(from.Account.Label, from.Model.Name, to.Account.Label, to.Model.Name,
                "账号切换", taskId);
        }
        else if (!string.Equals(from.Model.Name, to.Model.Name, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Raise(new BatchEvent(BatchEventKind.ModelSwitched, ctx.Clock.Now,
                $"模型 {from.Model.Name} 免费额度已用尽，已自动切换至 {to.Model.Name}，当前任务继续执行",
                taskId, to.Account.Label, to.Model.Name));
            ctx.Logger.WriteModelSwitched(from.Account.Label, from.Model.Name, to.Account.Label, to.Model.Name,
                "额度耗尽", taskId);
        }

        ctx.Report.AddTimeline("切换", $"{from} → {to}", taskId, to.Account.Label, to.Model.Name);
    }
}
