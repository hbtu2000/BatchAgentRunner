using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.Workflow;
using Xunit;

namespace BatchAgentRunner.Tests.Workflow;

/// <summary>模板仓库测试（二期场景模板库 S1–S16）：内置播种、列举、用户自定义、容错。</summary>
public class FileWorkflowTemplateStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bar-tpl-").FullName;

    public void Dispose()
    {
        // CA1816：SuppressFinalize 让派生类型引入终结器时无需重新实现 IDisposable
        GC.SuppressFinalize(this);
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    [Fact]
    public async Task 首次列举_自动播种16个内置模板()
    {
        var store = new FileWorkflowTemplateStore(_root);

        var all = await store.ListAsync();

        Assert.Equal(16, all.Count);
        Assert.Equal(16, all.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task 二次列举_不重复播种()
    {
        var store = new FileWorkflowTemplateStore(_root);

        await store.ListAsync();
        var again = await store.ListAsync();

        Assert.Equal(16, again.Count);
    }

    [Fact]
    public async Task 按id获取_返回对应模板()
    {
        var store = new FileWorkflowTemplateStore(_root);
        await store.ListAsync();

        var tpl = await store.GetAsync("S1");

        Assert.NotNull(tpl);
        Assert.Equal("S1", tpl!.Id);
        Assert.NotNull(tpl.Workflow);
        Assert.NotEmpty(tpl.Workflow!.Stages);
    }

    [Fact]
    public async Task 按id获取_不存在时返回null()
    {
        var store = new FileWorkflowTemplateStore(_root);
        await store.ListAsync();

        Assert.Null(await store.GetAsync("S999"));
    }

    [Fact]
    public async Task 保存用户模板_出现在列表中()
    {
        var store = new FileWorkflowTemplateStore(_root);
        await store.ListAsync();

        await store.SaveUserTemplateAsync(new TemplateSpec
        {
            Id = "U1",
            Name = "我的模板",
            InstructionTemplate = "处理 {var:thing}",
            Vars = new List<TemplateVarDef> { new() { Name = "thing", Default = "X" } }
        });
        var all = await store.ListAsync();

        Assert.Equal(17, all.Count);
        Assert.Contains(all, t => string.Equals(t.Id, "U1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 用户模板覆盖同名内置模板_获取返回用户版()
    {
        var store = new FileWorkflowTemplateStore(_root);
        await store.ListAsync();

        await store.SaveUserTemplateAsync(new TemplateSpec { Id = "S1", Name = "覆盖版" });

        var fetched = await store.GetAsync("S1");
        Assert.Equal("覆盖版", fetched!.Name);
    }

    [Fact]
    public async Task 损坏的模板文件_被跳过不影响其他模板()
    {
        var store = new FileWorkflowTemplateStore(_root);
        await store.ListAsync();

        var userDir = Path.Combine(_root, "user");
        Directory.CreateDirectory(userDir);
        await File.WriteAllTextAsync(Path.Combine(userDir, "broken.json"), "{ 这不是合法 json ");

        var all = await store.ListAsync();

        Assert.Equal(16, all.Count);
    }

    [Fact]
    public async Task 所有内置模板_工作流阶段id唯一且指令非空()
    {
        var store = new FileWorkflowTemplateStore(_root);
        var all = await store.ListAsync();

        foreach (var tpl in all.Where(t => t.Workflow is not null))
        {
            var ids = tpl.Workflow!.Stages.Select(s => s.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(tpl.Workflow!.Stages, s => Assert.False(string.IsNullOrWhiteSpace(s.InstructionTemplate)));
        }
    }
}
