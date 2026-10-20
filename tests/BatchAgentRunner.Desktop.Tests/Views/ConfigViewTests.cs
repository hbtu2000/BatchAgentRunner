using Avalonia.Headless.XUnit;
using BatchAgentRunner.Desktop.Tests.Helpers;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using BatchAgentRunner.Desktop.Views;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Views;

/// <summary>
/// P1-19：配置页视图契约（churn 热点 <c>ConfigView.axaml</c> 此前零测试，是 Desktop 回归面最大文件之一）。
/// 验证两项不变量：① 视图可在 headless 下构造（XAML 编译 / 资源加载契约不被破坏）；
/// ② 视图↔VM 接线：DataContext 设为 <see cref="ConfigViewModel"/> 后，<c>OnDataContextChanged</c>
/// 把文件选择回调注入 VM（防回归成「未接线」导致点选任务文件无反应）。
/// </summary>
public class ConfigViewTests : DesktopHeadlessTestBase
{
    [AvaloniaFact]
    public async Task 构造并绑定ConfigViewModel_视图注入PickTaskFile回调()
    {
        var fake = new FakeEngineHost();
        var vm = new ConfigViewModel(fake);
        var view = new ConfigView { DataContext = vm };

        // OnDataContextChanged 把视图的文件选择回调注入 VM（视图↔VM 接线契约）
        Assert.NotNull(vm.PickTaskFile);

        // 视图未挂到顶级窗口时，回调安全返回 null（无 TopLevel 不抛）
        var picked = await vm.PickTaskFile!();
        Assert.Null(picked);
    }
}
