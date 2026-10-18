using System.Collections.Generic;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P1-19：报告页 ViewModel 白箱单测（churn 热点之一）。把汇总文本 / 待人工清单 / 二期概览
/// 的计算契约锁在 <see cref="FakeEngineHost.LastReport"/> 注入的 BatchReport 上。
/// </summary>
public class ReportViewModelTests
{
    [Fact]
    public void 无报告时_Summary与路径为占位()
    {
        var fake = new FakeEngineHost { LastReport = null };
        var vm = new ReportViewModel(fake);

        Assert.False(vm.HasReport);
        Assert.Equal("尚无报告：请先运行一次批处理。", vm.SummaryText);
        Assert.Equal("（尚未生成）", vm.ReportPath);
    }

    [Fact]
    public void SummaryText_聚合各状态计数()
    {
        var report = new BatchReport
        {
            Total = 10,
            Done = 7,
            Failed = 1,
            Interrupted = 1,
            Pending = 1,
            SkippedAlreadyDone = 0,
            NeedsAttention = 2,
            Blocked = 3,
        };
        var fake = new FakeEngineHost { LastReport = report };
        var vm = new ReportViewModel(fake);

        Assert.True(vm.HasReport);
        Assert.Contains("总数 10", vm.SummaryText);
        Assert.Contains("成功 7", vm.SummaryText);
        Assert.Contains("失败 1", vm.SummaryText);
        Assert.Contains("中断 1", vm.SummaryText);
        Assert.Contains("未处理 1", vm.SummaryText);
        Assert.Contains("续跑跳过 0", vm.SummaryText);
        Assert.Contains("待人工 2", vm.SummaryText);
        Assert.Contains("依赖阻塞 3", vm.SummaryText);
    }

    [Fact]
    public void AttentionTasks_收集待人工与依赖阻塞()
    {
        var report = new BatchReport
        {
            Tasks = new List<TaskReportEntry>
            {
                new() { TaskId = "t1", Status = BatchAgentRunner.Core.Domain.TaskStatus.NeedsAttention },
                new() { TaskId = "t2", Status = BatchAgentRunner.Core.Domain.TaskStatus.Blocked },
                new() { TaskId = "t3", Status = BatchAgentRunner.Core.Domain.TaskStatus.Done },
            },
        };
        var fake = new FakeEngineHost { LastReport = report };
        var vm = new ReportViewModel(fake);

        Assert.Equal(2, vm.AttentionTasks.Count);
        Assert.True(vm.HasAttentionTasks);
    }

    [Fact]
    public void Phase2Text_含Diff变更时非空()
    {
        var report = new BatchReport
        {
            Diffs = new List<DiffRecord> { new() { TaskId = "t1", FilePath = "a.txt", Kind = DiffKind.Added } },
        };
        var fake = new FakeEngineHost { LastReport = report };
        var vm = new ReportViewModel(fake);

        Assert.Contains("diff 变更", vm.Phase2Text);
        Assert.True(vm.HasPhase2);
    }
}
