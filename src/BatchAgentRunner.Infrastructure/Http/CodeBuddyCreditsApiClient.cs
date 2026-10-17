using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Infrastructure.Http;

/// <summary>
/// CodeBuddy 控制台积分 API 客户端（每日积分领取）。
/// 契约 2026-10-16 已抓包实测确认（Bearer accessToken 鉴权，统一信封 code/msg/data/requestId）：
/// - 余额：POST /billing/meter/get-user-resource-summary {} → data.Packages[].CycleRemainCapacity 求和
/// - 领取：POST /billing/meter/daily-checkin {} → 200 + code:0 + data.credit（本次发放量）；
///   今日已领取 → HTTP 400 + code:10001「今天已签到，请明天再来」
/// 重要：余额接口有 WAF 校验——不带浏览器 User-Agent 的请求会被 403（code:10085「请求不合法」）
/// 拦截（实测），故必须携带浏览器 UA。
/// </summary>
public sealed class CodeBuddyCreditsApiClient : ICreditsApiClient
{
    /// <summary>服务端「今日已签到」错误码（实测值）。</summary>
    public const int AlreadyCheckedInCode = 10001;

    private static readonly string[] AlreadyKeywords = ["已签", "已领", "重复", "明天再来", "already", "checked in"];

    /// <summary>WAF 要求浏览器形态的 User-Agent（缺失时余额接口 403 code:10085，实测）。</summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private readonly CreditsClaimConfig _config;
    private readonly HttpClient _client;

    public CodeBuddyCreditsApiClient(CreditsClaimConfig config, HttpClient? client = null)
    {
        _config = config;
        _client = client ?? new HttpClient();
        _client.Timeout = TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        }
    }

    public async Task<CreditsBalanceResult> GetBalanceAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        var (json, _) = await PostAsync(_config.BalancePath, accessToken, expectSuccess: true, cancellationToken)
            .ConfigureAwait(false);
        return new CreditsBalanceResult(ParseBalance(json), json);
    }

    public async Task<CreditsClaimApiResult> ClaimDailyAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        // 400（今日已签到）不是异常路径，需要读 body 区分，故不在 Send 阶段抛错。
        var (json, status) = await PostAsync(_config.ClaimPath, accessToken, expectSuccess: false, cancellationToken)
            .ConfigureAwait(false);

        int? code = null;
        string? message = null;
        decimal? granted = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
                {
                    code = c.GetInt32();
                }

                if (root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    message = m.GetString();
                }

                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("credit", out var credit) &&
                        credit.ValueKind == JsonValueKind.Number && credit.TryGetDecimal(out var g))
                    {
                        granted = g;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应 → 仅按状态码判断
        }

        if (status == HttpStatusCode.OK && code == 0)
        {
            return new CreditsClaimApiResult(Success: true, AlreadyClaimed: false,
                BalanceAfter: null, Granted: granted, Message: message);
        }

        if (IsAlreadyClaimed(code, message))
        {
            return new CreditsClaimApiResult(Success: false, AlreadyClaimed: true,
                BalanceAfter: null, Granted: null, Message: message ?? "今日已签到，请明天再来");
        }

        throw new HttpRequestException(
            $"积分领取接口返回 {(int)status}（code={code?.ToString() ?? "?"}）：{message ?? Truncate(json)}",
            null, status);
    }

    /// <summary>是否「今日已签到」（实测 code=10001；同时兼容消息关键词，防服务端换码）。</summary>
    public static bool IsAlreadyClaimed(int? code, string? message) =>
        code == AlreadyCheckedInCode ||
        (message is not null && AlreadyKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase)));

    /// <summary>解析资源汇总响应：所有资源包剩余额度（credits）求和；结构不符时返回 null。</summary>
    public static decimal? ParseBalance(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("Packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            decimal sum = 0;
            var any = false;
            foreach (var pkg in packages.EnumerateArray())
            {
                if (pkg.ValueKind != JsonValueKind.Object ||
                    !pkg.TryGetProperty("CycleRemainCapacity", out var capacity))
                {
                    continue;
                }

                var text = capacity.ValueKind switch
                {
                    JsonValueKind.String => capacity.GetString(),
                    JsonValueKind.Number => capacity.GetRawText(),
                    _ => null
                };
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                {
                    sum += value;
                    any = true;
                }
            }

            return any ? sum : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(string Json, HttpStatusCode Status)> PostAsync(string path, string accessToken,
        bool expectSuccess, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (expectSuccess && !response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"积分接口 POST {path} 返回 {(int)response.StatusCode}：{Truncate(json)}", null, response.StatusCode);
        }

        return (json, response.StatusCode);
    }

    private Uri BuildUri(string path)
    {
        var baseUri = new Uri(_config.ApiBase.TrimEnd('/') + "/");
        return new Uri(baseUri, path.StartsWith('/') ? path : "/" + path);
    }

    private static string Truncate(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";
}
