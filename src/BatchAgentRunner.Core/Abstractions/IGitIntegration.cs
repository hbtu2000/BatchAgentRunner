using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>版本控制集成端口（二期项目级任务）：建分支、开 PR。尽力而为，失败不阻断任务。</summary>
public interface IGitIntegration
{
    /// <summary>在仓库中为本任务创建分支（命名规则由调用方决定）。返回创建的分支名；失败返回 null。</summary>
    Task<string?> CreateBranchAsync(ProjectRef projectRef, string taskId, CancellationToken cancellationToken = default);

    /// <summary>创建 Pull Request（基于已建分支）。返回 PR 链接；失败返回 null。</summary>
    Task<string?> OpenPullRequestAsync(ProjectRef projectRef, string taskId, string branch, string? title = null,
        CancellationToken cancellationToken = default);
}
