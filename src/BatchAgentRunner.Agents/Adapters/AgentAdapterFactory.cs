using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Agents.Adapters;

/// <summary>适配层工厂：按配置选择 WorkBuddy / CodeBuddy / 仿真适配器。</summary>
public static class AgentAdapterFactory
{
    /// <summary>按 AgentKind 创建对应适配器，并注入日志、凭据注册表与运行日志。</summary>
    public static IAgentCliAdapter Create(RunOptions options, Action<string>? log = null,
        ICredentialLocationRegistry? registry = null, IRunLogger? logger = null)
    {
        var settings = new CliAdapterSettings
        {
            ExecutablePath = options.CliPath,
            ArgumentsTemplate = options.CliArgumentsTemplate,
            ReasoningEffort = options.ReasoningEffort,
            ShowWindow = options.ShowCliWindow,
            Log = log,
            Logger = logger
        };

        return options.AgentKind switch
        {
            AgentKind.CodeBuddy => new CodeBuddyAdapter(settings, options.AutoAnswer, registry),
            AgentKind.Mock => new MockAdapter(),
            _ => new WorkBuddyAdapter(settings, options.AutoAnswer, registry)
        };
    }
}
