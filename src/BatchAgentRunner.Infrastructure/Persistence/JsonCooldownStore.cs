using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.Persistence;

/// <summary>
/// 冷却记录持久化（REQ-004c）：随运行日志落盘，工具重启后仍然有效。
/// </summary>
public sealed class JsonCooldownStore : JsonStoreBase<List<QuotaCooldownRecord>>, ICooldownStore
{
    public JsonCooldownStore(string? directory = null)
        : base(directory, "cooldowns.json")
    {
    }

    // 基类的文档类型是 List<T>，接口要求 IReadOnlyList<T> / IEnumerable<T>——仅做签名转发，不含逻辑。
    async Task<IReadOnlyList<QuotaCooldownRecord>> ICooldownStore.LoadAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken).ConfigureAwait(false);

    Task ICooldownStore.SaveAsync(IEnumerable<QuotaCooldownRecord> records, CancellationToken cancellationToken) =>
        SaveAsync(records.ToList(), cancellationToken);

    /// <inheritdoc />
    protected override List<QuotaCooldownRecord> CreateEmpty() => new();

    /// <summary>
    /// 自愈：早期版本把整段 CLI 输出（可达数十 MB）塞进了 raw 字段，导致 cooldowns.json 膨胀、
    /// 引擎初始化变慢。读取时若发现超长 raw 就截断，并在确有变化时原子重写，使已膨胀的文件
    /// 在下次启动时自动瘦身（2026-10-18 现场：1 条记录 raw 19MB）。
    /// </summary>
    protected override async ValueTask<List<QuotaCooldownRecord>> NormalizeAsync(
        List<QuotaCooldownRecord> value, CancellationToken cancellationToken)
    {
        if (!value.Any(r => r.RawValue is { Length: > QuotaCooldownRecord.MaxRawChars }))
        {
            return value;
        }

        foreach (var r in value)
        {
            if (r.RawValue is { Length: > QuotaCooldownRecord.MaxRawChars })
            {
                r.RawValue = "…(已截断，原文 " + r.RawValue.Length + " 字符)…\n"
                             + r.RawValue[^QuotaCooldownRecord.MaxRawChars..];
            }
        }

        await SaveAsync(value, cancellationToken).ConfigureAwait(false);
        return value;
    }
}
