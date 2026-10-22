using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「版本控制」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的 Git 域）：
/// 仓库路径 / 基线分支 / 分支命名 / PR 目标分支 / 完成后自动开 PR。空白回落与默认命名
/// 单一来源见 <see cref="ConfigFieldRules"/>（仓库路径空白回落空串而非 null，与原始语义一致）。
/// </summary>
public sealed class GitSectionViewModel : ViewModelBase
{
    private string _gitRepoPath = string.Empty;
    private string _gitBaseBranch = string.Empty;
    private string _gitBranchNaming = "bar/{task_id}-{timestamp}";
    private string _gitPrTargetBranch = string.Empty;
    private bool _gitAutoOpenPr;

    /// <summary>任务执行所用的 Git 仓库路径；空白写回为空串。</summary>
    public string GitRepoPath
    {
        get => _gitRepoPath;
        set => SetProperty(ref _gitRepoPath, value);
    }

    /// <summary>分支创建所基于的基线分支名；空白写回为 null。</summary>
    public string GitBaseBranch
    {
        get => _gitBaseBranch;
        set => SetProperty(ref _gitBaseBranch, value);
    }

    /// <summary>自动建分支命名规则（支持 {task_id} / {timestamp} / {date}）。空白回落内置默认命名。</summary>
    public string GitBranchNaming
    {
        get => _gitBranchNaming;
        set => SetProperty(ref _gitBranchNaming, value);
    }

    /// <summary>自动开 PR 时的目标分支名；空白表示沿用默认。</summary>
    public string GitPrTargetBranch
    {
        get => _gitPrTargetBranch;
        set => SetProperty(ref _gitPrTargetBranch, value);
    }

    /// <summary>任务完成后是否自动开 PR（需 gh CLI，不可用时退化为推送分支）。</summary>
    public bool GitAutoOpenPr
    {
        get => _gitAutoOpenPr;
        set => SetProperty(ref _gitAutoOpenPr, value);
    }

    /// <summary>从运行配置加载本域状态。</summary>
    public void LoadFrom(RunOptions o)
    {
        GitRepoPath = o.Git.RepoPath ?? string.Empty;
        GitBaseBranch = o.Git.BaseBranch ?? string.Empty;
        GitBranchNaming = o.Git.BranchNaming ?? "bar/{task_id}-{timestamp}";
        GitPrTargetBranch = o.Git.PrTargetBranch ?? string.Empty;
        GitAutoOpenPr = o.Git.AutoOpenPr;
    }

    /// <summary>把本域状态写回运行配置（空白回落见 <see cref="ConfigFieldRules"/>）。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.Git.RepoPath = ConfigFieldRules.TrimmedOrEmpty(GitRepoPath);
        o.Git.BaseBranch = ConfigFieldRules.TrimmedOrNull(GitBaseBranch);
        o.Git.BranchNaming = ConfigFieldRules.OrFallback(GitBranchNaming, "bar/{task_id}-{timestamp}").Trim();
        o.Git.PrTargetBranch = ConfigFieldRules.TrimmedOrNull(GitPrTargetBranch);
        o.Git.AutoOpenPr = GitAutoOpenPr;
    }
}
