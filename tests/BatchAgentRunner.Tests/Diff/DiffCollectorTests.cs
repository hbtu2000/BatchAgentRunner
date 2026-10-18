using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.Diff;
using BatchAgentRunner.Infrastructure.Logging;
using Xunit;

namespace BatchAgentRunner.Tests.Diff;

/// <summary>结果 diff 采集测试（REQ-017）：快照对比、变更类型、统一 diff 文本。</summary>
public class DiffCollectorTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("bar-diff-").FullName;

    public void Dispose()
    {
        // CA1816：SuppressFinalize 让派生类型引入终结器时无需重新实现 IDisposable
        GC.SuppressFinalize(this);
        try { Directory.Delete(_ws, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    private DiffCollector Build(params string[] globs)
    {
        var options = new RunOptions
        {
            DefaultWorkspace = _ws,
            DiffTargetGlobs = globs.ToList()
        };
        return new DiffCollector(new NullRunLogger(), options,
            new DiffCollectorOptions { TargetGlobs = globs.ToList() });
    }

    private static TaskItem TaskIn(string workspace) => new() { Id = "t1", Instruction = "x", Workspace = workspace };

    [Fact]
    public async Task 无变更_不产生记录()
    {
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "hello");
        var collector = Build("a.txt");
        var task = TaskIn(_ws);

        var snapshot = await collector.SnapshotAsync(task);
        var records = await collector.CollectAsync(snapshot, task);

        Assert.Empty(records);
    }

    [Fact]
    public async Task 修改文件_产出Modified记录与统一diff()
    {
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "line1\nline2\n");
        var collector = Build("a.txt");
        var task = TaskIn(_ws);

        var snapshot = await collector.SnapshotAsync(task);
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "line1\nline2-changed\n");
        var records = await collector.CollectAsync(snapshot, task);

        var record = Assert.Single(records);
        Assert.Equal(DiffKind.Modified, record.Kind);
        Assert.Equal("a.txt", record.FilePath);
        Assert.Contains("-line2", record.UnifiedDiff);
        Assert.Contains("+line2-changed", record.UnifiedDiff);
        Assert.Contains("diff --git", record.UnifiedDiff);
        Assert.NotEqual(record.BeforeHash, record.AfterHash);
    }

    [Fact]
    public async Task 新增文件_产出Added记录()
    {
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "seed");
        var collector = Build("*.txt");
        var task = TaskIn(_ws);

        var snapshot = await collector.SnapshotAsync(task);
        File.WriteAllText(Path.Combine(_ws, "b.txt"), "new file");
        var records = await collector.CollectAsync(snapshot, task);

        var added = records.Single(r => r.Kind == DiffKind.Added);
        Assert.Equal("b.txt", added.FilePath);
        Assert.Contains("+new file", added.UnifiedDiff);
    }

    [Fact]
    public async Task 删除文件_产出Deleted记录()
    {
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "to be removed");
        var collector = Build("a.txt");
        var task = TaskIn(_ws);

        var snapshot = await collector.SnapshotAsync(task);
        File.Delete(Path.Combine(_ws, "a.txt"));
        var records = await collector.CollectAsync(snapshot, task);

        var deleted = Assert.Single(records);
        Assert.Equal(DiffKind.Deleted, deleted.Kind);
        Assert.Equal("a.txt", deleted.FilePath);
        Assert.NotNull(deleted.BeforeHash);
    }

    [Fact]
    public async Task 快照句柄无效_返回空记录()
    {
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "x");
        var collector = Build("a.txt");

        var records = await collector.CollectAsync("not-a-real-snapshot", TaskIn(_ws));

        Assert.Empty(records);
    }

    [Fact]
    public async Task 未配置目标范围_不采集任何文件()
    {
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "before");
        var collector = Build(); // 无 glob
        var task = TaskIn(_ws);

        var snapshot = await collector.SnapshotAsync(task);
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "after");
        var records = await collector.CollectAsync(snapshot, task);

        Assert.Empty(records);
    }

    [Fact]
    public async Task 多个快照互不干扰()
    {
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "v1");
        var collector = Build("a.txt");
        var task = TaskIn(_ws);

        var snap1 = await collector.SnapshotAsync(task);
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "v2");
        var snap2 = await collector.SnapshotAsync(task);
        File.WriteAllText(Path.Combine(_ws, "a.txt"), "v3");

        var from1 = await collector.CollectAsync(snap1, task);
        var from2 = await collector.CollectAsync(snap2, task);

        Assert.Contains("+v3", Assert.Single(from1).UnifiedDiff!);
        Assert.Contains("+v3", Assert.Single(from2).UnifiedDiff!);
        Assert.Contains("-v2", Assert.Single(from2).UnifiedDiff!);
    }
}
