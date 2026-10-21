using Avalonia.Headless.XUnit;
using BatchAgentRunner.Desktop.Tests.Helpers;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using BatchAgentRunner.Desktop.Views;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Views;

/// <summary>
/// P2-10 守卫：登录页 / 配置向导页原各写 ~30 / ~11 处 <c>#hex</c> 硬编码字面色（深色主题下失效），
/// 现已全部改为 <c>DynamicResource</c> 引用 <c>AppStyles.xaml</c> 的 token（双主题自动切换）。
/// 本测试锁死：① 两视图可在 headless 下构造（XAML 编译 / 资源加载契约不被破坏）；
/// ② 视图↔VM 接线（DataContext 注入不抛）。
/// </summary>
public class OnboardingViewTests : DesktopHeadlessTestBase
{
    [AvaloniaFact]
    public void 构造并绑定LoginViewModel_视图可构造且接线()
    {
        var fake = new FakeEngineHost();
        var vm = new LoginViewModel(fake);

        var view = new LoginView { DataContext = vm };

        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public void 构造并绑定SetupWizardViewModel_视图可构造且接线()
    {
        var fake = new FakeEngineHost();
        var vm = new SetupWizardViewModel(fake);

        var view = new SetupWizardView { DataContext = vm };

        Assert.NotNull(view);
    }
}
