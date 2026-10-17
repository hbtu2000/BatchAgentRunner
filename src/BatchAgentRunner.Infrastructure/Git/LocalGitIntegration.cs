using System.Diagnostics;
using System.Text;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.Git;

/// <summary>
/// 本地 Git 集成（二期项目级任务）。尽力而为：失败返回 null，不阻断任务。
/// 建分支用 git；开 PR 优先 gh CLI，不可用时退化为推送并返回提示。
/// </summary>
public sealed class LocalGitIntegration : IGitIntegration
{
    public async Task<string?> CreateBranchAsync(ProjectRef projectRef, string taskId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRef.RepoPath) || !Directory.Exists(projectRef.RepoPath))
        {
            return null;
        }

        var branch = projectRef.TaskBranch
                     ?? $"bar/{taskId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var baseBranch = projectRef.BaseBranch;

        var args = string.IsNullOrWhiteSpace(baseBranch)
            ? $"-C \"{projectRef.RepoPath}\" checkout -b {branch}"
            : $"-C \"{projectRef.RepoPath}\" checkout -b {branch} {baseBranch}";

        var (code, _, err) = await RunGitAsync(args, cancellationToken).ConfigureAwait(false);
        return code == 0 ? branch : null;
    }

    public async Task<string?> OpenPullRequestAsync(ProjectRef projectRef, string taskId, string branch, string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRef.RepoPath) || !Directory.Exists(projectRef.RepoPath))
        {
            return null;
        }

        // 先推送分支
        var push = await RunGitAsync($"-C \"{projectRef.RepoPath}\" push -u origin {branch}", cancellationToken).ConfigureAwait(false);
        if (push.Code != 0)
        {
            return null;
        }

        // 优先 gh CLI 开 PR
        var prTitle = title ?? $"[{taskId}] 自动任务分支 {branch}";
        var gh = await RunAsync("gh", $"pr create --title \"{prTitle}\" --body \"自动生成 PR（BatchAgentRunner）\"",
            projectRef.RepoPath, cancellationToken).ConfigureAwait(false);
        if (gh.Code == 0 && !string.IsNullOrWhiteSpace(gh.StdOut))
        {
            return gh.StdOut.Trim();
        }

        // 退化：返回推送提示
        return $"分支 {branch} 已推送（未检测到 gh CLI，未自动创建 PR）";
    }

    private static Task<(int Code, string StdOut, string StdErr)> RunGitAsync(string args, CancellationToken ct) =>
        RunAsync("git", args, null, ct);

    private static async Task<(int Code, string StdOut, string StdErr)> RunAsync(string fileName, string args, string? workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
        {
            psi.WorkingDirectory = workDir;
        }

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) outSb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) errSb.AppendLine(e.Data); };

        if (!p.Start())
        {
            return (-1, string.Empty, "无法启动 git/gh 进程");
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* ignore */ }
            throw;
        }

        return (p.ExitCode, outSb.ToString(), errSb.ToString());
    }
}
