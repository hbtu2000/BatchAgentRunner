using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Options;

/// <summary>⑩ 二期配置域：通知 / 会话恢复 / 质量门禁 / 回写 / DAG / 模板展开 / diff 采集 / 版本控制。</summary>
public sealed partial class RunOptions
{
    /// <summary>通知规则（REQ-018）。默认空（不推送）。</summary>
    [JsonPropertyName("notifications")]
    public List<NotificationRule> Notifications { get; set; } = new();

    /// <summary>启用断点会话恢复（方案 b，PRD 15.4.6）。默认关，回退一期方案 a（完整指令重发）。</summary>
    [JsonPropertyName("enable_session_resume")]
    public bool EnableSessionResume { get; set; }

    /// <summary>质量门禁修订循环默认上限（二期）。被阶段 MaxLoops 覆盖。</summary>
    [JsonPropertyName("default_max_gate_loops")]
    public int DefaultMaxGateLoops { get; set; } = 3;

    /// <summary>Markdown 任务文件原地回写（二期）。默认关，保持一期 sidecar 行为。</summary>
    [JsonPropertyName("markdown_in_place_writeback")]
    public bool MarkdownInPlaceWriteback { get; set; }

    /// <summary>依赖失败策略（DAG，二期）。</summary>
    [JsonPropertyName("dependency_failure_policy")]
    public DependencyFailurePolicy DependencyFailurePolicy { get; set; } = DependencyFailurePolicy.BlockDependents;

    /// <summary>模板展开单批上限（REQ-016）：超过则要求确认。默认 50（对齐一期每批 ≥50 条验证规模）。</summary>
    [JsonPropertyName("template_batch_expansion_limit")]
    public int TemplateBatchExpansionLimit { get; set; } = 50;

    /// <summary>是否采集结果 diff（REQ-017）。默认关（由引擎可选注入 IDiffCollector 时生效）。</summary>
    [JsonPropertyName("collect_diffs")]
    public bool CollectDiffs { get; set; }

    /// <summary>diff 采集的受关注文件 glob（相对于工作空间）。</summary>
    [JsonPropertyName("diff_target_globs")]
    public List<string> DiffTargetGlobs { get; set; } = new();

    /// <summary>
    /// 版本控制全局默认（二期项目级任务，配置域 12）。
    /// 作为任务级 <see cref="ProjectRef"/> 的兜底：任务未填的字段用这里的值，
    /// 任务已填的字段以任务为准。仓库路径为空 = 未配置 Git 集成。
    /// </summary>
    [JsonPropertyName("git")]
    public GitConfig Git { get; set; } = new();
}
