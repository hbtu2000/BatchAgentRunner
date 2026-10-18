using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.Logging;
using Xunit;

namespace BatchAgentRunner.Tests.Engine;

/// <summary>
/// Git 工作流协作者单测（P1-1 拆分产物 ④）：锁定「尽力而为不阻断」「项目配置解析」
/// 「分支名一次解析前后一致」「Markdown 回写开关门」四条契约。
/// </summary>
[Trait("Category", "Invariant")]
public class GitWorkflowTests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    private static RunOptions OptionsWithGit() => new()
    {
        TaskFilePath = "tasks.json",
        AgentKind = AgentKind.CodeBuddy,
        CollectDiffs = true,
        Git = new GitConfig
        {
            RepoPath = "C:/repo",
            BaseBranch = "main",
            BranchNaming = "bar/{task_id}",
            AutoOpenPr = true
        }
    };

    private static (GitWorkflow Workflow, FakeGitIntegration Git, FakeDiffCollector Diffs,
        FakeMarkdownWriter Markdown) Build(RunOptions options)
    {
        var git = new FakeGitIntegration();
        var diffs = new FakeDiffCollector();
        var markdown = new FakeMarkdownWriter();
        var workflow = new GitWorkflow(options, diffs, git, markdown, new NullRunLogger());
        return (workflow, git, diffs, markdown);
    }

    [Fact]
    public async Task 未配置Git_Begin不开分支且Complete不开PR()
    {
        var options = new RunOptions { TaskFilePath = "tasks.json" };
        var (workflow, git, _, _) = Build(options);

        var ctx = await workflow.BeginAsync(new TaskItem { Id = "t1" }, default);
        await workflow.CompleteAsync(new TaskItem { Id = "t1", Status = TaskStatus.Done }, ctx, default);

        Assert.Null(ctx.Project);
        Assert.Equal(0, git.CreateBranchCalls);
        Assert.Equal(0, git.OpenPrCalls);
    }

    [Fact]
    public async Task 项目级任务_建分支与开PR均发生且分支名前后一致()
    {
        var (workflow, git, _, _) = Build(OptionsWithGit());

        var task = new TaskItem { Id = "t1", Status = TaskStatus.Done };
        var ctx = await workflow.BeginAsync(task, default);
        await workflow.CompleteAsync(task, ctx, default);

        Assert.NotNull(ctx.Project);
        Assert.Equal("bar/t1", ctx.Project!.TaskBranch);
        Assert.Equal(1, git.CreateBranchCalls);
        Assert.Equal(1, git.OpenPrCalls);
        Assert.Equal("bar/t1", git.LastPrBranch); // 开 PR 用的分支 = 建分支时的名字
    }

    [Fact]
    public async Task 任务未完成_不开PR()
    {
        var (workflow, git, _, _) = Build(OptionsWithGit());

        var task = new TaskItem { Id = "t1", Status = TaskStatus.Failed };
        var ctx = await workflow.BeginAsync(task, default);
        await workflow.CompleteAsync(task, ctx, default);

        Assert.Equal(1, git.CreateBranchCalls); // 执行前仍建分支
        Assert.Equal(0, git.OpenPrCalls); // 未成功不开 PR
    }

    [Fact]
    public async Task 分支名模板含时间戳_前后解析仍稳定()
    {
        var options = OptionsWithGit();
        options.Git.BranchNaming = "bar/{task_id}-{timestamp}";
        var (workflow, git, _, _) = Build(options);

        var task = new TaskItem { Id = "t1", Status = TaskStatus.Done };
        var ctx = await workflow.BeginAsync(task, default);
        var branch = ctx.Project!.TaskBranch;
        await workflow.CompleteAsync(task, ctx, default);

        // 关键：不能二次解析出新时间戳分支名
        Assert.Equal(branch, git.LastPrBranch);
        Assert.StartsWith("bar/t1-", branch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 任务级ProjectRef_空字段用全局兜底_显式字段优先()
    {
        var options = OptionsWithGit();
        var (workflow, git, _, _) = Build(options);
        var task = new TaskItem
        {
            Id = "t1",
            Status = TaskStatus.Done,
            ProjectRef = new ProjectRef { RepoPath = "C:/other", AutoCreateBranch = true }
        };

        var ctx = await workflow.BeginAsync(task, default);

        Assert.Equal("C:/other", ctx.Project!.RepoPath); // 任务级显式仓库优先
        Assert.Equal("main", ctx.Project.BaseBranch); // 空字段回落全局
    }

    [Fact]
    public async Task 采集开启_快照与采集都发生且diff进入批次记录()
    {
        var (workflow, _, diffs, _) = Build(OptionsWithGit());

        var ctx = await workflow.BeginAsync(new TaskItem { Id = "t1" }, default);
        Assert.NotNull(ctx.SnapshotId);
        await workflow.CompleteAsync(new TaskItem { Id = "t1", Status = TaskStatus.Done }, ctx, default);

        Assert.Equal(1, diffs.SnapshotCalls);
        Assert.Equal(1, diffs.CollectCalls);
        var record = Assert.Single(workflow.Diffs);
        Assert.Equal("t1", record.TaskId);
    }

    [Fact]
    public async Task 采集关闭_不快照不采集()
    {
        var options = OptionsWithGit();
        options.CollectDiffs = false;
        var (workflow, _, diffs, _) = Build(options);

        var ctx = await workflow.BeginAsync(new TaskItem { Id = "t1" }, default);
        await workflow.CompleteAsync(new TaskItem { Id = "t1", Status = TaskStatus.Done }, ctx, default);

        Assert.Null(ctx.SnapshotId);
        Assert.Equal(0, diffs.SnapshotCalls);
        Assert.Equal(0, diffs.CollectCalls);
    }

    [Fact]
    public async Task Markdown任务文件且开启回写_才原地回写()
    {
        var options = OptionsWithGit();
        options.TaskFilePath = "C:/batch/tasks.md";
        options.MarkdownInPlaceWriteback = true;
        var (workflow, _, _, markdown) = Build(options);

        var ctx = await workflow.BeginAsync(new TaskItem { Id = "t1" }, default);
        await workflow.CompleteAsync(new TaskItem { Id = "t1", Status = TaskStatus.Done }, ctx, default);

        Assert.Equal(1, markdown.WriteBackCalls);
    }

    [Fact]
    public async Task JSON任务文件_不回写()
    {
        var options = OptionsWithGit();
        options.MarkdownInPlaceWriteback = true;
        var (workflow, _, _, markdown) = Build(options);

        var ctx = await workflow.BeginAsync(new TaskItem { Id = "t1" }, default);
        await workflow.CompleteAsync(new TaskItem { Id = "t1", Status = TaskStatus.Done }, ctx, default);

        Assert.Equal(0, markdown.WriteBackCalls);
    }

    [Fact]
    public async Task Git失败_尽力而为不抛异常()
    {
        var (workflow, git, _, _) = Build(OptionsWithGit());
        git.Throw = true;

        var task = new TaskItem { Id = "t1", Status = TaskStatus.Done };
        var ctx = await workflow.BeginAsync(task, default);
        await workflow.CompleteAsync(task, ctx, default); // 不抛即通过
    }

    private sealed class FakeGitIntegration : IGitIntegration
    {
        public int CreateBranchCalls { get; private set; }

        public int OpenPrCalls { get; private set; }

        public string? LastPrBranch { get; private set; }

        public bool Throw { get; set; }

        public Task<string?> CreateBranchAsync(ProjectRef projectRef, string taskId,
            CancellationToken cancellationToken = default)
        {
            CreateBranchCalls++;
            if (Throw)
            {
                throw new InvalidOperationException("git 故障");
            }

            return Task.FromResult<string?>(projectRef.TaskBranch);
        }

        public Task<string?> OpenPullRequestAsync(ProjectRef projectRef, string taskId, string branch,
            string? title = null, CancellationToken cancellationToken = default)
        {
            OpenPrCalls++;
            LastPrBranch = branch;
            if (Throw)
            {
                throw new InvalidOperationException("git 故障");
            }

            return Task.FromResult<string?>($"https://pr/{branch}");
        }
    }

    private sealed class FakeDiffCollector : IDiffCollector
    {
        public int SnapshotCalls { get; private set; }

        public int CollectCalls { get; private set; }

        public Task<string> SnapshotAsync(TaskItem task, CancellationToken cancellationToken = default)
        {
            SnapshotCalls++;
            return Task.FromResult("snap-" + task.Id);
        }

        public Task<IReadOnlyList<DiffRecord>> CollectAsync(string snapshotId, TaskItem task,
            CancellationToken cancellationToken = default)
        {
            CollectCalls++;
            return Task.FromResult<IReadOnlyList<DiffRecord>>(new[]
            {
                new DiffRecord { TaskId = task.Id, FilePath = "a.txt", Kind = DiffKind.Modified }
            });
        }
    }

    private sealed class FakeMarkdownWriter : IMarkdownInPlaceWriter
    {
        public int WriteBackCalls { get; private set; }

        public Task WriteBackAsync(string path, TaskItem task, CancellationToken cancellationToken = default)
        {
            WriteBackCalls++;
            return Task.CompletedTask;
        }
    }
}
