using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BatchAgentRunner.Desktop.Services;
using BatchAgentRunner.Desktop.ViewModels;
using BatchAgentRunner.Desktop.Views;

namespace BatchAgentRunner.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // P1-6 异步初始化门：先创建 VM，再 await 宿主初始化（不阻塞 UI 线程），
            // 完成后再创建并显示主窗口——窗口可见时 VM 已就绪，绑定一次性成立。
            var viewModel = new MainViewModel();
            _ = ShowAfterInitializedAsync(desktop, viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 异步初始化后展示主窗口（P1-6）。初始化失败走兜底（与 CLR 崩溃兜底 B3/B4 同类）：
    /// 记录后仍然展示窗口，使进程不崩溃、用户可到「配置」页重试。
    /// </summary>
    private static async Task ShowAfterInitializedAsync(
        IClassicDesktopStyleApplicationLifetime desktop, MainViewModel viewModel)
    {
        await viewModel.InitializeAsync().ConfigureAwait(false);
        UiThread.Post(() => desktop.MainWindow = new MainWindow
        {
            DataContext = viewModel,
        });
    }
}
