using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 任务级 Git 工作流上下文（P1-1 拆分产物 ④）：执行前解析一次并贯穿执行前后，
/// 保证「建分支用的分支名」与「开 PR 用的分支名」一致（分支名含 {timestamp} 时不能二次解析）。
/// </summary>
public sealed record GitTaskContext(ProjectRef? Project, string? SnapshotId);

/// <summary>
/// Git 工作流协作者（P1-1 拆分产物 ④，§3.4 <c>IGitWorkflow</c>）：从 <see cref="BatchEngine"/>
/// 抽出「项目级任务建分支/开 PR + 结果 diff 采集 + Markdown 原地回写」的全部机制，
/// 含生效项目配置解析（<c>EffectiveProjectRef</c>，私有方法）与分支命名渲染。
/// 所有步骤均为「尽力而为，失败不阻断任务」（WARN 日志后继续）。
/// </summary>
public interface IGitWorkflow
{
    /// <summary>
    /// 执行前：解析生效的项目级 Git 配置 + 采集 diff 快照。
    /// 未配置 Git / 未开启 diff 采集 / 已取消时对应项为 null。
    /// </summary>
    Task<GitTaskContext> BeginAsync(TaskItem task, CancellationToken ct);

    /// <summary>
    /// 执行后：项目级任务成功且显式开启 auto_open_pr 时开 PR → 采集结果 diff →
    /// Markdown 任务文件原地回写（按原顺序，均尽力而为）。
    /// </summary>
    Task CompleteAsync(TaskItem task, GitTaskContext context, CancellationToken ct);

    /// <summary>本批已采集的 diff 记录（报告用；并发采集经内部锁保护）。</summary>
    IReadOnlyList<DiffRecord> Diffs { get; }
}

/// <summary>默认实现：机制全部来自构造期注入的端口，无隐藏静态依赖。</summary>
public sealed class GitWorkflow : IGitWorkflow
{
    private readonly RunOptions _options;
    private readonly IDiffCollector? _diffCollector;
    private readonly IGitIntegration? _git;
    private readonly IMarkdownInPlaceWriter? _markdownWriter;
    private readonly IRunLogger _logger;
    private readonly object _diffsLock = new();
    private readonly List<DiffRecord> _diffs = new();

    /// <summary>构造 Git 工作流协作者（端口全部来自注入，缺失即对应能力未启用）。</summary>
    public GitWorkflow(
        RunOptions options,
        IDiffCollector? diffCollector,
        IGitIntegration? git,
        IMarkdownInPlaceWriter? markdownWriter,
        IRunLogger logger)
    {
        _options = options;
        _diffCollector = diffCollector;
        _git = git;
        _markdownWriter = markdownWriter;
        _logger = logger;
    }

    /// <summary>本批已采集的 diff 记录（报告用；并发采集经内部锁保护）。</summary>
    public IReadOnlyList<DiffRecord> Diffs
    {
        get { lock (_diffsLock) { return _diffs.ToList(); } }
    }

