using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>专家引用类型（REQ-004a）：未指定 / 单专家 / 专家团；经 <c>JsonStringEnumConverter</c> 以字符串形式持久化。</summary>
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

    /// <summary>专家团（多专家协作）。</summary>
    Team
}

/// <summary>
/// 专家 / 专家团引用（REQ-004a）。
/// 若 M0 V-5 验证 CLI 不支持专家参数，降级为 prompt 注入角色描述（<see cref="RolePrompt"/>）。
/// </summary>
public sealed class ExpertRef
{
    /// <summary>专家类型：<see cref="ExpertKind.None"/> 未指定 / <see cref="ExpertKind.Single"/> 单专家 / <see cref="ExpertKind.Team"/> 专家团。</summary>
    [JsonPropertyName("kind")]
    public ExpertKind Kind { get; set; } = ExpertKind.None;

    /// <summary>专家 id（CLI 专家参数值；<see cref="ExpertKind.None"/> 时可为空）。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>展示名（界面 / 日志用，不参与调度）。</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>角色描述，用于 prompt 注入降级方案。</summary>
    [JsonPropertyName("role_prompt")]
    public string? RolePrompt { get; set; }

    /// <summary>是否已指定有效专家（类型非 <see cref="ExpertKind.None"/> 且 id 非空白）。</summary>
    [JsonIgnore]
    public bool IsSpecified => Kind != ExpertKind.None && !string.IsNullOrWhiteSpace(Id);

    /// <summary>默认引用：未指定专家，使用默认智能体。</summary>
    public static ExpertRef Default() => new();

    /// <summary>无参构造（反序列化与 <c>Default()</c> 使用）。</summary>
    public ExpertRef()
    {
    }

    /// <summary>按专家 id 构造「单专家」引用（二期工作流阶段执行器覆盖用）。</summary>
    public ExpertRef(string id)
    {
        Kind = ExpertKind.Single;
        Id = id;
    }

    /// <summary>展示用字符串：已指定为「展示名（专家 / 专家团）」，否则为「默认智能体」。</summary>
    public override string ToString() => IsSpecified
        ? $"{DisplayName ?? Id}{(Kind == ExpertKind.Team ? "（专家团）" : "（专家）")}"
        : "默认智能体";
}
