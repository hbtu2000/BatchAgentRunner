using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

public enum ExpertKind
{
    /// <summary>未指定，使用默认智能体（REQ-004a）。</summary>
    None,

    /// <summary>
    /// 单专家。
    /// CA1720（标识符含类型名 "Single"）显式豁免并登记于治理方案 §4.7 逃逸阀台账：
    /// <see cref="ExpertKind"/> 经 <c>JsonStringEnumConverter</c> 以字符串形式持久化在用户模板文件中，
    /// 重命名成员会破坏既有模板兼容性（同 <c>RunOptions</c> 契约保护原则）。
    /// </summary>
    [SuppressMessage("Naming", "CA1720:标识符包含类型名称", Justification = "持久化兼容：ExpertKind.Single 已随用户模板落盘，重命名破坏兼容；逃逸阀台账 #7 / 到期日 2027-01-20 / 责任人 工程")]
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
