namespace BatchAgentRunner.Core.Options;

/// <summary>
/// 一批任务的执行配置（PRD 第 9 章配置页 12 个配置域）。
/// 默认值全部对齐 PRD 明示值；标注「假设」的需真实任务校准（PRD 第 16 章）。
/// </summary>
/// <remarks>
/// <para><b>P-09 God Config 收口</b>：本类型是用户配置的产品契约（ARCH §2.2 不动清单），不能靠嵌套分组改变
/// <c>config.json</c> 形状，故以 <c>partial class</c> <b>按配置域横向切片</b>——类型面、属性名、
/// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/> 键名与落盘字节全部不变，
/// 每个配置域独占一个文件（`RunOptions.NN-*.cs`），新增配置落到所属域而不再堆进单一大类。</para>
/// <para><b>切片顺序即 <c>config.json</c> 落盘属性顺序</b>（`System.Text.Json` 按成员声明顺序序列化，
/// 而 partial 成员的声明顺序 = 文件声明顺序 = 文件名升序）：故文件名以两位序号固定，
/// 调整或新增切片须同步 <c>RunOptionsContractTests</c> 的顺序契约。</para>
/// </remarks>
public sealed partial class RunOptions
{
}
