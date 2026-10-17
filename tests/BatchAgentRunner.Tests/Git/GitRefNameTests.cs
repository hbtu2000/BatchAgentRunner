using BatchAgentRunner.Infrastructure.Git;

namespace BatchAgentRunner.Tests.Git;

/// <summary>
/// P0-3 不变量：来自配置文件 / 任务文件的 Git 引用名与仓库路径，
/// 不得以可被解析为命令行选项的形式进入子进程（原实现拼接字符串且只用引号包裹，
/// 可注入 <c>-c core.sshCommand=</c> 之类的 git 配置项实现任意命令执行）。
/// </summary>
public class GitRefNameTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("feature/bar-task-001")]
    [InlineData("bar/task-001-20261017120000")]
    [InlineData("release_v2.0")]
    [InlineData("feature.sub/branch-1")]
    public void 合法引用名通过校验(string value) => Assert.True(GitRefName.IsValid(value));

    [Theory]
    // 以 - 开头会被 git 解析成选项；-c 可注入任意配置项，其中 core.sshCommand 等价于远程代码执行
    [InlineData("--upload-pack=touch ./pwned")]
    [InlineData("-c core.sshCommand=touch ./pwned main")]
    // 路径穿越 / 绝对值 / 空白
    [InlineData("../../../etc/passwd")]
    [InlineData("/abs/branch")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(" ")]
    // git 内部禁止的写法
    [InlineData("branch.lock")]
    [InlineData("ref/../../evil")]
    public void 危险输入一律拒绝(string? value) => Assert.False(GitRefName.IsValid(value));

    [Fact]
    public void 超长引用名拒绝()
    {
        var tooLong = new string('a', 256);
        Assert.False(GitRefName.IsValid(tooLong));
        Assert.True(GitRefName.IsValid(new string('a', 255)));
    }

    [Fact]
    public void 含连续点号的路径穿越写法被拒绝()
    {
        // 白名单是保守校验收窄：宁可拒掉合法但不常见的写法，也不放过穿越可能性
        Assert.False(GitRefName.IsValid("feature../main"));
        Assert.False(GitRefName.IsValid("feature.../main"));
    }
}
