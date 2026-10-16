using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Tests;

/// <summary>内存任务文件存储（测试用）。</summary>
public sealed class InMemoryTaskFileStore : ITaskFileStore
{
    private List<TaskItem> _tasks = new();

    public List<TaskItem> Tasks
    {
        get => _tasks;
        set => _tasks = value;
    }

    public int SaveCount { get; private set; }

    public Task<TaskFileLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TaskFileLoadResult(_tasks, path, false, Array.Empty<TaskFileIssue>()));

    public Task SaveAsync(string path, IReadOnlyList<TaskItem> tasks, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        _tasks = tasks.ToList();
        return Task.CompletedTask;
    }

    public Task<string?> BackupAsync(string path, int retention, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public string ResolveResultPath(string path) => path;

    public bool IsMarkdown(string path) => false;
}

/// <summary>内存冷却存储（测试用）。</summary>
public sealed class InMemoryCooldownStore : ICooldownStore
{
    private List<QuotaCooldownRecord> _records = new();

    public Task<IReadOnlyList<QuotaCooldownRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<QuotaCooldownRecord>>(_records);

    public Task SaveAsync(IEnumerable<QuotaCooldownRecord> records, CancellationToken cancellationToken = default)
    {
        _records = records.ToList();
        return Task.CompletedTask;
    }
}

/// <summary>测试时间构造：本地时区的固定时刻。</summary>
public static class At
{
    public static DateTimeOffset Local(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));
}

/// <summary>
/// 测试用凭证保护器：魔数 + XOR 0xA5（对称可逆、确定性、跨平台）。
/// 损坏密文解密时抛 CryptographicException，与真实 DPAPI 行为一致，
/// 用于验证「快照静态存储 ≠ 明文」与「回填失败回滚」契约。
/// </summary>
public sealed class TestCredentialProtector : ICredentialProtector
{
    private static readonly byte[] Magic = [(byte)'V', (byte)'P', (byte)'1'];

    public byte[] Protect(byte[] plaintext)
    {
        var output = new byte[plaintext.Length + Magic.Length];
        Buffer.BlockCopy(Magic, 0, output, 0, Magic.Length);
        for (var i = 0; i < plaintext.Length; i++)
        {
            output[Magic.Length + i] = (byte)(plaintext[i] ^ 0xA5);
        }

        return output;
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (ciphertext.Length < Magic.Length || !Magic.AsSpan().SequenceEqual(ciphertext.AsSpan(0, Magic.Length)))
        {
            throw new System.Security.Cryptography.CryptographicException("凭证密文损坏（魔数不匹配）");
        }

        var output = new byte[ciphertext.Length - Magic.Length];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = (byte)(ciphertext[Magic.Length + i] ^ 0xA5);
        }

        return output;
    }
}

/// <summary>固定返回值的 CLI 进程守卫假件。</summary>
public sealed class FakeCliProcessGuard(bool running = false) : ICliProcessGuard
{
    public bool Running { get; set; } = running;

    public bool IsCliRunning(AgentKind cli) => Running;
}

