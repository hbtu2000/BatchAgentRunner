using Avalonia.Controls;
using Avalonia.Threading;

namespace BatchAgentRunner.Desktop.Views;

/// <summary>
/// 关机倒计时窗口（REQ-015 可撤销倒计时；技术债务 P0-10）。
/// 对话框结果：true = 倒计时走完 / 用户点「立即关机」；false = 用户取消。
/// 窗口被强制关闭（Alt+F4 等）按「取消」处理 —— 破坏性操作的默认取向必须是不执行。
/// </summary>
public partial class ShutdownCountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remainingSeconds;

    /// <summary>以给定秒数启动关机倒计时，关闭时返回是否执行关机。</summary>
    public ShutdownCountdownWindow(int countdownSeconds)
    {
        InitializeComponent();

        _remainingSeconds = countdownSeconds;
        CountdownText.Text = countdownSeconds.ToString();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, OnTick);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        if (_remainingSeconds <= 0)
        {
            Finish(true);
            return;
        }

        CountdownText.Text = _remainingSeconds.ToString();
    }

    private void OnShutdownNowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Finish(true);

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Finish(false);

    private void Finish(bool proceed)
    {
        _timer.Stop();
        Close(proceed);
    }

    /// <summary>窗口关闭时停止计时器，避免已关闭窗口继续倒计时。</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _timer.Stop();
        base.OnClosing(e);
    }
}
