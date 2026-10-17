using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>结果 diff 采集端口（REQ-017）：任务/阶段前后对受关注文件做快照并生成 diff。</summary>
public interface IDiffCollector
{
    /// <summary>任务开始执行前对受关注文件做快照（返回快照句柄 id）。</summary>
    Task<string> SnapshotAsync(TaskItem task, CancellationToken cancellationToken = default);

    /// <summary>任务结束后采集 diff；返回该任务的变更记录。</summary>
    Task<IReadOnlyList<DiffRecord>> CollectAsync(string snapshotId, TaskItem task, CancellationToken cancellationToken = default);
}

/// <summary>diff 采集配置（决定受关注文件范围）。</summary>
public sealed class DiffCollectorOptions
{
    /// <summary>要采集的文件 glob（相对于工作空间）。为空表示仅采集 agent 显式报告改动的文件。</summary>
    public List<string> TargetGlobs { get; set; } = new();

    /// <summary>单个文件最大 diff 行数（超出只记录 kind + size，不输出文本）。</summary>
    public int MaxDiffLines { get; set; } = 2000;

    /// <summary>受关注文件的建议来源：是否采信 agent 输出中报出的改动文件路径。</summary>
    public bool TrustAgentReportedPaths { get; set; } = true;
}
