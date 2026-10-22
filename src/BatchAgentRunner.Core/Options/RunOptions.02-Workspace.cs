using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Options;

/// <summary>② 工作空间（REQ-003a / REQ-019）。</summary>
public sealed partial class RunOptions
{
    /// <summary>命名工作空间列表（三级覆盖：单任务 &gt; 本批默认 &gt; 全局默认）。</summary>
    [JsonPropertyName("workspaces")]
    public List<WorkspaceConfig> Workspaces { get; set; } = new();

    /// <summary>本批默认工作空间名称；为空则回退到全局默认（<c>is_global_default</c> 项）。</summary>
    [JsonPropertyName("default_workspace")]
    public string? DefaultWorkspace { get; set; }

    /// <summary>
    /// 命名工作空间数量上限（REQ-019，默认 10）。引擎不设绝对硬上限，以配置值为唯一校验准绳；
    /// 配置页建议对 UI 输入范围作合理限制（如 1–100）以避免不可维护，但允许高级用户经
    /// <c>config.json</c> 设更大值。所有数量校验（新增拦截、预检告警）均以该配置值为准，而非硬编码。
    /// </summary>
    [JsonPropertyName("max_workspaces")]
    public int MaxWorkspaces { get; set; } = 10;
}
