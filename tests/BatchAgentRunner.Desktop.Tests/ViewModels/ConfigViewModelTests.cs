using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Desktop.Models;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P1-19：配置页 ViewModel 白箱单测（阶段 2 文档点名的 churn 热点之一）。
/// 仅依赖 <see cref="FakeEngineHost"/>，把 RunOptions ↔ 属性 的双向映射不变量锁在协作者契约上。
/// P-04 准入补充（2026-10-21）：分解 <see cref="ConfigViewModel"/> 之前，先把跨全部配置域的
/// Reload/Save 往返与模型池迁移行为锁死在 facade 契约上——结构改造时这些断言逐条保持绿
/// 即证明映射行为等价（交互类行为锁——工作空间上限 / 准备度 / 模型拉取 / 取消重载——
/// 见同目录 <see cref="ConfigViewModelInteractionContractTests"/>）。
/// </summary>
public class ConfigViewModelTests
{
    private static RunOptions 全配置域样例() => new()
    {
        TaskFilePath = "tasks.json",
        AgentKind = AgentKind.Mock,
        CliPath = "cli.exe",
        CliArgumentsTemplate = "-p {prompt}",
        DefaultWorkspace = "ws-default",
        ShowCliWindow = true,
        DegradationStrategy = DegradationStrategy.AccountFirst,
        Expert = { Kind = ExpertKind.Single, Id = "exp-1", DisplayName = "专家甲" },
        Fallback = { Enabled = true, MaxCostPerTask = 2.5m, MaxCostPerBatch = 20m },
        AutoShutdown = { Enabled = true, OnlyWhenZeroFailure = true, CountdownSeconds = 90 },
        Concurrency = 3,
        MaxRetriesPerTask = 4,
        RateLimitBackoffRetries = 5,
        TaskTimeoutMinutes = 11,
        MaxAttemptsPerTask = 12,
        MaxTotalMinutesPerTask = 13,
        MaxWaitMinutes = 14,
        DefaultCooldownMinutes = 15,
        WaitForQuotaReset = false,
        MaxWorkspaces = 7,
        ForceRerun = true,
        StopOnFailure = true,
        AutoAnswer =
        {
            AllowedWhitelist = new List<string> { "读取文件", "写入文件" },
            DangerousBlacklist = new List<string> { "rm -rf" },
            DefaultAnswer = "n",
            StdinIdleSeconds = 45,
            DefaultChoiceIndex = 2,
        },
        ResultMaxKB = 200,
        BackupRetention = 9,
        DefaultMaxGateLoops = 4,
        EnableSessionResume = true,
        MarkdownInPlaceWriteback = true,
        DependencyFailurePolicy = DependencyFailurePolicy.AbortBatch,
        TemplateBatchExpansionLimit = 60,
        CollectDiffs = true,
        DiffTargetGlobs = new List<string> { "*.cs", "*.md" },
        Git =
        {
            RepoPath = "D:/repo",
            BaseBranch = "main",
            BranchNaming = "feat/{task_id}",
            PrTargetBranch = "release",
            AutoOpenPr = true,
        },
        Notifications =
        {
            new NotificationRule
            {
                Channel = NotificationChannel.Webhook,
                Triggers = { NotifyTrigger.TaskFailed },
                WebhookUrl = "https://hook.example/x",
            },
        },
        Workspaces = { new WorkspaceConfig { Name = "w1", Path = "D:/w1" } },
        Accounts =
        {
            new AccountConfig
            {
                Id = "a1",
                DisplayName = "账号一",
                CredentialReference = "ref-1",
                Enabled = false,
                ModelCandidates = new List<ModelCandidate> { new ModelCandidate { Name = "m-own" } },
            },
        },
        ModelCandidates = new List<ModelCandidate> { new ModelCandidate { Name = "m-shared" } },
    };

