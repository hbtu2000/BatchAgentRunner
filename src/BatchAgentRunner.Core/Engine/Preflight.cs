using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 启动前预检协作者（P1-1 拆分产物 ⑥，§3.4 <c>IPreflight</c>）：从 <see cref="BatchEngine"/>
/// 抽出「启动前预检（REQ-010）」的全部逻辑——任务文件 / 工作空间校验、DAG 环检测、CLI 可用性、
/// 账号池预检（含 Vault 快照切换）、降级矩阵可用性评估。纯函数式：不持有引擎可变状态，
/// 通过 <see cref="PreflightOutcome"/> 把「报告 + 已加载任务 + 重建后的降级矩阵」一并交还引擎同步。
/// </summary>
public interface IPreflight
{
    /// <summary>
    /// 执行预检。返回报告、已加载任务（供引擎填充 <c>_tasks</c>）与重建后的降级矩阵（供引擎同步 <c>_matrix</c>）。
    /// <see cref="PreflightOutcome.Loaded"/> 为 false 时说明任务文件未成功加载，引擎不应覆盖既有 <c>_tasks</c>。
    /// </summary>
    Task<PreflightOutcome> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>预检结果（P1-1 拆分产物 ⑥）：把引擎需同步的状态一并交还，避免协作者反向写引擎。</summary>
public sealed record PreflightOutcome(
    PreflightReport Report,
    IReadOnlyList<TaskItem> Tasks,
    DegradationMatrix Matrix,
    bool Loaded);

/// <summary>默认实现：机制全部来自构造期注入的端口，无隐藏静态依赖。</summary>
public sealed class StartupPreflight : IPreflight
{
    private readonly RunOptions _options;
    private readonly ITaskFileStore _taskStore;
    private readonly IFileSystem _fileSystem;
    private readonly IAgentCliAdapter _adapter;
    private readonly ICredentialVault? _vault;
    private readonly IClock _clock;
    private readonly QuotaCooldownRegistry _cooldowns;
    private readonly Action<BatchEvent> _raise;
    private readonly Func<string?, string?> _resolveWorkspace;

    public StartupPreflight(
        RunOptions options,
        ITaskFileStore taskStore,
        IFileSystem fileSystem,
        IAgentCliAdapter adapter,
        IClock clock,
        QuotaCooldownRegistry cooldowns,
        Action<BatchEvent> raise,
        Func<string?, string?> resolveWorkspace,
        ICredentialVault? vault = null)
    {
        _options = options;
        _taskStore = taskStore;
        _fileSystem = fileSystem;
        _adapter = adapter;
        _clock = clock;
        _cooldowns = cooldowns;
        _raise = raise;
        _resolveWorkspace = resolveWorkspace;
        _vault = vault;
    }

    public async Task<PreflightOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        var report = new PreflightReport();
        IReadOnlyList<TaskItem> loadedTasks = Array.Empty<TaskItem>();
        var loaded = false;

