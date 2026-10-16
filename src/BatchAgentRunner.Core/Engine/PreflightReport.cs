using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Engine;

/// <summary>启动前预检结果（REQ-010）。</summary>
public sealed class PreflightReport
{
    public bool CanStart => Errors.Count == 0;

    public List<string> Errors { get; } = new();

    public List<string> Warnings { get; } = new();

    public List<string> DisabledAccounts { get; } = new();

    /// <summary>启动时全部组合不可用（时段外 / 冷却中）。</summary>
    public bool AllCombosUnavailable { get; set; }

    public DateTimeOffset? EarliestRestoreAt { get; set; }

    public WaitKind WaitKind { get; set; }

    public string? UnavailableReason { get; set; }

    public int LoadedTaskCount { get; set; }

    public int PendingTaskCount { get; set; }

    public void AddError(string message) => Errors.Add(message);

    public void AddWarning(string message) => Warnings.Add(message);
}
