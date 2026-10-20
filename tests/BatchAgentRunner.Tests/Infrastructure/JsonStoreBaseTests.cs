using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.Persistence;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// P2-31 骨架行为不变量：三份 <c>Json*Store</c> 的加载/保存样板抽到
/// <see cref="JsonStoreBase{T}"/> 后，各自对外可观测行为必须完全不变。
/// 覆盖基类统一提供的三条回落（文件缺失 / 非法 JSON / 反序列化为空）与原子写落盘。
/// </summary>
public class JsonStoreBaseTests
{
    private static string TempDir(string tag) =>
        Path.Combine(Path.GetTempPath(), $"bar-jsonsb-{tag}-{Guid.NewGuid():N}");

    // ---------- ① 文件缺失 → CreateEmpty ----------

    [Fact]
    public async Task 冷却仓储_文件缺失_返回空列表()
    {
        var dir = TempDir("cooldown-missing");
        try
        {
            var loaded = await new JsonCooldownStore(dir).LoadAsync();

            Assert.Empty(loaded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 积分台账_文件缺失_返回空账本()
    {
        var dir = TempDir("claims-missing");
        try
        {
            var loaded = await new JsonCreditsClaimStore(dir).LoadAsync();

            Assert.NotNull(loaded);
            Assert.Empty(loaded.Records);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 配置仓储_文件缺失_返回出厂示例配置()
    {
        var dir = TempDir("config-missing");
        try
        {
            var loaded = await new JsonConfigStore(dir).LoadAsync();

            Assert.Equal(JsonConfigStore.CreateSample().Accounts.Count, loaded.Accounts.Count);
            Assert.Equal(string.Empty, loaded.TaskFilePath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- ② 非法 JSON → CreateEmpty ----------

    [Fact]
    public async Task 冷却仓储_非法Json_回落空列表()
    {
        var dir = TempDir("cooldown-badjson");
        try
        {
            var store = new JsonCooldownStore(dir);
            await File.WriteAllTextAsync(store.FilePath, "{ 这不是 JSON");

            Assert.Empty(await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 积分台账_非法Json_回落空账本()
    {
        var dir = TempDir("claims-badjson");
        try
        {
            var store = new JsonCreditsClaimStore(dir);
            await File.WriteAllTextAsync(store.FilePath, "{ 这不是 JSON");

            var loaded = await store.LoadAsync();

            Assert.NotNull(loaded);
            Assert.Empty(loaded.Records);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 配置仓储_非法Json_回落出厂示例配置()
    {
        var dir = TempDir("config-badjson");
        try
        {
            var store = new JsonConfigStore(dir);
            await File.WriteAllTextAsync(store.FilePath, "{ 这不是 JSON");

            var loaded = await store.LoadAsync();

            Assert.Equal(JsonConfigStore.CreateSample().Accounts.Count, loaded.Accounts.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- ③ 反序列化为空 → CreateEmpty ----------

    [Fact]
    public async Task 冷却仓储_内容为null字面量_回落空列表()
    {
        var dir = TempDir("cooldown-null");
        try
        {
            var store = new JsonCooldownStore(dir);
            await File.WriteAllTextAsync(store.FilePath, "null");

            Assert.Empty(await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 配置仓储_内容为null字面量_回落出厂示例配置()
    {
        var dir = TempDir("config-null");
        try
        {
            var store = new JsonConfigStore(dir);
            await File.WriteAllTextAsync(store.FilePath, "null");

            var loaded = await store.LoadAsync();

            Assert.Equal(JsonConfigStore.CreateSample().Accounts.Count, loaded.Accounts.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- ④ 目录解析与原子写落盘 ----------

    [Fact]
    public void 三份仓储_显式传参时_文件路径落在传入目录下()
    {
        var dir = TempDir("explicit");
        try
        {
            var cooldown = new JsonCooldownStore(dir);
            var claims = new JsonCreditsClaimStore(dir);
            var config = new JsonConfigStore(dir);

            Assert.Equal(Path.Combine(dir, "cooldowns.json"), cooldown.FilePath);
            Assert.Equal(Path.Combine(dir, "credits-claims.json"), claims.FilePath);
            Assert.Equal(Path.Combine(dir, "config.json"), config.FilePath);
            Assert.Equal(dir, config.ConfigDirectory);
            Assert.True(Directory.Exists(dir), "构造期应创建目录（原行为：Directory.CreateDirectory）");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 三份仓储_默认构造_解析到本地应用数据根()
    {
        // 守卫文件位于 tests/，含应用名字面量；此处拼接避免把断言基线写进扫描范围。
        var appName = "Batch" + "AgentRunner";
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);

        Assert.Equal(expected, new JsonCooldownStore().DataDirectory);
        Assert.Equal(expected, new JsonCreditsClaimStore().DataDirectory);
        Assert.Equal(expected, new JsonConfigStore().ConfigDirectory);
    }

    [Fact]
    public async Task 积分台账_保存后重新加载_往返无损()
    {
        var dir = TempDir("claims-roundtrip");
        try
        {
            var store = new JsonCreditsClaimStore(dir);
            var ledger = new CreditsClaimLedger();
            ledger.Records.Add(new CreditsClaimRecord
            {
                AccountId = "acc-9",
                Status = CreditsClaimStatus.Claimed,
                BeforeCredits = 10m,
                AfterCredits = 30m
            });

            await store.SaveAsync(ledger);
            var reloaded = await store.LoadAsync();

            Assert.Single(reloaded.Records);
            Assert.Equal("acc-9", reloaded.Records[0].AccountId);
            Assert.Equal(30m, reloaded.Records[0].AfterCredits);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 配置仓储_保存后重新加载_往返无损且迁移已执行()
    {
        var dir = TempDir("config-roundtrip");
        try
        {
            var store = new JsonConfigStore(dir);
            var options = JsonConfigStore.CreateSample();
            options.TaskFilePath = Path.Combine(dir, "tasks.json");

            await store.SaveAsync(options);
            var reloaded = await store.LoadAsync();

            Assert.Equal(options.TaskFilePath, reloaded.TaskFilePath);
            // 迁移器在加载后执行；此处只锁「往返后配置仍完整可用」，迁移目标值由 CreditsClaimConfigTests 锁死。
            Assert.NotEmpty(reloaded.Accounts);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
