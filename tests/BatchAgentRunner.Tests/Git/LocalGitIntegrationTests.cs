using System.Diagnostics;
using System.Reflection;
using System.Text;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.Git;

namespace BatchAgentRunner.Tests.Git;

/// <summary>
/// P1-14：<see cref="LocalGitIntegration"/> 子进程执行加固（超时回收 + stdin 重定向 + 启动失败收敛）。
/// 原实现 <c>WaitForExitAsync(ct)</c> 无超时、无 stdin 重定向：挂死的 git/gh（网络卡死、
/// 等待终端凭证输入）会无限期挂起任务线程，违背「尽力而为，失败返回 null，不阻断任务」的接口契约；
/// 且 gh CLI 缺失时 <c>Start()</c> 抛异常（原「无法启动」分支不可达），异常直接击穿调用方。
/// </summary>
[Trait("Category", "Process")]
[Trait("Category", "Invariant")]
public class LocalGitIntegrationTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(3);

    // ───────────────────────── RunAsync 直接驱动（P1-14 internal 缝，见 Infrastructure.csproj InternalsVisibleTo） ─────────────────────────

    /// <summary>P1-14 不变量：挂死的子进程必须在上限到时整树回收并按失败返回，不得无限期挂起调用方。</summary>
    [Fact]
    public async Task RunAsync_进程挂死_超时后整树回收并按失败返回()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 用 cmd/ping 构造挂起进程，仅 Windows 验证
        }

        var started = Stopwatch.StartNew();
        var (code, _, stdErr) = await LocalGitIntegration.RunAsync(
            "cmd", new[] { "/c", "ping -n 60 127.0.0.1 > nul" }, null, ShortTimeout, CancellationToken.None);
        started.Stop();

        Assert.Equal(-1, code);
        Assert.Contains("超时", stdErr);
        // 挂起进程约 60s：远超 30s 才返回即说明超时回收未生效
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"超时回收未生效，调用耗时 {started.Elapsed}");
    }

    /// <summary>
    /// P1-14 不变量：stdin 已重定向且启动后立即关闭——等待输入的子进程拿到 EOF 后必须继续执行而非挂起。
    /// （CI 上父进程 stdin 通常已是 null，行为上无法与「父进程恰好 EOF」区分，故结构性保证另由
    /// <see cref="LocalGitIntegration_子进程执行_IL必须重定向stdin并设置超时上限"/> 的 IL 扫描锁死。）
    /// </summary>
    [Fact]
    public async Task RunAsync_等待stdin输入的子进程_立即EOF不再挂起()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // cmd/set /p 仅 Windows 验证
        }

        // set /p 在 stdin 未关闭时会一直等待用户输入；stdin 重定向 + 立即关闭后应立即 EOF 并继续
        var script = Path.Combine(Path.GetTempPath(), $"bar-git-stdin-{Guid.NewGuid()}.cmd");
        File.WriteAllText(script, "@echo off\r\nset /p answer=\r\necho ANSWER=[%answer%]\r\n", Encoding.Default);
        try
        {
            var (code, stdOut, _) = await LocalGitIntegration.RunAsync(
                "cmd", new[] { "/c", script }, null, TimeSpan.FromSeconds(15), CancellationToken.None);

            Assert.Equal(0, code);
            Assert.Contains("ANSWER=[]", stdOut);
        }
        finally
        {
            File.Delete(script);
        }
    }

    /// <summary>P1-14 基本语义：正常退出的进程不受超时影响，按真实退出码返回、不误报超时。</summary>
    [Fact]
    public async Task RunAsync_正常退出进程_按退出码返回不误报超时()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (code, stdOut, stdErr) = await LocalGitIntegration.RunAsync(
            "cmd", new[] { "/c", "echo hi & exit 3" }, null, ShortTimeout, CancellationToken.None);

        Assert.Equal(3, code);
        Assert.Contains("hi", stdOut);
        Assert.DoesNotContain("超时", stdErr);
    }

    /// <summary>P1-14：调用方取消必须向上传播 OperationCanceledException——超时逻辑不得吞掉取消语义。</summary>
    [Fact]
    public async Task RunAsync_调用方取消_抛出OperationCanceledException()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalGitIntegration.RunAsync(
            "cmd", new[] { "/c", "ping -n 60 127.0.0.1 > nul" }, null, TimeSpan.FromMinutes(5), cts.Token));
    }

    /// <summary>
    /// P1-14：可执行文件不存在时 <c>Start()</c> 抛异常而非返回 false——
    /// 必须收敛为「无法启动」失败码，不把异常抛给调用方（gh CLI 未安装时的真实路径）。
    /// </summary>
    [Fact]
    public async Task RunAsync_可执行文件不存在_返回无法启动失败码而不抛异常()
    {
        var (code, _, stdErr) = await LocalGitIntegration.RunAsync(
            "bar-nonexistent-binary-xyz", Array.Empty<string>(), null, ShortTimeout, CancellationToken.None);

        Assert.Equal(-1, code);
        Assert.Contains("无法启动", stdErr);
    }

    /// <summary>
    /// P1-14 结构断言（§4.3 IL 扫描惯例）：RunAsync 必须把
    /// <c>RedirectStandardInput</c> 置为 <c>true</c>（调用点前序指令须为 <c>ldc.i4.1</c>），
    /// 且必须调用 <c>CancellationTokenSource.CancelAfter</c> 叠加上限。
    /// 行为测试在「父进程 stdin 恰好为 EOF」的环境下无法区分有无重定向，故以 IL 锁死实现本身。
    /// </summary>
    [Fact]
    public void LocalGitIntegration_子进程执行_IL必须重定向stdin并设置超时上限()
    {
        var stdinSetter = typeof(ProcessStartInfo).GetProperty(nameof(ProcessStartInfo.RedirectStandardInput))!.GetSetMethod()!;
        var redirectStdinToTrue = false;
        var hasCancelAfter = false;

        // async 方法的真实 IL 在编译器生成的状态机嵌套类型（<RunAsync>d__NN.MoveNext）里，必须一并扫描
        var types = new List<Type> { typeof(LocalGitIntegration) };
        types.AddRange(typeof(LocalGitIntegration).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                MethodBody? body;
                try { body = method.GetMethodBody(); }
                catch { continue; }
                if (body is null)
                {
                    continue;
                }

                var il = body.GetILAsByteArray();
                if (il is null)
                {
                    continue;
                }

                foreach (var (opcodePos, token) in DecodeCallTokens(il))
                {
                    MethodBase? target;
                    try { target = type.Module.ResolveMethod(token); }
                    catch { continue; } // 字节流中非 call 指令的操作数被误判为 token：解析失败即跳过
                    if (target is null)
                    {
                        continue;
                    }

                    if (target == stdinSetter)
                    {
                        // 调用点前一条指令须为 ldc.i4.1（0x17），即 RedirectStandardInput = true
                        redirectStdinToTrue |= opcodePos >= 1 && il[opcodePos - 1] == 0x17;
                    }

                    if (target.Name == "CancelAfter" && target.DeclaringType == typeof(CancellationTokenSource))
                    {
                        hasCancelAfter = true;
                    }
                }
            }
        }

        Assert.True(redirectStdinToTrue,
            "LocalGitIntegration 必须把 ProcessStartInfo.RedirectStandardInput 置为 true（P1-14 stdin 重定向）");
        Assert.True(hasCancelAfter,
            "LocalGitIntegration 必须调用 CancellationTokenSource.CancelAfter 叠加上限（P1-14 超时回收）");
    }

    // ───────────────────────── 公共 API 回归（真实 git；尽力而为契约不变） ─────────────────────────

    /// <summary>P1-14 回归：真实 git happy path 行为不变——建分支成功返回分支名且分支已检出。</summary>
    [Fact]
    public async Task CreateBranchAsync_真实仓库_创建分支并返回分支名()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = CreateTempRepo();
        try
        {
            var integration = new LocalGitIntegration();
            var branch = await integration.CreateBranchAsync(
                new ProjectRef { RepoPath = dir, TaskBranch = "bar/p1-14-happy" }, "t-1");

            Assert.Equal("bar/p1-14-happy", branch);
            Assert.Equal("ref: refs/heads/bar/p1-14-happy", File.ReadAllText(Path.Combine(dir, ".git", "HEAD")).Trim());
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    /// <summary>P1-14 回归：仓库路径无效 / 分支名非法时保持返回 null（加固不得改变尽力而为边界）。</summary>
    [Fact]
    public async Task CreateBranchAsync_仓库无效或分支名非法_返回null()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var integration = new LocalGitIntegration();
        var dir = CreateTempRepo();
        try
        {
            Assert.Null(await integration.CreateBranchAsync(
                new ProjectRef { RepoPath = Path.Combine(Path.GetTempPath(), "bar-no-such-repo-xyz") }, "t-1"));
            Assert.Null(await integration.CreateBranchAsync(
                new ProjectRef { RepoPath = dir, TaskBranch = "-c core.sshCommand=touch ./pwned" }, "t-1"));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    /// <summary>P1-14 回归：推送失败（未配置 origin）时按尽力而为契约返回 null，不抛异常。</summary>
    [Fact]
    public async Task OpenPullRequestAsync_推送失败_返回null()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = CreateTempRepo();
        try
        {
            var integration = new LocalGitIntegration();
            var result = await integration.OpenPullRequestAsync(
                new ProjectRef { RepoPath = dir }, "t-1", "bar/p1-14-nopush");

            Assert.Null(result);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    /// <summary>
    /// P1-14 回归：推送成功但 gh CLI 不可用时，必须退化为「已推送」提示而非崩溃——
    /// 原实现 gh 缺失时 <c>Start()</c> 抛 <c>Win32Exception</c> 直接击穿调用方（「无法启动」分支不可达）。
    /// 已安装 gh 的机器上 gh 对本地路径 remote 同样快速失败，退化路径一致。
    /// </summary>
    [Fact]
    public async Task OpenPullRequestAsync_推送成功但gh不可用_退化为已推送提示()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = CreateTempRepo();
        try
        {
            var bare = Path.Combine(Path.GetTempPath(), $"bar-git-bare-{Guid.NewGuid()}.git");
            RunGitPlain(Path.GetTempPath(), "init", "--bare", "-q", bare);
            RunGitPlain(dir, "remote", "add", "origin", bare);
            RunGitPlain(dir, "checkout", "-q", "-b", "bar/p1-14-gh");

            var integration = new LocalGitIntegration();
            var result = await integration.OpenPullRequestAsync(
                new ProjectRef { RepoPath = dir }, "t-1", "bar/p1-14-gh");

            Assert.NotNull(result);
            Assert.StartsWith("分支 bar/p1-14-gh", result);
            Assert.Contains("已推送", result);
            TryDeleteDir(bare);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    // ───────────────────────── 测试基建 ─────────────────────────

    /// <summary>创建带一个空提交的临时 git 仓库（真实 git，验证 happy path 不被加固改变）。</summary>
    private static string CreateTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bar-git-repo-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        RunGitPlain(dir, "init", "-q");
        RunGitPlain(dir, "-c", "user.email=bar@test.local", "-c", "user.name=BAR",
            "commit", "-q", "--allow-empty", "-m", "init");
        return dir;
    }

    private static void RunGitPlain(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    /// <summary>清理临时目录（git 对象文件只读，需先清属性再递归删）。</summary>
    private static void TryDeleteDir(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch { /* ignore */ }
            }

            Directory.Delete(dir, true);
        }
        catch { /* 清理失败不影响断言结论 */ }
    }

    /// <summary>粗解码 IL 中的 call/callvirt 目标（操作码位置 + 4 字节小端 token；0x28 / 0x6F）。</summary>
    private static IEnumerable<(int OpcodePos, int Token)> DecodeCallTokens(byte[] il)
    {
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is 0x28 or 0x6F)
            {
                yield return (i, BitConverter.ToInt32(il, i + 1));
                i += 4;
            }
        }
    }
}
