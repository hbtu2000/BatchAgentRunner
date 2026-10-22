using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>每日积分领取结果行（界面上逐账号展示「领取前 → 领取后」）。</summary>
public sealed class CreditsClaimRow
{
    /// <summary>由一条领取记录初始化，生成文案与状态标记。</summary>
    public CreditsClaimRow(CreditsClaimRecord record)
    {
        AccountName = string.IsNullOrWhiteSpace(record.DisplayName) ? record.AccountId : record.DisplayName;
        Status = record.Status switch
        {
            CreditsClaimStatus.Claimed => "领取成功",
            CreditsClaimStatus.AlreadyClaimed => "今日已领取过",
            CreditsClaimStatus.Failed => "失败",
            CreditsClaimStatus.Skipped => "跳过",
            _ => record.Status.ToString()
        };
        Before = Format(record.BeforeCredits);
        After = Format(record.AfterCredits);
        Delta = record.DeltaCredits.HasValue
            ? (record.DeltaCredits.Value >= 0 ? "+" : "") + record.DeltaCredits.Value.ToString("0.##")
            : "-";
        Detail = record.Message ?? record.Error ?? string.Empty;
        IsFailed = record.Status == CreditsClaimStatus.Failed;
        IsClaimed = record.Status == CreditsClaimStatus.Claimed;
        IsNeutral = !IsFailed && !IsClaimed;
    }

    /// <summary>账号显示名；空白时退回账号 Id。</summary>
    public string AccountName { get; }
    /// <summary>领取结果的可读文案（成功/已领取/失败/跳过）。</summary>
    public string Status { get; }
    /// <summary>领取前的积分余额文案，未知时显示「未知」。</summary>
    public string Before { get; }
    /// <summary>领取后的积分余额文案，未知时显示「未知」。</summary>
    public string After { get; }
    /// <summary>积分变化量文案，带正负号；无数据时为「-」。</summary>
    public string Delta { get; }
    /// <summary>领取说明或错误详情，无内容时为空串。</summary>
    public string Detail { get; }
    /// <summary>本次领取是否失败（供行样式绑定）。</summary>
    public bool IsFailed { get; }
    /// <summary>本次是否成功领取到积分（供行样式绑定）。</summary>
    public bool IsClaimed { get; }
    /// <summary>是否既非失败也非成功领取（已领取/跳过）。</summary>
    public bool IsNeutral { get; }

    private static string Format(decimal? value) => value.HasValue ? value.Value.ToString("0.##") : "未知";
}
