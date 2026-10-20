using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Infrastructure.Persistence;

/// <summary>
/// 积分领取配置迁移器：v1 假设端点 → 2026-10-16 实测端点。
/// 背景：端点未实测前程序曾把假设值（/console/credits 等）作为默认值序列化进用户 config.json；
/// 端点实测确认后，仅改代码默认值对【已有配置文件】不生效（文件值覆盖默认值），
/// 故加载时识别旧默认值并迁移。用户自定义的非旧默认值一律不动。
/// </summary>
public static class CreditsClaimMigrator
{
    private const string LegacyBalancePath = "/console/credits";
    private const string LegacyClaimPath = "/console/credits/daily-claim";

    /// <summary>识别并迁移旧默认端点（幂等；用户自定义值不迁移）。返回是否发生了迁移。</summary>
    public static bool Migrate(RunOptions options)
    {
        var migrated = false;
        var claim = options.CreditsClaim;

        if (string.Equals(claim.BalancePath?.Trim(), LegacyBalancePath, StringComparison.OrdinalIgnoreCase))
        {
            claim.BalancePath = CodeBuddyApiConstants.BalancePath;
            migrated = true;
        }

        if (string.Equals(claim.ClaimPath?.Trim(), LegacyClaimPath, StringComparison.OrdinalIgnoreCase))
        {
            claim.ClaimPath = CodeBuddyApiConstants.ClaimPath;
            migrated = true;
        }

        return migrated;
    }
}
