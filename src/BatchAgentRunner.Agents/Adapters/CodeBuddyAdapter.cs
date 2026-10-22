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
    /// <summary>构造 CodeBuddy 适配器；未传设置时使用默认 CLI 路径与参数模板。</summary>
    public CodeBuddyAdapter(CliAdapterSettings? settings = null, AutoAnswerPolicy? policy = null,
        ICredentialLocationRegistry? registry = null)
        : base(settings ?? new CliAdapterSettings(), policy, registry: registry)
    {
    }

    /// <summary>固定为 CodeBuddy 类型。</summary>
    public override AgentKind Kind => AgentKind.CodeBuddy;

    /// <summary>默认可执行文件名 codebuddy，供 PATH 查找使用。</summary>
    protected override string DefaultExecutable => "codebuddy";

    /// <summary>CodeBuddy 模板：-p 打印模式 + stream-json 输出 + 逐条增量消息。</summary>
    protected override string DefaultArgumentsTemplate => CliCommandBuilder.CodeBuddyTemplate;
}
