using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「调度与重试」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的调度域）：
/// 并发 / 普通重试 / 限流退避 / 单次超时 / 总尝试 / 总时长 / 最长可等待 / 默认冷却 /
/// 额度无限等待 / 强制重跑 / 失败即停。保存时的下限钳制一律走 <see cref="ConfigFieldRules"/>。
/// </summary>
public sealed class ScheduleSectionViewModel : ViewModelBase
{
    private int _concurrency = 1;
    private int _maxRetries = 2;
    private int _rateLimitRetries = 3;
    private int _taskTimeoutMinutes = 10;
    private int _maxAttempts = 10;
    private int _maxTotalMinutes = 60;
    private int _maxWaitMinutes = 60;
    private int _defaultCooldownMinutes = 60;
    private bool _waitForQuotaReset = true;
    private bool _forceRerun;
    private bool _stopOnFailure;

    public int Concurrency
    {
        get => _concurrency;
        set => SetProperty(ref _concurrency, value);
    }

    public int MaxRetries
    {
        get => _maxRetries;
        set => SetProperty(ref _maxRetries, value);
    }

    public int RateLimitRetries
    {
        get => _rateLimitRetries;
        set => SetProperty(ref _rateLimitRetries, value);
    }

    public int TaskTimeoutMinutes
    {
        get => _taskTimeoutMinutes;
        set => SetProperty(ref _taskTimeoutMinutes, value);
    }

    public int MaxAttempts
    {
        get => _maxAttempts;
        set => SetProperty(ref _maxAttempts, value);
    }

    public int MaxTotalMinutes
    {
        get => _maxTotalMinutes;
        set => SetProperty(ref _maxTotalMinutes, value);
    }

    public int MaxWaitMinutes
    {
        get => _maxWaitMinutes;
        set => SetProperty(ref _maxWaitMinutes, value);
    }

    public int DefaultCooldownMinutes
    {
        get => _defaultCooldownMinutes;
        set => SetProperty(ref _defaultCooldownMinutes, value);
    }

    /// <summary>额度耗尽时无限等待重置（暂停不计入时长）。</summary>
    public bool WaitForQuotaReset
    {
        get => _waitForQuotaReset;
        set => SetProperty(ref _waitForQuotaReset, value);
    }

    public bool ForceRerun
    {
        get => _forceRerun;
        set => SetProperty(ref _forceRerun, value);
    }

    public bool StopOnFailure
    {
        get => _stopOnFailure;
        set => SetProperty(ref _stopOnFailure, value);
    }

    /// <summary>从运行配置加载本域状态。</summary>
    public void LoadFrom(RunOptions o)
    {
        Concurrency = o.Concurrency;
        MaxRetries = o.MaxRetriesPerTask;
        RateLimitRetries = o.RateLimitBackoffRetries;
        TaskTimeoutMinutes = o.TaskTimeoutMinutes;
        MaxAttempts = o.MaxAttemptsPerTask;
        MaxTotalMinutes = o.MaxTotalMinutesPerTask;
        MaxWaitMinutes = o.MaxWaitMinutes;
        DefaultCooldownMinutes = o.DefaultCooldownMinutes;
        WaitForQuotaReset = o.WaitForQuotaReset;
        ForceRerun = o.ForceRerun;
        StopOnFailure = o.StopOnFailure;
    }

    /// <summary>把本域状态写回运行配置（下限钳制见 <see cref="ConfigFieldRules"/>）。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.Concurrency = ConfigFieldRules.AtLeastOne(Concurrency);
        o.MaxRetriesPerTask = ConfigFieldRules.AtLeastZero(MaxRetries);
        o.RateLimitBackoffRetries = ConfigFieldRules.AtLeastZero(RateLimitRetries);
        o.TaskTimeoutMinutes = ConfigFieldRules.AtLeastOne(TaskTimeoutMinutes);
        o.MaxAttemptsPerTask = ConfigFieldRules.AtLeastOne(MaxAttempts);
        o.MaxTotalMinutesPerTask = ConfigFieldRules.AtLeastOne(MaxTotalMinutes);
        o.MaxWaitMinutes = ConfigFieldRules.AtLeastZero(MaxWaitMinutes);
        o.DefaultCooldownMinutes = ConfigFieldRules.AtLeastOne(DefaultCooldownMinutes);
        o.WaitForQuotaReset = WaitForQuotaReset;
        o.ForceRerun = ForceRerun;
        o.StopOnFailure = StopOnFailure;
    }
}