    /// <summary>执行前：解析生效项目级 Git 配置 + 采集 diff 快照（尽力而为，失败不阻断）。</summary>
    /// <param name="task">即将执行的任务（提供项目引用与工作目录）。</param>
    /// <param name="ct">取消令牌；已取消时跳过 diff 采集。</param>
    /// <returns>本次执行的 Git 上下文（生效配置 + 快照标识），供收尾阶段比对。</returns>
    public async Task<GitTaskContext> BeginAsync(TaskItem task, CancellationToken ct)
    {
        var project = EffectiveProjectRef(task);

        string? snapshotId = null;
        if (_diffCollector is not null && _options.CollectDiffs && !ct.IsCancellationRequested)
        {
            try
            {
                snapshotId = await _diffCollector.SnapshotAsync(task, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Write(RunLogLevels.Warn, $"任务 {task.Id} diff 快照失败（已忽略）：{ex.Message}");
            }
        }

        // 项目级任务——执行前建分支（尽力而为，失败不阻断任务）
        if (_git is not null && project is not null)
        {
            try
            {
                await _git.CreateBranchAsync(project, task.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Write(RunLogLevels.Warn, $"任务 {task.Id} 创建 Git 分支失败（已忽略）：{ex.Message}");
            }
        }

        return new GitTaskContext(project, snapshotId);
    }

    /// <summary>执行后：开 PR → 采集结果 diff → Markdown 原地回写（均尽力而为）。</summary>
    public async Task CompleteAsync(TaskItem task, GitTaskContext context, CancellationToken ct)
    {
        // 项目级任务成功后开 PR（尽力而为；需任务/全局显式开启 auto_open_pr）
        if (_git is not null && context.Project is not null && task.Status == TaskStatus.Done
            && context.Project.AutoOpenPr
            && !string.IsNullOrWhiteSpace(context.Project.TaskBranch))
        {
            try
            {
                await _git.OpenPullRequestAsync(context.Project, task.Id, context.Project.TaskBranch, null, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Write(RunLogLevels.Warn, $"任务 {task.Id} 打开 PR 失败（已忽略）：{ex.Message}");
            }
        }

        // 结果 diff 采集
        if (context.SnapshotId is not null && _diffCollector is not null)
        {
            try
            {
                var recs = await _diffCollector.CollectAsync(context.SnapshotId, task, ct).ConfigureAwait(false);
                if (recs.Count > 0)
                {
                    lock (_diffsLock)
                    {
                        _diffs.AddRange(recs);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Write(RunLogLevels.Warn, $"任务 {task.Id} diff 采集失败（已忽略）：{ex.Message}");
            }
        }

        // Markdown 任务文件原地回写（仅在显式开启且任务文件为 .md 时）
        if (_markdownWriter is not null && _options.MarkdownInPlaceWriteback && IsMarkdownTaskFile())
        {
            try
            {
                await _markdownWriter.WriteBackAsync(_options.TaskFilePath, task, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Write(RunLogLevels.Warn, $"任务 {task.Id} Markdown 原回写失败（已忽略）：{ex.Message}");
            }
        }
    }

    /// <summary>任务文件是否为 Markdown（决定二期原地回写是否适用）。</summary>
    private bool IsMarkdownTaskFile() =>
        !string.IsNullOrWhiteSpace(_options.TaskFilePath) &&
        _options.TaskFilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 生效的项目级 Git 配置（二期）：任务级 <see cref="TaskItem.ProjectRef"/> 优先，
    /// 空字段用全局 <see cref="RunOptions.Git"/> 兜底；任务无 ProjectRef 但全局配置了仓库时按全局合成。
    /// 全局仓库路径为空 = 未配置 Git 集成，返回 null（不执行任何 Git 操作）。
    /// </summary>
    private ProjectRef? EffectiveProjectRef(TaskItem task)
    {
        var git = _options.Git;
        var globalReady = !string.IsNullOrWhiteSpace(git.RepoPath);

        if (task.ProjectRef is null)
        {
            if (!globalReady)
            {
                return null;
            }

            var branch = RenderBranchName(git.BranchNaming, task.Id);
            return new ProjectRef
            {
                RepoPath = git.RepoPath,
                BaseBranch = git.BaseBranch,
                TaskBranch = branch,
                PrTargetBranch = git.PrTargetBranch ?? git.BaseBranch,
                AutoCreateBranch = true,
                AutoOpenPr = git.AutoOpenPr
            };
        }

        if (!globalReady && string.IsNullOrWhiteSpace(task.ProjectRef.RepoPath))
        {
            return null; // 两级都没配仓库
        }

        var effective = task.ProjectRef.Clone();
        if (string.IsNullOrWhiteSpace(effective.RepoPath))
        {
            effective.RepoPath = git.RepoPath;
        }

        if (string.IsNullOrWhiteSpace(effective.BaseBranch))
        {
            effective.BaseBranch = git.BaseBranch;
        }

        if (string.IsNullOrWhiteSpace(effective.PrTargetBranch))
        {
            effective.PrTargetBranch = git.PrTargetBranch ?? effective.BaseBranch;
        }

        if (string.IsNullOrWhiteSpace(effective.TaskBranch) && effective.AutoCreateBranch)
        {
            effective.TaskBranch = RenderBranchName(
                string.IsNullOrWhiteSpace(git.BranchNaming) ? "bar/{task_id}-{timestamp}" : git.BranchNaming,
                task.Id);
        }

        return effective;
    }

    /// <summary>渲染分支命名模板：{task_id} / {timestamp}(yyyyMMddHHmmss) / {date}(yyyyMMdd)。</summary>
    private static string RenderBranchName(string template, string taskId)
    {
        var now = DateTimeOffset.Now;
        return template
            .Replace("{task_id}", taskId, StringComparison.OrdinalIgnoreCase)
            .Replace("{timestamp}", now.ToString("yyyyMMddHHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);
    }
}
