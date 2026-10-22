using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「自动关机」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的
/// 自动关机域，REQ-015）：总开关 / 仅失败率为 0 时关机 / 可撤销倒计时秒数。
/// </summary>
public sealed class ShutdownSectionViewModel : ViewModelBase
{
    private bool _shutdownEnabled;
    private bool _shutdownOnlyZeroFailure;
    private int _shutdownCountdown = 60;

    /// <summary>是否在批次结束后自动关机（REQ-015）。</summary>
    public bool ShutdownEnabled
    {
        get => _shutdownEnabled;
        set => SetProperty(ref _shutdownEnabled, value);
    }

    /// <summary>是否仅在失败率为零时才执行自动关机。</summary>
    public bool ShutdownOnlyZeroFailure
    {
        get => _shutdownOnlyZeroFailure;
        set => SetProperty(ref _shutdownOnlyZeroFailure, value);
    }

    /// <summary>可撤销倒计时秒数；0 = 跳过窗口直接执行（不推荐）。</summary>
    public int ShutdownCountdown
    {
        get => _shutdownCountdown;
        set => SetProperty(ref _shutdownCountdown, value);
    }

    /// <summary>从运行配置加载本域状态。</summary>
    public void LoadFrom(RunOptions o)
    {
        ShutdownEnabled = o.AutoShutdown.Enabled;
        ShutdownOnlyZeroFailure = o.AutoShutdown.OnlyWhenZeroFailure;
        ShutdownCountdown = o.AutoShutdown.CountdownSeconds;
    }

    /// <summary>把本域状态写回运行配置。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.AutoShutdown.Enabled = ShutdownEnabled;
        o.AutoShutdown.OnlyWhenZeroFailure = ShutdownOnlyZeroFailure;
        o.AutoShutdown.CountdownSeconds = ShutdownCountdown;
    }
}
