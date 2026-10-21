using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置保存/加载协调器（P-04 分解产物）。
/// 原 <c>ConfigViewModel</c> 的 <c>Reload</c> / <c>SaveAsync</c> 把「9 个配置域的双向映射 + 工作空间上限拦截
/// + 模型池落盘合并」全部内联在一个 1000+ 行的上帝类里；分解后各域由子编辑器各自持有
/// <c>LoadFrom</c> / <c>ApplyTo</c>（单一事实来源、钳制规则只此一份），本协调器只做「跨域编排」：
/// <see cref="Load"/> 把 <see cref="RunOptions"/> 分发到各子编辑器；<see cref="Apply"/> 反向收集并先做
/// REQ-019 工作空间上限拦截（拦截则不改写、不落盘、不重建），通过后再由
/// <see cref="ModelsAccountSectionViewModel.WriteModelPool"/> 做模型池合并落盘。
/// </summary>
internal sealed class ConfigSaveCoordinator
{
    private readonly ConfigStatus _status;
    private readonly ModelsAccountSectionViewModel _models;
    private readonly TaskSectionViewModel _task;
    private readonly ScheduleSectionViewModel _schedule;
    private readonly ShutdownSectionViewModel _shutdown;
    private readonly SafetySectionViewModel _safety;
    private readonly ExpertDegradationSectionViewModel _expert;
    private readonly FallbackSectionViewModel _fallback;
    private readonly NotificationSectionViewModel _notif;
    private readonly GitSectionViewModel _git;
    private readonly WorkflowSectionViewModel _workflow;
    private readonly MiscSectionViewModel _misc;

    public ConfigSaveCoordinator(
        ConfigStatus status,
        ModelsAccountSectionViewModel models,
        TaskSectionViewModel task,
        ScheduleSectionViewModel schedule,
        ShutdownSectionViewModel shutdown,
        SafetySectionViewModel safety,
        ExpertDegradationSectionViewModel expert,
        FallbackSectionViewModel fallback,
        NotificationSectionViewModel notif,
        GitSectionViewModel git,
        WorkflowSectionViewModel workflow,
        MiscSectionViewModel misc)
    {
        _status = status;
        _models = models;
        _task = task;
        _schedule = schedule;
        _shutdown = shutdown;
        _safety = safety;
        _expert = expert;
        _fallback = fallback;
        _notif = notif;
        _git = git;
        _workflow = workflow;
        _misc = misc;
    }

    /// <summary>把运行配置分发到各子编辑器（Reload / 取消时调用）。</summary>
    public void Load(RunOptions o)
    {
        _task.LoadFrom(o);
        _schedule.LoadFrom(o);
        _shutdown.LoadFrom(o);
        _safety.LoadFrom(o);
        _expert.LoadFrom(o);
        _fallback.LoadFrom(o);
        _notif.LoadFrom(o);
        _git.LoadFrom(o);
        _workflow.LoadFrom(o);
        _misc.LoadFrom(o);
        _models.LoadFrom(o);
    }

    /// <summary>
    /// 把各子编辑器状态收集回运行配置。<returns>true=已改写（调用方应落盘+重建）；false=被工作空间上限拦截（不改写）。</returns>
    /// </summary>
    public bool Apply(RunOptions o)
    {
        _task.ApplyTo(o);
        _schedule.ApplyTo(o);
        _shutdown.ApplyTo(o);
        _safety.ApplyTo(o);
        _expert.ApplyTo(o);
        _fallback.ApplyTo(o);
        _workflow.ApplyTo(o);
        _misc.ApplyTo(o);
        _git.ApplyTo(o);

        // REQ-019：保存前以配置上限拦截，杜绝静默超出（提示中的 N 取配置值）
        if (_task.Workspaces.Count > o.MaxWorkspaces)
        {
            _status.Show($"保存被拦截：工作空间数量（{_task.Workspaces.Count}）已超过上限（{o.MaxWorkspaces}），请精简后再保存");
            return false;
        }

        _notif.ApplyTo(o);
        _models.WriteModelPool(o);
        return true;
    }
}
