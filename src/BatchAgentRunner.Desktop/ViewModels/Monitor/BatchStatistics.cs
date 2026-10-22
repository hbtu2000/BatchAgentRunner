using System;
using System.Collections.Generic;
using System.Linq;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 批次统计纯计算（P-06 从 <see cref="MonitorViewModel"/> 抽离，无副作用、可独立单测）。
/// 输入任务清单（领域态），输出 <see cref="Snapshot"/>：各状态计数 + 预计剩余
/// （已完成任务均值 × 待处理数，无已完成样本时回退「—」）+ 当前执行任务的
/// 任务 / 账号·模型 / 工作空间三栏文案。不含任何绑定属性，由 <see cref="BatchStatsState"/> 承接。
/// </summary>
internal static class BatchStatistics
{
    internal sealed record Snapshot(
        int Total,
        int Done,
        int Failed,
        int Running,
        int Interrupted,
        int Pending,
        int NeedsAttention,
        int Blocked,
        string EtaText,
        string CurrentTaskText,
        string CurrentComboText,
        string CurrentWorkspaceText);

    /// <summary>按任务清单汇总各状态计数、预计剩余时间与当前任务三栏文案。</summary>
    public static Snapshot Compute(IReadOnlyList<TaskItem> tasks, string? defaultWorkspace)
    {
        var current = tasks.FirstOrDefault(t => t.Status == TaskStatus.Running);

        // 预计剩余：已完成任务均值 × 待处理数（无已完成样本时回退「—」）
        var etaText = "—";
        var done = tasks.Count(t => t.Status == TaskStatus.Done);
        var pending = tasks.Count(t => t.Status == TaskStatus.Pending);
        if (done > 0 && pending > 0)
        {
            var avg = tasks.Where(t => t.Status == TaskStatus.Done)
                           .Average(t => Math.Max(t.DurationSeconds, 1.0));
            var remain = TimeSpan.FromSeconds(avg * pending);
            etaText = remain.TotalMinutes >= 1
                ? $"~{Math.Ceiling(remain.TotalMinutes)} min"
                : $"~{Math.Ceiling(remain.TotalSeconds)} s";
        }

        return new Snapshot(
            tasks.Count,
            done,
            tasks.Count(t => t.Status == TaskStatus.Failed),
            tasks.Count(t => t.Status == TaskStatus.Running),
            tasks.Count(t => t.Status == TaskStatus.Interrupted),
            pending,
            tasks.Count(t => t.Status == TaskStatus.NeedsAttention),
            tasks.Count(t => t.Status == TaskStatus.Blocked),
            etaText,
            current is null ? "无" : $"{current.Id} {current.Instruction}",
            current is null
                ? "-"
                : string.Join(" + ", new[] { current.AccountUsed, current.ModelUsed }.Where(x => !string.IsNullOrEmpty(x))),
            current?.Workspace ?? defaultWorkspace ?? "-");
    }
}