        if (string.IsNullOrWhiteSpace(_options.TaskFilePath))
        {
            report.AddError("未选择任务文件");
        }
        else if (!_fileSystem.FileExists(_options.TaskFilePath))
        {
            report.AddError($"任务文件不存在：{_options.TaskFilePath}");
        }
        else
        {
            try
            {
                var result = await _taskStore.LoadAsync(_options.TaskFilePath, cancellationToken).ConfigureAwait(false);
                loadedTasks = result.Tasks;
                loaded = true;
                report.LoadedTaskCount = result.Tasks.Count;
                report.PendingTaskCount = result.Tasks.Count(t => t.Status is TaskStatus.Pending or TaskStatus.Interrupted);
                foreach (var issue in result.Issues)
                {
                    report.AddWarning(issue.Message);
                }

                if (loadedTasks.Count == 0)
                {
                    report.AddError("任务文件中没有解析到任何任务");
                }

                // 工作空间校验（REQ-003a）
                foreach (var ws in _options.Workspaces.Where(w => w.IsValid))
                {
                    if (!_fileSystem.DirectoryExists(ws.Path))
                    {
                        report.AddError($"工作空间「{ws.Name}」目录不存在或无访问权限：{ws.Path}");
                    }
                }

                // REQ-019：命名工作空间数量上限（以配置值 max_workspaces 为准，非硬编码）
                var configuredWorkspaces = _options.Workspaces
                    .Where(w => !string.IsNullOrWhiteSpace(w.Name))
                    .ToList();
                if (configuredWorkspaces.Count > _options.MaxWorkspaces)
                {
                    var over = configuredWorkspaces
                        .Skip(_options.MaxWorkspaces)
                        .Select(w => w.Name)
                        .ToList();
                    report.AddError(
                        $"命名工作空间数量（{configuredWorkspaces.Count}）已超过配置上限（{_options.MaxWorkspaces}）；" +
                        $"请精简至 {_options.MaxWorkspaces} 个以内再启动。超限条目：" +
                        string.Join("、", over.Select(n => $"「{n}」")));
                }

                foreach (var t in loadedTasks.Where(t => !string.IsNullOrWhiteSpace(t.Workspace)))
                {
                    var resolved = _resolveWorkspace(t.Workspace);
                    if (resolved is null)
                    {
                        report.AddError($"任务 {t.Id} 引用的工作空间「{t.Workspace}」不存在（命名空间悬空）");
                    }
                }

                // 二期 DAG 预检：环检测（错误，阻止启动）+ 悬空依赖（告警，执行时保守阻断）
                if (loadedTasks.Any(t => t.DependsOn is { Count: > 0 }))
                {
                    try
                    {
                        DagScheduler.TopoSort(loadedTasks);
                    }
                    catch (DagCycleException ex)
                    {
                        report.AddError($"任务依赖存在环：{ex.Message}");
                    }

                    var ids = new HashSet<string>(loadedTasks.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
                    foreach (var t in loadedTasks.Where(t => t.DependsOn is { Count: > 0 }))
                    {
                        foreach (var dep in t.DependsOn!)
                        {
                            if (!ids.Contains(dep))
                            {
                                report.AddWarning($"任务 {t.Id} 的依赖 {dep} 不存在（悬空依赖，执行时将保守阻断）");
                            }
                        }
                    }
                }
            }
            catch (TaskFileFormatException ex)
            {
                report.AddError($"任务文件解析失败：{ex.Message}");
            }
        }

        // CLI 可用性
        var env = await _adapter.ValidateEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        if (!env.Ok)
        {
            report.AddError($"Agent CLI 不可用：{env.Message}");
        }

        // 账号池预检：失效账号自动停用（REQ-010）
        var enabledAccounts = _options.Accounts.Where(a => a.Enabled).ToList();
        if (_vault is not null && _options.Concurrency > 1)
        {
            // DESIGN §3 / REQ-021：并发 > 1 下同一时刻每个 CLI 只有一个登录身份；
            // 已由「统一切换（槽位冻结 + 全局派发暂停 + 在飞全部结束后一次切换）」机制保证不混用，故不再建议降为 1。
            report.AddWarning("已启用凭证 Vault 且并发 > 1：并行任务共用同一活动账号，账号切换将延后至在飞任务全部结束后统一执行（REQ-021）");
        }

        if (enabledAccounts.Count == 0)
        {
            report.AddError("账号池为空：请至少添加一个启用账号");
        }

        foreach (var account in enabledAccounts)
        {
            // 凭证 Vault 集成（DESIGN §7）：预检前先切换到该账号；预检即验证快照有效性。
            if (_vault is not null)
            {
                if (!_vault.SnapshotExists(_options.AgentKind, account.Id))
                {
                    account.Enabled = false;
                    report.DisabledAccounts.Add(account.Label);
                    report.AddWarning($"账号 {account.Label} 尚无凭证快照，已停用：请在配置页对该账号执行「引导登录」");
                    continue;
                }

                try
                {
                    await _vault.SwitchToAsync(_options.AgentKind, account.Id, "preflight", cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CredentialVaultException ex)
                {
                    account.Enabled = false;
                    report.DisabledAccounts.Add(account.Label);
                    report.AddWarning($"账号 {account.Label} 凭证切换失败已停用：{ex.Message}");
                    continue;
                }
            }

            var r = await _adapter.ValidateAccountAsync(account, cancellationToken).ConfigureAwait(false);
            if (!r.Ok)
            {
                account.Enabled = false;
                report.DisabledAccounts.Add(account.Label);
                report.AddWarning($"账号 {account.Label} 预检失败已停用：{r.Message}");
                continue;
            }

            if (_vault is not null)
            {
                try
                {
                    await _vault.MarkVerifiedAsync(_options.AgentKind, account.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CredentialVaultException ex)
                {
                    report.AddWarning($"账号 {account.Label} 快照验证时间更新失败：{ex.Message}");
                }
            }
        }

        if (_options.Fallback.Enabled && _options.Accounts.All(a => !a.Enabled || !a.HasFallbackModels))
        {
            report.AddWarning("已启用兜底，但没有任何启用账号配置兜底模型池（accounts[].fallback_models），兜底不会生效");
        }

        // 专家（REQ-004a）
        if (_options.Expert.IsSpecified && _adapter.Kind == AgentKind.Mock)
        {
            report.AddWarning("当前使用仿真适配器，专家参数不会被真实消费");
        }

        // 重建降级矩阵（与原 PreflightAsync 内 RebuildMatrix 语义一致：反映预检期停用的账号）
        var matrix = new DegradationMatrix(_options.Accounts, _options.ModelCandidates, _options.DegradationStrategy);
        var evaluator = new AvailabilityEvaluator(_cooldowns);

        // 降级矩阵为空 = 所有启用账号既没有自有候选池、全局共享列表也为空（REQ-003，故事 2.1）
        if (matrix.IsEmpty)
        {
            report.AddError("降级矩阵为空：请为至少一个启用账号配置候选模型池（accounts[].model_candidates），或填写全局共享候选模型列表");
        }

        // 时段 / 冷却可用性评估（REQ-003b / REQ-004c）
        var now = _clock.Now;
        if (!matrix.IsEmpty)
        {
            var selection = matrix.SelectFirst(now, evaluator);
            if (!selection.Found)
            {
                report.AllCombosUnavailable = true;
                var earliest = matrix.EarliestRestore(now, evaluator);
                report.EarliestRestoreAt = earliest?.RestoreAt;
                report.WaitKind = earliest?.WaitKind ?? WaitKind.None;
                report.UnavailableReason = earliest is null
                    ? "无可用组合"
                    : string.Join("；", selection.Skipped.Take(3).Select(s => s.DescribeSkip()));
            }
        }

        return new PreflightOutcome(report, loadedTasks, matrix, loaded);
    }
}
