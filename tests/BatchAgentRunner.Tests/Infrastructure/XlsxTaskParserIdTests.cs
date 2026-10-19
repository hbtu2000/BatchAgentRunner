using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Infrastructure.TaskFiles;
using ClosedXML.Excel;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// 2026-10-18 现场回归：xlsx 任务表「部分行填了 id、部分行留空」时，
/// 旧的自动生成逻辑用「已解析条数 + 1」编号，会与后面行里显式填写的 id 撞车，
/// 进而在 TaskFileStore.NormalizeAndValidateIds 抛「任务 ID 重复」，整批加载失败。
/// 修复后：自动生成的 id 必须避开全表已填写的 id。
/// </summary>
public class XlsxTaskParserIdTests
{
    private static string TempXlsx(string name) =>
        Path.Combine(Path.GetTempPath(), $"bar-{name}-{Guid.NewGuid():N}.xlsx");

    private static void WriteSheet(string path, params (string Id, string Instruction)[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("tasks");
        sheet.Cell(1, 1).Value = "id";
        sheet.Cell(1, 2).Value = "workspace";
        sheet.Cell(1, 3).Value = "instruction";
        sheet.Cell(1, 4).Value = "status";

        for (var i = 0; i < rows.Length; i++)
        {
            var r = i + 2;
            sheet.Cell(r, 1).Value = rows[i].Id;
            sheet.Cell(r, 2).Value = "ws-a";
            sheet.Cell(r, 3).Value = rows[i].Instruction;
            sheet.Cell(r, 4).Value = "Pending";
        }

        workbook.SaveAs(path);
    }

    [Fact]
    public async Task 自动生成的_id_不与后续显式_id_撞车()
    {
        var path = TempXlsx("mixed-ids");
        try
        {
            // 复刻现场形状：第 1 行显式 task-001，中间若干行留空，之后再出现显式 task-007
            WriteSheet(path,
                ("task-001", "指令 1"),
                ("", "指令 2"),
                ("", "指令 3"),
                ("", "指令 4"),
                ("", "指令 5"),
                ("", "指令 6"),
                ("", "指令 7"),
                ("task-007", "指令 8"),
                ("", "指令 9"),
                ("task-008", "指令 10"));

            var store = new TaskFileStore();
            var result = await store.LoadAsync(path);

            Assert.Equal(10, result.Tasks.Count);

            var ids = result.Tasks.Select(t => t.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // 显式填写的 id 必须原样保留（不被自动生成覆盖）
            Assert.Contains("task-001", ids);
            Assert.Contains("task-007", ids);
            Assert.Contains("task-008", ids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 全表无_id_时仍按顺序生成()
    {
        var path = TempXlsx("no-ids");
        try
        {
            WriteSheet(path, ("", "指令 1"), ("", "指令 2"), ("", "指令 3"));

            var store = new TaskFileStore();
            var result = await store.LoadAsync(path);

            Assert.Equal(
                new[] { "task-001", "task-002", "task-003" },
                result.Tasks.Select(t => t.Id).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 真正重复的显式_id_仍然报错()
    {
        var path = TempXlsx("dup-ids");
        try
        {
            WriteSheet(path, ("task-001", "指令 1"), ("task-001", "指令 2"));

            var store = new TaskFileStore();

            await Assert.ThrowsAsync<TaskFileFormatException>(() => store.LoadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
