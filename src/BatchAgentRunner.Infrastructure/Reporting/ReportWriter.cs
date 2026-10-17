using System.Globalization;
using System.Text;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.TaskFiles;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Infrastructure.Reporting;

/// <summary>
/// 汇总报告写出（REQ-011 + 二期 §10.3 增强）：保存为 JSON + Markdown，与任务文件同目录，可在界面查看。
/// 切换原因四段区分（额度耗尽 / 临时限流 / 时段外跳过 / 冷却跳过），
/// 并统计时段外与冷却的累计等待时长。
/// 二期章节（工作流阶段时间线 / DAG 概览 / 门禁统计 / 变更概览 / 待人工介入清单）
/// 仅在对应数据存在时渲染，一期报告输出保持不变。
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
        if (r.NeedsAttention > 0 || r.Blocked > 0)
        {
            // 二期：待人工/阻断不计入失败率，单独成行
            sb.AppendLine($"| 待人工接管 | {r.NeedsAttention} |");
            sb.AppendLine($"| 依赖阻断 | {r.Blocked} |");
        }

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

        AppendWorkflowTimeline(sb, r);
        AppendDagOverview(sb, r);
        AppendGateStats(sb, r);
        AppendDiffOverview(sb, r);
        AppendAttentionList(sb, r);

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

    // ---------------- 二期章节（§10.3） ----------------

    /// <summary>工作流阶段时间线：每任务各阶段的起止、门禁结果、循环次数。</summary>
    private static void AppendWorkflowTimeline(StringBuilder sb, BatchReport r)
    {
        var workflows = r.Tasks.Where(t => t.StageResults is { Count: > 0 }).ToList();
        if (workflows.Count == 0)
        {
            return;
        }

        sb.AppendLine("## 工作流阶段时间线");
        sb.AppendLine();
        foreach (var t in workflows)
        {
            sb.AppendLine($"### {t.TaskId}（{t.Status}，耗时 {t.DurationSeconds:0.#} 秒）");
            sb.AppendLine();
            sb.AppendLine("| 阶段 | 状态 | 门禁 | 循环次数 | 账号/模型 | 产出摘要 |");
            sb.AppendLine("|------|------|------|----------|-----------|----------|");
            foreach (var s in t.StageResults!)
            {
                var gate = s.GatePassed ? "通过" : s.Status == TaskStatus.Done ? "无门禁" : "未通过";
                var loops = s.LoopCount > 0 ? s.LoopCount.ToString() : "-";
                var who = $"{s.AccountUsed ?? "-"}/{s.ModelUsed ?? "-"}";
                sb.AppendLine($"| {Escape(s.Name ?? s.StageId)} | {s.Status} | {gate} | {loops} | {Escape(who)} | {Escape(Truncate(s.OutputSummary, 120))} |");
            }

            sb.AppendLine();
        }
    }

    /// <summary>DAG 概览：任务依赖关系与阻塞原因（blocked_by）。</summary>
    private static void AppendDagOverview(StringBuilder sb, BatchReport r)
    {
        var withDeps = r.Tasks.Where(t => t.DependsOn is { Count: > 0 }).ToList();
        if (withDeps.Count == 0)
        {
            return;
        }

        sb.AppendLine("## DAG 依赖概览");
        sb.AppendLine();
        sb.AppendLine("| 任务 | 状态 | 依赖 | 阻断来源 |");
        sb.AppendLine("|------|------|------|----------|");
        foreach (var t in withDeps)
        {
            var deps = string.Join("、", t.DependsOn!);
            var blockedBy = t.BlockedBy is { Count: > 0 } ? string.Join("、", t.BlockedBy) : "-";
            sb.AppendLine($"| {Escape(t.TaskId)} | {t.Status} | {Escape(deps)} | {Escape(blockedBy)} |");
        }

        sb.AppendLine();
    }

    /// <summary>门禁统计：通过/未通过/遗留问题数，按场景模板归类。</summary>
    private static void AppendGateStats(StringBuilder sb, BatchReport r)
    {
        var stages = r.Tasks.Where(t => t.StageResults is { Count: > 0 })
            .SelectMany(t => t.StageResults!.Select(s => (Task: t, Stage: s)))
            .Where(x => x.Stage.LoopCount > 0 || !x.Stage.GatePassed)
            .ToList();
        if (stages.Count == 0)
        {
            return;
        }

        var passed = stages.Count(x => x.Stage.GatePassed);
        var failed = stages.Count - passed;
        var withLeftover = stages.Count(x => !string.IsNullOrWhiteSpace(x.Stage.GateLeftover));
        var totalLoops = stages.Sum(x => x.Stage.LoopCount);

        sb.AppendLine("## 门禁统计");
        sb.AppendLine();
        sb.AppendLine($"- 门禁阶段总数：{stages.Count}");
        sb.AppendLine($"- 通过：{passed}");
        sb.AppendLine($"- 未通过：{failed}");
        sb.AppendLine($"- 遗留问题阶段数：{withLeftover}");
        sb.AppendLine($"- 修订循环总次数：{totalLoops}");
        sb.AppendLine();

        var byTemplate = stages
            .Where(x => !string.IsNullOrWhiteSpace(x.Task.TemplateId))
            .GroupBy(x => x.Task.TemplateId!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        if (byTemplate.Any())
        {
            sb.AppendLine("| 场景模板 | 门禁阶段 | 通过 | 未通过 |");
            sb.AppendLine("|-----------|----------|------|--------|");
            foreach (var g in byTemplate)
            {
                var gp = g.Count(x => x.Stage.GatePassed);
                sb.AppendLine($"| {Escape(g.Key)} | {g.Count()} | {gp} | {g.Count() - gp} |");
            }

            sb.AppendLine();
        }

        var leftovers = stages.Where(x => !string.IsNullOrWhiteSpace(x.Stage.GateLeftover)).ToList();
        if (leftovers.Count > 0)
        {
            sb.AppendLine("### 遗留问题明细");
            sb.AppendLine();
            foreach (var x in leftovers)
            {
                sb.AppendLine($"- **{Escape(x.Task.TaskId)} / {Escape(x.Stage.Name ?? x.Stage.StageId)}**：{Escape(Truncate(x.Stage.GateLeftover, 300))}");
            }

            sb.AppendLine();
        }
    }

    /// <summary>变更概览（diff）：改动文件数、按阶段分布（引用 DiffView）。</summary>
    private static void AppendDiffOverview(StringBuilder sb, BatchReport r)
    {
        if (r.Diffs.Count == 0)
        {
            return;
        }

        var added = r.Diffs.Count(d => d.Kind == DiffKind.Added);
        var modified = r.Diffs.Count(d => d.Kind == DiffKind.Modified);
        var deleted = r.Diffs.Count(d => d.Kind == DiffKind.Deleted);

        sb.AppendLine("## 变更概览（diff）");
        sb.AppendLine();
        sb.AppendLine($"- 改动文件总数：{r.Diffs.Count}（新增 {added} / 修改 {modified} / 删除 {deleted}）");
        sb.AppendLine();

        var byStage = r.Diffs
            .GroupBy(d => string.IsNullOrEmpty(d.StageId) ? "（任务级）" : d.StageId!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        if (byStage.Any())
        {
            sb.AppendLine("| 阶段 | 改动文件数 | 文件 |");
            sb.AppendLine("|------|------------|------|");
            foreach (var g in byStage)
            {
                var files = string.Join("、", g.Select(d => Path.GetFileName(d.FilePath)).Distinct());
                sb.AppendLine($"| {Escape(g.Key)} | {g.Count()} | {Escape(Truncate(files, 200))} |");
            }

            sb.AppendLine();
        }
    }

    /// <summary>待人工介入清单：NeedsAttention / Blocked 任务及遗留问题（不计入「真失败率」）。</summary>
    private static void AppendAttentionList(StringBuilder sb, BatchReport r)
    {
        var attention = r.Tasks
            .Where(t => t.Status is TaskStatus.NeedsAttention or TaskStatus.Blocked)
            .ToList();
        if (attention.Count == 0)
        {
            return;
        }

        sb.AppendLine("## 待人工介入清单");
        sb.AppendLine();
        sb.AppendLine("> 以下任务不计入失败率：NeedsAttention 需人工接管，Blocked 因依赖失败/悬空被阻断。");
        sb.AppendLine();
        sb.AppendLine("| 任务 | 状态 | 原因 / 遗留问题 |");
        sb.AppendLine("|------|------|-----------------|");
        foreach (var t in attention)
        {
            var reason = t.BlockedBy is { Count: > 0 }
                ? $"被依赖阻断：{string.Join("、", t.BlockedBy)}"
                : t.Leftover ?? "-";
            sb.AppendLine($"| {Escape(t.TaskId)} | {t.Status} | {Escape(Truncate(reason, 300))} |");
        }

        sb.AppendLine();
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";
}
