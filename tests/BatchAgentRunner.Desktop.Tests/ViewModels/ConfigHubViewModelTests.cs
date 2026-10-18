using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P1-19：配置中心 ViewModel 白箱单测（churn 热点之一）。验证新版统一外壳的契约：
/// 构造时把登录 / 向导注入配置页并刷新准备度、统一配置中心始终激活、HubNavigate 触发 Reload。
/// （旧版「顶部标签在 LoginView / ConfigView 子视图间切换」的契约已随重设计移除——
/// 登录内嵌于「账号与凭据」区段、配置向导并入「快速开始」区段与准备度引导。）
/// </summary>
public class ConfigHubViewModelTests
{
    [Fact]
    public void 构造时注入登录向导并刷新准备度()
    {
        var fake = new FakeEngineHost();
        var login = new LoginViewModel(fake);
        var wizard = new SetupWizardViewModel(fake);
        var config = new ConfigViewModel(fake);
        var hub = new ConfigHubViewModel(login, wizard, config);

        // 外壳直接暴露三个子组件
        Assert.Same(login, hub.Login);
        Assert.Same(wizard, hub.Wizard);
        Assert.Same(config, hub.Config);

        // 注入契约：配置页已持有 Login / Wizard（账号区段内嵌登录、快速开始可重跑向导）
        Assert.Same(login, config.Login);
        Assert.Same(wizard, config.Wizard);

        // 准备度已在构造时刷新（6 步引导）
        Assert.Equal(6, config.ReadinessSteps.Count);

        // 统一配置中心默认激活
        Assert.Equal("config", hub.ActiveSubview);
    }

    [Fact]
    public void HubNavigate_触发配置Reload并保持统一配置中心()
    {
        var options = new RunOptions { Concurrency = 9 };
        var fake = new FakeEngineHost(options);
        var login = new LoginViewModel(fake);
        var wizard = new SetupWizardViewModel(fake);
        var config = new ConfigViewModel(fake);
        var hub = new ConfigHubViewModel(login, wizard, config);

        Assert.Equal(9, config.Concurrency);

        hub.HubNavigate("config");

        Assert.Equal("config", hub.ActiveSubview);
        Assert.Equal(9, config.Concurrency);   // Reload 后仍为配置值
        Assert.Equal(6, config.ReadinessSteps.Count); // 准备度保持刷新
    }

    [Fact]
    public void HubNavigate_任意key都落在统一配置中心()
    {
        var fake = new FakeEngineHost();
        var hub = new ConfigHubViewModel(
            new LoginViewModel(fake),
            new SetupWizardViewModel(fake),
            new ConfigViewModel(fake));

        hub.HubNavigate("whatever");

        // 新版已无独立登录子视图，统一配置中心承载所有区段
        Assert.Equal("config", hub.ActiveSubview);
    }
}
