using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>
/// 从 CLI 当前 Live 凭证文件提取 accessToken（积分 API 鉴权用）。
/// 解析策略：递归在 JSON 中查找名为 accessToken 的字符串属性——CodeBuddy 的
/// Tencent-Cloud.coding-copilot.info 结构为 { account, auth: { accessToken, refreshToken, ... } }，
/// 但不假设嵌套层级，凭证文件结构变化时无需改这里。
/// 安全约定：只返回票据值给调用方（仅内存传递），不写日志、不落盘。
/// </summary>
public sealed class LiveCredentialTokenReader : ICredentialTokenReader
{
    private readonly ICredentialLocationRegistry _registry;

    public LiveCredentialTokenReader(ICredentialLocationRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public string? TryReadAccessToken(AgentKind cli)
    {
        CredentialLocationSpec spec;
        try
        {
            spec = _registry.Resolve(cli);
        }
        catch (CredentialVaultException)
        {
            return null;
        }

        if (!spec.IsConfigured)
        {
            return null; // 如 WorkBuddy 的登录文件位置尚未实测登记
        }

        foreach (var file in spec.Files)
        {
            var path = _registry.ResolveLiveFile(spec, file.RelativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (FindAccessToken(doc.RootElement) is { Length: > 0 } token)
                {
                    return token;
                }
            }
            catch (JsonException)
            {
                // 凭证文件非 JSON 或损坏 → 继续尝试下一个文件
            }
        }

        return null;
    }

    internal static string? FindAccessToken(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        string.Equals(prop.Name, "accessToken", StringComparison.OrdinalIgnoreCase))
                    {
                        return prop.Value.GetString();
                    }

                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                        FindAccessToken(prop.Value) is { Length: > 0 } nested)
                    {
                        return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindAccessToken(item) is { Length: > 0 } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }
}
