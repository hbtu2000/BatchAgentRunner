using BatchAgentRunner.Agents.Internal;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Agents.Adapters;

/// <summary>
/// CodeBuddy CLI 适配器。参数形态与错误特征待 M0 V-1/V-2/V-3 实测后校准。
/// </summary>
public sealed class CodeBuddyAdapter : CliAdapterBase
{
    public CodeBuddyAdapter(CliAdapterSettings? settings = null, AutoAnswerPolicy? policy = null,
        ICredentialLocationRegistry? registry = null)
        : base(settings ?? new CliAdapterSettings(), policy, registry: registry)
    {
    }

    public override AgentKind Kind => AgentKind.CodeBuddy;

    protected override string DefaultExecutable => "codebuddy";

    protected override string DefaultArgumentsTemplate => CliCommandBuilder.CodeBuddyTemplate;
}
