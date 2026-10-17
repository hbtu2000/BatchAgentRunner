using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

public enum ExpertKind
{
    /// <summary>未指定，使用默认智能体（REQ-004a）。</summary>
    None,
    Single,
    Team
}

/// <summary>
/// 专家 / 专家团引用（REQ-004a）。
/// 若 M0 V-5 验证 CLI 不支持专家参数，降级为 prompt 注入角色描述（<see cref="RolePrompt"/>）。
/// </summary>
public sealed class ExpertRef
{
    [JsonPropertyName("kind")]
    public ExpertKind Kind { get; set; } = ExpertKind.None;

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>角色描述，用于 prompt 注入降级方案。</summary>
    [JsonPropertyName("role_prompt")]
    public string? RolePrompt { get; set; }

    [JsonIgnore]
    public bool IsSpecified => Kind != ExpertKind.None && !string.IsNullOrWhiteSpace(Id);

    public static ExpertRef Default() => new();

    public ExpertRef()
    {
    }

    /// <summary>按专家 id 构造「单专家」引用（二期工作流阶段执行器覆盖用）。</summary>
    public ExpertRef(string id)
    {
        Kind = ExpertKind.Single;
        Id = id;
    }

    public override string ToString() => IsSpecified
        ? $"{DisplayName ?? Id}{(Kind == ExpertKind.Team ? "（专家团）" : "（专家）")}"
        : "默认智能体";
}
