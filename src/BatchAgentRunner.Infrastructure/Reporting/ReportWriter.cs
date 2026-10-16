using System.Globalization;
using System.Text;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Infrastructure.Reporting;

/// <summary>
/// 汇总报告写出（REQ-011）：保存为 JSON + Markdown，与任务文件同目录，可在界面查看。
/// 切换原因四段区分（额度耗尽 / 临时限流 / 时段外跳过 / 冷却跳过），
/// 并统计时段外与冷却的累计等待时长。
/// </summary>
public sealed class ReportWriter : IReportWriter
{
    public async Task<string> WriteAsync(BatchReport report, string taskFilePath,
        CancellationToken cancellationToken = default)
    {
        var directory = string.IsNullOrWhiteSpace(taskFilePath)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(taskFilePath)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        var stamp = report.GeneratedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(directory, $"report-{stamp}.json");
        var mdPath = Path.Combine(directory, $"report-{stamp}.md");

        await AtomicFileWriter.WriteAsync(jsonPath, JsonDefaults.Serialize(report), cancellationToken)
            .ConfigureAwait(false);
        await AtomicFileWriter.WriteAsync(mdPath, ToMarkdown(report), cancellationToken).ConfigureAwait(false);

        return mdPath;
    }

    public static string ToMarkdown(BatchReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 批处理汇总报告");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间：{r.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 任务文件：{r.TaskFilePath}");
        sb.AppendLine($"- 批级状态：{r.BatchStatus}");
        sb.AppendLine($"- 耗时：{r.DurationSeconds:0.#} 秒");
        sb.AppendLine();

        sb.AppendLine("## 成败统计");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 数量 |");
        sb.AppendLine("|------|------|");
        sb.AppendLine($"| 总数 | {r.Total} |");
        sb.AppendLine($"| 成功 | {r.Done} |");
        sb.AppendLine($"| 失败 | {r.Failed} |");
        sb.AppendLine($"| 中断 | {r.Interrupted} |");
        sb.AppendLine($"| 未处理 | {r.Pending} |");
        sb.AppendLine($"| 续跑跳过已完成 | {r.SkippedAlreadyDone} |");
        sb.AppendLine();

        sb.AppendLine("## 账号 / 模型使用量");
        sb.AppendLine();
        sb.AppendLine("| 类型 | 名称 | 任务数 | 花费 | 口径 |");
        sb.AppendLine("|------|------|--------|------|------|");
        foreach (var u in r.AccountUsage)
        {
            sb.AppendLine($"| 账号 | {u.Name} | {u.TaskCount} | {DescribeCost(u)} | {DescribeBasis(u)} |");
        }

        foreach (var u in r.ModelUsage)
        {
            sb.AppendLine($"| 模型 | {u.Name} | {u.TaskCount} | {DescribeCost(u)} | {DescribeBasis(u)} |");
        }

        sb.AppendLine();

        sb.AppendLine("## 兜底模型");
        sb.AppendLine();
        sb.AppendLine($"- 触发次数：{r.Fallback.UsedCount}");
        sb.AppendLine($"- 兜底模型：{r.Fallback.Model ?? "未配置"}");
        sb.AppendLine($"- 总花费：¥{r.Fallback.TotalCost:0.####}（{(r.Fallback.CostBasis == CostBasis.Measured ? "实测" : "估算")}）");
        sb.AppendLine($"- 是否因成本上限停用：{(r.Fallback.StoppedByLimit ? "是" : "否")}");
        sb.AppendLine();

        sb.AppendLine("## 等待时长");
        sb.AppendLine();
        sb.AppendLine($"- 免费时段外累计等待：{r.WindowWaitSeconds:0.#} 秒");
        sb.AppendLine($"- 额度冷却累计等待：{r.CooldownWaitSeconds:0.#} 秒");
        sb.AppendLine();

        if (r.FailedTasks.Count > 0)
        {
            sb.AppendLine("## 失败任务清单");
            sb.AppendLine();
            sb.AppendLine("| 任务 ID | 重试次数 | 账号 | 模型 | 错误 |");
            sb.AppendLine("|---------|----------|------|------|------|");
            foreach (var f in r.FailedTasks)
            {
                sb.AppendLine($"| {f.TaskId} | {f.RetryCount} | {f.Account ?? "-"} | {f.Model ?? "-"} | {Escape(f.Error)} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## 切换 / 事件时间线");
        sb.AppendLine();
        sb.AppendLine("| 时间 | 类型 | 说明 |");
        sb.AppendLine("|------|------|------|");
        foreach (var t in r.Timeline)
        {
            sb.AppendLine($"| {t.At:HH:mm:ss} | {t.Kind} | {Escape(t.Message)} |");
        }

        if (r.Shutdown.Requested)
        {
            sb.AppendLine();
            sb.AppendLine("## 自动关机");
            sb.AppendLine();
            sb.AppendLine($"- 结果：{(r.Shutdown.Executed ? "已执行关机" : "未执行")}");
            sb.AppendLine($"- 原因：{r.Shutdown.Reason ?? "-"}");
        }

        return sb.ToString();
    }

    private static string DescribeCost(UsageEntry u) => u.Cost is null ? "-" : $"¥{u.Cost:0.####}";

    private static string DescribeBasis(UsageEntry u) =>
        u.CostBasis is null ? "-" : u.CostBasis == CostBasis.Measured ? "实测" : "估算";

    private static string Escape(string? text) =>
        string.IsNullOrEmpty(text) ? "-" : text.Replace("|", "\\|").Replace("\n", " ");
}
