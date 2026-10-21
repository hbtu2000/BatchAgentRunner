using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>每日积分领取结果行（界面上逐账号展示「领取前 → 领取后」）。</summary>
public sealed class CreditsClaimRow
{
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

    public string AccountName { get; }
    public string Status { get; }
    public string Before { get; }
    public string After { get; }
    public string Delta { get; }
    public string Detail { get; }
    public bool IsFailed { get; }
    public bool IsClaimed { get; }
    public bool IsNeutral { get; }

    private static string Format(decimal? value) => value.HasValue ? value.Value.ToString("0.##") : "未知";
}
