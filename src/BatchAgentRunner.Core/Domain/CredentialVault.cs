using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>快照内单个凭证文件的指纹（DESIGN-账号凭证切换-v1.0 §4.2）。</summary>
public sealed record CredentialFileInfo(
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

/// <summary>
/// 快照清单 manifest.json（DESIGN-账号凭证切换-v1.0 §4.2，snake_case 契约）。
/// 只含元数据与哈希，不含凭证内容；哈希为明文内容的 SHA-256（用于 Live ↔ Snapshot 比对）。
/// </summary>
public sealed class SnapshotManifest
{
    /// <summary>必须与配置中 accounts[].id 一致（同时是 Vault 目录名）。</summary>
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    /// <summary>codebuddy | workbuddy | mock。</summary>
    [JsonPropertyName("cli")]
    public string Cli { get; set; } = string.Empty;

    /// <summary>账号展示名（人读）。</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>登录站点（CodeBuddy CLI 首启选择）；切换时若与当前 Live 不一致需提示。</summary>
    [JsonPropertyName("site")]
    public string? Site { get; set; }

    /// <summary>快照创建时间（UTC）。</summary>
    [JsonPropertyName("backed_up_at")]
    public DateTimeOffset BackedUpAt { get; set; }

    /// <summary>最近一次预检通过时间。</summary>
    [JsonPropertyName("last_verified_at")]
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>快照内凭证文件指纹清单（DESIGN §4.2）。</summary>
    [JsonPropertyName("files")]
    public List<CredentialFileInfo> Files { get; set; } = new();

    /// <summary>快照结构版本（当前 1）。</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;
}

/// <summary>快照摘要（不含凭证内容，供界面展示 / 预检判断）。</summary>
public sealed record CredentialSnapshotInfo(
    string AccountId,
    string Cli,
    string? DisplayName,
    string? Site,
    DateTimeOffset BackedUpAt,
    DateTimeOffset? LastVerifiedAt,
    IReadOnlyList<CredentialFileInfo> Files);

/// <summary>
/// 凭证 Vault 操作失败（进程互斥冲突 / Live 凭证不存在 / 快照缺失 / 回填失败已回滚等）。
/// 引擎与界面按消息提示用户，不中断整个批处理（引擎侧转为停用该账号）。
/// </summary>
public sealed class CredentialVaultException : Exception
{
    /// <summary>构造凭证 Vault 异常（仅消息）。</summary>
    public CredentialVaultException(string message) : base(message)
    {
    }

    /// <summary>构造凭证 Vault 异常（消息 + 内部异常）。</summary>
    public CredentialVaultException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
