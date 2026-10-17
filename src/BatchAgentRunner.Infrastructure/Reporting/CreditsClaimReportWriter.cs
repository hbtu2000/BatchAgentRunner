using System.Globalization;
using System.Text;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Infrastructure.Reporting;

/// <summary>
/// 每日积分领取报告写出（JSON + Markdown，与批处理报告同风格）：
/// 逐账号列出「领取前积分 → 领取后积分（变化量）」与状态说明，保存目录由调用方指定
/// （有任务文件时与其同目录，否则配置目录）。
/// </summary>
public static class CreditsClaimReportWriter
{
    public static async Task<string> WriteAsync(CreditsClaimSummary summary, string directory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var periodLabel = "daily";
        var stamp = summary.GeneratedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(directory, $"credits-claim-{periodLabel}-{stamp}.json");
        var mdPath = Path.Combine(directory, $"credits-claim-{periodLabel}-{stamp}.md");

        await AtomicFileWriter.WriteAsync(jsonPath, JsonDefaults.Serialize(summary), cancellationToken)
            .ConfigureAwait(false);
        await AtomicFileWriter.WriteAsync(mdPath, ToMarkdown(summary), cancellationToken).ConfigureAwait(false);

        return mdPath;
    }

    public static string ToMarkdown(CreditsClaimSummary s)
    {
        var title = "每日积分领取报告";
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间：{s.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 执行工具：{s.Cli}");
        sb.AppendLine($"- 结果：成功 {s.ClaimedCount} / 已领取过 {s.AlreadyClaimedCount} / " +
                      $"失败 {s.FailedCount} / 跳过 {s.SkippedCount}");
        sb.AppendLine();
        sb.AppendLine("## 逐账号明细");
        sb.AppendLine();
        sb.AppendLine("| 账号 | 状态 | 领取前 | 领取后 | 变化 | 说明 |");
        sb.AppendLine("|------|------|--------|--------|------|------|");
        foreach (var r in s.Records)
        {
            sb.AppendLine(
                $"| {Escape(r.DisplayName ?? r.AccountId)} | {StatusText(r.Status)} | " +
                $"{Format(r.BeforeCredits)} | {Format(r.AfterCredits)} | {Delta(r)} | {Escape(r.Message ?? r.Error)} |");
        }

        return sb.ToString();
    }

    private static string StatusText(CreditsClaimStatus status) => status switch
    {
        CreditsClaimStatus.Claimed => "领取成功",
        CreditsClaimStatus.AlreadyClaimed => "已领取过",
        CreditsClaimStatus.Failed => "失败",
        CreditsClaimStatus.Skipped => "跳过",
        _ => status.ToString()
    };

    private static string Format(decimal? value) => value.HasValue ? value.Value.ToString("0.##") : "-";

    private static string Delta(CreditsClaimRecord r)
    {
        if (!r.DeltaCredits.HasValue)
        {
            return "-";
        }

        return (r.DeltaCredits.Value >= 0 ? "+" : "") + r.DeltaCredits.Value.ToString("0.##");
    }

    private static string Escape(string? text) =>
        string.IsNullOrEmpty(text) ? "-" : text.Replace("|", "\\|").Replace("\n", " ");
}
