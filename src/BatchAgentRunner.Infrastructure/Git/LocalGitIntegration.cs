using System.Diagnostics;
using System.Text;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.Git;

/// <summary>
/// Git 引用名（分支 / 基线分支）白名单校验。
/// P0-3：仓库路径、基线分支、PR 标题均来自配置文件或任务文件（外部输入），若拼进命令行字符串，
/// 可注入 <c>-c core.sshCommand=...</c> 之类的 git 配置项实现任意命令执行。
/// 现在参数一律走 <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>（不经 shell、不做字符串解析），
/// 引用名再叠一层白名单，杜绝「以 - 开头被当成选项」与路径穿越。
/// </summary>
public static class GitRefName
{
    /// <summary>合法字符集：字母数字开头，后续允许字母数字与 . _ / -；且不含 <c>..</c>（防路径穿越）。</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 255
        && value[0] is not ('-' or '/' or '.')
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-');
}

/// <summary>
/// 本地 Git 集成（二期项目级任务）。尽力而为：失败返回 null，不阻断任务。
/// 建分支用 git；开 PR 优先 gh CLI，不可用时退化为推送并返回提示。
/// </summary>
public sealed class LocalGitIntegration : IGitIntegration
{
    public async Task<string?> CreateBranchAsync(ProjectRef projectRef, string taskId, CancellationToken cancellationToken = default)
    {
        if (!TryResolveRepoPath(projectRef.RepoPath, out var repoPath))
        {
            return null;
        }

        var branch = projectRef.TaskBranch
                     ?? $"bar/{taskId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var baseBranch = projectRef.BaseBranch;
        if (!GitRefName.IsValid(branch))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(baseBranch) && !GitRefName.IsValid(baseBranch))
        {
            return null;
        }

        // P0-3：逐参数传递，不做命令行字符串拼接。
        var args = new List<string> { "-C", repoPath, "checkout", "-b", branch };
        if (!string.IsNullOrWhiteSpace(baseBranch))
        {
            args.Add(baseBranch);
        }

        var (code, _, _) = await RunGitAsync(args, cancellationToken).ConfigureAwait(false);
        return code == 0 ? branch : null;
    }

    public async Task<string?> OpenPullRequestAsync(ProjectRef projectRef, string taskId, string branch, string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveRepoPath(projectRef.RepoPath, out var repoPath))
        {
            return null;
        }

        if (!GitRefName.IsValid(branch))
        {
            return null;
        }

        // 先推送分支
        var push = await RunGitAsync(new[] { "-C", repoPath, "push", "-u", "origin", branch }, cancellationToken).ConfigureAwait(false);
        if (push.Code != 0)
        {
            return null;
        }

        // 优先 gh CLI 开 PR。标题用 --title=<value> 单参数形式：即便标题以 - 开头也不会被解析为选项。
        var prTitle = title ?? $"[{taskId}] 自动任务分支 {branch}";
        var gh = await RunAsync("gh", new[]
        {
            "pr", "create",
            $"--title={prTitle}",
            "--body=自动生成 PR（BatchAgentRunner）"
        }, repoPath, cancellationToken).ConfigureAwait(false);
        if (gh.Code == 0 && !string.IsNullOrWhiteSpace(gh.StdOut))
        {
            return gh.StdOut.Trim();
        }

        // 退化：返回推送提示
        return $"分支 {branch} 已推送（未检测到 gh CLI，未自动创建 PR）";
    }

    private static bool TryResolveRepoPath(string? repoPath, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return false;
        }

        resolved = Path.GetFullPath(repoPath);
        return true;
    }

    private static Task<(int Code, string StdOut, string StdErr)> RunGitAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        RunAsync("git", args, null, ct);

    private static async Task<(int Code, string StdOut, string StdErr)> RunAsync(string fileName, IReadOnlyList<string> args, string? workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

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
