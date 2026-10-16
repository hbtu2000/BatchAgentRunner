using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 命名工作空间（REQ-003a）。三级覆盖：单任务 &gt; 本批默认 &gt; 全局默认。
/// </summary>
public sealed class WorkspaceConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("is_global_default")]
    public bool IsGlobalDefault { get; set; }

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Path);

    public override string ToString() => $"{Name}：{Path}";
}
