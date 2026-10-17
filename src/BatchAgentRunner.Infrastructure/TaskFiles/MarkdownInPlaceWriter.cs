using System.Text;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.TaskFiles;

/// <summary>
/// Markdown 原地回写器（二期：在 .md 任务文件中就地写回状态/结果，兼容一期 sidecar）。
/// 通过哨兵注释包裹托管区（不影响用户手写的 instruction 文本）；幂等更新。
/// </summary>
public sealed class MarkdownInPlaceWriter : IMarkdownInPlaceWriter
{
    private const string BeginTag = "<!-- batch-agent-runner:meta ";
    private const string EndTag = "<!-- /batch-agent-runner:meta -->";

    /// <summary>将任务状态/结果写回 markdown 文件（原地更新托管区）。</summary>
    public async Task WriteBackAsync(string markdownPath, TaskItem task, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        var updated = UpdateTaskMeta(text, task);

        // P0-8：原为 File.WriteAllTextAsync 直接覆写——写入过程中被中断（强杀 / 断电 / 与 CLI
        // 并发写）会把用户的任务文件截成半截。改用同库已有的原子写（临时文件 + 原子替换）。
        await AtomicFileWriter.WriteAsync(markdownPath, updated, cancellationToken).ConfigureAwait(false);
    }

    internal static string UpdateTaskMeta(string markdown, TaskItem task)
    {
        var lines = markdown.Split('\n');
        var headingIdx = FindHeading(lines, task.Id);
        if (headingIdx < 0)
        {
            return markdown; // 未找到该任务标题，不改动
        }

        var metaBlock = BuildMetaBlock(task);
        var (existingBegin, existingEnd) = FindMetaRegion(lines, headingIdx);

        if (existingBegin >= 0)
        {
            // 替换既有托管区
            var result = new List<string>();
            result.AddRange(lines.Take(existingBegin));
            result.Add(metaBlock);
            result.AddRange(lines.Skip(existingEnd + 1));
            return string.Join("\n", result);
        }

        // 在标题块末尾（下一 ## 之前）插入
        var nextHeading = FindNextHeading(lines, headingIdx + 1);
        var result2 = new List<string>();
        result2.AddRange(lines.Take(nextHeading < 0 ? lines.Length : nextHeading));
        result2.Add(metaBlock);
        if (nextHeading >= 0)
        {
            result2.AddRange(lines.Skip(nextHeading));
        }

        return string.Join("\n", result2);
    }

    private static string BuildMetaBlock(TaskItem task)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{BeginTag}{task.Id} -->");
        sb.AppendLine($"- status: {task.Status}");
        if (!string.IsNullOrEmpty(task.ModelUsed)) sb.AppendLine($"- model_used: {task.ModelUsed}");
        if (!string.IsNullOrEmpty(task.AccountUsed)) sb.AppendLine($"- account_used: {task.AccountUsed}");
        if (!string.IsNullOrEmpty(task.ExpertUsed)) sb.AppendLine($"- expert_used: {task.ExpertUsed}");
        sb.AppendLine($"- fallback_used: {task.FallbackUsed.ToString().ToLowerInvariant()}");
        if (task.StartedAt.HasValue) sb.AppendLine($"- started_at: {task.StartedAt:o}");
        if (task.FinishedAt.HasValue) sb.AppendLine($"- finished_at: {task.FinishedAt:o}");
        sb.AppendLine($"- duration: {task.DurationSeconds}");
        if (!string.IsNullOrEmpty(task.Result))
        {
            var result = task.Result.Replace("\n", "\n  ");
            sb.AppendLine($"- result: |");
            sb.AppendLine($"  {result}");
        }

        if (task.StageResults is { Count: > 0 })
        {
            sb.AppendLine("- stages:");
            foreach (var s in task.StageResults)
            {
                sb.AppendLine($"  - {s.StageId}: {s.Status}{(s.GatePassed ? " (gate✓)" : "")}");
            }
        }

        sb.AppendLine(EndTag);
        return sb.ToString().TrimEnd('\n');
    }

    private static int FindHeading(string[] lines, string id)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal) &&
                string.Equals(line[3..].Trim(), id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindNextHeading(string[] lines, int from)
    {
        for (var i = from; i < lines.Length; i++)
        {
            if (lines[i].Trim().StartsWith("## ", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static (int Begin, int End) FindMetaRegion(string[] lines, int headingIdx)
    {
        for (var i = headingIdx + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().StartsWith(BeginTag, StringComparison.Ordinal))
            {
                var begin = i;
                for (var j = i; j < lines.Length; j++)
                {
                    if (lines[j].Trim().StartsWith(EndTag, StringComparison.Ordinal))
                    {
                        return (begin, j);
                    }

                    if (lines[j].Trim().StartsWith("## ", StringComparison.Ordinal))
                    {
                        break; // 遇到下一个任务标题仍未闭合，视为异常，不处理
                    }
                }

                return (begin, begin);
            }

            if (lines[i].Trim().StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }
        }

        return (-1, -1);
    }
}
