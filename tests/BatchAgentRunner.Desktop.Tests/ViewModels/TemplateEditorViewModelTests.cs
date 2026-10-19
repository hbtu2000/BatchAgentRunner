using System.Collections.Generic;
using System.IO;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P1-19：模板库页 ViewModel 白箱单测（churn 热点之一）。验证单指令模板的变量多值展开契约，
/// 全程零文件 IO（不触碰 <c>FileWorkflowTemplateStore</c>）。
/// </summary>
public class TemplateEditorViewModelTests
{
    [Fact]
    public void 构造产出默认输出路径且未加载模板()
    {
        var fake = new FakeEngineHost();
        var vm = new TemplateEditorViewModel(fake);

        Assert.EndsWith("template-output.tasks.json", vm.OutputPath);
        Assert.Empty(vm.Templates);
        Assert.False(vm.HasPreviewTasks);
        Assert.False(string.IsNullOrEmpty(vm.Workspace));
    }

    [Fact]
    public void Preview_命名工作空间按名称写入生成的任务()
    {
        var spec = new TemplateSpec
        {
            Name = "demo",
            InstructionTemplate = "处理 {var:topic}",
            Vars = new List<TemplateVarDef> { new() { Name = "topic", Default = "X" } },
        };
        var fake = new FakeEngineHost();
        fake.Options.Workspaces.Add(new WorkspaceConfig
        {
            Name = "ws-a",
            Path = Path.Combine(Path.GetTempPath(), "wsdir")
        });
        var vm = new TemplateEditorViewModel(fake);
        vm.SelectedTemplate = new TemplateRowViewModel(spec);
        vm.Workspace = "ws-a";

        vm.Preview();

        var task = Assert.Single(vm.PreviewTasks);
        Assert.Equal("ws-a", task.Task.Workspace);
    }

    [Fact]
    public void Preview_目录路径工作空间按绝对路径写入生成的任务()
    {
        var spec = new TemplateSpec
        {
            Name = "demo",
            InstructionTemplate = "处理 {var:topic}",
            Vars = new List<TemplateVarDef> { new() { Name = "topic", Default = "X" } },
        };
        var fake = new FakeEngineHost();
        var vm = new TemplateEditorViewModel(fake);
        vm.SelectedTemplate = new TemplateRowViewModel(spec);
        var dir = Path.Combine(Path.GetTempPath(), "myproject");
        vm.Workspace = dir;

        vm.Preview();

        var task = Assert.Single(vm.PreviewTasks);
        Assert.Equal(dir, task.Task.Workspace);
    }

    [Fact]
    public void Preview_变量多值展开为多条任务()
    {
        var spec = new TemplateSpec
        {
            Name = "demo",
            InstructionTemplate = "处理 {var:topic}",
            Vars = new List<TemplateVarDef> { new() { Name = "topic", Values = new List<string> { "x", "y" } } },
        };
        var fake = new FakeEngineHost();
        var vm = new TemplateEditorViewModel(fake);
        vm.SelectedTemplate = new TemplateRowViewModel(spec);

        // LoadVarInputs 已按 spec.Vars 生成 VarInputs；设定多值后展开
        var input = vm.VarInputs[0];
        input.ValuesText = "x\ny";
        vm.Preview();

        Assert.Equal(2, vm.PreviewTasks.Count);
        Assert.True(vm.HasPreviewTasks);
        Assert.Contains(vm.PreviewTasks, t => t.Instruction.Contains('x'));
        Assert.Contains(vm.PreviewTasks, t => t.Instruction.Contains('y'));
    }

    [Fact]
    public void Preview_未选模板不抛异常且清空()
    {
        var fake = new FakeEngineHost();
        var vm = new TemplateEditorViewModel(fake);

        vm.Preview();

        Assert.Empty(vm.PreviewTasks);
    }
}
