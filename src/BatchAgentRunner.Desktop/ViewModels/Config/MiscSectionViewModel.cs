using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「结果大小与备份」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的
/// 杂项域）：单任务结果上限（KB）/ 每次启动前备份份数。下限钳制见 <see cref="ConfigFieldRules"/>。
/// </summary>
public sealed class MiscSectionViewModel : ViewModelBase
{
    private int _maxResultKb = 100;
    private int _backupRetentionCount = 5;

    /// <summary>单任务结果大小上限（KB，超出截断并提示全量日志）。</summary>
    public int MaxResultKb
    {
        get => _maxResultKb;
        set => SetProperty(ref _maxResultKb, value);
    }

    /// <summary>每次启动前备份份数（0 = 不备份）。</summary>
    public int BackupRetentionCount
    {
        get => _backupRetentionCount;
        set => SetProperty(ref _backupRetentionCount, value);
    }

    /// <summary>从运行配置加载本域状态。</summary>
    public void LoadFrom(RunOptions o)
    {
        MaxResultKb = o.ResultMaxKB;
        BackupRetentionCount = o.BackupRetention;
    }

    /// <summary>把本域状态写回运行配置（下限钳制见 <see cref="ConfigFieldRules"/>）。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.ResultMaxKB = ConfigFieldRules.AtLeastZero(MaxResultKb);
        o.BackupRetention = ConfigFieldRules.AtLeastZero(BackupRetentionCount);
    }
}
