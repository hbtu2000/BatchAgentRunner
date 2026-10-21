using Avalonia.Headless.XUnit;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Tests.Helpers;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P-06 准入契约：登录页 facade 公开契约锁（分解前后必须逐字节等价）。
/// <para>
/// <c>LoginViewModel</c> 原为 751 行上帝类（一键登录全流程 + 每日积分领取 + 账号池一键操作 +
/// 步骤条 / 结果横幅 / 账号行 / 积分行四类 UI 状态）。P-06 按 P-04/P-05 范式分解为
/// 「状态持有 + 绑定转发 + 流程编排」薄 facade + Login 命名空间协作者。本测试在分解前锁定
/// 其对外契约：步骤条初始化、账号行重建、一键切换、两段式删除（含 Live 拒绝与异常翻译）、
/// 积分领取各分支文案、AgentKind 守卫、错误翻译。配合
/// <see cref="MonitorLoginViewBindingContractTests"/>（LoginView.axaml 绑定路径静态可解析）
/// 构成 R-4（VM 分解破坏 UI 行为）的机器防线。
/// </para>
/// <para>
/// 不触发真实登录 / CLI（引导登录涉真实文件轮询，由集成测试覆盖）——只走 Mock/WorkBuddy
/// 守卫分支与 Vault 操作路径。
/// </para>
/// </summary>
public class LoginViewModelContractTests : DesktopHeadlessTestBase
{
    private static LoginAccountRow Row(LoginViewModel vm, AccountConfig account, bool isCurrentLive = false) =>
        new(account, "无快照", isCurrentLive, vm);

