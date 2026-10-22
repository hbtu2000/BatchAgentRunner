using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 账号池项（REQ-003 / 故事 2.3；v1.12：候选模型池与兜底模型池挂在账号下）。
/// 安全约定：此处仅存放账号「标识 + 凭据引用」，凭据明文不写入任务文件与日志（PRD 第 8 章）。
/// </summary>
public sealed class AccountConfig
{
    /// <summary>账号唯一标识（任务文件按 id 引用该账号）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>展示名（界面 / 日志用；为空时回退显示 Id）。</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>凭据引用（profile 名 / 环境变量名 / 系统凭据库 key），不明文保存。</summary>
    [JsonPropertyName("credential_ref")]
    public string CredentialReference { get; set; } = string.Empty;

    /// <summary>是否启用（默认 true；false 的账号不参与调度）。</summary>
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

    /// <summary>备注：仅供用户自解释，不参与任何调度逻辑。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>界面标签：DisplayName 非空时取之，否则回退 Id。</summary>
    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;

    /// <summary>该账号是否具备兜底能力（配置了至少一个兜底模型）。</summary>
    [JsonIgnore]
    public bool HasFallbackModels => FallbackModels is { Count: > 0 };

    /// <summary>返回界面标签（<see cref="Label"/>）。</summary>
    public override string ToString() => Label;
}