    [Fact]
    public void Reload_映射RunOptions到属性()
    {
        var options = new RunOptions
        {
            Concurrency = 5,
            AgentKind = AgentKind.CodeBuddy,
            EnableSessionResume = true,
            AutoAnswer = { AllowedWhitelist = new List<string> { "x", "y" } },
            DiffTargetGlobs = new List<string> { "a", "b" },
        };
        var fake = new FakeEngineHost(options);
        var vm = new ConfigViewModel(fake);

        Assert.Equal(5, vm.Concurrency);
        Assert.Equal(AgentKind.CodeBuddy, vm.AgentKind);
        Assert.True(vm.EnableSessionResume);
        Assert.Equal("x\ny", vm.WhitelistText);
        Assert.Equal("a\nb", vm.DiffGlobsText);
        Assert.Equal(ModelPoolMode.Shared, vm.ModelPoolMode);
    }

    [Fact]
    public async Task SaveAsync_字段改动回写RunOptions且触发落盘与重建()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new ConfigViewModel(fake);
        vm.Concurrency = 7;
        vm.EnableSessionResume = true;
        vm.WhitelistText = " a \n\n b ";
        vm.DiffGlobsText = "*.cs\n*.md";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(7, fake.Options.Concurrency);
        Assert.True(fake.Options.EnableSessionResume);
        Assert.Equal(new[] { "a", "b" }, fake.Options.AutoAnswer.AllowedWhitelist);
        Assert.Equal(new[] { "*.cs", "*.md" }, fake.Options.DiffTargetGlobs);
        Assert.Equal(1, fake.SaveConfigAsyncCallCount);
        Assert.Equal(1, fake.RebuildEngineCallCount);
    }

    [Fact]
    public async Task SaveAsync_零值下限钳制为1()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new ConfigViewModel(fake);
        vm.Concurrency = 0;
        vm.MaxAttempts = 0;
        vm.TaskTimeoutMinutes = 0;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.Options.Concurrency);
        Assert.Equal(1, fake.Options.MaxAttemptsPerTask);
        Assert.Equal(1, fake.Options.TaskTimeoutMinutes);
    }

    [Fact]
    public void Reload_白名单往返不丢项()
    {
        var options = new RunOptions { AutoAnswer = { AllowedWhitelist = new List<string> { "p", "q" } } };
        var fake = new FakeEngineHost(options);
        var vm = new ConfigViewModel(fake);

        Assert.Equal("p\nq", vm.WhitelistText);
        Assert.Equal(new[] { "p", "q" }, fake.Options.AutoAnswer.AllowedWhitelist);
    }

    // ---------------- P-04 准入：跨配置域行为锁（分解前后断言不变） ----------------

    [Fact]
    public void Reload_全配置域映射到属性()
    {
        var vm = new ConfigViewModel(new FakeEngineHost(全配置域样例()));

        // 任务与执行工具
        Assert.Equal("tasks.json", vm.TaskFilePath);
        Assert.True(vm.HasTaskFile);
        Assert.Equal(AgentKind.Mock, vm.AgentKind);
        Assert.Equal("cli.exe", vm.CliPath);
        Assert.Equal("-p {prompt}", vm.CliArgumentsTemplate);
        Assert.Equal("ws-default", vm.DefaultWorkspace);
        Assert.True(vm.ShowCliWindow);
        // 降级与模型池
        Assert.Equal(DegradationStrategy.AccountFirst, vm.DegradationStrategy);
        Assert.Equal(ModelPoolMode.PerAccount, vm.ModelPoolMode); // 任一账号配了自有池即视为每账号独立
        Assert.Equal("m-shared", vm.Models.Single().Name);
        Assert.Equal("m-own", vm.Accounts.Single().Models.Single().Name);
        // 专家 / 兜底 / 自动关机
        Assert.Equal(ExpertKind.Single, vm.ExpertKind);
        Assert.Equal("exp-1", vm.ExpertId);
        Assert.Equal("专家甲", vm.ExpertDisplayName);
        Assert.True(vm.FallbackEnabled);
        Assert.Equal(2.5m, vm.FallbackMaxPerTask);
        Assert.Equal(20m, vm.FallbackMaxPerBatch);
        Assert.True(vm.ShutdownEnabled);
        Assert.True(vm.ShutdownOnlyZeroFailure);
        Assert.Equal(90, vm.ShutdownCountdown);
        // 调度与重试
        Assert.Equal(3, vm.Concurrency);
        Assert.Equal(4, vm.MaxRetries);
        Assert.Equal(5, vm.RateLimitRetries);
        Assert.Equal(11, vm.TaskTimeoutMinutes);
        Assert.Equal(12, vm.MaxAttempts);
        Assert.Equal(13, vm.MaxTotalMinutes);
        Assert.Equal(14, vm.MaxWaitMinutes);
        Assert.Equal(15, vm.DefaultCooldownMinutes);
        Assert.False(vm.WaitForQuotaReset);
        Assert.True(vm.ForceRerun);
        Assert.True(vm.StopOnFailure);
        // 安全与交互
        Assert.Equal("读取文件\n写入文件", vm.WhitelistText);
        Assert.Equal("rm -rf", vm.BlacklistText);
        Assert.Equal("n", vm.DefaultAnswerText);
        Assert.Equal(45, vm.SilenceSeconds);
        Assert.Equal(2, vm.ChoiceDefaultIndex);
        // 二期杂项 / 工作流
        Assert.Equal(200, vm.MaxResultKb);
        Assert.Equal(9, vm.BackupRetentionCount);
        Assert.Equal(4, vm.DefaultMaxGateLoops);
        Assert.True(vm.EnableSessionResume);
        Assert.True(vm.MarkdownInPlaceWriteback);
        Assert.Equal(DependencyFailurePolicy.AbortBatch, vm.DependencyFailurePolicy);
        Assert.Equal(60, vm.TemplateBatchExpansionLimit);
        Assert.True(vm.CollectDiffs);
        Assert.Equal("*.cs\n*.md", vm.DiffGlobsText);
        // 通知
        var rule = vm.NotificationRules.Single();
        Assert.Equal(NotificationChannel.Webhook, rule.Channel);
        Assert.Equal("https://hook.example/x", rule.WebhookUrl);
        // Git
        Assert.Equal("D:/repo", vm.GitRepoPath);
        Assert.Equal("main", vm.GitBaseBranch);
        Assert.Equal("feat/{task_id}", vm.GitBranchNaming);
        Assert.Equal("release", vm.GitPrTargetBranch);
        Assert.True(vm.GitAutoOpenPr);
        // 工作空间
        Assert.Equal(7, vm.MaxWorkspaces);
        Assert.Equal("w1", vm.Workspaces.Single().Name);
        Assert.False(vm.WorkspaceLimitReached);
        Assert.Equal("当前 1 / 上限 7", vm.WorkspaceLimitHint);
    }

    [Fact]
    public async Task SaveAsync_全配置域回写RunOptions()
    {
        var fake = new FakeEngineHost(new RunOptions());
        var vm = new ConfigViewModel(fake);

        vm.TaskFilePath = "t.json";
        vm.AgentKind = AgentKind.Mock;
        vm.CliPath = string.Empty;               // 空白 → null
        vm.CliArgumentsTemplate = "  ";          // 空白 → null
        vm.DefaultWorkspace = string.Empty;      // 空白 → null
        vm.DegradationStrategy = DegradationStrategy.AccountFirst;
        vm.ExpertKind = ExpertKind.Single;
        vm.ExpertId = string.Empty;              // 空白 → null
        vm.ExpertDisplayName = string.Empty;     // 空白 → null
        vm.FallbackEnabled = true;
        vm.FallbackMaxPerTask = 3m;
        vm.FallbackMaxPerBatch = 30m;
        vm.ShutdownEnabled = true;
        vm.ShutdownOnlyZeroFailure = true;
        vm.ShutdownCountdown = 30;
        vm.Concurrency = 2;
        vm.MaxRetries = 1;
        vm.RateLimitRetries = 6;
        vm.TaskTimeoutMinutes = 20;
        vm.MaxAttempts = 21;
        vm.MaxTotalMinutes = 22;
        vm.MaxWaitMinutes = 0;
        vm.DefaultCooldownMinutes = 23;
        vm.WaitForQuotaReset = false;
        vm.ForceRerun = true;
        vm.StopOnFailure = true;
        vm.ShowCliWindow = true;
        vm.WhitelistText = "a\nb";
        vm.BlacklistText = "rm -rf";
        vm.DefaultAnswerText = string.Empty;     // 空白 → 回落 "y"
        vm.SilenceSeconds = 10;
        vm.ChoiceDefaultIndex = 1;
        vm.MaxResultKb = 50;
        vm.BackupRetentionCount = 3;
        vm.DefaultMaxGateLoops = 5;
        vm.EnableSessionResume = true;
        vm.MarkdownInPlaceWriteback = true;
        vm.DependencyFailurePolicy = DependencyFailurePolicy.SkipDependents;
        vm.TemplateBatchExpansionLimit = 55;
        vm.CollectDiffs = true;
        vm.DiffGlobsText = "*.ts";
        vm.GitRepoPath = "  D:/r  ";             // 去空白
        vm.GitBaseBranch = " main ";             // 去空白
        vm.GitBranchNaming = string.Empty;       // 空白 → 回落默认命名
        vm.GitPrTargetBranch = string.Empty;     // 空白 → null
        vm.GitAutoOpenPr = true;
        vm.NotificationRules.Add(new NotificationRuleEditor(new NotificationRule
        {
            Channel = NotificationChannel.Webhook,
            WebhookUrl = " https://h ",          // 去空白
        }));

        await vm.SaveCommand.ExecuteAsync(null);

        var o = fake.Options;
        Assert.Equal("t.json", o.TaskFilePath);
        Assert.Equal(AgentKind.Mock, o.AgentKind);
        Assert.Null(o.CliPath);
        Assert.Null(o.CliArgumentsTemplate);
        Assert.Null(o.DefaultWorkspace);
        Assert.Equal(DegradationStrategy.AccountFirst, o.DegradationStrategy);
        Assert.Equal(ExpertKind.Single, o.Expert.Kind);
        Assert.Null(o.Expert.Id);
        Assert.Null(o.Expert.DisplayName);
        Assert.True(o.Fallback.Enabled);
        Assert.Equal(3m, o.Fallback.MaxCostPerTask);
        Assert.Equal(30m, o.Fallback.MaxCostPerBatch);
        Assert.True(o.AutoShutdown.Enabled);
        Assert.True(o.AutoShutdown.OnlyWhenZeroFailure);
        Assert.Equal(30, o.AutoShutdown.CountdownSeconds);
        Assert.Equal(2, o.Concurrency);
        Assert.Equal(1, o.MaxRetriesPerTask);
        Assert.Equal(6, o.RateLimitBackoffRetries);
        Assert.Equal(20, o.TaskTimeoutMinutes);
        Assert.Equal(21, o.MaxAttemptsPerTask);
        Assert.Equal(22, o.MaxTotalMinutesPerTask);
        Assert.Equal(0, o.MaxWaitMinutes);
        Assert.Equal(23, o.DefaultCooldownMinutes);
        Assert.False(o.WaitForQuotaReset);
        Assert.True(o.ForceRerun);
        Assert.True(o.StopOnFailure);
        Assert.True(o.ShowCliWindow);
        Assert.Equal(new[] { "a", "b" }, o.AutoAnswer.AllowedWhitelist);
        Assert.Equal(new[] { "rm -rf" }, o.AutoAnswer.DangerousBlacklist);
        Assert.Equal("y", o.AutoAnswer.DefaultAnswer);
        Assert.Equal(10, o.AutoAnswer.StdinIdleSeconds);
        Assert.Equal(1, o.AutoAnswer.DefaultChoiceIndex);
        Assert.Equal(50, o.ResultMaxKB);
        Assert.Equal(3, o.BackupRetention);
        Assert.Equal(5, o.DefaultMaxGateLoops);
        Assert.True(o.EnableSessionResume);
        Assert.True(o.MarkdownInPlaceWriteback);
        Assert.Equal(DependencyFailurePolicy.SkipDependents, o.DependencyFailurePolicy);
        Assert.Equal(55, o.TemplateBatchExpansionLimit);
        Assert.True(o.CollectDiffs);
        Assert.Equal(new[] { "*.ts" }, o.DiffTargetGlobs);
        Assert.Equal("D:/r", o.Git.RepoPath);
        Assert.Equal("main", o.Git.BaseBranch);
        Assert.Equal("bar/{task_id}-{timestamp}", o.Git.BranchNaming);
        Assert.Null(o.Git.PrTargetBranch);
        Assert.True(o.Git.AutoOpenPr);
        var saved = o.Notifications.Single();
        Assert.Equal(NotificationChannel.Webhook, saved.Channel);
        Assert.Equal("https://h", saved.WebhookUrl);
    }

    [Fact]
    public async Task SaveAsync_共享模式_全局列表落盘且账号自有池清空()
    {
        var fake = new FakeEngineHost(new RunOptions
        {
            ModelCandidates = new List<ModelCandidate> { new ModelCandidate { Name = "m1" } },
            Accounts = { new AccountConfig { Id = "a1", ModelCandidates = new List<ModelCandidate> { new ModelCandidate { Name = "own" } } } },
        });
        var vm = new ConfigViewModel(fake); // 账号有自有池 → 每账号独立
        Assert.Equal(ModelPoolMode.PerAccount, vm.ModelPoolMode);

        vm.ModelPoolMode = ModelPoolMode.Shared; // 合并回共享并清空账号池

        await vm.SaveCommand.ExecuteAsync(null);

        // 独立→共享：共享列表被账号池合并结果整体替换（去重后仅剩 own），账号自有池落盘为 null
        Assert.Equal(new[] { "own" }, fake.Options.ModelCandidates.Select(m => m.Name));
        Assert.All(fake.Options.Accounts, a => Assert.Null(a.ModelCandidates));
    }

    [Fact]
    public async Task SaveAsync_每账号独立模式_账号自有池落盘且全局列表清空()
    {
        var fake = new FakeEngineHost(new RunOptions
        {
            ModelCandidates = new List<ModelCandidate> { new ModelCandidate { Name = "m1" } },
            Accounts = { new AccountConfig { Id = "a1" } },
        });
        var vm = new ConfigViewModel(fake);
        Assert.Equal(ModelPoolMode.Shared, vm.ModelPoolMode);

        vm.ModelPoolMode = ModelPoolMode.PerAccount; // 共享列表复制到空池账号

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(fake.Options.ModelCandidates);
        Assert.Equal(new[] { "m1" }, fake.Options.Accounts.Single().ModelCandidates!.Select(m => m.Name));
    }

    [Fact]
    public void ModelPoolMode_共享切每账号独立_空池账号继承共享池且保留时段与单价折扣()
    {
        var shared = new ModelCandidate
        {
            Name = "m1",
            FreeTimeWindow = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0)),
            EstimatedUnitPrice = 0.5m,
            Discount = 0.8m,
        };
        var fake = new FakeEngineHost(new RunOptions
        {
            ModelCandidates = new List<ModelCandidate> { shared },
            Accounts = { new AccountConfig { Id = "a1" }, new AccountConfig { Id = "a2" } },
        });
        var vm = new ConfigViewModel(fake);

        vm.ModelPoolMode = ModelPoolMode.PerAccount;

        Assert.All(vm.Accounts, a =>
        {
            var copied = a.Models.Single();
            Assert.Equal("m1", copied.Name);
            Assert.NotNull(copied.Model.FreeTimeWindow);
            Assert.Equal(0.5m, copied.Model.EstimatedUnitPrice);
            Assert.Equal(0.8m, copied.Model.Discount);
        });
        // 共享列表本身不受迁移影响
        Assert.Equal("m1", vm.Models.Single().Name);
    }

    [Fact]
    public void ModelPoolMode_独立切共享_按名去重合并并清空账号池()
    {
        var fake = new FakeEngineHost(new RunOptions
        {
            Accounts =
            {
                new AccountConfig { Id = "a1", ModelCandidates = new List<ModelCandidate> { new ModelCandidate { Name = "Model-A" } } },
                new AccountConfig
                {
                    Id = "a2",
                    ModelCandidates = new List<ModelCandidate> { new ModelCandidate { Name = "model-a" }, new ModelCandidate { Name = "Model-B" } },
                },
            },
        });
        var vm = new ConfigViewModel(fake);
        Assert.Equal(ModelPoolMode.PerAccount, vm.ModelPoolMode);

        vm.ModelPoolMode = ModelPoolMode.Shared;

        Assert.Equal(new[] { "Model-A", "Model-B" }, vm.Models.Select(m => m.Name));
        Assert.All(vm.Accounts, a => Assert.Empty(a.Models));
    }
}
