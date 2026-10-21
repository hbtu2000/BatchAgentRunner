using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Models;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 任务列表协作者（P-06 从 <see cref="MonitorViewModel"/> 抽离）。
/// 独占 <see cref="Tasks"/> / <see cref="DagRows"/> / <see cref="SelectedStages"/> 三个集合与两类同步：
/// 整表重建（引擎 <c>TasksChanged</c>，P1-7：移出前逐行 Dispose 解除长生命周期 TaskItem 订阅）
/// 与单行 upsert（引擎事件带的 TaskItem）；另承载依赖看板的拓扑层级 / blocked_by 计算
/// （与引擎 ApplyDependencyBlockAsync 判定一致）与选中任务的阶段树刷新。
/// </summary>
internal sealed class TaskListCoordinator
{
    private readonly ObservableCollection<TaskRowViewModel> _tasks = new();
    private readonly ObservableCollection<DagRowViewModel> _dagRows = new();
    private readonly ObservableCollection<StageRowViewModel> _selectedStages = new();

    /// <summary>任务列表行（筛选 chip 的数据源经 <see cref="TaskFilterState"/> 过滤）。</summary>
    public ObservableCollection<TaskRowViewModel> Tasks => _tasks;

    /// <summary>依赖看板行（DagBoardView 数据源；仅当批次含 DAG 依赖时有内容）。</summary>
    public ObservableCollection<DagRowViewModel> DagRows => _dagRows;

    /// <summary>选中任务的工作流阶段树（WorkflowView / GateResultView 数据源；非工作流任务为空）。</summary>
    public ObservableCollection<StageRowViewModel> SelectedStages => _selectedStages;

    /// <summary>
    /// 整表重建：处置旧行 VM 解除订阅后清空重建；返回原选中行（按 Id）对应的新行，
    /// 由调用方无声重指（不触发绑定通知，与原行为一致）。
    /// </summary>
    public TaskRowViewModel? RebuildAll(IReadOnlyList<TaskItem>? tasks, string? previousSelectedId)
    {
        // P1-7：移出集合前先处置旧行 VM，解除其对长生命周期 TaskItem 的订阅，避免反复重建泄漏。
        foreach (var old in _tasks)
        {
            old.Dispose();
        }

        _tasks.Clear();
        if (tasks is not null)
        {
            foreach (var t in tasks)
            {
                _tasks.Add(new TaskRowViewModel(t));
            }
        }

        RebuildDagRows();
        return previousSelectedId is null
            ? null
            : _tasks.FirstOrDefault(r => r.Id == previousSelectedId);
    }

    /// <summary>单行同步：不存在则新增，存在则刷新；随后重建依赖看板。</summary>
    public void Upsert(TaskItem item)
    {
        var row = _tasks.FirstOrDefault(r => r.Item.Id == item.Id);
        if (row is null)
        {
            _tasks.Add(new TaskRowViewModel(item));
        }
        else
        {
            row.Refresh();
        }

        RebuildDagRows();
    }

    /// <summary>重建选中任务的工作流阶段树（WorkflowView / GateResultView）。</summary>
    public void RefreshStages(TaskItem? selected)
    {
        _selectedStages.Clear();
        var stages = selected?.StageResults;
        if (stages is null)
        {
            return;
        }

        foreach (var s in stages)
        {
            _selectedStages.Add(new StageRowViewModel(s));
        }
    }

    /// <summary>
    /// 重建依赖看板（DagBoardView）：按拓扑层级缩进排列，
    /// blocked_by 取自引擎的依赖阻断判定（与报告一致）。
    /// </summary>
    public void RebuildDagRows()
    {
        _dagRows.Clear();
        var items = _tasks.Select(t => t.Item).ToList();
        if (!items.Any(t => t.DependsOn is { Count: > 0 }))
        {
            return;
        }

        var levels = ComputeLevels(items);
        foreach (var item in items)
        {
            var blockedBy = ComputeBlockedByView(item, items);
            _dagRows.Add(new DagRowViewModel(item, levels.GetValueOrDefault(item.Id, 0), blockedBy));
        }
    }

    /// <summary>拓扑层级：无依赖 = 0，否则 = max(依赖层级) + 1（环/悬空按 0 层保守处理）。</summary>
    private static Dictionary<string, int> ComputeLevels(IReadOnlyList<TaskItem> items)
    {
        var byId = items.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int LevelOf(TaskItem t)
        {
            if (levels.TryGetValue(t.Id, out var cached))
            {
                return cached;
            }

            levels[t.Id] = 0; // 防环：先占位
            var level = 0;
            if (t.DependsOn is { Count: > 0 })
            {
                foreach (var dep in t.DependsOn)
                {
                    if (byId.TryGetValue(dep, out var depTask))
                    {
                        level = Math.Max(level, LevelOf(depTask) + 1);
                    }
                }
            }

            levels[t.Id] = level;
            return level;
        }

        foreach (var t in items)
        {
            LevelOf(t);
        }

        return levels;
    }

    /// <summary>看板视图的 blocked_by：上游失败/阻塞/悬空的依赖 Id（与引擎 ApplyDependencyBlockAsync 判定一致）。</summary>
    private static List<string> ComputeBlockedByView(TaskItem item, IReadOnlyList<TaskItem> items)
    {
        if (item.DependsOn is not { Count: > 0 } ||
            item.Status is not (TaskStatus.Pending or TaskStatus.Blocked))
        {
            return new List<string>();
        }

        var byId = items.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        return item.DependsOn.Where(dep =>
            !byId.TryGetValue(dep, out var upstream) ||
            upstream.Status is TaskStatus.Failed or TaskStatus.Blocked or TaskStatus.NeedsAttention).ToList();
    }
}