    [AvaloniaFact]
    public void 构造时_步骤条初始化六个待办步骤且状态空闲()
    {
        var vm = new LoginViewModel(new FakeEngineHost());

        Assert.Equal(6, vm.Steps.Count);
        Assert.All(vm.Steps, s => Assert.True(s.IsPending));
        Assert.True(vm.IsIdle);
        Assert.True(vm.IsClaimIdle);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsClaiming);
        Assert.False(vm.HasAccounts);
        Assert.False(vm.HasClaimRows);
        Assert.Empty(vm.ClaimRows);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.False(vm.HasResult);
    }

    [AvaloniaFact]
    public void Refresh_同步AgentKind并重建账号行()
    {
        var options = new RunOptions();
        var fake = new FakeEngineHost(options);
        var vm = new LoginViewModel(fake);

        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        Assert.False(vm.HasAccounts);

        vm.Refresh();

        Assert.True(vm.HasAccounts);
        Assert.Single(vm.Rows);
        Assert.Equal("甲", vm.Rows[0].DisplayName);
        Assert.Equal("acc-1", vm.Rows[0].Id);
        Assert.False(vm.Rows[0].IsCurrentLive);
        Assert.False(vm.Rows[0].IsConfirmingDelete);
        Assert.True(vm.Rows[0].IsNotConfirmingDelete);

        // 宿主配置改了 AgentKind → Refresh 同步到页面选择
        options.AgentKind = AgentKind.WorkBuddy;
        vm.Refresh();
        Assert.Equal(AgentKind.WorkBuddy, vm.AgentKind);
    }

    [AvaloniaFact]
    public void 账号行_快照状态经VaultHost描述()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        var vm = new LoginViewModel(new FakeEngineHost(options));

        Assert.Equal("快照：尚未备份（无快照）", vm.Rows[0].SnapshotStatus);
    }

    [AvaloniaFact]
    public async Task LoginCommand_Mock与WorkBuddy_拒绝登录并给出可读文案()
    {
        var fake = new FakeEngineHost();
        var vm = new LoginViewModel(fake);

        vm.AgentKind = AgentKind.Mock;
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsError);
        Assert.False(vm.IsSuccess);
        Assert.Contains("Mock 是仿真工具", vm.ResultText);
        Assert.False(vm.IsBusy); // 拒绝路径不进入忙碌态

        vm.AgentKind = AgentKind.WorkBuddy;
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Contains("暂不支持一键登录", vm.ResultText);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task ReLoginAsync_预填显示名并按守卫拒绝Mock()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        var vm = new LoginViewModel(new FakeEngineHost(options));
        vm.AgentKind = AgentKind.Mock;

        await vm.ReLoginAsync(Row(vm, options.Accounts[0]));

        Assert.Equal("甲", vm.NewAccountDisplayName);
        Assert.Contains("Mock 是仿真工具", vm.ResultText);
    }

    [AvaloniaFact]
    public async Task SwitchToAsync_成功后刷新列表并回写状态()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        var vm = new LoginViewModel(new FakeEngineHost(options));
        var row = Row(vm, options.Accounts[0]);

        await vm.SwitchToAsync(row);

        Assert.Contains("已切换", vm.StatusText);
        Assert.Contains("甲", vm.StatusText);
        Assert.Single(vm.Rows); // 切换后 RebuildRows
    }

    [AvaloniaFact]
    public async Task SwitchToAsync_Vault异常_翻译为可读文案且不抛()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        var fake = new FakeEngineHost(options);
        fake.VaultFailure = new InvalidOperationException("CLI 进程仍在运行");
        var vm = new LoginViewModel(fake);

        await vm.SwitchToAsync(Row(vm, options.Accounts[0]));

        Assert.True(vm.IsError);
        Assert.Contains("有一个 CLI 窗口还开着", vm.ResultText);
        Assert.Contains("CLI 进程仍在运行", vm.ResultText); // 细节原文保留
    }

    [AvaloniaFact]
    public async Task DeleteAsync_当前Live账号_拒绝删除()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        var vm = new LoginViewModel(new FakeEngineHost(options));
        var row = Row(vm, options.Accounts[0], isCurrentLive: true);

        await vm.DeleteAsync(row); // 第一击：进入确认态
        Assert.True(row.IsConfirmingDelete);

        await vm.DeleteAsync(row); // 第二击：Live 拒绝
        Assert.True(vm.IsError);
        Assert.Contains("是 CLI 当前正在使用的账号，不能直接删除", vm.ResultText);
        Assert.Single(options.Accounts); // 未移除
        Assert.False(row.IsConfirmingDelete); // finally 复位
    }

    [AvaloniaFact]
    public async Task DeleteAsync_Vault异常_翻译失败且账号池不动()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        var fake = new FakeEngineHost(options);
        fake.VaultFailure = new InvalidOperationException("删除快照失败");
        var vm = new LoginViewModel(fake);
        var row = Row(vm, options.Accounts[0]);

        await vm.DeleteAsync(row);
        await vm.DeleteAsync(row);

        Assert.True(vm.IsError);
        Assert.Contains("删除快照失败", vm.ResultText);
        Assert.Single(options.Accounts);
        Assert.Equal(0, fake.SaveConfigAsyncCallCount); // 未落盘
        Assert.Equal(0, fake.RebuildEngineCallCount);
    }

    [AvaloniaFact]
    public void CancelDelete_撤销确认态并清空状态栏()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        var vm = new LoginViewModel(new FakeEngineHost(options));
        var row = Row(vm, options.Accounts[0]);

        row.IsConfirmingDelete = true;
        vm.CancelDelete(row);

        Assert.False(row.IsConfirmingDelete);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [AvaloniaFact]
    public async Task ClaimCredits_无启用账号_给出引导文案()
    {
        var vm = new LoginViewModel(new FakeEngineHost());
        vm.AgentKind = AgentKind.CodeBuddy; // 默认 WorkBuddy 会先被守卫拒绝

        await vm.ClaimCreditsCommand.ExecuteAsync(null);

        Assert.True(vm.IsError);
        Assert.Contains("账号池里还没有启用的账号", vm.ResultText);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsClaiming);
        Assert.True(vm.IsClaimIdle);
    }

    [AvaloniaFact]
    public async Task ClaimCredits_WorkBuddy_拒绝领取()
    {
        var vm = new LoginViewModel(new FakeEngineHost());
        vm.AgentKind = AgentKind.WorkBuddy;

        await vm.ClaimCreditsCommand.ExecuteAsync(null);

        Assert.Contains("暂不支持领取积分", vm.ResultText);
    }

    [AvaloniaFact]
    public async Task ClaimCredits_汇总为空_提示没有可领取的账号()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲", Enabled = true });
        var vm = new LoginViewModel(new FakeEngineHost(options));
        vm.AgentKind = AgentKind.CodeBuddy; // 默认 WorkBuddy 会先被守卫拒绝

        await vm.ClaimCreditsCommand.ExecuteAsync(null); // 默认空汇总

        Assert.True(vm.IsError);
        Assert.Contains("没有可领取的账号（账号池为空）", vm.ResultText);
        Assert.False(vm.HasClaimRows);
    }

    [AvaloniaFact]
    public async Task ClaimCredits_全部成功_填充结果行并汇总()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲", Enabled = true });
        options.Accounts.Add(new AccountConfig { Id = "acc-2", DisplayName = "乙", Enabled = true });
        var fake = new FakeEngineHost(options);
        fake.ClaimResult = new CreditsClaimSummary
        {
            Records =
            {
                new CreditsClaimRecord
                {
                    AccountId = "acc-1", DisplayName = "甲", Status = CreditsClaimStatus.Claimed,
                    BeforeCredits = 10, AfterCredits = 25, DeltaCredits = 15
                },
                new CreditsClaimRecord
                {
                    AccountId = "acc-2", DisplayName = "乙", Status = CreditsClaimStatus.AlreadyClaimed,
                    BeforeCredits = 30, AfterCredits = 30
                }
            }
        };
        var vm = new LoginViewModel(fake);
        vm.AgentKind = AgentKind.CodeBuddy; // 默认 WorkBuddy 会先被守卫拒绝

        await vm.ClaimCreditsCommand.ExecuteAsync(null);

        Assert.True(vm.IsSuccess);
        Assert.False(vm.IsError);
        Assert.True(vm.HasClaimRows);
        Assert.Equal(2, vm.ClaimRows.Count);
        Assert.Equal("领取成功", vm.ClaimRows[0].Status);
        Assert.Equal("+15", vm.ClaimRows[0].Delta);
        Assert.True(vm.ClaimRows[0].IsClaimed);
        Assert.Equal("今日已领取过", vm.ClaimRows[1].Status);
        Assert.True(vm.ClaimRows[1].IsNeutral);
        Assert.Contains("每日积分领取完成：成功 1 个，已领取过 1 个，跳过 0 个", vm.ResultText);
        Assert.Equal("积分领取全部完成", vm.StatusText);
        Assert.True(vm.IsClaimIdle);
    }

    [AvaloniaFact]
    public async Task ClaimCredits_部分失败_仍算完成但状态栏标注()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲", Enabled = true });
        var fake = new FakeEngineHost(options);
        fake.ClaimResult = new CreditsClaimSummary
        {
            Records =
            {
                new CreditsClaimRecord
                {
                    AccountId = "acc-1", DisplayName = "甲", Status = CreditsClaimStatus.Failed,
                    Error = "读取票据失败"
                }
            }
        };
        var vm = new LoginViewModel(fake);
        vm.AgentKind = AgentKind.CodeBuddy; // 默认 WorkBuddy 会先被守卫拒绝

        await vm.ClaimCreditsCommand.ExecuteAsync(null);

        Assert.True(vm.IsSuccess); // 与原实现一致：有记录即算完成
        Assert.Single(vm.ClaimRows);
        Assert.True(vm.ClaimRows[0].IsFailed);
        Assert.Equal("失败", vm.ClaimRows[0].Status);
        Assert.Contains("领取结束：成功 0，已领取过 0，失败 1", vm.ResultText);
        Assert.Equal("积分领取完成（部分失败）", vm.StatusText);
    }

    [AvaloniaFact]
    public void CancelLoginCommand_可执行且无操作时取消不抛()
    {
        var vm = new LoginViewModel(new FakeEngineHost());

        Assert.True(vm.CancelLoginCommand.CanExecute(null));
        vm.CancelLoginCommand.Execute(null); // 无进行中登录 → 空转不抛
    }

    [AvaloniaFact]
    public void Dispose_取消未完成操作不抛且可重复调用()
    {
        var vm = new LoginViewModel(new FakeEngineHost());

        vm.Dispose();
        vm.Dispose(); // 幂等
    }

    [AvaloniaFact]
    public async Task 行命令_经facade路由到对应操作()
    {
        var options = new RunOptions();
        options.Accounts.Add(new AccountConfig { Id = "acc-1", DisplayName = "甲" });
        var vm = new LoginViewModel(new FakeEngineHost(options));
        var row = Row(vm, options.Accounts[0]);

        // 行命令存在且可执行（facade 公开契约：Switch/Relogin/Delete/CancelDelete 四命令）
        Assert.NotNull(row.SwitchCommand);
        Assert.NotNull(row.ReloginCommand);
        Assert.NotNull(row.DeleteCommand);
        Assert.NotNull(row.CancelDeleteCommand);

        // 删除命令第一击 → 确认态（经行命令走完整路由）
        await row.DeleteCommand.ExecuteAsync(null);
        Assert.True(row.IsConfirmingDelete);

        row.CancelDeleteCommand.Execute(null);
        Assert.False(row.IsConfirmingDelete);
    }
}
