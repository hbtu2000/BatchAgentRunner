using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// Markdown 原地回写端口（二期 15.4.7）。将任务状态/结果写回 .md 任务文件中的托管区，
/// 不破坏用户手写的 instruction 文本。由 Infrastructure 层具体实现。
/// </summary>
public interface IMarkdownInPlaceWriter
{
    Task WriteBackAsync(string markdownPath, TaskItem task, CancellationToken cancellationToken = default);
}
