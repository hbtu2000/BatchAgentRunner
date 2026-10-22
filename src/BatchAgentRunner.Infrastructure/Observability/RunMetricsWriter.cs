using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Infrastructure.Observability;

/// <summary>
/// 运行级指标落盘（ARCH P-10 / A-8）：<c>metrics-&lt;run_id&gt;.json</c>，与汇总报告同目录。
/// </summary>
/// <remarks>
/// 与 <see cref="Reporting.ReportWriter"/> 的分工：<b>报告回答"这批任务做得怎么样"（给用户），
/// 指标回答"这批运行跑得怎么样"（给运维）</b>——降级次数 / 兜底触发 / 冷却与时段跳过 /
/// 失败率 / 误切换率 / 吞吐 / 平均耗时，全部是可跨批次比较的趋势量。
/// 因此二者刻意分离落盘：报告按批次时间命名、内容随需求演进；指标按 <c>run_id</c> 命名、
/// 字段集由 <c>ObservabilityHygieneTests</c> 锁定，便于长期采集与横向对比。
/// 落盘一律 <see cref="AtomicFileWriter"/>（P0-8）：排障时不会读到半截 JSON。
/// </remarks>
public sealed class RunMetricsWriter : IMetricsWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // snake_case：与 config/sidecar/报告三套对外契约的命名口径一致（P-09 命名契约同源）
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>将运行指标按 run_id 原子写入 JSON，返回文件路径。</summary>
    public async Task<string> WriteAsync(RunMetricsSnapshot snapshot, string taskFilePath,
        CancellationToken cancellationToken = default)
    {
        var directory = string.IsNullOrWhiteSpace(taskFilePath)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(taskFilePath)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"metrics-{snapshot.RunId}.json");
        await AtomicFileWriter.WriteAsync(path, JsonSerializer.Serialize(snapshot, Options), cancellationToken)
            .ConfigureAwait(false);
        return path;
    }
}
