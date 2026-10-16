using System.Text;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.CredentialVault;
using BatchAgentRunner.Infrastructure.Http;
using BatchAgentRunner.Infrastructure.Persistence;
using BatchAgentRunner.Infrastructure.Reporting;

namespace BatchAgentRunner.Tests.CredentialVault;

/// <summary>每日积分领取编排器测试（领取前后记录 / 台账去重 / 失败隔离）。</summary>
[Collection("credential-vault")]
public class CreditsClaimTests
{
    private static readonly DateTimeOffset T1 = At.Local(2026, 10, 15, 9, 0);

    // ---------------- 测试替身 ----------------

    /// <summary>脚本化积分 API：按票据独立记账（每账号各自 100 → 120），可配置失败 / 已领取。</summary>
    private sealed class ScriptedCreditsApi(decimal balance = 100m) : ICreditsApiClient
    {
        private readonly Dictionary<string, decimal> _balances = new(StringComparer.Ordinal);
        public decimal InitialBalance { get; set; } = balance;
        public bool ClaimFails { get; set; }
        public bool AlreadyClaimed { get; set; }
        public int BalanceCalls { get; private set; }
        public int ClaimCalls { get; private set; }

        public Task<CreditsBalanceResult> GetBalanceAsync(string accessToken, CancellationToken ct = default)
        {
            BalanceCalls++;
            var current = _balances.TryGetValue(accessToken, out var value) ? value : InitialBalance;
            return Task.FromResult(new CreditsBalanceResult(current, "{}"));
        }

        public Task<CreditsClaimApiResult> ClaimDailyAsync(string accessToken, CancellationToken ct = default)
        {
            ClaimCalls++;
            if (ClaimFails)
            {
                throw new HttpRequestException("积分接口 500");
            }

            if (AlreadyClaimed)
            {
                var current = _balances.TryGetValue(accessToken, out var v) ? v : InitialBalance;
                return Task.FromResult(new CreditsClaimApiResult(
                    Success: false, AlreadyClaimed: true, BalanceAfter: current,
                    Granted: null, Message: "今日已领取过"));
            }

            var balance = _balances.TryGetValue(accessToken, out var value) ? value : InitialBalance;
            balance += 20m;
            _balances[accessToken] = balance;
            return Task.FromResult(new CreditsClaimApiResult(
                Success: true, AlreadyClaimed: false, BalanceAfter: balance,
                Granted: 20m, Message: "领取成功"));
        }
    }

    /// <summary>Vault 假件：快照恒存在、切换恒成功（编排器的切换分支已在 VaultSwitchTests 覆盖）。</summary>
    private sealed class FakeVault : ICredentialVault
    {
        public List<(AgentKind Cli, string AccountId, string Reason)> Switches { get; } = new();
        public bool SnapshotMissing { get; set; }

