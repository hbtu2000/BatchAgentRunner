using System.Collections.Generic;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 持久化协调者（P-02 / ARCH §3.3 + A-7）：从 <see cref="BatchEngine"/> 抽出「落盘单写者 / 读改写同锁 /
/// 冷却记录加载与落盘 / 任务文件加载与备份 / ID 快照校验」全部逻辑，使 facade 退化为「按序编排协作者」。
/// 独占写门（<see cref="_writeGate"/>）与持久化门（<see cref="_persistGate"/>），对外暴露
/// <see cref="LoadCooldownsAsync"/> / <see cref="LoadTasksAsync"/> / <see cref="PersistAsync"/> /
/// <see cref="EnterWriteGateAsync"/> / <see cref="SaveCooldownsAsync"/>，facade 仅委托调用。
/// </summary>
/// <remarks>
/// 公开行为（落盘字节、序列化时机、ID 快照告警）与抽离前逐字节等价：仅把状态与读写体从引擎搬迁至此，
/// 无业务语义改动。所有对外依赖经构造注入，与 <see cref="WaitCoordinator"/> / <see cref="TaskDispatcher"/>
/// 同范式，便于单测替换。
/// </remarks>
public sealed class BatchPersistence : IDisposable
{
    private readonly ITaskFileStore _taskStore;
    private readonly ICooldownStore _cooldownStore;
    private readonly IRunLogger _logger;
    private readonly QuotaCooldownRegistry _cooldowns;
    private readonly IClock _clock;
    private readonly RunOptions _options;
    private readonly List<TaskItem> _tasks;
    private readonly Action _tasksChanged;
    private readonly Action<BatchEvent> _raise;

    /// <summary>任务状态写入区（P0-4「读改写同锁」）。用 <see cref="SemaphoreSlim"/> 而非 <c>lock</c>：落盘序列化必须在持锁期间 <c>await</c>。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    /// <summary>持久化单写者门（P0-4）：同一时刻只允许一个写者落盘任务文件。</summary>
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private HashSet<string>? _idSnapshot;

    /// <summary>构造持久化协作者（写门/持久化门自此协作者独占，由 <see cref="BatchEngine"/> 装配与释放）。</summary>
    public BatchPersistence(
        ITaskFileStore taskStore,
        ICooldownStore cooldownStore,
        IRunLogger logger,
        QuotaCooldownRegistry cooldowns,
        IClock clock,
        RunOptions options,
        List<TaskItem> tasks,
        Action tasksChanged,
        Action<BatchEvent> raise)
    {
        _taskStore = taskStore;
        _cooldownStore = cooldownStore;
        _logger = logger;
        _cooldowns = cooldowns;
        _clock = clock;
        _options = options;
        _tasks = tasks;
        _tasksChanged = tasksChanged;
        _raise = raise;
    }

    /// <summary>加载冷却记录（重启后仍有效）。对应原 <see cref="BatchEngine.InitializeAsync"/> 的冷却恢复分支。</summary>
    public async Task LoadCooldownsAsync(CancellationToken cancellationToken = default)
    {
        var records = await _cooldownStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var r in records)
        {
            _cooldowns.Record(r.AccountId, r.ModelName, r.ResetAt, _clock.Now, r.RawValue);
        }

        if (records.Count > 0)
        {
            _raise(new BatchEvent(BatchEventKind.Info, _clock.Now,
                $"已加载 {records.Count} 条额度冷却记录（重启后仍然有效）"));
        }
    }

    /// <summary>加载任务文件（REQ-001 / REQ-001a）。对应原 <see cref="BatchEngine.LoadTasksAsync"/>。</summary>
    public async Task<IReadOnlyList<TaskItem>> LoadTasksAsync(CancellationToken cancellationToken = default)
    {
        var result = await _taskStore.LoadAsync(_options.TaskFilePath, cancellationToken).ConfigureAwait(false);

        using (await EnterWriteGateAsync().ConfigureAwait(false))
        {
            _tasks.Clear();
            _tasks.AddRange(result.Tasks);
        }

        foreach (var issue in result.Issues)
        {
            _raise(new BatchEvent(
                string.Equals(issue.Level, "error", StringComparison.OrdinalIgnoreCase)
                    ? BatchEventKind.Warning
                    : BatchEventKind.Info,
                _clock.Now, issue.Message, issue.TaskId));
        }

        _tasksChanged();
        return _tasks;
    }

    /// <summary>任务文件备份（启动前，保留最近 N 份）。对应原 <see cref="BatchEngine.StartAsync"/> 的备份分支。</summary>
    /// <param name="cancellationToken">批次取消令牌（复制大文件时可中断）。</param>
    public async Task BackupAsync(CancellationToken cancellationToken = default) =>
        await _taskStore.BackupAsync(_options.TaskFilePath, Math.Max(0, _options.BackupRetention), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 冷却记录落盘（尽力而为，失败仅告警）。对应原 <see cref="BatchEngine"/> 的 <c>SaveCooldownsAsync</c>。
    /// </summary>
    public async Task SaveCooldownsAsync()
    {
        try
        {
            await _cooldownStore.SaveAsync(_cooldowns.Snapshot(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Write(RunLogLevels.Warn, $"冷却记录落盘失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 任务文件回写（P0-4 单写者）：持久化门串行化写入 + 写门内序列化（读改写同锁）。
    /// 对应原 <c>BatchEngine.PersistAsync</c>（P-02 随持久化职责一并下沉）。
    /// </summary>
    public async Task PersistAsync(CancellationToken ct)
    {
        await _persistGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using (await EnterWriteGateAsync().ConfigureAwait(false))
            {
                await _taskStore.SaveAsync(_options.TaskFilePath, _tasks, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Write(RunLogLevels.Error, $"任务文件回写失败：{ex.Message}");
        }
        finally
        {
            _persistGate.Release();
        }
    }

    /// <summary>
    /// 进入任务状态写入区（P0-4）。不可取消：等待期间的取消会导致终态丢失，故固定用
    /// <see cref="CancellationToken.None"/>，由整个批处理的取消入口统一收敛。
    /// 对应原 <c>BatchEngine.EnterWriteGateAsync</c>（P-02 下沉）。
    /// </summary>
    public async Task<IDisposable> EnterWriteGateAsync()
    {
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        return new GateReleaser(_writeGate);
    }

    /// <summary>ID 快照校验（REQ-001a）：检测已完成任务 ID 消失/变更，告警可能重跑。对应原 <c>BatchEngine.CheckIdSnapshot</c>（P-02 下沉）。</summary>
    public void CheckIdSnapshot()
    {
        var current = _tasks.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_idSnapshot is null)
        {
            _idSnapshot = current;
            return;
        }

        var doneIds = _tasks.Where(t => t.Status == TaskStatus.Done).Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = _idSnapshot.Where(id => doneIds.Contains(id) && !current.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            _raise(new BatchEvent(BatchEventKind.IdSnapshotWarning, _clock.Now,
                $"检测到已完成任务 ID 消失或变更（{string.Join("、", missing.Take(5))}），若任务被重排可能导致重跑，请确认",
                null)
            { Detail = string.Join(",", missing) });
        }

        _idSnapshot = current;
    }

    /// <summary>释放两道 <see cref="SemaphoreSlim"/> 门（CA1001）。</summary>
    public void Dispose()
    {
        _writeGate.Dispose();
        _persistGate.Dispose();
    }

    private sealed class GateReleaser(SemaphoreSlim gate) : IDisposable
    {
        /// <summary>释放写闸（等价 using 作用域结束时让出写入权）。</summary>
        public void Dispose() => gate.Release();
    }
}
