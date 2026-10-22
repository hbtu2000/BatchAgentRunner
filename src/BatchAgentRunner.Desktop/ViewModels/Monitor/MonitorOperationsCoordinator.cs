using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Services;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 监控页操作编排协作者（P-06 从 <see cref="MonitorViewModel"/> 抽离）。
/// 承载「加载任务文件 / 启动前预检 / 开始执行 / 续跑」四类宿主交互编排：
/// 预检门（<see cref="EnsurePreflightPassedAsync"/>）、结果回写（<see cref="FinishWithReport"/>）、
/// 失败双写（<see cref="NotifyIssue"/>：状态栏 + 提示板，避免「点了按钮似乎没反应」——
/// 状态栏在窗口最底部极易被忽略，2026-10-18 现场）。线程收敛经 <see cref="UiThread"/>（P0-6/P0-7）：
/// <c>ConfigureAwait(false)</c> 续延不在 UI 线程，全部 UI 写回与原实现一致地投递。
/// 状态栏文案与 LastReportPath 绑定通知经 <see cref="MonitorOperationsContext"/> 的回调写回 facade，
/// 本协作者不持有可绑定状态。
/// </summary>
internal sealed class MonitorOperationsCoordinator
{
    private readonly MonitorOperationsContext _context;

    /// <summary>注入宿主、提示板与回写回调，构造阶段不发起任何 IO。</summary>
    public MonitorOperationsCoordinator(MonitorOperationsContext context)
    {
        _context = context;
    }

    /// <summary>加载任务文件（未选文件时 warning 双写，不抛）。</summary>
    public async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_context.Host.Options.TaskFilePath))
        {
            NotifyIssue("请先在配置页选择任务文件", level: "warning");
            return;
        }

        try
        {
            await _context.Host.LoadTasksAsync().ConfigureAwait(false);
            UiThread.Post(() => _context.WriteStatusMessage($"已加载任务文件（{DateTime.Now:HH:mm:ss}）"));
        }
        catch (Exception ex)
        {
            NotifyIssue($"加载失败：{ex.Message}");
        }
    }

    /// <summary>启动前预检（通过回写状态栏；未通过 warning 双写）。</summary>
    public async Task PreflightAsync()
    {
        try
        {
            var report = await _context.Host.PreflightAsync().ConfigureAwait(false);
            if (report.CanStart)
            {
                UiThread.Post(() => _context.WriteStatusMessage($"预检通过：{report.LoadedTaskCount} 条任务，待处理 {report.PendingTaskCount} 条"));
            }
            else
            {
                NotifyIssue("预检未通过：" + string.Join("；", report.Errors), level: "warning");
            }
        }
        catch (Exception ex)
        {
            NotifyIssue($"预检异常：{ex.Message}", level: "warning");
        }
    }

    /// <summary>开始执行（预检门 → 启动 → 结果回写）。</summary>
    public async Task StartAsync(bool waitIfUnavailable)
    {
        if (!await EnsurePreflightPassedAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var report = await _context.Host.StartAsync(waitIfUnavailable).ConfigureAwait(false);
            FinishWithReport(report);
        }
        catch (Exception ex)
        {
            UiThread.Post(() => _context.WriteStatusMessage($"执行异常：{ex.Message}"));
        }
    }

    /// <summary>续跑（与开始执行同链路：预检门 → 启动 → 结果回写）。</summary>
    public async Task ResumeAsync(bool waitIfUnavailable)
    {
        if (!await EnsurePreflightPassedAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var report = await _context.Host.StartAsync(waitIfUnavailable).ConfigureAwait(false);
            FinishWithReport(report);
        }
        catch (Exception ex)
        {
            UiThread.Post(() => _context.WriteStatusMessage($"执行异常：{ex.Message}"));
        }
    }

    /// <summary>预检门：通过放行；未通过/异常则回写状态栏并拦截启动。</summary>
    private async Task<bool> EnsurePreflightPassedAsync()
    {
        try
        {
            var report = await _context.Host.PreflightAsync().ConfigureAwait(false);
            if (report.CanStart)
            {
                return true;
            }

            UiThread.Post(() => _context.WriteStatusMessage("预检未通过，已取消启动：" + string.Join("；", report.Errors)));
            return false;
        }
        catch (Exception ex)
        {
            UiThread.Post(() => _context.WriteStatusMessage($"预检异常，已取消启动：{ex.Message}"));
            return false;
        }
    }

    private void FinishWithReport(BatchReport report) => UiThread.Post(() =>
    {
        _context.WriteStatusMessage($"批处理结束：{report.BatchStatus}，成功 {report.Done} / 失败 {report.Failed} / 中断 {report.Interrupted}");
        _context.NotifyLastReportPathChanged();
    });

    private void NotifyIssue(string message, string level = "danger")
    {
        UiThread.Post(() => _context.WriteStatusMessage(message));
        UiThread.Post(() =>
        {
            _context.Toasts.AddIssue(message, level);
        });
    }
}

/// <summary>
/// 监控页操作编排协作者的上下文注入（P-06，沿用 P-01/P-02「上下文注入」范式）：
/// 协作者经回调把状态栏文案 / LastReportPath 绑定通知写回 facade，提示板与宿主直接持有。
/// </summary>
internal sealed class MonitorOperationsContext
{
    /// <summary>引擎宿主，操作编排据此读配置、加载任务并启动批处理。</summary>
    public required IEngineHost Host { get; init; }

    /// <summary>事件提示板，失败信息在这里与状态栏同步展示。</summary>
    public required EventToastBoard Toasts { get; init; }

    /// <summary>直接写回 facade 的 StatusMessage 绑定属性（线程收敛由调用方经 <see cref="UiThread"/> 负责，P0-6/P0-7）。</summary>
    public required Action<string> WriteStatusMessage { get; init; }

    /// <summary>刷新 facade 的 LastReportPath 绑定通知（新报告生成后；与状态栏同在一个 UI 线程作业内写入）。</summary>
    public required Action NotifyLastReportPathChanged { get; init; }
}
