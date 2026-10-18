using System.Globalization;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.TaskFiles;
using ClosedXML.Excel;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// P1-15 高危盲区：<see cref="XlsxTaskWriter"/> 是唯一的二进制原子写回路径
/// （temp 文件 + 原子 Move）。写回中断 = 用户 xlsx 任务文件损坏，必须经断言锁住：
/// ① 缺失的规范列自动追加、② 原地原子更新且无临时文件残留、③ 目标目录不存在时自建。
/// </summary>
public class XlsxTaskWriterTests
{
    private static string TempXlsx(string name) =>
        Path.Combine(Path.GetTempPath(), $"bar-xlsxwriter-{name}-{Guid.NewGuid():N}.xlsx");

    private static void Seed(string path, params (string Id, string Instruction)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Tasks");
        ws.Cell(1, 1).Value = "id";
        ws.Cell(1, 2).Value = "instruction";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Id;
            ws.Cell(i + 2, 2).Value = rows[i].Instruction;
        }

        wb.SaveAs(path);
    }

    private static TaskItem MakeTask(string id, string instruction) => new()
    {
        Id = id,
        Instruction = instruction,
        Status = TaskStatus.Done,
        Result = "ok",
        ModelUsed = "gpt-4",
        AccountUsed = "acc-1",
        ExpertUsed = "rev",
        FallbackUsed = false,
        FallbackCost = 0m,
        RetryCount = 0,
        Error = null,
        StartedAt = DateTimeOffset.Parse("2026-10-17T10:00:00+08:00", CultureInfo.InvariantCulture),
        FinishedAt = DateTimeOffset.Parse("2026-10-17T10:01:00+08:00", CultureInfo.InvariantCulture),
        DurationSeconds = 60
    };

    [Fact]
    public async Task WriteAsync_缺失规范列_自动追加且值正确写回()
    {
        var path = TempXlsx("append");
        Seed(path, ("t1", "指令1"), ("t2", "指令2"));
        var tasks = new List<TaskItem> { MakeTask("t1", "指令1"), MakeTask("t2", "指令2") };

        await XlsxTaskWriter.WriteAsync(path, tasks);

        var issues = new List<TaskFileIssue>();
        var parsed = XlsxTaskParser.Parse(path, issues);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("ok", parsed[0].Result);
        Assert.Equal(TaskStatus.Done, parsed[0].Status);
        Assert.Equal("gpt-4", parsed[0].ModelUsed);
        // 缺失的 13 个规范列应被追加到 id/instruction 之后 → 表头共 15 列（原始仅 2 列）
        using (var wb = new XLWorkbook(path))
        {
            var headerCount = wb.Worksheets.First().Row(1).CellsUsed().Count();
            Assert.Equal(15, headerCount);
        }
    }

    [Fact]
    public async Task WriteAsync_原子写回_无临时文件残留且文件原地更新()
    {
        var path = TempXlsx("atomic");
        Seed(path, ("t1", "指令1"));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        await XlsxTaskWriter.WriteAsync(path, new List<TaskItem> { MakeTask("t1", "指令1") });

        // 原子写：temp 文件必须已被清理
        var leftovers = Directory.GetFiles(dir, "*.xlsx")
            .Where(f => Path.GetFileName(f).Contains(".tmp-", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(leftovers);
        // 目标文件仍存在且内容已更新
        Assert.True(File.Exists(path));
        var issues = new List<TaskFileIssue>();
        var parsed = XlsxTaskParser.Parse(path, issues);
        Assert.Equal("ok", parsed[0].Result);
    }
}
