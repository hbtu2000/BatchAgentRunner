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
            return JsonSerializer.Deserialize<List<QuotaCooldownRecord>>(json, JsonDefaults.Options)
                   ?? new List<QuotaCooldownRecord>();
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
