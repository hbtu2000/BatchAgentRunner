using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 凭证切换协调者（P-02 / ARCH §3.3 + A-7）：从 <see cref="BatchEngine"/> 抽出「任务边界凭证切换 /
/// 并行下统一切换（REQ-021）/ 并行启动前固定活动账号 / 账号旋转置首」全部逻辑，使 facade 退化为
/// 「按序编排协作者」。对外暴露 <see cref="EnsureLiveCredentialAsync"/> / <see cref="SwitchAccountAsync"/> /
/// <see cref="EnsurePreferredLiveAccountAsync"/>，facade 经协作者注入到单任务/派发/工作流上下文。
/// </summary>
/// <remarks>
/// 公开行为（切换时机、事件/时间线语义、降级停用路径）与抽离前逐字节等价：仅把状态与切换体搬迁至此，
/// 无业务语义改动。降级矩阵重建经 <see cref="AccountSwitchContext.RebuildMatrix"/> 委托回引擎
/// （矩阵仍是引擎持有的共享可变状态），账号旋转直接改 <see cref="RunOptions.Accounts"/>（共享引用）。
/// </remarks>
public sealed class AccountSwitchCoordinator
{
    private readonly AccountSwitchContext _ctx;

    public AccountSwitchCoordinator(AccountSwitchContext context) => _ctx = context;

    /// <summary>
    /// 任务边界凭证切换（DESIGN §6）：确保 Live 凭证属于该组合账号；账号变化时执行保活回写 + 原子回填，
    /// 失败则停用该账号走既有降级逻辑。REQ-021 并发下禁用即时切换（延后由协调器统一调度）。
    /// </summary>
    public async Task<bool> EnsureLiveCredentialAsync(TaskItem task, AgentCombo combo, CancellationToken ct)
    {
        if (_ctx.Vault is null)
        {
            return true;
        }

        var current = _ctx.Vault.CurrentLiveAccount(_ctx.Options.AgentKind);
        if (string.Equals(current, combo.Account.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // REQ-021：并发下禁止即时切换 Live 凭证（否则在飞任务会新旧账号混用）；延后切换由协调器统一调度。
        if (_ctx.Options.Concurrency > 1)
        {
            return true;
        }

        try
        {
            await _ctx.Vault.SwitchToAsync(_ctx.Options.AgentKind, combo.Account.Id, "task-boundary", ct)
                .ConfigureAwait(false);
            _ctx.Raise(new BatchEvent(BatchEventKind.AccountSwitched, _ctx.Clock.Now,
                $"凭证已切换：{current ?? "（无）"} → {combo.Account.Label}（任务边界）",
                task.Id, combo.Account.Label, combo.Model.Name));
            _ctx.Logger.WriteModelSwitched(current, null, combo.Account.Id, combo.Model.Name, "凭证切换", task.Id);
            return true;
        }
        catch (CredentialVaultException ex)
        {
            combo.Account.Enabled = false;
            _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                $"账号 {combo.Account.Label} 凭证切换失败，已停用并切换下一账号：{ex.Message}",
                task.Id, combo.Account.Label));
            return false;
        }
    }

