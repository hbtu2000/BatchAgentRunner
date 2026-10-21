using System.Threading;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 凭证切换协作器（A-2 / P-03）：从 <see cref="TaskRunner"/> 下沉的任务边界凭证切换（DESIGN §6）。
/// 请求执行前确保 Live 凭证属于该组合账号；账号变化时「保活回写 + 原子回填」，
/// 失败则停用该账号走既有降级逻辑。
/// </summary>
internal static class TaskCredentialSwitch
{
    public static async Task<bool> EnsureLiveCredentialAsync(TaskRunnerContext ctx, TaskItem task, AgentCombo combo, CancellationToken ct)
    {
        if (ctx.Vault is null)
        {
            return true;
        }

        // REQ-021：并发下禁止即时切换 Live 凭证（在飞任务会新旧账号混用）。活动账号已由引擎在派发前固定
        // （EnsurePreferredLiveAccountAsync）与每次统一切换后重建（SwitchAccountAsync 旋转矩阵首选），
        // 故在飞任务始终共用同一账号，无需、也不允许逐任务切换。
        if (ctx.DeferredAccountSwitchEnabled)
        {
            return true;
        }

        var current = ctx.Vault.CurrentLiveAccount(ctx.Options.AgentKind);
        if (string.Equals(current, combo.Account.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            await ctx.Vault.SwitchToAsync(ctx.Options.AgentKind, combo.Account.Id, "task-boundary", ct)
                .ConfigureAwait(false);
            ctx.Raise(new BatchEvent(BatchEventKind.AccountSwitched, ctx.Clock.Now,
                $"凭证已切换：{current ?? "（无）"} → {combo.Account.Label}（任务边界）",
                task.Id, combo.Account.Label, combo.Model.Name));
            ctx.Logger.WriteModelSwitched(current, null, combo.Account.Id, combo.Model.Name, "凭证切换", task.Id);
            // P-10 / A-8：任务边界凭证切换同属「切换」口径（会占用一次真实切换机会）
            ctx.Metrics.RecordSwitch();
            return true;
        }
        catch (CredentialVaultException ex)
        {
            combo.Account.Enabled = false;
            ctx.Raise(new BatchEvent(BatchEventKind.Warning, ctx.Clock.Now,
                $"账号 {combo.Account.Label} 凭证切换失败，已停用并切换下一账号：{ex.Message}",
                task.Id, combo.Account.Label));
            return false;
        }
    }
}
