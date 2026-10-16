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
        Directory.CreateDirectory(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchAgentRunner"));
        _filePath = Path.Combine(directory!, "credits-claims.json");
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
