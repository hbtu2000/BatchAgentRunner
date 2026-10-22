using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.Reporting;
using Xunit;

namespace BatchAgentRunner.Tests.Reporting;

/// <summary>
/// 二期报告章节测试（设计文档 §10.3）：工作流阶段时间线 / DAG 概览 / 门禁统计 /
/// 变更概览（diff）/ 待人工介入清单。一期章节输出不受影响。
/// </summary>
public class ReportWriterPhase2Tests
{
    private static BatchReport BaseReport() => new()
    {
        GeneratedAt = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero),
        BatchStatus = BatchStatus.Completed,
        TaskFilePath = "tasks.json",
        Total = 3,
        Done = 2,
        Failed = 1,
        NeedsAttention = 1,
        Blocked = 1
    };

    /// <summary>纯一期形状：无二期计数、无任务明细、无 diff。</summary>
    private static BatchReport Phase1Report() => new()
    {
        GeneratedAt = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero),
        BatchStatus = BatchStatus.Completed,
        TaskFilePath = "tasks.json",
        Total = 2,
        Done = 2
    };

    [Fact]
    public void 一期报告_无二期数据时不渲染二期章节()
    {
        var report = Phase1Report();

        var md = ReportWriter.ToMarkdown(report);

        Assert.Contains("## 成败统计", md);
        Assert.DoesNotContain("## 工作流阶段时间线", md);
        Assert.DoesNotContain("## DAG 依赖概览", md);
        Assert.DoesNotContain("## 门禁统计", md);
        Assert.DoesNotContain("## 变更概览（diff）", md);
        Assert.DoesNotContain("## 待人工介入清单", md);
        // 一期不因为二期统计行而改变输出
        Assert.DoesNotContain("| 待人工接管 |", md);
    }

    [Fact]
    public void 工作流任务_渲染阶段时间线含门禁与循环次数()
    {
        var report = BaseReport();
        report.Tasks.Add(new TaskReportEntry
        {
            TaskId = "wf-1",
            Status = TaskStatus.Done,
            DurationSeconds = 12.5,
            ModelUsed = "m1",
            AccountUsed = "账号 1",
            StageResults = new List<StageResult>
            {
                new() { StageId = "s1", Name = "设计", Status = TaskStatus.Done, GatePassed = true, LoopCount = 2, OutputSummary = "设计产出" },
                new() { StageId = "s2", Name = "实现", Status = TaskStatus.Done, GatePassed = false, LoopCount = 0, OutputSummary = "实现产出" }
            }
        });

        var md = ReportWriter.ToMarkdown(report);

        Assert.Contains("## 工作流阶段时间线", md);
        Assert.Contains("wf-1", md);
        Assert.Contains("| 设计 | Done | 通过 | 2 |", md);
        Assert.Contains("| 实现 | Done | 无门禁 | - |", md);
    }

    [Fact]
    public void 依赖任务_渲染DAG概览含阻断来源()
    {
        var report = BaseReport();
        report.Tasks.Add(new TaskReportEntry { TaskId = "a", Status = TaskStatus.Failed });
        report.Tasks.Add(new TaskReportEntry
        {
            TaskId = "b",
            Status = TaskStatus.Blocked,
            DependsOn = new List<string> { "a" },
            BlockedBy = new List<string> { "a" }
        });
        report.Tasks.Add(new TaskReportEntry
        {
            TaskId = "c",
            Status = TaskStatus.Blocked,
            DependsOn = new List<string> { "ghost" },
            BlockedBy = new List<string> { "ghost（不存在）" }
        });

        var md = ReportWriter.ToMarkdown(report);

        Assert.Contains("## DAG 依赖概览", md);
        Assert.Contains("| b | Blocked | a | a |", md);
        Assert.Contains("ghost（不存在）", md);
    }

    [Fact]
    public void 门禁数据_渲染统计与遗留问题明细()
    {
        var report = BaseReport();
        report.Tasks.Add(new TaskReportEntry
        {
            TaskId = "wf-1",
            Status = TaskStatus.NeedsAttention,
            TemplateId = "S1",
            Leftover = "需求文档缺少验收标准",
            StageResults = new List<StageResult>
            {
                new() { StageId = "review", Name = "审查", Status = TaskStatus.NeedsAttention, GatePassed = false, LoopCount = 3, GateLeftover = "范围不清晰" }
            }
        });

        var md = ReportWriter.ToMarkdown(report);

        Assert.Contains("## 门禁统计", md);
        Assert.Contains("- 未通过：1", md);
        Assert.Contains("| S1 | 1 | 0 | 1 |", md);
        Assert.Contains("### 遗留问题明细", md);
        Assert.Contains("范围不清晰", md);
    }

    [Fact]
    public void Diff记录_渲染变更概览按阶段分布()
    {
        var report = BaseReport();
        report.Diffs.Add(new DiffRecord { TaskId = "t-1", StageId = "fix", FilePath = "src/A.cs", Kind = DiffKind.Modified });
        report.Diffs.Add(new DiffRecord { TaskId = "t-1", StageId = "fix", FilePath = "src/B.cs", Kind = DiffKind.Added });
        report.Diffs.Add(new DiffRecord { TaskId = "t-1", StageId = null, FilePath = "src/C.cs", Kind = DiffKind.Deleted });

        var md = ReportWriter.ToMarkdown(report);

        Assert.Contains("## 变更概览（diff）", md);
        Assert.Contains("改动文件总数：3（新增 1 / 修改 1 / 删除 1）", md);
        Assert.Contains("| fix | 2 |", md);
        Assert.Contains("（任务级）", md);
    }

    [Fact]
    public void 待人工与阻断任务_渲染介入清单且注明不计失败率()
    {
        var report = BaseReport();
        report.Tasks.Add(new TaskReportEntry
        {
            TaskId = "wf-2",
            Status = TaskStatus.NeedsAttention,
            Leftover = "门禁修订循环达上限"
        });
        report.Tasks.Add(new TaskReportEntry
        {
            TaskId = "t-3",
            Status = TaskStatus.Blocked,
            DependsOn = new List<string> { "wf-2" },
            BlockedBy = new List<string> { "wf-2" }
        });

        var md = ReportWriter.ToMarkdown(report);

        Assert.Contains("## 待人工介入清单", md);
        Assert.Contains("不计入失败率", md);
        Assert.Contains("门禁修订循环达上限", md);
        Assert.Contains("被依赖阻断：wf-2", md);
        // 成败统计补充二期两行
        Assert.Contains("| 待人工接管 | 1 |", md);
        Assert.Contains("| 依赖阻断 | 1 |", md);
    }
}
