using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// 引擎工厂契约（阶段 2 P1-4/P1-5）。
/// </summary>
/// <remarks>
/// 将「引擎需要哪些依赖、如何装配」从 UI 宿主中抽离。组合根（Desktop 的 <c>EngineFactory</c>）实现本接口，
/// 集中 <c>new</c> 全部 Infrastructure/Agents 具体类型，使 UI 层只依赖 Core 抽象，
/// 解除「UI 直引 Infrastructure/Agents 具体类型」的架构倒置（§4.3 架构断言）。
/// </remarks>
public interface IEngineFactory
{
    /// <summary>
    /// 装配一个 <see cref="IBatchEngine"/> 实例。适配器 / 日志 / 通知分发器 / Diff 采集器 / Git / Markdown 回写等
    /// 具体实现由实现方在内部构造，调用方只传抽象依赖。
    /// </summary>
    /// <param name="options">运行配置（必填）。</param>
    /// <param name="logger">运行日志（必填，由实现方或调用方提供的 <see cref="IRunLogger"/>）。</param>
    /// <param name="credentialVault">凭证 Vault；null = 不启用凭证切换。</param>
    /// <param name="raiseToast">桌面 toast 回调（标题, 消息），用于桌面通知通道。</param>
    IBatchEngine CreateEngine(
        RunOptions options,
        IRunLogger logger,
        ICredentialVault? credentialVault,
        Action<string, string> raiseToast);
}
