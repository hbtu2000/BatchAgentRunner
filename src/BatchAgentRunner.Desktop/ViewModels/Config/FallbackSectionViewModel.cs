using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「特殊模型兜底」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的
/// 兜底域）：总开关 / 单任务成本上限 / 单批成本上限。兜底模型池本身挂在账号卡（见
/// <see cref="ModelsAccountSectionViewModel"/>），本编辑器只管全局开关与上限。
/// </summary>
public sealed class FallbackSectionViewModel : ViewModelBase
{
    private bool _fallbackEnabled;
    private decimal _fallbackMaxPerTask;
    private decimal _fallbackMaxPerBatch;

    public bool FallbackEnabled
    {
        get => _fallbackEnabled;
        set => SetProperty(ref _fallbackEnabled, value);
    }

    public decimal FallbackMaxPerTask
    {
        get => _fallbackMaxPerTask;
        set => SetProperty(ref _fallbackMaxPerTask, value);
    }

    public decimal FallbackMaxPerBatch
    {
        get => _fallbackMaxPerBatch;
        set => SetProperty(ref _fallbackMaxPerBatch, value);
    }

    /// <summary>从运行配置加载本域状态。</summary>
    public void LoadFrom(RunOptions o)
    {
        FallbackEnabled = o.Fallback.Enabled;
        FallbackMaxPerTask = o.Fallback.MaxCostPerTask;
        FallbackMaxPerBatch = o.Fallback.MaxCostPerBatch;
    }

    /// <summary>把本域状态写回运行配置（原样写回，上限不在此处钳制）。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.Fallback.Enabled = FallbackEnabled;
        o.Fallback.MaxCostPerTask = FallbackMaxPerTask;
        o.Fallback.MaxCostPerBatch = FallbackMaxPerBatch;
    }
}
