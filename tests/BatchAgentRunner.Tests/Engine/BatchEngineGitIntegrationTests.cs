using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.Logging;
using Xunit;

namespace BatchAgentRunner.Tests.Engine;

/// <summary>
/// 二期项目级任务 Git 集成测试：全局 Git 配置作为任务级 ProjectRef 的兜底合并
/// （任务优先、全局填空、任务无 ProjectRef 时按全局合成），auto_open_pr 闸门。
/// </summary>
public class BatchEngineGitIntegrationTests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 17, 12, 0);

    /// <summary>记录调用的假 Git 集成。</summary>
    private sealed class RecordingGit : IGitIntegration
    {
        public List<(ProjectRef Ref, string TaskId)> Created { get; } = new();
        public List<(ProjectRef Ref, string TaskId, string Branch)> OpenedPrs { get; } = new();

        public Task<string?> CreateBranchAsync(ProjectRef projectRef, string taskId, CancellationToken cancellationToken = default)
        {
            Created.Add((projectRef, taskId));
            return Task.FromResult<string?>(projectRef.TaskBranch ?? "fallback-branch");
        }

        public Task<string?> OpenPullRequestAsync(ProjectRef projectRef, string taskId, string branch, string? title = null,
            CancellationToken cancellationToken = default)
        {
            OpenedPrs.Add((projectRef, taskId, branch));
            return Task.FromResult<string?>("https://example.invalid/pr/1");
        }
    }

    private static RunOptions Options() => new()
    {
        TaskFilePath = Path.Combine(Path.GetTempPath(), "bar-git-tasks.json"),
        AgentKind = AgentKind.Mock,
        Accounts = new List<AccountConfig> { new() { Id = "acc1", DisplayName = "账号 1" } },
        ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" } },
        Concurrency = 1,
        MaxRetriesPerTask = 0,
        TaskTimeoutMinutes = 1
    };

    private static (BatchEngine Engine, RecordingGit Git) Build(RunOptions options, List<TaskItem> tasks)
    {
        var clock = new FakeClock(Noon);
        var git = new RecordingGit();
        var engine = new BatchEngine(options, new MockAdapter(stepDuration: TimeSpan.Zero, clock: clock),
            new InMemoryTaskFileStore { Tasks = tasks }, new InMemoryCooldownStore(), new NullRunLogger(),
            new BatchEngineDependencies { Clock = clock, Git = git });
        engine.InitializeAsync().Wait();
        return (engine, git);
    }

    [Fact]
    public async Task 全局配置仓库_任务无ProjectRef_按全局合成并建分支()
    {
        var options = Options();
        options.Git = new GitConfig
        {
            RepoPath = "D:/repos/demo",
            BaseBranch = "main",
            BranchNaming = "bar/{task_id}-{date}"
        };
        var (engine, git) = Build(options, new List<TaskItem> { new() { Id = "t-1", Instruction = "做事" } });

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        var call = Assert.Single(git.Created);
        Assert.Equal("t-1", call.TaskId);
        Assert.Equal("D:/repos/demo", call.Ref.RepoPath);
        Assert.Equal("main", call.Ref.BaseBranch);
        Assert.StartsWith("bar/t-1-", call.Ref.TaskBranch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 任务ProjectRef优先_空字段由全局兜底()
    {
        var options = Options();
        options.Git = new GitConfig
        {
            RepoPath = "D:/repos/global",
            BaseBranch = "develop",
            BranchNaming = "bar/{task_id}"
        };
        var (engine, git) = Build(options, new List<TaskItem>
        {
            new()
            {
                Id = "t-2",
                Instruction = "做事",
                ProjectRef = new ProjectRef { TaskBranch = "feature/custom" }
            }
        });

        await engine.StartAsync();

        var call = Assert.Single(git.Created);
        // 任务填了分支就用任务的；仓库/基线用全局兜底
        Assert.Equal("feature/custom", call.Ref.TaskBranch);
        Assert.Equal("D:/repos/global", call.Ref.RepoPath);
        Assert.Equal("develop", call.Ref.BaseBranch);
    }

    [Fact]
    public async Task 两级都未配置仓库_完全不触发Git操作()
    {
        var options = Options();
        var (engine, git) = Build(options, new List<TaskItem> { new() { Id = "t-3", Instruction = "做事" } });

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Empty(git.Created);
        Assert.Empty(git.OpenedPrs);
    }

    [Fact]
    public async Task 自动开PR关闭_成功后不开PR()
    {
        var options = Options();
        options.Git = new GitConfig { RepoPath = "D:/repos/demo", AutoOpenPr = false };
        var (engine, git) = Build(options, new List<TaskItem> { new() { Id = "t-4", Instruction = "做事" } });

        await engine.StartAsync();

        Assert.Single(git.Created);
        Assert.Empty(git.OpenedPrs);
    }

    [Fact]
    public async Task 自动开PR开启_成功后按生效分支开PR()
    {
        var options = Options();
        options.Git = new GitConfig
        {
            RepoPath = "D:/repos/demo",
            AutoOpenPr = true,
            BranchNaming = "bar/{task_id}"
        };
        var (engine, git) = Build(options, new List<TaskItem> { new() { Id = "t-5", Instruction = "做事" } });

        await engine.StartAsync();

        var pr = Assert.Single(git.OpenedPrs);
        Assert.Equal("t-5", pr.TaskId);
        Assert.Equal("bar/t-5", pr.Branch);
    }
}
