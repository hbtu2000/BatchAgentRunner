using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.TaskFiles;
using Xunit;

namespace BatchAgentRunner.Tests.TaskFiles;

/// <summary>Markdown 原地回写测试（二期 15.4.7）：托管区哨兵、幂等、不破坏手写指令。</summary>
[Trait("Category", "Invariant")]
public class MarkdownInPlaceWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bar-md-").FullName;

    public void Dispose()
    {
        // CA1816：SuppressFinalize 让派生类型引入终结器时无需重新实现 IDisposable
        GC.SuppressFinalize(this);
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    private static TaskItem Done(string id) => new()
    {
        Id = id,
        Instruction = "实现功能",
        Status = TaskStatus.Done,
        ModelUsed = "m1",
        AccountUsed = "账号 1",
        StartedAt = DateTimeOffset.Parse("2026-10-17T02:00:00+08:00"),
        FinishedAt = DateTimeOffset.Parse("2026-10-17T02:05:00+08:00"),
        DurationSeconds = 300,
        Result = "已完成"
    };

    private string WriteMd(string content)
    {
        var path = Path.Combine(_dir, "tasks.md");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task 首次回写_在标题块后插入托管区()
    {
        var path = WriteMd("# 任务清单\n\n## task-001\n\n实现功能\n\n## task-002\n\n另一个任务\n");

        await new MarkdownInPlaceWriter().WriteBackAsync(path, Done("task-001"));

        var text = File.ReadAllText(path);
        Assert.Contains("<!-- batch-agent-runner:meta task-001 -->", text);
        Assert.Contains("- status: Done", text);
        Assert.Contains("实现功能", text); // 手写指令保留
    }

    [Fact]
    public async Task 二次回写_替换而非追加托管区()
    {
        var path = WriteMd("## task-001\n\n实现功能\n");
        var writer = new MarkdownInPlaceWriter();

        await writer.WriteBackAsync(path, Done("task-001"));
        var failed = Done("task-001");
        failed.Status = TaskStatus.Failed;
        await writer.WriteBackAsync(path, failed);

        var text = File.ReadAllText(path);
        Assert.Equal(1, CountOccurrences(text, "batch-agent-runner:meta task-001"));
        Assert.Contains("- status: Failed", text);
        Assert.DoesNotContain("- status: Done", text);
    }

    [Fact]
    public async Task 未找到任务标题_文件保持原样()
    {
        var original = "# 无关内容\n\n没有匹配标题\n";
        var path = WriteMd(original);

        await new MarkdownInPlaceWriter().WriteBackAsync(path, Done("task-999"));

        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task 多任务_各自管理区互不影响()
    {
        var path = WriteMd("## task-001\n\n甲\n\n## task-002\n\n乙\n");
        var writer = new MarkdownInPlaceWriter();

        await writer.WriteBackAsync(path, Done("task-001"));
        var second = Done("task-002");
        second.Result = "乙完成";
        await writer.WriteBackAsync(path, second);

        var text = File.ReadAllText(path);
        Assert.Contains("<!-- batch-agent-runner:meta task-001 -->", text);
        Assert.Contains("<!-- batch-agent-runner:meta task-002 -->", text);
        Assert.Contains("甲", text);
        Assert.Contains("乙", text);
    }

    [Fact]
    public async Task 工作流阶段结果_写入托管区()
    {
        var task = Done("task-001");
        task.StageResults = new List<StageResult>
        {
            new() { StageId = "s1", Status = TaskStatus.Done, GatePassed = true },
            new() { StageId = "s2", Status = TaskStatus.Done }
        };
        var path = WriteMd("## task-001\n\n实现功能\n");

        await new MarkdownInPlaceWriter().WriteBackAsync(path, task);

        var text = File.ReadAllText(path);
        Assert.Contains("- stages:", text);
        Assert.Contains("s1: Done (gate✓)", text);
        Assert.Contains("s2: Done", text);
    }

    /// <summary>
    /// P0-8 不变量：回写必须是「临时文件 + 原子替换」，不得再出现 File.WriteAllTextAsync 直接覆写。
    /// 观测方式：写入完成后目录里不得残留 .tmp-* 临时文件（残留说明走了非原子路径或原子写未收尾）。
    /// </summary>
    [Fact]
    public async Task 回写走原子写_目录内不残留临时文件()
    {
        var path = WriteMd("## task-001\n\n实现功能\n");

        await new MarkdownInPlaceWriter().WriteBackAsync(path, Done("task-001"));

        var leftovers = Directory.GetFiles(_dir).Select(Path.GetFileName).Where(n => n!.Contains(".tmp-")).ToList();
        Assert.Empty(leftovers);
        Assert.Single(Directory.GetFiles(_dir));
        Assert.Contains("<!-- batch-agent-runner:meta task-001 -->", File.ReadAllText(path));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }

        return count;
    }
}
