namespace BatchAgentRunner.Core.Options;

/// <summary>
/// CodeBuddy 控制台积分 API 的规范化端点常量（实测值，2026-10-16 抓包确认）。
/// <para>
/// 生产基址与 <c>/billing/meter/*</c> 路径集中于此，<see cref="CreditsClaimConfig"/> 的默认值
/// 与 <see cref="BatchAgentRunner.Infrastructure.Persistence.CreditsClaimMigrator"/> 的迁移目标
/// 均引用本类，杜绝在多处硬编码同一组魔法串（P1-21）。
/// </para>
/// <para>
/// 切换环境（staging / 自测）请显式覆盖 <see cref="CreditsClaimConfig.ApiBase"/> 等字段，
/// <b>不应</b>改动本类中的生产值——本类即「生产端点」的唯一事实来源，改动它等于改变全仓默认行为。
/// </para>
/// </summary>
public static class CodeBuddyApiConstants
{
    /// <summary>生产环境 API 基址（实测值）。</summary>
    public const string ProductionApiBase = "https://www.codebuddy.cn";

    /// <summary>余额查询路径（POST；实测值）。</summary>
    public const string BalancePath = "/billing/meter/get-user-resource-summary";

    /// <summary>每日领取路径（POST；实测值）。</summary>
    public const string ClaimPath = "/billing/meter/daily-checkin";
}
