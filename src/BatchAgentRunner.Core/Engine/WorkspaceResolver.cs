using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 工作区路径解析（REQ-003a）：三级覆盖——单任务 &gt; 本批默认 &gt; 全局默认。
/// </summary>
/// <remarks>
/// 从 <see cref="BatchEngine"/> facade 下沉（ARCH P-10）：facade 收口 ≤400 行的硬约束下，
/// 可观测性（RunId / 指标）本轮要往引擎里加字段与访问器，若不先挪走这块纯查询逻辑就会顶破上限。
/// 此处为无状态纯函数（输入配置 + 任务声明，输出路径），迁移不改变任何行为。
/// </remarks>
internal static class WorkspaceResolver
{
    /// <summary>按「任务声明 &gt; 全局默认」解析工作区路径；无法解析时返回 null。</summary>
    public static string? Resolve(RunOptions options, string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            var globalDefault = options.Workspaces.FirstOrDefault(w => w.IsGlobalDefault);
            return globalDefault?.Path;
        }

        var named = options.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, workspace, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return named.Path;
        }

        return Path.IsPathRooted(workspace) ? workspace : null;
    }
}
