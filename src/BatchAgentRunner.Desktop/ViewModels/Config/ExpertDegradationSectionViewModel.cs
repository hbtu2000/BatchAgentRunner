using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Helpers;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「降级策略与专家」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的
/// 降级域 + 专家域）：<c>DegradationStrategy</c> / <c>Expert.Kind/Id/DisplayName</c>。
/// 写回时的空白回落（<see cref="ConfigFieldRules"/>）与枚举选择列表单一来源都在本编辑器内收敛。
/// </summary>
public sealed class ExpertDegradationSectionViewModel : ViewModelBase
{
    private DegradationStrategy _degradationStrategy;
    private ExpertKind _expertKind;
    private string _expertId = string.Empty;
    private string _expertDisplayName = string.Empty;

    /// <summary>降级策略：模型优先或账号优先，决定额度不足时的让位顺序。</summary>
    public DegradationStrategy DegradationStrategy
    {
        get => _degradationStrategy;
        set => SetProperty(ref _degradationStrategy, value);
    }

    /// <summary>专家模式类型（无 / 单专家 / 专家团）。</summary>
    public ExpertKind ExpertKind
    {
        get => _expertKind;
        set => SetProperty(ref _expertKind, value);
    }

    /// <summary>所选专家的标识，未配置时为空串。</summary>
    public string ExpertId
    {
        get => _expertId;
        set => SetProperty(ref _expertId, value);
    }

    /// <summary>专家显示名；空白写回时归一为 null。</summary>
    public string ExpertDisplayName
    {
        get => _expertDisplayName;
        set => SetProperty(ref _expertDisplayName, value);
    }

    /// <summary>降级策略下拉的可选值列表。</summary>
    public IReadOnlyList<DegradationStrategy> Strategies { get; } =
        new[]
        {
            global::BatchAgentRunner.Core.Domain.DegradationStrategy.ModelFirst,
            global::BatchAgentRunner.Core.Domain.DegradationStrategy.AccountFirst
        };

    /// <summary>专家模式下拉的可选值列表。</summary>
    public IReadOnlyList<ExpertKind> ExpertKinds { get; } =
        new[]
        {
            global::BatchAgentRunner.Core.Domain.ExpertKind.None,
            global::BatchAgentRunner.Core.Domain.ExpertKind.Single,
            global::BatchAgentRunner.Core.Domain.ExpertKind.Team
        };

    /// <summary>从运行配置加载本域状态。</summary>
    public void LoadFrom(RunOptions o)
    {
        DegradationStrategy = o.DegradationStrategy;
        ExpertKind = o.Expert.Kind;
        ExpertId = o.Expert.Id ?? string.Empty;
        ExpertDisplayName = o.Expert.DisplayName ?? string.Empty;
    }

    /// <summary>把本域状态写回运行配置（空白回落见 <see cref="ConfigFieldRules"/>）。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.DegradationStrategy = DegradationStrategy;
        o.Expert.Kind = ExpertKind;
        o.Expert.Id = ConfigFieldRules.NullIfBlank(ExpertId);
        o.Expert.DisplayName = ConfigFieldRules.NullIfBlank(ExpertDisplayName);
    }
}
