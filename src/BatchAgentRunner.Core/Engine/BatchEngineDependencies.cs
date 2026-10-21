using System.Collections.Generic;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// <see cref="BatchEngine"/> 的可选依赖聚合（阶段 2 P1-3「窄化构造」）。
/// </summary>
/// <remarks>
/// 构造参数曾膨胀到 16 个，违反 §4.4「构造参数个数 ≤ 7」门禁，且每抽出一个协作者（§3.4）
/// 都要再往签名上加一个转发参数——签名的增长速度等于拆分速度，外壳永远无法收敛。
/// 此处把全部可选叶依赖收敛为一个聚合对象：<b>引擎构造 = 5 个必需依赖 + 1 个可选聚合</b>。
/// <para>
/// 契约：成员为 <c>null</c> 一律表示「用默认实现」，与窄化前逐个 <c>?? new …</c> 的回退语义
/// 严格一致；聚合对象本身为 <c>null</c> 等价于「全用默认」。因此窄化是纯签名重构，行为不变。
/// </para>
/// </remarks>
public sealed class BatchEngineDependencies
{
    /// <summary>时间源；null = <see cref="SystemClock"/>。</summary>
    public IClock? Clock { get; init; }

    /// <summary>额度错误分类器；null = 新建 <see cref="QuotaErrorClassifier"/>。</summary>
    public QuotaErrorClassifier? Classifier { get; init; }

    /// <summary>凭证 Vault；null = 不启用凭证切换。</summary>
    public ICredentialVault? CredentialVault { get; init; }

    /// <summary>通知规则；null = 回落 <see cref="RunOptions.Notifications"/>。</summary>
    public IEnumerable<NotificationRule>? Notifications { get; init; }

    /// <summary>Diff 采集器；null = 不采集。</summary>
    public IDiffCollector? DiffCollector { get; init; }

    /// <summary>Git 集成；null = 不建分支/不开 PR。</summary>
    public IGitIntegration? Git { get; init; }

    /// <summary>通知分发器；null = 不分发。</summary>
    public INotificationDispatcher? NotificationDispatcher { get; init; }

    /// <summary>Markdown 原地回写器；null = 不回写。</summary>
    public IMarkdownInPlaceWriter? MarkdownWriter { get; init; }

    /// <summary>命令闸门求值器；null = 新建（进程经 <see cref="ProcessRunner"/> 端口）。</summary>
    public GateEvaluator? GateEvaluator { get; init; }

    /// <summary>进程运行器；null = <see cref="NullProcessRunner"/>（仅在未显式给闸门时使用）。</summary>
    public IProcessRunner? ProcessRunner { get; init; }

    /// <summary>文件系统端口（P1-10）；null = <see cref="SystemFileSystem"/>。</summary>
    public IFileSystem? FileSystem { get; init; }

    /// <summary>
    /// 运行级指标采集器（ARCH P-10 / A-8）；null = 引擎按自身 RunId 新建。
    /// 注入口存在的意义是让测试能直接断言埋点结果，而无需解析日志文本。
    /// </summary>
    public RunMetrics? Metrics { get; init; }
}