    /// <summary>
    /// 并行下统一账号切换（REQ-021）：由 <see cref="DispatchCoordinator"/> 在「当前在飞任务全部结束后」调用一次。
    /// 执行凭证切换至目标账号，并记 <see cref="BatchEventKind.AccountSwitched"/>（<c>DeferredSwitch=true</c>）
    /// 与报告时间线（<c>deferred_switch=true</c>）。
    /// </summary>
    public async Task SwitchAccountAsync(string targetAccountId, CancellationToken ct)
    {
        if (_ctx.Vault is null)
        {
            return;
        }

        var target = _ctx.Options.Accounts.FirstOrDefault(a =>
            string.Equals(a.Id, targetAccountId, StringComparison.OrdinalIgnoreCase));
        var targetLabel = target?.Label ?? targetAccountId;
        var current = _ctx.Vault.CurrentLiveAccount(_ctx.Options.AgentKind);

        try
        {
            await _ctx.Vault.SwitchToAsync(_ctx.Options.AgentKind, targetAccountId, "deferred-unified", ct)
                .ConfigureAwait(false);
            // REQ-021：目标账号旋转为矩阵首选，后续并行任务自然以目标账号起步（避免 Live 凭证错配）
            RotateAccountToFront(targetAccountId);
            _ctx.RebuildMatrix();
            _ctx.Raise(new BatchEvent(BatchEventKind.AccountSwitched, _ctx.Clock.Now,
                $"并行下统一切换：{current ?? "（无）"} → {targetLabel}（延后至在飞任务全部结束后）",
                null, targetLabel)
            { DeferredSwitch = true });
            _ctx.Logger.WriteModelSwitched(current, null, targetAccountId, null, "并发延后统一切换", null);
            _ctx.Report.AddTimeline("切换", $"{current ?? "（无）"} → {targetLabel}（并发下延后统一切换）",
                null, targetLabel, null, deferredSwitch: true);
        }
        catch (CredentialVaultException ex)
        {
            if (target is not null)
            {
                target.Enabled = false;
            }

            _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                $"并行下统一切换至 {targetLabel} 失败，已停用并转常规降级：{ex.Message}", null, targetLabel));
        }
    }

    /// <summary>
    /// 并行下在派发开始前把 Live 凭证固定到「首个可用」的降级矩阵账号（REQ-021 前提）：若非首选则旋转账号顺序使其成为首选。
    /// </summary>
    public async Task EnsurePreferredLiveAccountAsync(CancellationToken ct)
    {
        if (_ctx.Vault is null || _ctx.Options.Concurrency <= 1)
        {
            return;
        }

        var first = _ctx.MatrixProvider().SelectFirst(_ctx.Clock.Now, _ctx.Evaluator).Combo;
        if (first is null)
        {
            return;
        }

        var current = _ctx.Vault.CurrentLiveAccount(_ctx.Options.AgentKind);
        if (!string.Equals(current, first.Account.Id, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await _ctx.Vault.SwitchToAsync(_ctx.Options.AgentKind, first.Account.Id, "preflight", ct)
                    .ConfigureAwait(false);
                _ctx.Raise(new BatchEvent(BatchEventKind.AccountSwitched, _ctx.Clock.Now,
                    $"并行启动前固定活动账号：{current ?? "（无）"} → {first.Account.Label}", null, first.Account.Label));
            }
            catch (CredentialVaultException ex)
            {
                _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                    $"并行启动前固定活动账号失败：{ex.Message}", null, first.Account.Label));
            }
        }

        RotateAccountToFront(first.Account.Id);
        _ctx.RebuildMatrix();
    }

    /// <summary>
    /// 将指定账号旋转至启用账号列表首位（REQ-021）：统一切换后使目标账号成为降级矩阵首选。
    /// </summary>
    private void RotateAccountToFront(string accountId)
    {
        if (_ctx.Options.Accounts is not { Count: > 1 })
        {
            return;
        }

        var idx = _ctx.Options.Accounts.FindIndex(a =>
            string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (idx <= 0)
        {
            return;
        }

        var account = _ctx.Options.Accounts[idx];
        _ctx.Options.Accounts.RemoveAt(idx);
        _ctx.Options.Accounts.Insert(0, account);
    }
}

/// <summary>凭证切换所需的引擎能力集合（沿用 <see cref="TaskRunnerContext"/> 的上下文注入范式）。</summary>
public sealed class AccountSwitchContext
{
    public RunOptions Options { get; init; } = null!;
    public ICredentialVault? Vault { get; init; }
    public IBatchReportBuilder Report { get; init; } = null!;
    public IRunLogger Logger { get; init; } = null!;
    public IClock Clock { get; init; } = null!;
    public AvailabilityEvaluator Evaluator { get; init; } = null!;
    public Func<DegradationMatrix> MatrixProvider { get; init; } = null!;
    public Action RebuildMatrix { get; init; } = null!;
    public Action<BatchEvent> Raise { get; init; } = null!;
}
