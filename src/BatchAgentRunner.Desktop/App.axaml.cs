using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BatchAgentRunner.Desktop.ViewModels;
using BatchAgentRunner.Desktop.Views;

namespace BatchAgentRunner.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

#if DEBUG
        // 新包 AvaloniaUI.DiagnosticsSupport 已废弃旧的 AppBuilder.WithDeveloperTools()，
        // 改为在 Application.Initialize() 内挂载开发者工具桥接（F12 唤起）。
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 确定性启动（2026-10-22 修复）：同步创建并登记主窗口，lifetime 会在本次
            // OnFrameworkInitializationCompleted 末尾 Show 它 → 窗口立即出现，不再依赖
            // 「异步初始化完成后 Dispatcher.Post 再显示」这一存在时序竞态/挂死风险的路径。
            // 宿主初始化随后在后台进行，VM 就绪后经 PropertyChanged 刷新已显示的窗口。
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            Program.Log("INFO", "[启动] 主窗口已登记（由 lifetime 显示）");
            _ = InitializeAsync(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 后台初始化主视图（加载配置 / 建引擎 / 装配页面），完成后经 <see cref="MainViewModel"/> 的
    /// PropertyChanged 刷新已显示的窗口。失败不崩溃、仅落日志（用户可重试）。
    /// </summary>
    private static async Task InitializeAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync().ConfigureAwait(false);
            Program.Log("INFO", "[启动] 主视图初始化完成");
        }
        catch (Exception ex)
        {
            // fire-and-forget 的异常若不自接住会被 UnobservedTaskException 静默吞掉（仅 GC 时触发）。
            Program.Log("ERROR", $"[启动] 主视图初始化失败：{ex}");
        }
    }
}
