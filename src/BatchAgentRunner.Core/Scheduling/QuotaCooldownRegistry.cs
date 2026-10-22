using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 「账号 × 模型」组合的额度冷却登记表（REQ-004c）。
/// 线程安全：并发场景下多个任务共享同一可用性视图（REQ-012）。
/// </summary>
public sealed class QuotaCooldownRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, QuotaCooldownRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly IClock _clock;
    private TimeSpan _defaultCooldown;

    /// <summary>新冷却记录写入事件（供上层广播 / 落盘）。</summary>
    public event EventHandler<QuotaCooldownRecord>? Recorded;

    /// <summary>构造冷却登记表。</summary>
    public QuotaCooldownRegistry(IClock clock, TimeSpan? defaultCooldown = null,
        IEnumerable<QuotaCooldownRecord>? initial = null)
    {
        _clock = clock;
        _defaultCooldown = defaultCooldown ?? TimeSpan.FromMinutes(60);
        if (initial is not null)
        {
            foreach (var r in initial)
            {
                _records[QuotaCooldownRecord.BuildKey(r.AccountId, r.ModelName)] = r;
            }
        }
    }

    /// <summary>默认冷却时长（CLI 重置时间缺失 / 不可靠时使用）。</summary>
    public TimeSpan DefaultCooldown
    {
        get { lock (_sync) { return _defaultCooldown; } }
        set { lock (_sync) { _defaultCooldown = value; } }
    }

    /// <summary>当前全部记录的只读快照（按重置时间升序）。</summary>
    /// <returns>记录列表快照。</returns>
    public IReadOnlyList<QuotaCooldownRecord> Snapshot()
    {
        lock (_sync)
        {
            return _records.Values.OrderBy(r => r.ResetAt).ToList();
        }
    }

    /// <summary>判定组合是否处于冷却中（重置时间晚于当前时刻）。</summary>
    /// <returns>冷却中返回 true。</returns>
    public bool IsCooling(string accountId, string modelName, DateTimeOffset now, out DateTimeOffset resetAt)
    {
        lock (_sync)
        {
            if (_records.TryGetValue(QuotaCooldownRecord.BuildKey(accountId, modelName), out var rec) && rec.ResetAt > now)
            {
                resetAt = rec.ResetAt;
                return true;
            }
        }

        resetAt = default;
        return false;
    }

    /// <summary>
    /// 记录额度耗尽。cliResetAt 为空或不可靠（早于当前时刻）时降级为默认冷却时长并标注来源。
    /// </summary>
    public QuotaCooldownRecord Record(string accountId, string modelName, DateTimeOffset? cliResetAt,
        DateTimeOffset now, string? raw = null)
    {
        var reliable = cliResetAt.HasValue && cliResetAt.Value > now;
        var resetAt = reliable ? cliResetAt!.Value : now.Add(_defaultCooldown);
        var rec = new QuotaCooldownRecord
        {
            AccountId = accountId,
            ModelName = modelName,
            ResetAt = resetAt,
            Source = reliable ? CooldownSource.CliParsed : CooldownSource.DefaultFallback,
            RawValue = raw,
            RecordedAt = now
        };

        lock (_sync)
        {
            _records[rec.Key] = rec;
        }

        Recorded?.Invoke(this, rec);
        return rec;
    }

    /// <summary>手动重置某组合的冷却标记。</summary>
    public void Clear(string accountId, string modelName)
    {
        lock (_sync)
        {
            _records.Remove(QuotaCooldownRecord.BuildKey(accountId, modelName));
        }
    }

    /// <summary>清空全部冷却标记。</summary>
    public void ClearAll()
    {
        lock (_sync)
        {
            _records.Clear();
        }
    }

    /// <summary>清理已过期的记录（组合已自动恢复）。</summary>
    public int PurgeExpired(DateTimeOffset now)
    {
        lock (_sync)
        {
            var expired = _records.Where(kv => kv.Value.ResetAt <= now).Select(kv => kv.Key).ToList();
            foreach (var k in expired)
            {
                _records.Remove(k);
            }

            return expired.Count;
        }
    }
}
