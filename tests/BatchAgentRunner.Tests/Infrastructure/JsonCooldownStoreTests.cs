using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.Persistence;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// 2026-10-18 现场回归：早期版本把整段 CLI 输出（可达数十 MB）塞进冷却记录的 raw 字段，
/// cooldowns.json 因此膨胀到 20MB，拖慢引擎初始化。读取时应自愈——截断超长 raw 并重写文件。
/// </summary>
public class JsonCooldownStoreTests
{
    private static string TempDir(string name) => Path.Combine(Path.GetTempPath(), $"bar-cooldown-{name}-{Guid.NewGuid():N}");

    [Fact]
    public async Task 读取时超长_raw_被截断且文件瘦身()
    {
        var dir = TempDir("shrink");
        try
        {
            var store = new JsonCooldownStore(dir);
            var bigRaw = new string('x', 100_000) + "quota exceeded, resets in 1h";

            await store.SaveAsync(new List<QuotaCooldownRecord>
            {
                new()
                {
                    AccountId = "acc-1",
                    ModelName = "hy3",
                    ResetAt = DateTimeOffset.Parse("2026-10-15T12:00:00+08:00"),
                    Source = CooldownSource.CliParsed,
                    RawValue = bigRaw,
                    RecordedAt = DateTimeOffset.Parse("2026-10-15T10:00:00+08:00")
                }
            });

            var bloatedSize = new FileInfo(store.FilePath).Length;
            Assert.True(bloatedSize > 100_000, "前置条件：文件应已膨胀");

            // 读取触发自愈（截断 + 原子重写）
            var loaded = await store.LoadAsync();

            Assert.Single(loaded);
            var raw = loaded[0].RawValue!;
            Assert.True(raw.Length <= QuotaCooldownRecord.MaxRawChars + 64,
                $"raw 未被截断：{raw.Length}");
            Assert.Contains("quota exceeded", raw);

            // 重新落盘后的体积应显著缩小
            var shrunkSize = new FileInfo(store.FilePath).Length;
            Assert.True(shrunkSize < bloatedSize / 2,
                $"文件未瘦身：{bloatedSize} → {shrunkSize}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 正常长度_raw_读取后不被改写()
    {
        var dir = TempDir("keep");
        try
        {
            var store = new JsonCooldownStore(dir);
            var normalRaw = "额度已用尽，请在 2026-10-15 12:00:00 后重试";

            await store.SaveAsync(new List<QuotaCooldownRecord>
            {
                new()
                {
                    AccountId = "acc-2",
                    ModelName = "hy4",
                    ResetAt = DateTimeOffset.Parse("2026-10-15T12:00:00+08:00"),
                    Source = CooldownSource.DefaultFallback,
                    RawValue = normalRaw,
                    RecordedAt = DateTimeOffset.Parse("2026-10-15T10:00:00+08:00")
                }
            });

            var loaded = await store.LoadAsync();

            Assert.Single(loaded);
            Assert.Equal(normalRaw, loaded[0].RawValue);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
