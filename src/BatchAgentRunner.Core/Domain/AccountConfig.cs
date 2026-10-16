using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 账号池项（REQ-003 / 故事 2.3；v1.12：候选模型池与兜底模型池挂在账号下）。
/// 安全约定：此处仅存放账号「标识 + 凭据引用」，凭据明文不写入任务文件与日志（PRD 第 8 章）。
/// </summary>
public sealed class AccountConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>凭据引用（profile 名 / 环境变量名 / 系统凭据库 key），不明文保存。</summary>
    [JsonPropertyName("credential_ref")]
    public string CredentialReference { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 该账号自有的有序候选模型池（REQ-003，v1.12）。
    /// null 或为空时回退使用全局共享列表 <see cref="Options.RunOptions.ModelCandidates"/>（向后兼容）。
    /// 同名模型在不同账号下视为不同组合（免费时段 / 单价独立）。
    /// </summary>
    [JsonPropertyName("model_candidates")]
    public List<ModelCandidate>? ModelCandidates { get; set; }

    /// <summary>
    /// 该账号自有的有序兜底模型池（REQ-004b，v1.12）。null 表示该账号无兜底能力；
    /// 兜底触发时按「账号顺序 × 该账号兜底池顺序」取第一个可用组合，不参与免费时段过滤。
    /// </summary>
    [JsonPropertyName("fallback_models")]
    public List<FallbackModel>? FallbackModels { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;

    /// <summary>
    /// 该账号自有的候选模型池（不含全局回退——「未配置自有池时回退全局列表」由
    /// <see cref="Scheduling.DegradationMatrix"/> 在展开矩阵时统一处理）。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ModelCandidate> EffectiveModelPool =>
        ModelCandidates is { Count: > 0 } own ? own : Array.Empty<ModelCandidate>();

    /// <summary>该账号是否具备兜底能力（配置了至少一个兜底模型）。</summary>
    [JsonIgnore]
    public bool HasFallbackModels => FallbackModels is { Count: > 0 };

    public override string ToString() => Label;
}
