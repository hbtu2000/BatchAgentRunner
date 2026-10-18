using System.Collections.Generic;
using System.Threading.Tasks;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P1-19：配置页 ViewModel 白箱单测（阶段 2 文档点名的 churn 热点之一）。
/// 仅依赖 <see cref="FakeEngineHost"/>，把 RunOptions ↔ 属性 的双向映射不变量锁在协作者契约上。
/// </summary>
public class ConfigViewModelTests
{
    [Fact]
    public void Reload_映射RunOptions到属性()
    {
        var options = new RunOptions
        {
            Concurrency = 5,
            AgentKind = AgentKind.CodeBuddy,
            EnableSessionResume = true,
            AutoAnswer = { AllowedWhitelist = new List<string> { "x", "y" } },
            DiffTargetGlobs = new List<string> { "a", "b" },
        };
        var fake = new FakeEngineHost(options);
        var vm = new ConfigViewModel(fake);

        Assert.Equal(5, vm.Concurrency);
        Assert.Equal(AgentKind.CodeBuddy, vm.AgentKind);
        Assert.True(vm.EnableSessionResume);
        Assert.Equal("x\ny", vm.WhitelistText);
        Assert.Equal("a\nb", vm.DiffGlobsText);
        Assert.Equal(ModelPoolMode.Shared, vm.ModelPoolMode);
    }

    [Fact]
    public async Task SaveAsync_字段改动回写RunOptions且触发落盘与重建()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new ConfigViewModel(fake);
        vm.Concurrency = 7;
        vm.EnableSessionResume = true;
        vm.WhitelistText = " a \n\n b ";
        vm.DiffGlobsText = "*.cs\n*.md";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(7, fake.Options.Concurrency);
        Assert.True(fake.Options.EnableSessionResume);
        Assert.Equal(new[] { "a", "b" }, fake.Options.AutoAnswer.AllowedWhitelist);
        Assert.Equal(new[] { "*.cs", "*.md" }, fake.Options.DiffTargetGlobs);
        Assert.Equal(1, fake.SaveConfigAsyncCallCount);
        Assert.Equal(1, fake.RebuildEngineCallCount);
    }

    [Fact]
    public async Task SaveAsync_零值下限钳制为1()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new ConfigViewModel(fake);
        vm.Concurrency = 0;
        vm.MaxAttempts = 0;
        vm.TaskTimeoutMinutes = 0;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.Options.Concurrency);
        Assert.Equal(1, fake.Options.MaxAttemptsPerTask);
        Assert.Equal(1, fake.Options.TaskTimeoutMinutes);
    }

    [Fact]
    public void Reload_白名单往返不丢项()
    {
        var options = new RunOptions { AutoAnswer = { AllowedWhitelist = new List<string> { "p", "q" } } };
        var fake = new FakeEngineHost(options);
        var vm = new ConfigViewModel(fake);

        Assert.Equal("p\nq", vm.WhitelistText);
        Assert.Equal(new[] { "p", "q" }, fake.Options.AutoAnswer.AllowedWhitelist);
    }
}
