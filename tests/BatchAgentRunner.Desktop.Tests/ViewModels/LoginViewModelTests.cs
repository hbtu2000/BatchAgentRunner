using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P1-19：登录页 ViewModel 白箱单测（Desktop 回归面之一）。
/// 仅依赖 <see cref="FakeEngineHost"/>，锁住账号行渲染、强制领取回写、两段式删除确认等契约；
/// 不触发真实登录 / CLI（避免外部进程副作用）——那些路径由集成测试覆盖。
/// </summary>
public class LoginViewModelTests
{
    [Fact]
    public void 构造时从Options渲染账号行_HasAccounts反映账号数()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        var fake = new FakeEngineHost(options);
        var vm = new LoginViewModel(fake);

        Assert.True(vm.HasAccounts);
        Assert.Single(vm.Rows);
        Assert.Equal("甲", vm.Rows[0].DisplayName);
    }

    [Fact]
    public void ForceClaim_设置回写配置并触发落盘()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new LoginViewModel(fake);
        Assert.True(vm.ForceClaim); // 默认勾选（忽略去重、每次都领）

        vm.ForceClaim = false;

        Assert.False(fake.Options.CreditsClaim.Force);
        Assert.Equal(1, fake.SaveConfigAsyncCallCount);
    }

    [Fact]
    public async Task DeleteAsync_两段式确认_第二次才执行删除()
    {
        var options = new RunOptions();
        var account = new AccountConfig { Id = "acc-1", DisplayName = "甲" };
        options.Accounts.Add(account);
        var fake = new FakeEngineHost(options);
        var vm = new LoginViewModel(fake);
        var row = new LoginAccountRow(account, "无快照", isCurrentLive: false, vm);

        // 第一次：仅进入确认态，不执行删除
        await vm.DeleteAsync(row);
        Assert.True(row.IsConfirmingDelete);
        Assert.Single(fake.Options.Accounts);
        Assert.Equal(0, fake.SaveConfigAsyncCallCount);

        // 第二次：执行删除（Fake Vault 无副作用），账号移出账号池并落盘 + 重建
        await vm.DeleteAsync(row);
        Assert.False(row.IsConfirmingDelete);
        Assert.Empty(fake.Options.Accounts);
        Assert.Equal(1, fake.SaveConfigAsyncCallCount);
        Assert.Equal(1, fake.RebuildEngineCallCount);
    }

    [Fact]
    public void IsClaimIdle_初始空闲()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new LoginViewModel(fake);
        Assert.True(vm.IsClaimIdle);
    }
}
