using System.Collections.Generic;
using System.Linq;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 并行调度协调器（REQ-020 / REQ-021）：在内存中维护「按工作空间在飞计数」「全局派发暂停」
/// 「统一切换目标」「冻结槽位」等运行态，作为 <see cref="TaskDispatcher"/> 与 <see cref="TaskRunner"/>
/// 之间无反向耦合的协调中枢。所有状态均经内部锁保护，可在受控并发下被多任务安全读写；
/// 全部为内存态，不落任务文件（DESIGN §4.2）。
/// </summary>
public sealed class DispatchCoordinator
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string?> _taskWorkspace = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlightTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _inFlightByWorkspace = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _frozenSlots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>全局派发暂停标志（REQ-021）：接入派发循环的 <c>!DispatchHalted</c> 守卫。</summary>
    public bool DispatchHalted { get; private set; }

    /// <summary>冻结时记录的「后续任务统一切换目标账号」（REQ-021）。</summary>
    public string? PendingSwitchTarget { get; private set; }

    /// <summary>当前在飞任务数（任意工作空间之和）。</summary>
    public int InFlightCount
    {
        get { lock (_lock) { return _inFlightTasks.Count; } }
    }

    /// <summary>当前在飞工作空间数（= 当前并行通道数，REQ-020）。</summary>
    public int InFlightWorkspaceCount
    {
        get { lock (_lock) { return _inFlightByWorkspace.Count(kv => kv.Value); } }
    }

    /// <summary>是否存在在飞任务（用于「手动切换禁用」闸：in-flight&gt;0 时禁用，REQ-021 衔接 §7）。</summary>
    public bool HasInFlight
    {
        get { lock (_lock) { return _inFlightTasks.Count > 0; } }
    }

    /// <summary>冻结槽位集合（快照）。</summary>
    public IReadOnlyCollection<string> FrozenSlots
    {
        get { lock (_lock) { return _frozenSlots.ToList(); } }
    }

    /// <summary>在飞工作空间集合（快照，监控展示用）。</summary>
    public IReadOnlyCollection<string> InFlightWorkspaces
    {
        get { lock (_lock) { return _inFlightByWorkspace.Where(kv => kv.Value).Select(kv => kv.Key).ToList(); } }
    }

    /// <summary>
    /// 任务开始派发：登记其在飞态与其所属工作空间（REQ-020）。同一工作空间在飞数恒为 0 或 1。
    /// <paramref name="workspaceKey"/> 由调用方归一化（null/空视为单一默认工作空间桶）。
    /// </summary>
    public void RegisterInFlight(string taskId, string workspaceKey)
    {
        lock (_lock)
        {
            _inFlightTasks.Add(taskId);
            _taskWorkspace[taskId] = workspaceKey;
            _inFlightByWorkspace[workspaceKey] = true;
        }
    }

    /// <summary>
    /// 任务到达终态或进入 waiting/interrupted：释放其工作空间在飞槽位（REQ-020）。
    /// 返回非空字符串表示「派发已暂停且当前在飞数已降为 0」，调度器应执行一次统一切换，
    /// 随后本方法已清除 <see cref="DispatchHalted"/> / <see cref="PendingSwitchTarget"/> / 冻结集合。
    /// 返回 null 表示尚无需统一切换。
    /// </summary>
    public string? ReleaseInFlight(string taskId)
    {
        lock (_lock)
        {
            _inFlightTasks.Remove(taskId);
            _frozenSlots.Remove(taskId);
            if (_taskWorkspace.TryGetValue(taskId, out var ws) && !string.IsNullOrWhiteSpace(ws))
            {
                var stillInWs = _inFlightTasks.Any(t =>
                    string.Equals(_taskWorkspace.GetValueOrDefault(t), ws, StringComparison.OrdinalIgnoreCase));
                if (!stillInWs)
                {
                    _inFlightByWorkspace[ws!] = false;
                }
            }

            _taskWorkspace.Remove(taskId);

            // 统一切换触发条件：派发已暂停 且 在飞数降为 0（waiting/interrupted 任务不计入在飞、不阻塞本次切换）
            if (DispatchHalted && _inFlightTasks.Count == 0)
            {
                var target = PendingSwitchTarget;
                DispatchHalted = false;
                PendingSwitchTarget = null;
                _frozenSlots.Clear();
                return target;
            }

            return null;
        }
    }

    /// <summary>
    /// 在飞任务触发切换需求（REQ-021）：冻结该在飞槽位、进入全局派发暂停、记录统一切换目标。
    /// 多任务并行冻结只产生唯一目标（设计「目标唯一性」论证），故仅首次设置 <see cref="PendingSwitchTarget"/>。
    /// </summary>
    public void RequestDeferredSwitch(string taskId, string targetAccountId)
    {
        lock (_lock)
        {
            _frozenSlots.Add(taskId);
            DispatchHalted = true;
            PendingSwitchTarget ??= targetAccountId;
        }
    }

    /// <summary>
    /// 派发选择守卫（REQ-020 + REQ-021）：不满足任一条件即不可派发新任务。
    /// <paramref name="distinctPendingWorkspaceCount"/> 为「尚有 pending/interrupted 待派发任务的不同工作空间数」。
    /// </summary>
    public bool CanDispatch(string workspaceKey, int distinctPendingWorkspaceCount, int concurrency)
    {
        lock (_lock)
        {
            if (DispatchHalted)
            {
                return false;
            }

            var cap = Math.Min(Math.Max(1, concurrency), Math.Max(1, distinctPendingWorkspaceCount));
            if (InFlightWorkspaceCount >= cap)
            {
                return false;
            }

            // 同工作空间已有在飞 → 不可再派发（按工作空间串行）
            if (_inFlightByWorkspace.TryGetValue(workspaceKey, out var flying) && flying)
            {
                return false;
            }

            return true;
        }
    }
}
