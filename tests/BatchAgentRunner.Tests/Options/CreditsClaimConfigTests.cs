using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Serialization;
using Xunit;

namespace BatchAgentRunner.Tests.Options;

/// <summary>
/// P1-21 不变量：积分领取端点的「生产默认值」必须来自 <see cref="CodeBuddyApiConstants"/> 单一事实来源，
/// 不得在 <see cref="CreditsClaimConfig"/> 中再硬编码魔法串（否则环境切换只能靠覆盖、且易误打到生产）。
/// 本组测试锁死：默认值 == 规范生产端点，且常量形态合规（基址 https、路径以 / 开头）。
/// </summary>
public class CreditsClaimConfigTests
{
    [Fact]
    public void 默认配置_端点默认值等于规范生产常量()
    {
        var config = new CreditsClaimConfig();

        Assert.Equal(CodeBuddyApiConstants.ProductionApiBase, config.ApiBase);
        Assert.Equal(CodeBuddyApiConstants.BalancePath, config.BalancePath);
        Assert.Equal(CodeBuddyApiConstants.ClaimPath, config.ClaimPath);
    }

    [Fact]
    public void 默认配置序列化后_api_base仍为生产基址()
    {
        // 端到端锁死：落盘默认值（JSON 中 api_base / balance_path / claim_path）即生产端点，
        // 若有人在 CreditsClaimConfig 中重新内联魔法串而非引用常量，此测试会立即暴露分歧。
        var json = JsonDefaults.Serialize(new RunOptions());

        Assert.Contains($"\"api_base\": \"{CodeBuddyApiConstants.ProductionApiBase}\"", json);
        Assert.Contains($"\"balance_path\": \"{CodeBuddyApiConstants.BalancePath}\"", json);
        Assert.Contains($"\"claim_path\": \"{CodeBuddyApiConstants.ClaimPath}\"", json);
    }

    [Fact]
    public void 规范常量_形态合规_基址为https且路径以斜杠开头()
    {
        Assert.StartsWith("https://", CodeBuddyApiConstants.ProductionApiBase, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("/", CodeBuddyApiConstants.BalancePath);
        Assert.StartsWith("/", CodeBuddyApiConstants.ClaimPath);
        Assert.Contains("/billing/meter/", CodeBuddyApiConstants.BalancePath);
        Assert.Contains("/billing/meter/", CodeBuddyApiConstants.ClaimPath);
    }

    [Fact]
    public void 显式覆盖_不回退到生产默认值()
    {
        // 环境切换（staging / 自测）靠显式覆盖生效，覆盖值应原样保留、不被常量回填。
        const string stagingBase = "https://staging.example.test";
        var config = new CreditsClaimConfig { ApiBase = stagingBase };

        Assert.Equal(stagingBase, config.ApiBase);
        Assert.NotEqual(CodeBuddyApiConstants.ProductionApiBase, config.ApiBase);
    }
}
