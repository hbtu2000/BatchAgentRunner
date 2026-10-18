using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using Xunit;

namespace BatchAgentRunner.Tests.Scheduling;

public class StageTemplateRendererTests
{
    [Fact]
    public void 渲染_变量与任务字段()
    {
        var ctx = new WorkflowRenderContext();
        ctx.SetVar("lang", "C#");
        ctx.SetTaskField("instruction", "实现需求");
        ctx.SetStageOutput("draft", "草稿内容");

        var result = StageTemplateRenderer.Render(
            "请用 {var:lang} 完成：{task.instruction}\n参考：{output:draft}", ctx);

        Assert.Equal("请用 C# 完成：实现需求\n参考：草稿内容", result.Text);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void 未解析占位符_保留原样并计入()
    {
        var ctx = new WorkflowRenderContext();
        ctx.SetVar("known", "值");

        var result = StageTemplateRenderer.Render("{var:known} / {var:missing} / {output:nope}", ctx);

        Assert.Equal("值 / {var:missing} / {output:nope}", result.Text);
        Assert.Contains("{var:missing}", result.Unresolved);
        Assert.Contains("{output:nope}", result.Unresolved);
    }

    [Fact]
    public void 空模板_返回空文本()
    {
        var result = StageTemplateRenderer.Render(string.Empty, new WorkflowRenderContext());

        Assert.Equal(string.Empty, result.Text);
    }
}

public class TemplateExpanderTests
{
    [Fact]
    public void 字面量笛卡尔积_每组合生成一个任务()
    {
        var template = new TemplateSpec
        {
            Id = "tpl",
            Name = "模板",
            InstructionTemplate = "为 {var:region} 生成 {var:kind} 报告",
            Vars = new List<TemplateVarDef>
            {
                new() { Name = "region", Values = new List<string> { "华东", "华南" } },
                new() { Name = "kind", Values = new List<string> { "销售", "库存" } }
            }
        };

        var tasks = TemplateExpander.Expand(template);

        Assert.Equal(4, tasks.Count);
        Assert.Contains(tasks, t => t.Instruction == "为 华东 生成 销售 报告");
        Assert.Contains(tasks, t => t.Instruction == "为 华南 生成 库存 报告");
        Assert.Equal("tpl", tasks[0].TemplateVars!.TemplateId);
    }

    [Fact]
    public void 单变量单值_生成一个任务()
    {
        var template = new TemplateSpec
        {
            Id = "tpl",
            InstructionTemplate = "处理 {var:name}",
            Vars = new List<TemplateVarDef> { new() { Name = "name", Default = "X" } }
        };

        var tasks = TemplateExpander.Expand(template);

        Assert.Single(tasks);
        Assert.Equal("处理 X", tasks[0].Instruction);
    }

    [Fact]
    public void 链式依赖_后一任务依赖前一任务()
    {
        var template = new TemplateSpec
        {
            Id = "chain",
            InstructionTemplate = "第 {var:n} 步",
            Vars = new List<TemplateVarDef> { new() { Name = "n", Values = new List<string> { "1", "2", "3" } } },
            ChainDependency = true
        };

        var tasks = TemplateExpander.Expand(template);

        Assert.Equal(3, tasks.Count);
        Assert.Null(tasks[0].DependsOn);
        Assert.Equal(new List<string> { "chain-001" }, tasks[1].DependsOn);
        Assert.Equal(new List<string> { "chain-002" }, tasks[2].DependsOn);
    }

    [Fact]
    public void 无链式依赖_任务相互独立()
    {
        var template = new TemplateSpec
        {
            Id = "flat",
            InstructionTemplate = "处理 {var:n}",
            Vars = new List<TemplateVarDef> { new() { Name = "n", Values = new List<string> { "1", "2" } } },
            ChainDependency = false
        };

        var tasks = TemplateExpander.Expand(template);

        Assert.All(tasks, t => Assert.Null(t.DependsOn));
    }

    [Fact]
    public void 模板可携带工作流_变量注入各阶段指令()
    {
        var template = new TemplateSpec
        {
            Id = "wf",
            InstructionTemplate = "整体：{var:target}",
            Vars = new List<TemplateVarDef> { new() { Name = "target", Values = new List<string> { "登录模块" } } },
            Workflow = new WorkflowSpec
            {
                Name = "测试流程",
                Stages = new List<WorkflowStage>
                {
                    new() { Id = "s1", Name = "设计", InstructionTemplate = "设计 {var:target}" },
                    new() { Id = "s2", Name = "实现", InstructionTemplate = "实现 {var:target}：{output:s1}" }
                }
            }
        };

        var tasks = TemplateExpander.Expand(template);

        var wf = Assert.Single(tasks).Workflow!;
        Assert.Equal("设计 登录模块", wf.Stages[0].InstructionTemplate);
        Assert.Equal("实现 登录模块：{output:s1}", wf.Stages[1].InstructionTemplate);
    }

    [Fact]
    public void 展开产物与手写任务字节级等价()
    {
        var template = new TemplateSpec
        {
            Id = "eq",
            InstructionTemplate = "固定指令（无变量）",
            Vars = new List<TemplateVarDef>()
        };

        var tasks = TemplateExpander.Expand(template);

        var single = Assert.Single(tasks);
        Assert.Equal("固定指令（无变量）", single.Instruction);
    }
}
