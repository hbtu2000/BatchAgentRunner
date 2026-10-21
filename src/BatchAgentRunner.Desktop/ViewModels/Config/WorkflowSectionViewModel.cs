using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Desktop.Helpers;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「二期工作流·模板·依赖」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的
/// 工作流域）：门禁循环上限 / 会话恢复 / Markdown 回写 / 依赖失败策略 / 模板展开上限 /
/// 采集 diff / diff glob。下限钳制与行文本互转见 <see cref="ConfigFieldRules"/> 与
/// <see cref="MultiValueText"/>。
/// </summary>
public sealed class WorkflowSectionViewModel : ViewModelBase
{
    private int _defaultMaxGateLoops = 3;
    private bool _enableSessionResume;
    private bool _markdownInPlaceWriteback;
    private DependencyFailurePolicy _dependencyFailurePolicy;
    private int _templateBatchExpansionLimit = 50;
    private bool _collectDiffs;
    private string _diffGlobsText = string.Empty;

    public int DefaultMaxGateLoops
    {
        get => _defaultMaxGateLoops;
        set => SetProperty(ref _defaultMaxGateLoops, value);
    }

    public bool EnableSessionResume
    {
        get => _enableSessionResume;
        set => SetProperty(ref _enableSessionResume, value);
    }

    public bool MarkdownInPlaceWriteback
    {
        get => _markdownInPlaceWriteback;
        set => SetProperty(ref _markdownInPlaceWriteback, value);
    }

    public DependencyFailurePolicy DependencyFailurePolicy
    {
        get => _dependencyFailurePolicy;
        set => SetProperty(ref _dependencyFailurePolicy, value);
    }

    public IReadOnlyList<DependencyFailurePolicy> DependencyPolicies { get; } =
        new[]
        {
            global::BatchAgentRunner.Core.Scheduling.DependencyFailurePolicy.BlockDependents,
            global::BatchAgentRunner.Core.Scheduling.DependencyFailurePolicy.AbortBatch,
            global::BatchAgentRunner.Core.Scheduling.DependencyFailurePolicy.SkipDependents
        };

    /// <summary>模板展开单批上限（超过则要求确认）。</summary>
    public int TemplateBatchExpansionLimit
    {
        get => _templateBatchExpansionLimit;
        set => SetProperty(ref _templateBatchExpansionLimit, value);
    }

    /// <summary>是否采集结果 diff（REQ-017）。</summary>
    public bool CollectDiffs
    {
        get => _collectDiffs;
        set => SetProperty(ref _collectDiffs, value);
    }

    /// <summary>diff 采集的受关注文件 glob（每行一条）。</summary>
    public string DiffGlobsText
    {
        get => _diffGlobsText;
        set => SetProperty(ref _diffGlobsText, value);
    }

    /// <summary>从运行配置加载本域状态。</summary>
    public void LoadFrom(RunOptions o)
    {
        DefaultMaxGateLoops = o.DefaultMaxGateLoops;
        EnableSessionResume = o.EnableSessionResume;
        MarkdownInPlaceWriteback = o.MarkdownInPlaceWriteback;
        DependencyFailurePolicy = o.DependencyFailurePolicy;
        TemplateBatchExpansionLimit = o.TemplateBatchExpansionLimit;
        CollectDiffs = o.CollectDiffs;
        DiffGlobsText = MultiValueText.Join(o.DiffTargetGlobs);
    }

    /// <summary>把本域状态写回运行配置（下限钳制见 <see cref="ConfigFieldRules"/>）。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.DefaultMaxGateLoops = ConfigFieldRules.AtLeastOne(DefaultMaxGateLoops);
        o.EnableSessionResume = EnableSessionResume;
        o.MarkdownInPlaceWriteback = MarkdownInPlaceWriteback;
        o.DependencyFailurePolicy = DependencyFailurePolicy;
        o.TemplateBatchExpansionLimit = ConfigFieldRules.AtLeastOne(TemplateBatchExpansionLimit);
        o.CollectDiffs = CollectDiffs;
        o.DiffTargetGlobs = MultiValueText.Split(DiffGlobsText, '\n').ToList();
    }
}
