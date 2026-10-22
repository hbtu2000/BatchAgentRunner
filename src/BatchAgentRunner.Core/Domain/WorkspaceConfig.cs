using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 命名工作空间（REQ-003a）。三级覆盖：单任务 &gt; 本批默认 &gt; 全局默认。
/// </summary>
public sealed class WorkspaceConfig
{
    /// <summary>工作空间名称（引用键：任务与批默认均按名引用）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>工作空间目录路径（本地文件系统路径）。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>是否为全局默认工作空间（三级覆盖中的最底层）。</summary>
    [JsonPropertyName("is_global_default")]
    public bool IsGlobalDefault { get; set; }

    /// <summary>配置是否有效（名称与路径均非空白）。</summary>
    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Path);

    /// <summary>展示用字符串：「名称：路径」。</summary>
    public override string ToString() => $"{Name}：{Path}";
}