        public string? CurrentLiveAccount(AgentKind cli) => null;
        public bool SnapshotExists(AgentKind cli, string accountId) => !SnapshotMissing;
        public CredentialSnapshotInfo? GetSnapshot(AgentKind cli, string accountId) => null;
        public Task<CredentialSnapshotInfo> BackupFromLiveAsync(AgentKind cli, string accountId,
            string? displayName = null, string? site = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SwitchToAsync(AgentKind cli, string accountId, string reason, CancellationToken ct = default)
        {
            Switches.Add((cli, accountId, reason));
            return Task.CompletedTask;
        }
        public Task MarkVerifiedAsync(AgentKind cli, string accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteSnapshotAsync(AgentKind cli, string accountId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>票据读取器假件：默认每次调用返回一个新票据（模拟逐账号切换后的不同 Live 身份）。</summary>
    private sealed class FakeTokenReader : ICredentialTokenReader
    {
        private int _counter;

        /// <summary>固定票据；null = 逐次生成新票据。</summary>
        public string? Token { get; set; }

        /// <summary>true = 模拟读不到票据（返回 null）。</summary>
        public bool FailReading { get; set; }

        public string? TryReadAccessToken(AgentKind cli)
        {
            if (FailReading)
            {
                return null;
            }

            return Token ?? "tok-" + ++_counter;
        }
    }

    private static AccountConfig Account(string id, string name = "") => new()
    {
        Id = id,
        DisplayName = name,
        CredentialReference = "profile:default",
        Enabled = true
    };

    private static (CreditsClaimOrchestrator Orchestrator, FakeVault Vault, ScriptedCreditsApi Api,
        JsonCreditsClaimStore Store) Build(CreditsClaimConfig? config = null, IRunLogger? logger = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-credits-" + Guid.NewGuid().ToString("N")[..10]);
        var vault = new FakeVault();
        var api = new ScriptedCreditsApi();
        var store = new JsonCreditsClaimStore(dir);
        var orchestrator = new CreditsClaimOrchestrator(vault, api, store,
            new FakeTokenReader(), new FakeClock(T1), logger,
            progress: null);
        return (orchestrator, vault, api, store);
    }

    // ---------------- 用例 ----------------

    [Fact]
    public async Task 领取成功_记录领取前后积分与变化量()
    {
        var (o, vault, api, store) = Build();
        var accounts = new List<AccountConfig> { Account("acc-1", "账号 1"), Account("acc-2", "账号 2") };

        var summary = await o.RunAllAsync(AgentKind.Mock, accounts);

        Assert.Equal(2, summary.ClaimedCount);
        Assert.All(summary.Records, r =>
        {
            Assert.Equal(100m, r.BeforeCredits);
            Assert.Equal(120m, r.AfterCredits);
            Assert.Equal(20m, r.DeltaCredits);
            Assert.Equal(CreditsClaimStatus.Claimed, r.Status);
        });
        // 每个账号都先切换凭证、再领取（各自独立身份）
        Assert.Equal(2, vault.Switches.Count);
        Assert.All(vault.Switches, s => Assert.Equal("credits-claim", s.Reason));
        // 台账已落库
        var ledger = await store.LoadAsync();
        Assert.Equal(2, ledger.Records.Count);
    }

    [Fact]
    public async Task 今日已领取过_状态为AlreadyClaimed且不重复发放()
    {
        var (o, _, api, _) = Build();
        api.AlreadyClaimed = true;

        var summary = await o.RunAllAsync(AgentKind.Mock, [Account("acc-1")]);

        var record = summary.Records.Single();
        Assert.Equal(CreditsClaimStatus.AlreadyClaimed, record.Status);
        Assert.Equal(0, summary.FailedCount);
    }

    [Fact]
    public async Task 台账去重_当天第二次领取直接跳过()
    {
        var (o, _, api, _) = Build();
        var accounts = new List<AccountConfig> { Account("acc-1") };

        await o.RunAllAsync(AgentKind.Mock, accounts);
        Assert.Equal(1, api.ClaimCalls);

        var second = await o.RunAllAsync(AgentKind.Mock, accounts);

        Assert.Equal(1, api.ClaimCalls); // 未再次调用领取接口
        var skipped = second.Records.Single();
        Assert.Equal(CreditsClaimStatus.Skipped, skipped.Status);
        Assert.Equal(1, second.SkippedCount);
    }

    [Fact]
    public async Task 台账去重_隔天可再次领取()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-credits-" + Guid.NewGuid().ToString("N")[..10]);
        var vault = new FakeVault();
        var api = new ScriptedCreditsApi();
        var store = new JsonCreditsClaimStore(dir);
        var clock = new FakeClock(T1);
        var o = new CreditsClaimOrchestrator(vault, api, store, new FakeTokenReader(), clock);
        var accounts = new List<AccountConfig> { Account("acc-1") };

        await o.RunAllAsync(AgentKind.Mock, accounts);

        clock.Set(T1.AddDays(1));
        var second = await o.RunAllAsync(AgentKind.Mock, accounts);

        Assert.Equal(2, api.ClaimCalls);
        Assert.Equal(CreditsClaimStatus.Claimed, second.Records.Single().Status);
    }

    [Fact]
    public async Task 强制重试_跳过去重但仍受服务端约束()
    {
        var (o, _, api, _) = Build();

        await o.RunAllAsync(AgentKind.Mock, [Account("acc-1")]);
        api.AlreadyClaimed = true; // 服务端今日不再发放
        var second = await o.RunAllAsync(AgentKind.Mock, [Account("acc-1")], force: true);

        Assert.Equal(2, api.ClaimCalls);
        Assert.Equal(CreditsClaimStatus.AlreadyClaimed, second.Records.Single().Status);
    }

    [Fact]
    public async Task 单账号失败_记录Failed且不中断其余账号()
    {
        var (o, _, api, store) = Build();
        api.ClaimFails = true;
        var accounts = new List<AccountConfig> { Account("acc-1", "会失败"), Account("acc-2", "也会失败") };

        var summary = await o.RunAllAsync(AgentKind.Mock, accounts);

        Assert.Equal(2, summary.FailedCount);
        Assert.Contains("积分接口 500", summary.Records[0].Error);
        // 失败也进台账（供排查），但去重只认成功/已领取
        var ledger = await store.LoadAsync();
        Assert.Equal(2, ledger.Records.Count);
        Assert.All(ledger.Records, r => Assert.Equal(CreditsClaimStatus.Failed, r.Status));
    }

    [Fact]
    public async Task 无快照账号_标记失败并继续处理后续账号()
    {
        var (o, vault, _, _) = Build();
        vault.SnapshotMissing = true;

        var summary = await o.RunAllAsync(AgentKind.Mock, [Account("acc-1")]);

        var record = summary.Records.Single();
        Assert.Equal(CreditsClaimStatus.Failed, record.Status);
        Assert.Contains("没有凭证快照", record.Error);
        Assert.Empty(vault.Switches);
    }

    [Fact]
    public async Task 读不到票据_标记失败()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-credits-" + Guid.NewGuid().ToString("N")[..10]);
        var o = new CreditsClaimOrchestrator(new FakeVault(), new ScriptedCreditsApi(),
            new JsonCreditsClaimStore(dir), new FakeTokenReader { FailReading = true }, new FakeClock(T1));

        var summary = await o.RunAllAsync(AgentKind.CodeBuddy, [Account("acc-1")]);

        Assert.Equal(CreditsClaimStatus.Failed, summary.Records.Single().Status);
        Assert.Contains("accessToken", summary.Records.Single().Error);
    }

    [Fact]
    public async Task 运行日志_输出领取前后积分()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-credits-" + Guid.NewGuid().ToString("N")[..10]);
        var logger = new RecordingRunLogger();
        var o = new CreditsClaimOrchestrator(new FakeVault(), new ScriptedCreditsApi(),
            new JsonCreditsClaimStore(dir), new FakeTokenReader(), new FakeClock(T1), logger);

        await o.RunAllAsync(AgentKind.Mock, [Account("acc-1", "账号 1")]);

        var line = logger.Lines.Single(l => l.Contains("积分领取"));
        Assert.Contains("账号 1", line);
        Assert.Contains("领取前 100", line);
        Assert.Contains("领取后 120", line);
        Assert.Contains("+20", line);
    }

