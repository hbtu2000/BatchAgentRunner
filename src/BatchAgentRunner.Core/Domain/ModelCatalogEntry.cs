namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 模型目录项：来自官方产品配置缓存的「模型 + 倍率」（对齐官方模型选择器右侧的 0.29x/1.62x 折扣列）。
/// <see cref="CreditsMultiplier"/> 为 null 表示该模型在官方目录里没有倍率数据。
/// </summary>
public sealed record ModelCatalogEntry(string Name, decimal? CreditsMultiplier);
