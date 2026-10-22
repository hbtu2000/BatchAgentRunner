using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Engine;

/// <summary>启动前预检结果（REQ-010）。</summary>
public sealed class PreflightReport
{
    /// <summary>无错误时方可启动。</summary>
    public bool CanStart => Errors.Count == 0;

    /// <summary>阻断启动的错误列表。</summary>
    public List<string> Errors { get; } = new();

    /// <summary>不阻断启动的警告列表。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>预检期被停用的账号（如凭据探测失败）。</summary>
    public List<string> DisabledAccounts { get; } = new();

    /// <summary>启动时全部组合不可用（时段外 / 冷却中）。</summary>
    public bool AllCombosUnavailable { get; set; }

    /// <summary>全部组合不可用时的最早恢复时间点。</summary>
    public DateTimeOffset? EarliestRestoreAt { get; set; }

    /// <summary>不可用时的等待类型（时段外 / 额度重置 / 混合）。</summary>
    public WaitKind WaitKind { get; set; }

    /// <summary>不可用原因描述。</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>任务文件中解析到的任务总数。</summary>
    public int LoadedTaskCount { get; set; }

    /// <summary>待执行（Pending / Interrupted）任务数。</summary>
    public int PendingTaskCount { get; set; }

    /// <summary>追加一条阻断启动的错误。</summary>
    /// <param name="message">错误描述（计入 Errors，使 CanStart 为 false）。</param>
    public void AddError(string message) => Errors.Add(message);

    /// <summary>追加一条不阻断启动的警告。</summary>
    /// <param name="message">警告描述（仅提示，不影响 CanStart）。</param>
    public void AddWarning(string message) => Warnings.Add(message);
}