    [Fact]
    public async Task 报告写出_逐账号含领取前后积分()
    {
        var (o, _, _, _) = Build();
        var dir = Path.Combine(Path.GetTempPath(), "bar-credits-report-" + Guid.NewGuid().ToString("N")[..10]);

        var summary = await o.RunAllAsync(AgentKind.Mock, [Account("acc-1", "账号 1")]);
        var mdPath = await CreditsClaimReportWriter.WriteAsync(summary, dir);

        var md = File.ReadAllText(mdPath);
        Assert.Contains("领取前", md);
        Assert.Contains("领取后", md);
        Assert.Contains("100", md);
        Assert.Contains("120", md);
        Assert.True(File.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(mdPath) + ".json")));
    }

    /// <summary>内存运行日志（断言用）。</summary>
    private sealed class RecordingRunLogger : IRunLogger
    {
        public List<string> Lines { get; } = new();

        public string LogFilePath => string.Empty;
        public void Write(BatchEvent ev) => Lines.Add(ev.Message ?? string.Empty);
        public void Write(string level, string message) => Lines.Add(message);
        public void WriteTaskStarted(string taskId, string? account, string? model, string? expert, string? workspace) { }
        public void WriteTaskFinished(string taskId, BatchAgentRunner.Core.Domain.TaskStatus status,
            double durationSeconds, int retryCount) { }
        public void WriteModelSwitched(string? fromAccount, string? fromModel, string? toAccount, string? toModel,
            string reason, string? taskId) { }
        public void WriteWindowSkipped(string model, DateTimeOffset? nextWindowStart) { }
        public void WriteCooldownSkipped(string account, string model, DateTimeOffset resetAt) { }
        public void WriteQuotaResetRecorded(string account, string model, DateTimeOffset resetAt, CooldownSource source) { }
        public void WriteFallbackUsed(string taskId, string model, decimal cost, CostBasis basis) { }
        public void WriteBatchWaiting(DateTimeOffset resumeAt, string? target, WaitKind kind) { }
        public void WriteBatchBlocked(string reason, int pendingCount) { }
        public void WriteBatchResumed(string reason) { }
        public void WriteBatchTerminated(int doneCount, int unfinishedCount) { }
        public void WriteDangerousOperation(string taskId, string interactionPoint) { }
        public void WriteAutoAnswered(string taskId, string interactionPoint, string answer) { }
        public void Dispose() { }
    }
}

