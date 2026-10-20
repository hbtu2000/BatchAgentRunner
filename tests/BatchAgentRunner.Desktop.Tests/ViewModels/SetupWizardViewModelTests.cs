using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P1-19：配置向导 ViewModel 白箱单测（Desktop 回归面之一）。
/// 锁住步骤流转、执行工具选择、保存回写等契约；不触发真实 CLI 检测（避免外部进程副作用）。
/// </summary>
public class SetupWizardViewModelTests
{
    [Fact]
    public void 初始处于步骤0且为第一步()
    {
        var fake = new FakeEngineHost();
        var vm = new SetupWizardViewModel(fake);

        Assert.Equal(0, vm.Step);
        Assert.True(vm.IsStep0);
        Assert.True(vm.IsFirstStep);
        Assert.False(vm.IsLastStep);
        Assert.Equal("第 1 / 2 步", vm.StepIndicator);
        Assert.Equal("欢迎 · 选择执行工具", vm.StepTitle);
    }

    [Fact]
    public void Next_Back_在边界钳制()
    {
        var fake = new FakeEngineHost();
        var vm = new SetupWizardViewModel(fake);

        vm.NextCommand.Execute(null);
        Assert.Equal(1, vm.Step);
        Assert.True(vm.IsStep1);
        Assert.True(vm.IsLastStep);

        vm.NextCommand.Execute(null); // 已到最后一步，钳制
        Assert.Equal(1, vm.Step);

        vm.BackCommand.Execute(null);
        Assert.Equal(0, vm.Step);

        vm.BackCommand.Execute(null); // 已到第一步，钳制
        Assert.Equal(0, vm.Step);
    }

    [Fact]
    public void AgentIsX_设置同步AgentKind()
    {
        var fake = new FakeEngineHost();
        var vm = new SetupWizardViewModel(fake);

        vm.AgentIsCodeBuddy = true;
        Assert.Equal(AgentKind.CodeBuddy, vm.AgentKind);

        vm.AgentIsWorkBuddy = true;
        Assert.Equal(AgentKind.WorkBuddy, vm.AgentKind);

        vm.AgentIsMock = true;
        Assert.Equal(AgentKind.Mock, vm.AgentKind);
    }

    [Fact]
    public async Task SaveAsync_写入AgentKind与CliPath并触发落盘与重建()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new SetupWizardViewModel(fake);
        vm.AgentIsCodeBuddy = true;
        vm.CliPath = "/usr/local/bin/codebuddy";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(AgentKind.CodeBuddy, fake.Options.AgentKind);
        Assert.Equal("/usr/local/bin/codebuddy", fake.Options.CliPath);
        Assert.Equal(1, fake.SaveConfigAsyncCallCount);
        Assert.Equal(1, fake.RebuildEngineCallCount);
        Assert.False(string.IsNullOrEmpty(vm.CompletedMessage));
    }
}
