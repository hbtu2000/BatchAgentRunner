namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 实时输出流的跟随模式：
/// <see cref="Selected"/> = 随任务联动（默认，仅显示联动任务的输出，避免并发任务混在一起）；
/// <see cref="All"/> = 显示全部并发任务的混合输出（旧行为，按需开启）。
/// </summary>
internal enum OutputFollowMode
{
    /// <summary>仅显示当前联动任务（选中任务优先；未选中时自动跟随运行中任务）的输出。</summary>
    Selected,

    /// <summary>显示全部并发任务的混合输出（旧行为，按需开启）。</summary>
    All
}
