using System.ComponentModel;
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
    /// <param name="value">待校验的分支或引用名。</param>
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
    /// <summary>
    /// 默认进程超时。git/gh 子进程超过该时长未退出即整树回收并按失败返回——
    /// P1-14：原实现 <c>WaitForExitAsync(ct)</c> 只响应取消令牌，挂死的 git/gh
    /// （网络卡死、等待终端凭证输入等）会无限期挂起任务线程。
    /// </summary>
    public const int DefaultTimeoutSeconds = 120;

    private readonly TimeSpan _timeout;

    /// <summary>使用默认 120 秒子进程超时。</summary>
    public LocalGitIntegration() : this(TimeSpan.FromSeconds(DefaultTimeoutSeconds))
    {
    }

    /// <summary>使用指定的子进程超时，非正值回落默认值。</summary>
    public LocalGitIntegration(TimeSpan timeout)
    {
        _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    /// <summary>在仓库中创建并切换到任务分支，失败返回 null 不阻断任务。</summary>
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

        var (code, _, _) = await RunGitAsync(args, _timeout, cancellationToken).ConfigureAwait(false);
        return code == 0 ? branch : null;
    }

    /// <summary>优先用 gh 创建 PR，不可用时推送分支并返回提示。</summary>
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
        var push = await RunGitAsync(new[] { "-C", repoPath, "push", "-u", "origin", branch }, _timeout, cancellationToken).ConfigureAwait(false);
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
        }, repoPath, _timeout, cancellationToken).ConfigureAwait(false);
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

    private static Task<(int Code, string StdOut, string StdErr)> RunGitAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        RunAsync("git", args, null, timeout, ct);

    /// <summary>
    /// 执行 git/gh 子进程并回收输出。P1-14 三项加固：
    /// ① <b>stdin 重定向</b>——<c>RedirectStandardInput=true</c> 并在启动后立即关闭，
    /// 等待终端输入的子进程（如 git 凭证提示、gh 交互）立刻得到 EOF，不会挂起；
    /// ② <b>超时回收</b>——<c>WaitForExitAsync</c> 经 linked CTS 设上限，超时即
    /// <c>Kill(entireProcessTree: true)</c> 整树回收并按失败返回，不无限期挂起调用线程；
    /// ③ <b>启动失败收敛</b>——可执行文件不在 PATH 等场景 <c>Start()</c> 抛异常而非返回
    /// false，按「尽力而为」契约收敛为失败码，不把异常抛给调用方。
    /// </summary>
    internal static async Task<(int Code, string StdOut, string StdErr)> RunAsync(string fileName, IReadOnlyList<string> args, string? workDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // P1-14：重定向 stdin 并立即关闭——子进程任何「等待输入」的提示都会拿到 EOF
            RedirectStandardInput = true,
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

        try
        {
            if (!p.Start())
            {
                return (-1, string.Empty, $"无法启动 {fileName} 进程");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // 可执行文件不存在 / 无权限等：Start() 抛异常而非返回 false（原 if (!p.Start()) 分支不可达），
            // 收敛为失败码以维持「失败返回 null，不阻断任务」的接口契约。
            return (-1, string.Empty, $"无法启动 {fileName} 进程：{ex.Message}");
        }

        // P1-14：启动后立即关闭 stdin——任何等待终端输入的提示直接得到 EOF
        try
        {
            p.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 子进程已自行退出导致 stdin 句柄失效：不影响后续等待与输出读取
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            // P1-14：超时保护。WaitForExitAsync(ct) 只响应调用方取消；挂死的 git/gh 会无限期等待，
            // 故经 linked CTS 叠加上限，超时（非调用方取消）时整树回收并按失败返回。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 超时取消（调用方令牌未触发）：整树回收后按失败返回
            try { p.Kill(entireProcessTree: true); } catch { /* 进程已退出等：回收目的已达成，忽略 */ }
            return (-1, outSb.ToString(), $"{fileName} 执行超时（>{timeout.TotalSeconds:0}s），已整树回收");
        }
        catch (OperationCanceledException)
        {
            // 调用方取消：与原语义一致，整树回收后向上传播
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        return (p.ExitCode, outSb.ToString(), errSb.ToString());
    }
}
