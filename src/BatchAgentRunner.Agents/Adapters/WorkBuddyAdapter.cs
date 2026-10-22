using BatchAgentRunner.Agents.Internal;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Agents.Adapters;

/// <summary>
/// WorkBuddy CLI 适配器。2026-10-15 实测（V-1/V-2 部分校准）：
/// WorkBuddy 品牌无独立可执行文件，CLI 即 codebuddy（npm @tencent-ai/codebuddy-code），
/// 故默认可执行名与参数模板同 codebuddy。
/// </summary>
public sealed class WorkBuddyAdapter : CliAdapterBase
{
    /// <summary>构造 WorkBuddy 适配器；未传设置时复用 codebuddy 的路径与参数模板。</summary>
    public WorkBuddyAdapter(CliAdapterSettings? settings = null, AutoAnswerPolicy? policy = null,
        ICredentialLocationRegistry? registry = null)
        : base(settings ?? new CliAdapterSettings(), policy, registry: registry)
    {
    }

    /// <summary>固定为 WorkBuddy 类型。</summary>
    public override AgentKind Kind => AgentKind.WorkBuddy;

    /// <summary>默认可执行名同为 codebuddy（WorkBuddy 无独立 CLI）。</summary>
    protected override string DefaultExecutable => "codebuddy";

    /// <summary>WorkBuddy 模板：--print 打印模式 + stream-json 输出，专家改走提示词注入。</summary>
    protected override string DefaultArgumentsTemplate => CliCommandBuilder.WorkBuddyTemplate;
}
