using System.Security.Cryptography;
using System.Text;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Infrastructure.Diff;

/// <summary>
/// 结果 diff 采集器（REQ-017）。
/// 任务开始前对受关注文件快照（路径 + 内容指纹 + 内容），结束后对比生成统一 diff。
/// 大文件仅记录变更类型，不输出文本 diff。
/// </summary>
public sealed class DiffCollector : IDiffCollector
{
    private readonly IRunLogger? _logger;
    private readonly RunOptions _options;
    private readonly DiffCollectorOptions _opts;
    private readonly Dictionary<string, Snapshot> _snapshots = new();

    public DiffCollector(IRunLogger? logger, RunOptions options, DiffCollectorOptions? opts = null)
    {
        _logger = logger;
        _options = options;
        _opts = opts ?? new DiffCollectorOptions();
    }

    public Task<string> SnapshotAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var ws = ResolveWorkspace(task);
        var snapshot = new Snapshot { Workspace = ws };

        if (Directory.Exists(ws))
        {
            var files = CollectFiles(ws);
            foreach (var f in files)
            {
                snapshot.Files[f] = ReadFile(f);
            }
        }

        lock (_snapshots)
        {
            _snapshots[id] = snapshot;
        }

        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<DiffRecord>> CollectAsync(string snapshotId, TaskItem task, CancellationToken cancellationToken = default)
    {
        Snapshot? before;
        lock (_snapshots)
        {
            _snapshots.TryGetValue(snapshotId, out before);
        }

        var records = new List<DiffRecord>();
        if (before is null)
        {
            return Task.FromResult<IReadOnlyList<DiffRecord>>(records);
        }

        var ws = before.Workspace;
        var after = new Dictionary<string, FileBlob>();
        if (Directory.Exists(ws))
        {
            foreach (var f in CollectFiles(ws))
            {
                after[f] = ReadFile(f);
            }
        }

        // 删除 / 修改
        foreach (var (path, blob) in before.Files)
        {
            if (!after.TryGetValue(path, out var afterBlob))
            {
                records.Add(new DiffRecord
                {
                    TaskId = task.Id,
                    FilePath = MakeRelative(path, ws),
                    Kind = DiffKind.Deleted,
                    BeforeHash = blob.Hash
                });
            }
            else if (!string.Equals(blob.Hash, afterBlob.Hash, StringComparison.Ordinal))
            {
                records.Add(new DiffRecord
                {
                    TaskId = task.Id,
                    FilePath = MakeRelative(path, ws),
                    Kind = DiffKind.Modified,
                    UnifiedDiff = UnifiedDiff(blob.Lines, afterBlob.Lines, MakeRelative(path, ws)),
                    BeforeHash = blob.Hash,
                    AfterHash = afterBlob.Hash
                });
            }
        }

        // 新增
        foreach (var (path, blob) in after)
        {
            if (!before.Files.ContainsKey(path))
            {
                records.Add(new DiffRecord
                {
                    TaskId = task.Id,
                    FilePath = MakeRelative(path, ws),
                    Kind = DiffKind.Added,
                    UnifiedDiff = UnifiedDiff(Array.Empty<string>(), blob.Lines, MakeRelative(path, ws)),
                    AfterHash = blob.Hash
                });
            }
        }

        _logger?.Write("DIFF", $"任务 {task.Id} 采集到 {records.Count} 处文件变更");
        return Task.FromResult<IReadOnlyList<DiffRecord>>(records);
    }

    // ---------------- 内部 ----------------

    private List<string> CollectFiles(string ws)
    {
        var result = new List<string>();
        var globs = _opts.TargetGlobs;
        if (globs is not { Count: > 0 })
        {
            return result; // 无目标范围：仅记录 agent 显式报告的文件（由调用方追加）
        }

        foreach (var g in globs)
        {
            var full = Path.Combine(ws, g);
            var dir = Path.GetDirectoryName(full) ?? ws;
            var name = Path.GetFileName(full);
            if (Directory.Exists(dir))
            {
                result.AddRange(Directory.EnumerateFiles(dir, string.IsNullOrWhiteSpace(name) ? "*" : name, SearchOption.TopDirectoryOnly));
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static FileBlob ReadFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            var hash = ComputeHash(bytes);
            var lines = text.Split('\n');
            var tooLarge = bytes.Length > 1_000_000;
            return new FileBlob
            {
                Hash = hash,
                Lines = tooLarge ? Array.Empty<string>() : lines,
                TooLarge = tooLarge
            };
        }
        catch
        {
            return new FileBlob { Hash = "unreadable", Lines = Array.Empty<string>() };
        }
    }

    private static string ComputeHash(byte[] bytes)
    {
        // CA1850：优先静态 SHA256.HashData（免一次 SHA256 实例分配）
        var h = SHA256.HashData(bytes);
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            sb.Append(h[i].ToString("x2"));
        }

        return sb.ToString();
    }

    private string ResolveWorkspace(TaskItem task)
    {
        var ws = task.Workspace ?? _options.DefaultWorkspace;
        if (string.IsNullOrWhiteSpace(ws))
        {
            return Directory.GetCurrentDirectory();
        }

        var named = _options.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, ws, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return named.Path;
        }

        return Path.IsPathRooted(ws) ? ws : Directory.GetCurrentDirectory();
    }

    private static string MakeRelative(string path, string ws) =>
        path.StartsWith(ws, StringComparison.OrdinalIgnoreCase)
            ? path[ws.Length..].TrimStart(Path.DirectorySeparatorChar)
            : path;

    private sealed class Snapshot
    {
        public string Workspace { get; init; } = string.Empty;
        public Dictionary<string, FileBlob> Files { get; init; } = new();
    }

    private sealed class FileBlob
    {
        public string Hash { get; init; } = string.Empty;
        public string[] Lines { get; init; } = Array.Empty<string>();
        public bool TooLarge { get; init; }
    }

    // ---------------- LCS 统一 diff ----------------

    internal static string UnifiedDiff(IReadOnlyList<string> a, IReadOnlyList<string> b, string filePath)
    {
        var n = a.Count;
        var m = b.Count;
        var dp = new int[n + 1, m + 1];
        for (var p = n - 1; p >= 0; p--)
        {
            for (var q = m - 1; q >= 0; q--)
            {
                dp[p, q] = string.Equals(a[p], b[q], StringComparison.Ordinal)
                    ? dp[p + 1, q + 1] + 1
                    : Math.Max(dp[p + 1, q], dp[p, q + 1]);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"diff --git a/{filePath} b/{filePath}");
        var i = 0;
        var j = 0;
        while (i < n && j < m)
        {
            if (string.Equals(a[i], b[j], StringComparison.Ordinal))
            {
                i++;
                j++;
            }
            else if (dp[i + 1, j] >= dp[i, j + 1])
            {
                sb.AppendLine($"-{a[i]}");
                i++;
            }
            else
            {
                sb.AppendLine($"+{b[j]}");
                j++;
            }
        }

        while (i < n)
        {
            sb.AppendLine($"-{a[i++]}");
        }

        while (j < m)
        {
            sb.AppendLine($"+{b[j++]}");
        }

        return sb.ToString();
    }
}
