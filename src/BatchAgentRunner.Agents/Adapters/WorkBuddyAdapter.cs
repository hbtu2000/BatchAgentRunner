using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Agents.Internal;

namespace BatchAgentRunner.Agents.Adapters;

/// <summary>
/// WorkBuddy CLI 适配器。2026-10-15 实测（V-1/V-2 部分校准）：
/// WorkBuddy 品牌无独立可执行文件，CLI 即 codebuddy（npm @tencent-ai/codebuddy-code），
/// 故默认可执行名与参数模板同 codebuddy。
/// </summary>
public sealed class WorkBuddyAdapter : CliAdapterBase
{
    public WorkBuddyAdapter(CliAdapterSettings? settings = null, AutoAnswerPolicy? policy = null)
        : base(settings ?? new CliAdapterSettings(), policy)
    {
    }

    public override AgentKind Kind => AgentKind.WorkBuddy;

    protected override string DefaultExecutable => "codebuddy";

    protected override string DefaultArgumentsTemplate => CliCommandBuilder.WorkBuddyTemplate;
}
