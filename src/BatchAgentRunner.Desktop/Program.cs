using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;

namespace BatchAgentRunner.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.

    // 应用级日志目录（与引擎运行日志 runs/ 区分：此处仅记录启动/退出/进程级异常）。
    private static string AppLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatchAgentRunner", "logs");

    private static string TodayLogPath =>
        Path.Combine(AppLogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    [STAThread]
    public static int Main(string[] args)
    {
        SetupUnhandledExceptionHandlers();

        try
        {
            Log("INFO",
                $"应用启动 | 版本={typeof(Program).Assembly.GetName().Version} | 参数=[{string.Join(" ", args)}]");
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Log("INFO", $"应用正常退出 | 退出码={exitCode}");
            return exitCode;
        }
        catch (Exception ex)
        {
            Log("FATAL", $"启动或运行期间发生未捕获异常，进程即将退出。{Environment.NewLine}{ex}");
            ShowFatalError(ex);
            return 1;
        }
    }

    /// <summary>进程级兜底：捕获 UI 线程之外或消息循环之外的未处理异常，避免静默崩溃。</summary>
    private static void SetupUnhandledExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception
                     ?? new Exception("捕获到非 Exception 类型的未处理异常对象");
            Log("FATAL", $"AppDomain 未处理异常（IsTerminating={e.IsTerminating}）。{Environment.NewLine}{ex}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("ERROR", $"未观察的任务异常（已标记为已处理，避免进程崩溃）。{Environment.NewLine}{e.Exception}");
            e.SetObserved();
        };
    }

    private static void Log(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level,-5} {message}";
        try
        {
            Directory.CreateDirectory(AppLogDirectory);
            File.AppendAllText(TodayLogPath, line + Environment.NewLine);
        }
        catch
        {
            // 日志自身写入失败不应再引发异常，降级为仅走 Trace。
        }

        Trace.WriteLine(line);
    }

    /// <summary>致命错误时弹出原生对话框，告知用户错误原因与日志位置（避免 WinExe 静默退出）。</summary>
    private static void ShowFatalError(Exception ex)
    {
        try
        {
            MessageBoxW(IntPtr.Zero,
                $"程序启动/运行失败：{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"详细日志已写入：{TodayLogPath}",
                "BatchAgentRunner 致命错误", 0x10 /* MB_ICONERROR */);
        }
        catch
        {
            // 连对话框都无法弹出时，仅保留日志即可。
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
