using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>单个账号每日积分领取的结果状态。</summary>
public enum CreditsClaimStatus
{
    /// <summary>本次成功领取（领取后积分 &gt; 领取前）。</summary>
    Claimed,

    /// <summary>服务端反馈今日已领取过（未重复发放）。</summary>
    AlreadyClaimed,

    /// <summary>领取失败（切换凭证 / 读票据 / HTTP 调用任一环节出错）。</summary>
    Failed,

    /// <summary>今日已领取过且未强制重试，跳过执行。</summary>
    Skipped
}

/// <summary>
/// 单账号一次积分领取的完整记录（同时用于台账持久化与界面/报告展示）。
/// 核心契约：必须记录「领取前积分」与「领取后积分」，变化量 = 后 − 前。
/// </summary>
public sealed class CreditsClaimRecord
{
    /// <summary>账号 id。</summary>
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    /// <summary>账号展示名（快照，便于台账直接展示）。</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>CLI 类型（codebuddy / workbuddy / mock）。</summary>
    [JsonPropertyName("cli")]
    public string Cli { get; set; } = string.Empty;

    /// <summary>领取结果状态。</summary>
    [JsonPropertyName("status")]
    public CreditsClaimStatus Status { get; set; }

    /// <summary>领取前积分（服务端未返回时为 null）。</summary>
    [JsonPropertyName("before_credits")]
    public decimal? BeforeCredits { get; set; }

    /// <summary>领取后积分（服务端未返回时为 null）。</summary>
    [JsonPropertyName("after_credits")]
    public decimal? AfterCredits { get; set; }

    /// <summary>积分变化量（领取后 − 领取前；任一侧未知时为 null）。</summary>
    [JsonPropertyName("delta_credits")]
    public decimal? DeltaCredits { get; set; }

    /// <summary>服务端返回的本次发放量（若有）。</summary>
    [JsonPropertyName("granted_credits")]
    public decimal? GrantedCredits { get; set; }

    /// <summary>服务端返回的提示消息（若有）。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>失败原因（仅 Failed 时有值；不含凭证明文）。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>领取动作发生时间（本地时间）。</summary>
    [JsonPropertyName("claimed_at")]
    public DateTimeOffset ClaimedAt { get; set; }

    /// <summary>摘要形如 <c>account_id:状态</c>（日志/排障用）。</summary>
    public override string ToString() => $"{AccountId}:{Status}";
}

/// <summary>积分领取台账（持久化于配置目录 credits-claims.json，跨次运行累积，用于「每日一次」去重）。</summary>
public sealed class CreditsClaimLedger
{
    /// <summary>台账结构版本（迁移钩子读它）。</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>历史领取记录（按时间追加）。</summary>
    [JsonPropertyName("records")]
    public List<CreditsClaimRecord> Records { get; set; } = new();
}

/// <summary>一次「批量领取」的汇总（本次运行范围内，非台账全量）。</summary>
public sealed class CreditsClaimSummary
{
    /// <summary>汇总生成时间。</summary>
    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>本次批量领取的 CLI 类型。</summary>
    [JsonPropertyName("cli")]
    public string Cli { get; set; } = string.Empty;

    /// <summary>本次运行范围内的领取记录。</summary>
    [JsonPropertyName("records")]
    public List<CreditsClaimRecord> Records { get; set; } = new();

    /// <summary>本次成功领取数。</summary>
    [JsonIgnore]
    public int ClaimedCount => Count(CreditsClaimStatus.Claimed);

    /// <summary>服务端反馈「今日已领取过」数。</summary>
    [JsonIgnore]
    public int AlreadyClaimedCount => Count(CreditsClaimStatus.AlreadyClaimed);

    /// <summary>失败数。</summary>
    [JsonIgnore]
    public int FailedCount => Count(CreditsClaimStatus.Failed);

    /// <summary>跳过（今日已领且未强制重试）数。</summary>
    [JsonIgnore]
    public int SkippedCount => Count(CreditsClaimStatus.Skipped);

    /// <summary>成功领取（含「已领取过」）的账号数。</summary>
    [JsonIgnore]
    public int OkCount => ClaimedCount + AlreadyClaimedCount;

    private int Count(CreditsClaimStatus status) =>
        Records.Count(r => r.Status == status);
}
