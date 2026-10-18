using System.Text.Json;
using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;

namespace BatchAgentRunner.Infrastructure.TaskFiles;

/// <summary>
/// 任务文件读写实现（REQ-001 / REQ-001a / REQ-006）。
/// JSON：原地原子回写；Markdown：只读输入 + sidecar 结果文件（续跑以 sidecar 为准）。
/// </summary>
public sealed class TaskFileStore : ITaskFileStore
{
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9._\-]+$", RegexOptions.Compiled);

    public bool IsMarkdown(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsXlsx(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".xlsm", StringComparison.OrdinalIgnoreCase);
    }

    public string ResolveResultPath(string path) =>
        IsMarkdown(path) ? path + ".result.json" : path;

    public async Task<TaskFileLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new TaskFileFormatException("任务文件不存在");
        }

        var issues = new List<TaskFileIssue>();
        var isMarkdown = IsMarkdown(path);
        var isXlsx = IsXlsx(path);
        var resultPath = ResolveResultPath(path);
        List<TaskItem> tasks;

        if (isMarkdown)
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            tasks = MarkdownTaskParser.Parse(content, issues);
            MergeSidecar(tasks, resultPath, cancellationToken);
        }
        else if (isXlsx)
        {
            tasks = XlsxTaskParser.Parse(path, issues);
        }
        else
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                tasks = new List<TaskItem>();
            }
            else
            {
                try
                {
                    tasks = JsonSerializer.Deserialize<List<TaskItem>>(content, JsonDefaults.Options)
                            ?? new List<TaskItem>();
                }
                catch (JsonException ex)
                {
                    throw new TaskFileFormatException($"JSON 解析失败：{ex.Message}", (int?)ex.LineNumber);
                }
            }
        }

        NormalizeAndValidateIds(tasks, issues);
        return new TaskFileLoadResult(tasks, resultPath, isMarkdown, issues);
    }

    public async Task SaveAsync(string path, IReadOnlyList<TaskItem> tasks,
        CancellationToken cancellationToken = default)
    {
        if (IsXlsx(path))
        {
            await XlsxTaskWriter.WriteAsync(path, tasks, cancellationToken).ConfigureAwait(false);
            return;
        }

        var resultPath = ResolveResultPath(path);
        var json = JsonDefaults.Serialize(tasks);
        await AtomicFileWriter.WriteAsync(resultPath, json, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> BackupAsync(string path, int retention, CancellationToken cancellationToken = default)
    {
        var backup = BackupManager.Backup(path, retention);
        var resultPath = ResolveResultPath(path);
        if (!string.Equals(path, resultPath, StringComparison.OrdinalIgnoreCase) && File.Exists(resultPath))
        {
            BackupManager.Backup(resultPath, retention);
        }

        return Task.FromResult(backup);
    }

    /// <summary>Markdown 续跑以 sidecar 为准：把已有状态/结果合并回任务列表。</summary>
    private static void MergeSidecar(List<TaskItem> tasks, string sidecarPath, CancellationToken ct)
    {
        if (!File.Exists(sidecarPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(sidecarPath, System.Text.Encoding.UTF8);
            var saved = JsonSerializer.Deserialize<List<TaskItem>>(json, JsonDefaults.Options);
            if (saved is null)
            {
                return;
            }

            foreach (var savedItem in saved)
            {
                var target = tasks.FirstOrDefault(t => string.Equals(t.Id, savedItem.Id, StringComparison.Ordinal));
                if (target is null)
                {
                    continue;
                }

                target.Status = savedItem.Status;
                target.Result = savedItem.Result;
                target.ModelUsed = savedItem.ModelUsed;
                target.AccountUsed = savedItem.AccountUsed;
                target.ExpertUsed = savedItem.ExpertUsed;
                target.FallbackUsed = savedItem.FallbackUsed;
                target.FallbackCost = savedItem.FallbackCost;
                target.RetryCount = savedItem.RetryCount;
                target.Error = savedItem.Error;
                target.StartedAt = savedItem.StartedAt;
                target.FinishedAt = savedItem.FinishedAt;
                target.DurationSeconds = savedItem.DurationSeconds;
            }
        }
        catch (JsonException)
        {
            // sidecar 损坏时忽略，按全新一批处理（不阻塞启动）
        }
    }

    /// <summary>ID 规则（REQ-001a）：缺失自动生成、重复/非法字符报错定位。</summary>
    private static void NormalizeAndValidateIds(List<TaskItem> tasks, List<TaskFileIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];

            if (string.IsNullOrWhiteSpace(task.Id))
            {
                task.Id = MarkdownTaskParser.GenerateId(i + 1);
                issues.Add(new TaskFileIssue("info", task.Id,
                    $"第 {i + 1} 条任务缺少 ID，已自动生成 {task.Id}"));
            }

            if (!IdPattern.IsMatch(task.Id))
            {
                throw new TaskFileFormatException(
                    $"任务 ID 含非法字符（仅允许字母、数字、点、下划线、连字符）：{task.Id}", taskId: task.Id);
            }

            if (!seen.Add(task.Id))
            {
                throw new TaskFileFormatException($"任务 ID 重复：{task.Id}", taskId: task.Id);
            }
        }
    }
}