/// <summary>accessToken 读取器测试（真实 JSON 文件解析路径）。</summary>
[Collection("credential-vault")]
public class LiveCredentialTokenReaderTests
{
    [Fact]
    public void 从嵌套auth结构中提取accessToken()
    {
        var liveRoot = Path.Combine(Path.GetTempPath(), "bar-tok-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(liveRoot);
        CredentialLocationRegistry.SetLiveRootOverride(AgentKind.Mock, liveRoot);
        var path = Path.Combine(liveRoot, ".credentials.json");
        File.WriteAllText(path,
            """{"account":{"uid":"u1"},"auth":{"accessToken":"at-abc123","refreshToken":"rt-1"},"accounts":[]}""");

        var token = new LiveCredentialTokenReader().TryReadAccessToken(AgentKind.Mock);

        Assert.Equal("at-abc123", token);
    }

    [Fact]
    public void 文件缺失或无token时返回null而不抛异常()
    {
        var liveRoot = Path.Combine(Path.GetTempPath(), "bar-tok-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(liveRoot);
        CredentialLocationRegistry.SetLiveRootOverride(AgentKind.Mock, liveRoot);
        File.WriteAllText(Path.Combine(liveRoot, ".credentials.json"), """{"refresh_token":"rt-1"}""");

        Assert.Null(new LiveCredentialTokenReader().TryReadAccessToken(AgentKind.Mock));

        File.Delete(Path.Combine(liveRoot, ".credentials.json"));
        Assert.Null(new LiveCredentialTokenReader().TryReadAccessToken(AgentKind.Mock));
    }

    [Fact]
    public void 余额解析_资源包剩余credits求和()
    {
        const string json = """
            {"code":0,"msg":"OK","requestId":"r","data":{"Packages":[
              {"PackageCode":"p1","CycleTotalCapacity":"3310","CycleRemainCapacity":"1844.52000155","CapacityUnit":"credits"},
              {"PackageCode":"p2","CycleTotalCapacity":"500","CycleRemainCapacity":"0","CapacityUnit":"credits"},
              {"PackageCode":"p3","CycleTotalCapacity":"200","CycleRemainCapacity":"200","CapacityUnit":"credits"}]}}
            """;

        Assert.Equal(2044.52000155m, CodeBuddyCreditsApiClient.ParseBalance(json));
        Assert.Null(CodeBuddyCreditsApiClient.ParseBalance("""{"code":0,"msg":"OK"}"""));
        Assert.Null(CodeBuddyCreditsApiClient.ParseBalance("not json"));
    }

    [Fact]
    public async Task 领取成功_解析发放量并携带Bearer鉴权()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler((req, _) =>
        {
            seen = req;
            return Response(200,
                """{"code":0,"msg":"OK","requestId":"r","data":{"credit":100,"streak_days":17,"is_streak_day":false}}""");
        });
        var client = new CodeBuddyCreditsApiClient(new CreditsClaimConfig(), new HttpClient(handler));

        var result = await client.ClaimDailyAsync("tok-1");

        Assert.True(result.Success);
        Assert.False(result.AlreadyClaimed);
        Assert.Equal(100m, result.Granted);
        Assert.Equal("POST", seen!.Method.Method);
        Assert.EndsWith("/billing/meter/daily-checkin", seen.RequestUri!.ToString());
        Assert.Equal("Bearer", seen.Headers.Authorization!.Scheme);
        Assert.Equal("tok-1", seen.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task 重复领取_HTTP400码10001_映射为AlreadyClaimed()
    {
        var handler = new StubHandler((req, _) => Response(400,
            """{"code":10001,"msg":"今天已签到，请明天再来","requestId":"r"}"""));
        var client = new CodeBuddyCreditsApiClient(new CreditsClaimConfig(), new HttpClient(handler));

        var result = await client.ClaimDailyAsync("tok-1");

        Assert.False(result.Success);
        Assert.True(result.AlreadyClaimed);
        Assert.Contains("已签到", result.Message);
    }

    [Fact]
    public async Task 余额查询_带Bearer且解析为资源包求和()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler((req, _) =>
        {
            seen = req;
            return Response(200,
                """{"code":0,"msg":"OK","data":{"Packages":[{"CycleRemainCapacity":"42.5","CapacityUnit":"credits"}]}}""");
        });
        var client = new CodeBuddyCreditsApiClient(new CreditsClaimConfig(), new HttpClient(handler));

        var balance = await client.GetBalanceAsync("tok-1");

        Assert.Equal(42.5m, balance.Balance);
        Assert.Equal("Bearer tok-1", seen!.Headers.Authorization!.ToString());
        // WAF 契约：余额接口必须携带浏览器 User-Agent（缺失时 403 code:10085，实测）
        Assert.Equal(CodeBuddyCreditsApiClient.BrowserUserAgent,
            seen.Headers.UserAgent!.ToString());
    }

    [Fact]
    public async Task 服务端其它错误_抛出并携带状态码与消息()
    {
        var handler = new StubHandler((req, _) => Response(500, """{"code":500,"msg":"boom"}"""));
        var client = new CodeBuddyCreditsApiClient(new CreditsClaimConfig(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ClaimDailyAsync("tok-1"));
        Assert.Contains("500", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    private static HttpResponseMessage Response(int status, string json) => new()
    {
        StatusCode = (System.Net.HttpStatusCode)status,
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>可编程 HTTP 假件：记录请求并按脚本返回响应。</summary>
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
}
