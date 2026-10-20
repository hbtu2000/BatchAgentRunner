using System.Net;
using System.Text;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.Http;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// P1-15 高危盲区：<see cref="CodeBuddyCreditsApiClient"/> 是积分领取链路上唯一直接打
/// CodeBuddy 控制台 API 的客户端（鉴权头 + 统一信封解析 + 「今日已签到」语义区分）。
/// 其解析逻辑（余额求和 / 已签到判定 / 错误码→异常）一旦出错会静默导致「每日积分领取」
/// 整条链路失效或误判，必须经断言锁死。HttpClient 经桩注入，不触网。
/// </summary>
public class CodeBuddyCreditsApiClientTests
{
    private static CodeBuddyCreditsApiClient MakeClient(HttpStatusCode status, string body)
    {
        var handler = new StubHandler((_, _) => Response((int)status, body));
        var config = new CreditsClaimConfig
        {
            ApiBase = "https://example.test",
            BalancePath = "/balance",
            ClaimPath = "/claim",
            TimeoutSeconds = 5,
        };
        return new CodeBuddyCreditsApiClient(config, new HttpClient(handler));
    }

    private static HttpResponseMessage Response(int status, string json) => new()
    {
        StatusCode = (HttpStatusCode)status,
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>可编程 HTTP 假件：按脚本返回响应，不触网。</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request, cancellationToken));
        }
    }

    // ---------------- ParseBalance ----------------

    [Fact]
    public void ParseBalance_正常包剩余额度按数字与字符串求和()
    {
        const string json = """
            {"code":0,"msg":"ok","data":{"Packages":[
                {"CycleRemainCapacity":10.5},
                {"CycleRemainCapacity":"20.5"},
                {"CycleRemainCapacity":"not-a-number"}
            ]}}
            """;

        var sum = CodeBuddyCreditsApiClient.ParseBalance(json);

        Assert.Equal(31m, sum);
    }

    [Fact]
    public void ParseBalance_无Packages键_返回null()
    {
        const string json = """{"code":0,"msg":"ok","data":{}}""";

        Assert.Null(CodeBuddyCreditsApiClient.ParseBalance(json));
    }

    [Fact]
    public void ParseBalance_非JSON_返回null不抛()
    {
        Assert.Null(CodeBuddyCreditsApiClient.ParseBalance("<<<not json>>>"));
    }

    [Fact]
    public void ParseBalance_顶层非对象_返回null()
    {
        Assert.Null(CodeBuddyCreditsApiClient.ParseBalance("[]"));
    }

    // ---------------- IsAlreadyClaimed ----------------

    [Fact]
    public void IsAlreadyClaimed_返回已签到码10001_判已签到()
    {
        Assert.True(CodeBuddyCreditsApiClient.IsAlreadyClaimed(CodeBuddyCreditsApiClient.AlreadyCheckedInCode, null));
    }

    [Theory]
    [InlineData("今天已签到，请明天再来")]   // 含「已签」
    [InlineData("已领取成功，明天再来")]       // 含「已领」「明天再来」
    [InlineData("请勿重复签到")]             // 含「重复」
    [InlineData("You have already checked in")] // 含「already」
    public void IsAlreadyClaimed_消息含关键词_判已签到(string message)
    {
        Assert.True(CodeBuddyCreditsApiClient.IsAlreadyClaimed(null, message));
    }

    [Fact]
    public void IsAlreadyClaimed_其它错误码与无关消息_判未签到()
    {
        Assert.False(CodeBuddyCreditsApiClient.IsAlreadyClaimed(500, "服务器开小差"));
    }

    [Fact]
    public void IsAlreadyClaimed_消息为null且码非10001_判未签到()
    {
        Assert.False(CodeBuddyCreditsApiClient.IsAlreadyClaimed(null, null));
    }

    // ---------------- GetBalanceAsync ----------------

    [Fact]
    public async Task GetBalanceAsync_成功响应_解析余额并保留原始Json()
    {
        var client = MakeClient(HttpStatusCode.OK,
            """{"code":0,"msg":"ok","data":{"Packages":[{"CycleRemainCapacity":12.25},{"CycleRemainCapacity":7.75}]}}""");

        var result = await client.GetBalanceAsync("tok");

        Assert.Equal(20m, result.Balance);
        Assert.Contains("CycleRemainCapacity", result.RawJson);
    }

    [Fact]
    public async Task GetBalanceAsync_余额接口失败_抛出HttpRequestException()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError, """{"code":500,"msg":"boom"}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetBalanceAsync("tok"));
        Assert.Contains("500", ex.Message);
    }

    // ---------------- ClaimDailyAsync ----------------

    [Fact]
    public async Task ClaimDailyAsync_首次领取成功_返回Success且带发放量()
    {
        var client = MakeClient(HttpStatusCode.OK, """{"code":0,"msg":"ok","data":{"credit":5}}""");

        var result = await client.ClaimDailyAsync("tok");

        Assert.True(result.Success);
        Assert.False(result.AlreadyClaimed);
        Assert.Equal(5m, result.Granted);
    }

    [Fact]
    public async Task ClaimDailyAsync_今日已签到_返回AlreadyClaimed且不抛()
    {
        var client = MakeClient(HttpStatusCode.BadRequest,
            """{"code":10001,"msg":"今天已签到，请明天再来"}""");

        var result = await client.ClaimDailyAsync("tok");

        Assert.False(result.Success);
        Assert.True(result.AlreadyClaimed);
        Assert.Contains("明天再来", result.Message);
    }

    [Fact]
    public async Task ClaimDailyAsync_服务端其它错误_抛出HttpRequestException并携带状态码()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError, """{"code":500,"msg":"boom"}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ClaimDailyAsync("tok"));
        Assert.Contains("500", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task ClaimDailyAsync_请求携带Bearer鉴权头与浏览器UA()
    {
        var handler = new StubHandler((req, _) => Response(200, """{"code":0,"msg":"ok","data":{"credit":1}}"""));
        var config = new CreditsClaimConfig { ApiBase = "https://example.test", ClaimPath = "/claim" };
        var client = new CodeBuddyCreditsApiClient(config, new HttpClient(handler));

        await client.ClaimDailyAsync("my-token");

        var req = Assert.Single(handler.Requests);
        Assert.Equal("Bearer my-token", req.Headers.Authorization?.ToString());
        Assert.Contains(CodeBuddyCreditsApiClient.BrowserUserAgent,
            req.Headers.UserAgent.ToString());
    }
}
