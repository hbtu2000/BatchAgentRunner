using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Infrastructure.Persistence;

/// <summary>
/// 积分领取台账持久化（配置目录 credits-claims.json，与 config.json 同目录）。
/// 只记结果元数据（账号 id / 状态 / 前后积分），不含任何票据内容。
/// </summary>
public sealed class JsonCreditsClaimStore : ICreditsClaimStore
{
    private readonly string _filePath;

    public JsonCreditsClaimStore(string? directory = null)
    {
        // P1-18：原实现 `directory ?? Path.Combine(...)` 建目录，却用 `Path.Combine(directory!, ...)`
        // 取路径——directory 为 null 时 `directory!` 仍 null，Path.Combine 必抛 ArgumentNullException。
        // 默认构造（不传参）在生产中虽总传参未暴露，但任何走默认构造的新调用点会立刻崩。改为先解析到局部变量。
        var resolved = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchAgentRunner");
        Directory.CreateDirectory(resolved);
        _filePath = Path.Combine(resolved, "credits-claims.json");
    }

    public string FilePath => _filePath;

    public async Task<CreditsClaimLedger> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return new CreditsClaimLedger();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CreditsClaimLedger>(json, JsonDefaults.Options) ?? new CreditsClaimLedger();
        }
        catch (JsonException)
        {
            return new CreditsClaimLedger();
        }
    }

    public Task SaveAsync(CreditsClaimLedger ledger, CancellationToken cancellationToken = default) =>
        AtomicFileWriter.WriteAsync(_filePath, JsonDefaults.Serialize(ledger), cancellationToken);
}
