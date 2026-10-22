using BatchAgentRunner.Core.Domain;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>依赖失败策略（任务依赖未满足时的处理）。</summary>
public enum DependencyFailurePolicy
{
    /// <summary>默认：依赖 Failed/Blocked 时，下游任务标记 Blocked（不执行）。</summary>
    BlockDependents,
    /// <summary>依赖失败直接终止整批。</summary>
    AbortBatch,
    /// <summary>忽略依赖失败，下游照常执行。</summary>
    SkipDependents
}

/// <summary>依赖图中存在环时抛出。</summary>
public sealed class DagCycleException : Exception
{
    /// <summary>构造循环依赖异常。</summary>
    public DagCycleException(string cyclePath) : base($"检测到循环依赖：{cyclePath}") { }
}

/// <summary>依赖判定结果。</summary>
public enum DependencyVerdict
{
    /// <summary>依赖已满足，可以执行。</summary>
    Satisfied,

    /// <summary>依赖未成功（失败/阻塞/悬空），本任务应被阻断或终止整批。</summary>
    Blocked,

    /// <summary>依赖尚未到达终态（串行拓扑序下不出现，保守按阻断处理）。</summary>
    Wait
}

/// <summary>
/// DAG 调度辅助（二期：任务依赖 topo 排序）。
/// 与一期串行/并发正交：环检测在加载期失败快速（不静默）；依赖失败按策略处理下游。
/// </summary>
public static class DagScheduler
{
    /// <summary>
    /// 对任务列表做拓扑排序（Kahn 算法）。存在环时抛出 <see cref="DagCycleException"/>（含环路径）。
    /// 无依赖的任务保持原输入顺序的相对次序。
    /// </summary>
    public static IReadOnlyList<TaskItem> TopoSort(IReadOnlyList<TaskItem> tasks)
    {
        var byId = new Dictionary<string, TaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
        {
            byId[t.Id] = t;
        }

        var indegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<TaskItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
        {
            indegree[t.Id] = 0;
        }

        foreach (var t in tasks)
        {
            foreach (var dep in t.DependsOn ?? Enumerable.Empty<string>())
            {
                if (!byId.ContainsKey(dep))
                {
                    // 悬空依赖：视为无依赖（不阻断），仅通过事件/日志提示
                    continue;
                }

                indegree[t.Id]++;
                if (!dependents.TryGetValue(dep, out var list))
                {
                    list = new List<TaskItem>();
                    dependents[dep] = list;
                }

                list.Add(t);
            }
        }

        // 稳定队列：按输入顺序入队（indegree==0 者）
        var queue = new Queue<TaskItem>();
        foreach (var t in tasks)
        {
            if (indegree[t.Id] == 0)
            {
                queue.Enqueue(t);
            }
        }

        var result = new List<TaskItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!seen.Add(t.Id))
            {
                continue;
            }

            result.Add(t);
            if (dependents.TryGetValue(t.Id, out var deps))
            {
                foreach (var d in deps)
                {
                    indegree[d.Id]--;
                    if (indegree[d.Id] == 0)
                    {
                        queue.Enqueue(d);
                    }
                }
            }
        }

        if (result.Count != tasks.Count)
        {
            var remaining = tasks.Where(t => !seen.Contains(t.Id)).Select(t => t.Id).ToList();
            throw new DagCycleException(string.Join(" → ", remaining));
        }

        return result;
    }

    /// <summary>
    /// 判定任务依赖是否满足（P2-3 偿还：原 <see cref="DependenciesSatisfied"/> 与 <c>TaskDispatcher</c> 私有
    /// <c>EvaluateDependency</c> 两份逐字重复的依赖失败分派状态机，收敛为本方法——全仓依赖判定的单一事实来源）。
    /// 默认成功谓词：依赖处于终态且非 Failed/Blocked；可选 <paramref name="blockOnNeedsAttention"/> 时，
    /// 依赖为 NeedsAttention 也视为阻塞。悬空依赖（不在 <paramref name="statusById"/>）保守判 <see cref="DependencyVerdict.Blocked"/>。
    /// </summary>
    public static DependencyVerdict EvaluateDependency(
        TaskItem task,
        IReadOnlyDictionary<string, TaskStatus> statusById,
        bool blockOnNeedsAttention,
        out string? blockingDep)
    {
        blockingDep = null;
        if (task.DependsOn is not { Count: > 0 })
        {
            return DependencyVerdict.Satisfied;
        }

        var wait = false;
        foreach (var dep in task.DependsOn)
        {
            if (!statusById.TryGetValue(dep, out var st))
            {
                blockingDep = dep; // 悬空依赖：保守阻断
                return DependencyVerdict.Blocked;
            }

            if (st is TaskStatus.Failed or TaskStatus.Blocked)
            {
                blockingDep = dep;
                return DependencyVerdict.Blocked;
            }

            if (st == TaskStatus.NeedsAttention && blockOnNeedsAttention)
            {
                blockingDep = dep;
                return DependencyVerdict.Blocked;
            }

            if (!st.IsTerminal())
            {
                wait = true; // 依赖尚未到达终态
            }
        }

        return wait ? DependencyVerdict.Wait : DependencyVerdict.Satisfied;
    }

    /// <summary>
    /// 依赖是否满足（可入队执行）。<see cref="EvaluateDependency"/> 的布尔包装，保持既有调用面不变。</summary>
    /// <param name="task">待判定任务，读取其 DependsOn 列表。</param>
    /// <param name="statusById">全批任务状态快照，按任务 id 查依赖终态。</param>
    /// <param name="blockOnNeedsAttention">依赖处于 NeedsAttention 时是否判阻塞（false 视为已满足）。</param>
    public static bool DependenciesSatisfied(
        TaskItem task,
        IReadOnlyDictionary<string, TaskStatus> statusById,
        bool blockOnNeedsAttention = false) =>
        EvaluateDependency(task, statusById, blockOnNeedsAttention, out _) == DependencyVerdict.Satisfied;

    /// <summary>
    /// 依赖失败（Failed/Blocked）时，找出应被标记 Blocked 的下游任务（传递性闭包）。
    /// 从失败任务出发沿依赖边反向 BFS：所有仍处于 Pending/Interrupted 的间接下游也纳入，
    /// 便于 UI 一次性展示「一个任务失败会连累哪些任务」。
    /// </summary>
    public static IReadOnlyList<TaskItem> GetBlockedDependents(
        TaskItem failedTask,
        IReadOnlyList<TaskItem> allTasks,
        DependencyFailurePolicy policy)
    {
        if (policy != DependencyFailurePolicy.BlockDependents)
        {
            return Array.Empty<TaskItem>();
        }

        var result = new List<TaskItem>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { failedTask.Id };
        var frontier = new List<string> { failedTask.Id };

        while (frontier.Count > 0)
        {
            var next = new List<string>();
            foreach (var t in allTasks)
            {
                if (t.DependsOn is not { Count: > 0 })
                {
                    continue;
                }

                // 仅当该任务直接依赖当前前沿（上一轮被阻断的任务）时才纳入闭包
                if (!t.DependsOn.Any(dep => frontier.Contains(dep, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!visited.Add(t.Id))
                {
                    continue; // 多条路径汇合：已处理过，不重复传播
                }

                if (t.Status is TaskStatus.Pending or TaskStatus.Interrupted)
                {
                    result.Add(t);
                    next.Add(t.Id); // 继续向它的下游传播
                }
            }

            frontier = next;
        }

        return result;
    }
}
