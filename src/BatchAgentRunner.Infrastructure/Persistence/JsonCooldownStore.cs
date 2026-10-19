using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Infrastructure.Persistence;

/// <summary>
/// 冷却记录持久化（REQ-004c）：随运行日志落盘，工具重启后仍然有效。
/// </summary>
public sealed class JsonCooldownStore : ICooldownStore
{
    private readonly string _filePath;

    public JsonCooldownStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchAgentRunner");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "cooldowns.json");
    }

    public string FilePath => _filePath;

    public async Task<IReadOnlyList<QuotaCooldownRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<QuotaCooldownRecord>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var records = JsonSerializer.Deserialize<List<QuotaCooldownRecord>>(json, JsonDefaults.Options)
                   ?? new List<QuotaCooldownRecord>();

            // 自愈：早期版本把整段 CLI 输出（可达数十 MB）塞进了 raw 字段，导致 cooldowns.json 膨胀、
            // 引擎初始化变慢。读取时若发现超长 raw 就截断，并在确有变化时原子重写，使已膨胀的文件
            // 在下次启动时自动瘦身（2026-10-18 现场：1 条记录 raw 19MB）。
            if (records.Any(r => r.RawValue is { Length: > QuotaCooldownRecord.MaxRawChars }))
            {
                foreach (var r in records)
                {
                    if (r.RawValue is { Length: > QuotaCooldownRecord.MaxRawChars })
                    {
                        r.RawValue = "…(已截断，原文 " + r.RawValue.Length + " 字符)…\n"
                                     + r.RawValue[^QuotaCooldownRecord.MaxRawChars..];
                    }
                }

                await SaveAsync(records, cancellationToken).ConfigureAwait(false);
            }

            return records;
        }
        catch (JsonException)
        {
            return Array.Empty<QuotaCooldownRecord>();
        }
    }

    public async Task SaveAsync(IEnumerable<QuotaCooldownRecord> records,
        CancellationToken cancellationToken = default)
    {
        var json = JsonDefaults.Serialize(records.ToList());
        await AtomicFileWriter.WriteAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }
}
