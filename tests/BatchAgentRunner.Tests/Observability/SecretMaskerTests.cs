using BatchAgentRunner.Core.Observability;
using Xunit;

namespace BatchAgentRunner.Tests.Observability;

/// <summary>
/// 运行日志脱敏（ARCH P-10 / A-8「凭证/密钥绝不落日志」）。
/// </summary>
/// <remarks>
/// 治理前脱敏零基础设施：只要某个写入点把 <c>BuildEnvironment</c> 的结果、CLI 回显或异常消息
/// 拼进日志，密钥就明文落到用户最常外发的运行日志文件里，且没有任何测试会拦。
/// 收口后所有日志写入在渲染入口过 <see cref="SecretMasker"/>，本文件锁住「哪些形态必须被吞、
/// 哪些业务字段必须原样保留」——后者同样重要：过度脱敏会让日志失去排障价值。
/// </remarks>
public class SecretMaskerTests
{
    [Theory]
    [InlineData("CODEBUDDY_API_KEY=abcdef123456", "CODEBUDDY_API_KEY=***")]
    [InlineData("api_key=abcdef123456", "api_key=***")]
    [InlineData("token: abcdef123456", "token: ***")]
    [InlineData("session_key=abcdef123456", "session_key=***")]
    [InlineData("credential: abcdef123456", "credential: ***")]
    [InlineData("Authorization: Bearer abcdef1234567890", "Authorization: ***")]
    public void 键名形态_键名与分隔符保留_值替换为占位符(string input, string expected)
    {
        // 键名必须保留：排障时要能看出是"哪个键被吞"，整行消失等于没有日志
        Assert.Equal(expected, SecretMasker.Mask(input));
        Assert.False(SecretMasker.ContainsSecret(SecretMasker.Mask(input)));
    }

    [Fact]
    public void JSON字段形态_引号内值替换为占位符()
    {
        const string input = "{\"password\": \"p@ssw0rd\"}";

        var masked = SecretMasker.Mask(input);

        Assert.DoesNotContain("p@ssw0rd", masked);
        Assert.Contains("\"password\"", masked);
        Assert.Contains(SecretMasker.Placeholder, masked);
    }

    [Fact]
    public void JWT_整体替换为占位符()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        var masked = SecretMasker.Mask($"登录返回 {jwt} 已缓存");

        Assert.DoesNotContain(jwt, masked);
        Assert.Contains(SecretMasker.Placeholder, masked);
        Assert.Contains("登录返回", masked);
    }

    [Theory]
    [InlineData("sk-live-abcdef1234567890")]
    [InlineData("ghp_abcdefghijklmnop1234")]
    [InlineData("xoxb-1234567890-abcdefghijkl")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    public void 常见密钥前缀_整体替换为占位符(string secret)
    {
        var masked = SecretMasker.Mask($"使用密钥 {secret} 调用");

        Assert.DoesNotContain(secret, masked);
        Assert.Contains(SecretMasker.Placeholder, masked);
    }

    [Theory]
    [InlineData("任务 task-001 使用 acc1 + glm-4.6 完成")]
    [InlineData("工作区：E:\\Src\\CodeBak\\WorkAgent")]
    [InlineData("model_cooldown_skipped acc1 × m1 | 预计恢复 2026-10-21 18:00")]
    [InlineData("凭据引用 env:CODEBUDDY_API_KEY 已注入")]
    public void 非敏感业务字段_原样保留(string text)
    {
        Assert.Equal(text, SecretMasker.Mask(text));
        Assert.False(SecretMasker.ContainsSecret(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 空输入_安全返回(string? input)
    {
        Assert.Equal(string.Empty, SecretMasker.Mask(input));
        Assert.False(SecretMasker.ContainsSecret(input));
    }

    [Fact]
    public void 同一行多处敏感值_全部替换()
    {
        const string input = "api_key=aaa111222333 与 token: bbb444555666 同时出现";

        var masked = SecretMasker.Mask(input);

        Assert.DoesNotContain("aaa111222333", masked);
        Assert.DoesNotContain("bbb444555666", masked);
        Assert.Equal(2, masked.Split(SecretMasker.Placeholder).Length - 1);
    }

    [Fact]
    public void 高频调用路径_幂等且无异常()
    {
        // CLI 输出逐行落盘（WriteTaskOutput）会走到脱敏：重复脱敏不得改变结果、不得抛异常
        const string input = "api_key=abcdef123456";
        var once = SecretMasker.Mask(input);

        Assert.Equal(once, SecretMasker.Mask(once));
        Assert.False(SecretMasker.ContainsSecret(once));
    }
}
