using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.Persistence;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// P1-18 不变量：<see cref="JsonCreditsClaimStore"/> 默认构造（不传 directory）必须可用。
/// 原实现建目录用 `directory ?? ...`、取路径用 `Path.Combine(directory!, ...)`，
/// directory 为 null 时后者必抛 <see cref="ArgumentNullException"/>——生产唯一调用点恰好总传参，
/// 故未暴露；本断言锁住「默认构造 = 定时炸弹已拆除」，任何走默认构造的新调用点不会再立刻崩。
/// 注意：默认路径落在真实 LocalAppData，本测试只构造 + 只读加载，绝不写入（不污染用户数据）；
/// 写路径往返一律走临时目录。
/// </summary>
public class JsonCreditsClaimStoreTests
{
    [Fact]
    public async Task 默认构造_不抛异常_路径解析到LocalAppData且可只读加载()
    {
        var store = new JsonCreditsClaimStore();

        // 默认目录必须解析到 LocalAppData/BatchAgentRunner（而非 null 拼接后崩在 Path.Combine）
        Assert.EndsWith("credits-claims.json", store.FilePath);
        Assert.Contains("BatchAgentRunner", store.FilePath);

        var ledger = await store.LoadAsync();
        Assert.NotNull(ledger);
        Assert.NotNull(ledger.Records);
    }

    [Fact]
    public async Task 临时目录_保存后可再加载_往返无损()
    {
        var dir = Directory.CreateTempSubdirectory("bar-claims-").FullName;
        try
        {
            var store = new JsonCreditsClaimStore(dir);
            var ledger = new CreditsClaimLedger();
            ledger.Records.Add(new CreditsClaimRecord
            {
                AccountId = "acc-1",
                Status = CreditsClaimStatus.Claimed,
                BeforeCredits = 100m,
                AfterCredits = 120m
            });

            await store.SaveAsync(ledger);

            Assert.Equal(Path.Combine(dir, "credits-claims.json"), store.FilePath);
            Assert.True(File.Exists(store.FilePath));

            var reloaded = await store.LoadAsync();
            Assert.Single(reloaded.Records);
            Assert.Equal("acc-1", reloaded.Records[0].AccountId);
            Assert.Equal(CreditsClaimStatus.Claimed, reloaded.Records[0].Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
