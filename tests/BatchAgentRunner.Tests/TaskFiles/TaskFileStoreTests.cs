using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.TaskFiles;
using ClosedXML.Excel;
using Xunit;

namespace BatchAgentRunner.Tests.TaskFiles;

public class TaskFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bar-tests-" + Guid.NewGuid().ToString("N"));

    public TaskFileStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // CA1816：SuppressFinalize 让派生类型引入终结器时无需重新实现 IDisposable
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private string Path_(string name) => System.IO.Path.Combine(_root, name);

    [Fact]
    public void Markdown_解析二级标题与多行指令()
    {
        var md = """
                 # Tasks

                 ## task-001
                 - instruction: 将 src/utils.ts 中所有函数补充 JSDoc 注释

                 ## task-002
                 - instruction: 修复登录模块缺陷：
                   1. 定位 src/auth/login.ts 的空指针风险并修复
                   2. 补充回归用例

                 ## task-003
                 - instruction: 为 src/parser.ts 补充单元测试
                 """;

        var issues = new List<TaskFileIssue>();
        var tasks = MarkdownTaskParser.Parse(md, issues);

        Assert.Equal(3, tasks.Count);
        Assert.Equal("task-001", tasks[0].Id);
        Assert.Equal("将 src/utils.ts 中所有函数补充 JSDoc 注释", tasks[0].Instruction);
        Assert.Contains("空指针风险", tasks[1].Instruction);
        Assert.Contains("补充回归用例", tasks[1].Instruction);
        Assert.Equal(TaskStatus.Pending, tasks[2].Status);
    }

    [Fact]
    public void Markdown_缺少ID时自动生成并提示()
    {
        var md = "## \n- instruction: a\n";
        var issues = new List<TaskFileIssue>();

        var tasks = MarkdownTaskParser.Parse(md, issues);

        Assert.Equal("task-001", tasks[0].Id);
        Assert.Contains(issues, i => i.Message.Contains("自动生成"));
    }

    [Fact]
    public async Task JSON_空字符串时间戳可解析为空()
    {
        var file = Path_("tasks.json");
        File.WriteAllText(file, """
        [
          { "id": "task-001", "instruction": "hello", "status": "pending", "started_at": "", "finished_at": "", "duration": 0 }
        ]
        """);

        var store = new TaskFileStore();
        var result = await store.LoadAsync(file);

        Assert.Single(result.Tasks);
        Assert.Null(result.Tasks[0].StartedAt);
        Assert.Equal("task-001", result.Tasks[0].Id);
    }

    [Fact]
    public async Task JSON_原地回写并保留字段()
    {
        var file = Path_("tasks.json");
        File.WriteAllText(file, """[{ "id": "t1", "instruction": "x" }]""");
        var store = new TaskFileStore();

        var loaded = await store.LoadAsync(file);
        loaded.Tasks[0].Status = TaskStatus.Done;
        loaded.Tasks[0].Result = "已完成";
        loaded.Tasks[0].ModelUsed = "m1";
        loaded.Tasks[0].AccountUsed = "acc1";
        loaded.Tasks[0].DurationSeconds = 12.5;
        await store.SaveAsync(file, loaded.Tasks);

        var reloaded = await store.LoadAsync(file);

        Assert.Equal(TaskStatus.Done, reloaded.Tasks[0].Status);
        Assert.Equal("已完成", reloaded.Tasks[0].Result);
        Assert.Equal("m1", reloaded.Tasks[0].ModelUsed);
        Assert.Equal(12.5, reloaded.Tasks[0].DurationSeconds);
        Assert.Contains("\"model_used\"", File.ReadAllText(file));
    }

    [Fact]
    public async Task Markdown_结果写入sidecar且不改动原文()
    {
        var file = Path_("tasks.md");
        var original = "## task-001\n- instruction: hello\n";
        File.WriteAllText(file, original);
        var store = new TaskFileStore();

        var loaded = await store.LoadAsync(file);
        loaded.Tasks[0].Status = TaskStatus.Done;
        loaded.Tasks[0].Result = "结果";
        await store.SaveAsync(file, loaded.Tasks);

        var sidecar = file + ".result.json";

        Assert.True(File.Exists(sidecar));
        Assert.Equal(original, File.ReadAllText(file));

        var reloaded = await store.LoadAsync(file);
        Assert.Equal(TaskStatus.Done, reloaded.Tasks[0].Status);
        Assert.Equal("结果", reloaded.Tasks[0].Result);
    }

    [Fact]
    public async Task 重复ID报错定位()
    {
        var file = Path_("dup.json");
        File.WriteAllText(file, """[{ "id": "t1", "instruction": "a" }, { "id": "t1", "instruction": "b" }]""");

        // xUnit1031：改 async 测试后异常不再被 AggregateException 包裹，直接断言具体类型
        var ex = await Assert.ThrowsAsync<TaskFileFormatException>(() => new TaskFileStore().LoadAsync(file));
        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public async Task 非法ID字符报错()
    {
        var file = Path_("bad.json");
        File.WriteAllText(file, """[{ "id": "task 001", "instruction": "a" }]""");

        var ex = await Assert.ThrowsAsync<TaskFileFormatException>(() => new TaskFileStore().LoadAsync(file));
        Assert.Contains("非法字符", ex.Message);
    }

    [Fact]
    public async Task 备份保留最近N份()
    {
        var file = Path_("backup.json");
        File.WriteAllText(file, "[]");
        var store = new TaskFileStore();

        for (var i = 0; i < 7; i++)
        {
            Thread.Sleep(1100); // 时间戳精确到秒，需错开
            await store.BackupAsync(file, 5);
        }

        var backups = Directory.GetFiles(_root, "backup.json.backup-*");
        Assert.Equal(5, backups.Length);
    }

    [Fact]
    public async Task 原子写_目标文件始终可读()
    {
        var file = Path_("atomic.json");
        await AtomicFileWriter.WriteAsync(file, """[{"id":"t1"}]""");

        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetFiles(_root, "atomic.json.tmp-*"));
        Assert.Equal("""[{"id":"t1"}]""".Length, File.ReadAllText(file).Length);
    }

    private static string WriteXlsx(string path, Action<IXLWorksheet> configure)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Tasks");
        configure(ws);
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public async Task Xlsx_解析id指令及中文表头()
    {
        var file = Path_("tasks.xlsx");
        WriteXlsx(file, ws =>
        {
            ws.Cell(1, 1).Value = "编号";
            ws.Cell(1, 2).Value = "指令";
            ws.Cell(1, 3).Value = "工作区";
            ws.Cell(2, 1).Value = "task-001";
            ws.Cell(2, 2).Value = "为 src/parser.ts 补充单元测试";
            ws.Cell(2, 3).Value = "NexYaw3D";
            ws.Cell(3, 2).Value = "修复登录模块缺陷"; // 缺 id，应自动生成
        });

        var store = new TaskFileStore();
        var result = await store.LoadAsync(file);

        Assert.Equal(2, result.Tasks.Count);
        Assert.Equal("task-001", result.Tasks[0].Id);
        Assert.Equal("NexYaw3D", result.Tasks[0].Workspace);
        Assert.Equal("为 src/parser.ts 补充单元测试", result.Tasks[0].Instruction);
        Assert.StartsWith("task-", result.Tasks[1].Id);
        Assert.Equal(TaskStatus.Pending, result.Tasks[1].Status);
    }

    [Fact]
    public async Task Xlsx_原地回写并保留结果列()
    {
        var file = Path_("tasks.xlsx");
        WriteXlsx(file, ws =>
        {
            ws.Cell(1, 1).Value = "id";
            ws.Cell(1, 2).Value = "instruction";
            ws.Cell(2, 1).Value = "t1";
            ws.Cell(2, 2).Value = "hello";
            ws.Cell(1, 5).Value = "自定义列"; // 用户自定义列应保留
            ws.Cell(2, 5).Value = "保留我";
        });

        var store = new TaskFileStore();
        var loaded = await store.LoadAsync(file);
        loaded.Tasks[0].Status = TaskStatus.Done;
        loaded.Tasks[0].Result = "已完成";
        loaded.Tasks[0].ModelUsed = "m1";
        loaded.Tasks[0].DurationSeconds = 12.5;
        await store.SaveAsync(file, loaded.Tasks);

        var reloaded = await store.LoadAsync(file);
        Assert.Equal(TaskStatus.Done, reloaded.Tasks[0].Status);
        Assert.Equal("已完成", reloaded.Tasks[0].Result);
        Assert.Equal("m1", reloaded.Tasks[0].ModelUsed);
        Assert.Equal(12.5, reloaded.Tasks[0].DurationSeconds);

        // 用户自定义列原样保留
        using var wb = new XLWorkbook(file);
        var ws = wb.Worksheet(1);
        Assert.Equal("保留我", ws.Cell(2, 5).GetString());
        // 缺失的规范列（如 workspace / status）被追加到末列；表头中应存在 status
        var header = ws.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("status", header);
        Assert.Contains("workspace", header);
    }

    [Fact]
    public async Task Xlsx_缺少必要列表头报错()
    {
        var file = Path_("bad.xlsx");
        WriteXlsx(file, ws =>
        {
            ws.Cell(1, 1).Value = "备注";
            ws.Cell(2, 1).Value = "无任务";
        });

        var ex = await Assert.ThrowsAsync<TaskFileFormatException>(() => new TaskFileStore().LoadAsync(file));
        Assert.Contains("id", ex.Message);
    }
}
