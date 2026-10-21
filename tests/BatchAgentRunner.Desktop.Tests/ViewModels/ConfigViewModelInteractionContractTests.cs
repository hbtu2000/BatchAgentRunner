using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Models;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P-04 准入：配置页交互行为契约（与 <see cref="ConfigViewModelTests"/> 的映射契约互补）。
/// 分解 <see cref="ConfigViewModel"/> 之前，先把工作空间上限拦截、区段导航与准备度计算、
/// 模型拉取填充、取消重载、Vault 快照刷新锁死在 facade 契约上——结构改造后断言不变即证明
/// UI 行为等价（配合 <c>ConfigViewBindingContractTests</c> 的绑定路径守卫双重保险）。
/// </summary>
public class ConfigViewModelInteractionContractTests
{
    [Fact]
    public async Task SaveAsync_工作空间超过上限_拦截保存且不落盘不重建()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new ConfigViewModel(fake);
        vm.MaxWorkspaces = 5;
        vm.AddWorkspaceCommand.Execute(null);
        vm.AddWorkspaceCommand.Execute(null);
        vm.AddWorkspaceCommand.Execute(null);
        vm.MaxWorkspaces = 2; // 3 个工作空间 > 上限 2

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.SaveConfigAsyncCallCount);
        Assert.Equal(0, fake.RebuildEngineCallCount);
        Assert.Contains("保存被拦截", vm.StatusMessage);
        Assert.Contains("已超过上限", vm.StatusMessage);
    }

    [Fact]
    public void AddWorkspace_达到上限_拦截新增并提示()
    {
        var vm = new ConfigViewModel(new FakeEngineHost(new RunOptions()));

        vm.MaxWorkspaces = 1;
        vm.AddWorkspaceCommand.Execute(null);
        vm.AddWorkspaceCommand.Execute(null); // 第二次被拦截

        Assert.Single(vm.Workspaces);
        Assert.True(vm.WorkspaceLimitReached);
        Assert.Equal("当前 1 / 上限 1", vm.WorkspaceLimitHint);
        Assert.Contains("已达上限", vm.StatusMessage);
    }

    [Fact]
    public void RemoveWorkspace_删除后刷新上限相关UI()
    {
        var vm = new ConfigViewModel(new FakeEngineHost(new RunOptions()));
        vm.MaxWorkspaces = 2;
        vm.AddWorkspaceCommand.Execute(null);
        vm.AddWorkspaceCommand.Execute(null);
        Assert.True(vm.WorkspaceLimitReached);

        vm.RemoveWorkspaceCommand.Execute(vm.Workspaces[0]);

        Assert.Single(vm.Workspaces);
        Assert.False(vm.WorkspaceLimitReached);
        Assert.Equal("当前 1 / 上限 2", vm.WorkspaceLimitHint);
    }

    [Fact]
    public void SectionNavigate_切换激活区段并刷新准备度()
    {
        var vm = new ConfigViewModel(new FakeEngineHost(new RunOptions()));
        Assert.Equal("quickstart", vm.ActiveSection);

        vm.SectionNavigateCommand.Execute("safety");

        Assert.Equal("safety", vm.ActiveSection);
        Assert.Contains("可随时运行", vm.ReadinessSummary);

        // 空区段名：保持当前区段（仅刷新准备度）
        vm.SectionNavigateCommand.Execute(null);
        Assert.Equal("safety", vm.ActiveSection);
    }

    [Fact]
    public void RefreshReadiness_按完成度计算步骤状态与摘要()
    {
        var fake = new FakeEngineHost(new RunOptions
        {
            TaskFilePath = "t.json",
            Workspaces = { new WorkspaceConfig { Name = "w1", Path = "D:/w1" } },
            Accounts = { new AccountConfig { Id = "a1" } },
            ModelCandidates = new List<ModelCandidate> { new ModelCandidate { Name = "m1" } },
        });
        var vm = new ConfigViewModel(fake);

        // 执行工具/账号/模型/任务文件/工作空间均完成，仅「保存」未完成
        Assert.Equal("完成 5 / 6 步，可随时运行", vm.ReadinessSummary);
        Assert.Equal("done", vm.ReadinessSteps[0].State);
        Assert.Equal("done", vm.ReadinessSteps[1].State);
        Assert.Equal("done", vm.ReadinessSteps[2].State);
        Assert.Equal("done", vm.ReadinessSteps[3].State);
        Assert.Equal("done", vm.ReadinessSteps[4].State);
        Assert.Equal("current", vm.ReadinessSteps[5].State); // 第一个未完成步即当前步
    }

    [Fact]
    public async Task RefreshReadiness_保存后第六步完成()
    {
        var fake = new FakeEngineHost(new RunOptions
        {
            TaskFilePath = "t.json",
            Workspaces = { new WorkspaceConfig { Name = "w1", Path = "D:/w1" } },
            Accounts = { new AccountConfig { Id = "a1" } },
            ModelCandidates = new List<ModelCandidate> { new ModelCandidate { Name = "m1" } },
        });
        var vm = new ConfigViewModel(fake);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("完成 6 / 6 步，可随时运行", vm.ReadinessSummary);
        Assert.Equal("done", vm.ReadinessSteps[5].State);
        Assert.Contains("配置已保存", vm.StatusMessage);
        Assert.Contains("引擎已按新配置重建", vm.StatusMessage);
    }

    [Fact]
    public async Task FetchModels_拉取可用模型_填充共享下拉并按目录倍率展示折扣()
    {
        var fake = new FakeEngineHost(new RunOptions());
        fake.SetFetchModelsResult(new[]
        {
            new ModelCatalogEntry("gpt-5", 0.79m),
            new ModelCatalogEntry("gpt-4", null),
        });
        var vm = new ConfigViewModel(fake);

        await vm.FetchModelsCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.AvailableModels.Count);
        Assert.Equal("gpt-5", vm.AvailableModels[0].Name);
        Assert.Equal("0.79x", vm.AvailableModels[0].DiscountDisplay);
        Assert.Equal("gpt-4", vm.AvailableModels[1].Name);
        Assert.Equal(string.Empty, vm.AvailableModels[1].DiscountDisplay);
        Assert.Contains("已拉取 2 个可用模型", vm.StatusMessage);
        Assert.False(vm.IsFetchingModels);
    }

    [Fact]
    public async Task FetchModels_已配折扣的模型_目录无倍率时回退展示折扣且未收录的追加尾部()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new ConfigViewModel(fake);
        vm.Models.Add(new ModelEditor(new ModelCandidate { Name = "m1", Discount = 0.65m }));
        fake.SetFetchModelsResult(new[]
        {
            new ModelCatalogEntry("m1", null),  // 目录无倍率 → 回退已配折扣
            new ModelCatalogEntry("m2", 0.5m),
            new ModelCatalogEntry("m1", null),  // 目录内重名去重
        });

        await vm.FetchModelsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "m1", "m2" }, vm.AvailableModels.Select(o => o.Name));
        Assert.Equal("0.65x", vm.AvailableModels[0].DiscountDisplay);
        Assert.Equal("0.50x", vm.AvailableModels[1].DiscountDisplay);
    }

    [Fact]
    public async Task FetchModels_空目录_提示直接输入模型名()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new ConfigViewModel(fake);
        // 未设置目录 = 空清单（桩恒成功）；失败路径经 host 抛异常覆盖
        fake.SetFetchModelsResult(Array.Empty<ModelCatalogEntry>());

        await vm.FetchModelsCommand.ExecuteAsync(null);

        Assert.Empty(vm.AvailableModels);
        Assert.Contains("未能从 CLI 读取模型列表", vm.StatusMessage);
        Assert.False(vm.IsFetchingModels);
    }

    [Fact]
    public async Task FetchModelsForAccount_按账号拉取_填充账号卡下拉并提示()
    {
        var fake = new FakeEngineHost(new RunOptions
        {
            Accounts = { new AccountConfig { Id = "a1", DisplayName = "账号一" } },
        });
        var vm = new ConfigViewModel(fake);
        fake.SetFetchModelsResult(new[] { new ModelCatalogEntry("gpt-5", 0.79m) });
        var account = vm.Accounts.Single();

        await vm.FetchModelsForAccountCommand.ExecuteAsync(account);

        Assert.Single(account.AvailableModels);
        Assert.Equal("gpt-5", account.AvailableModels[0].Name);
        Assert.Contains("已拉取账号「账号一」的 1 个可用模型", vm.StatusMessage);
        Assert.False(account.IsFetchingModels);
    }

    [Fact]
    public async Task FetchModelsForAccount_空目录_提示直接输入模型名()
    {
        var fake = new FakeEngineHost(new RunOptions
        {
            Accounts = { new AccountConfig { Id = "a1", DisplayName = "账号一" } },
        });
        var vm = new ConfigViewModel(fake);
        var account = vm.Accounts.Single();

        await vm.FetchModelsForAccountCommand.ExecuteAsync(account);

        Assert.Empty(account.AvailableModels);
        Assert.Contains("未能读取账号「账号一」的模型列表", vm.StatusMessage);
        Assert.False(account.IsFetchingModels);
    }

    [Fact]
    public void CancelCommand_放弃未保存修改_从Options重新加载()
    {
        var options = new RunOptions { Concurrency = 4, TaskFilePath = "orig.json" };
        var vm = new ConfigViewModel(new FakeEngineHost(options));

        vm.Concurrency = 9;
        vm.TaskFilePath = "dirty.json";
        vm.CancelCommand.Execute(null);

        Assert.Equal(4, vm.Concurrency);
        Assert.Equal("orig.json", vm.TaskFilePath);
    }

    [Fact]
    public void RefreshSnapshotStatuses_各账号卡刷新Vault快照状态()
    {
        var fake = new FakeEngineHost(new RunOptions
        {
            Accounts = { new AccountConfig { Id = "a1" }, new AccountConfig { Id = "a2" } },
        });
        var vm = new ConfigViewModel(fake);

        vm.RefreshSnapshotStatuses();

        // FakeVault 无任何快照 → DescribeSnapshot 回落「尚未备份」文案
        Assert.All(vm.Accounts, a => Assert.Equal("快照：尚未备份（无快照）", a.SnapshotStatus));
    }
}
