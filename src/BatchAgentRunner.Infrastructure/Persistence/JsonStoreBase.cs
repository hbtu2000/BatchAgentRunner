using System.Text.Json;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Infrastructure.Persistence;

/// <summary>
/// 单文件 JSON 仓储的公共骨架（P2-31）。
/// 统一此前三份 <c>Json*Store</c> 逐行同构的四段样板：
/// ① 目录解析（<c>null</c> → <see cref="AppPaths.Root"/>）并建目录；
/// ② <see cref="FilePath"/> 由「目录 + 文件名」拼出；
/// ③ <see cref="LoadAsync"/> 的两条回落：文件缺失 → <see cref="CreateEmpty"/>、
///    反序列化失败（<see cref="JsonException"/>）→ <see cref="CreateEmpty"/>；
/// ④ <see cref="SaveAsync"/> 一律走 <see cref="AtomicFileWriter"/> 原子写。
/// </summary>
/// <typeparam name="T">落盘的文档类型。</typeparam>
/// <remarks>
/// 派生类只需提供「空文档工厂」<see cref="CreateEmpty"/>；加载后仍需自愈/迁移的
/// （如冷却记录超长 raw 截断、运行配置字段迁移）覆写 <see cref="NormalizeAsync"/> 即可，
/// 不得自行复制 <c>Directory.CreateDirectory</c> / <c>JsonSerializer</c> / <c>AtomicFileWriter</c> 样板。
/// </remarks>
public abstract class JsonStoreBase<T>
{
    protected JsonStoreBase(string? directory, string fileName)
    {
        DataDirectory = directory ?? AppPaths.Root;
        Directory.CreateDirectory(DataDirectory);
        FilePath = Path.Combine(DataDirectory, fileName);
    }

    /// <summary>文档所在目录（构造时解析，含默认回落）。</summary>
    public string DataDirectory { get; }

    /// <summary>文档完整路径。</summary>
    public string FilePath { get; }

    public async Task<T> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return CreateEmpty();
        }

        try
        {
            var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            var value = JsonDefaults.Deserialize<T>(json);
            return value is null ? CreateEmpty() : await NormalizeAsync(value, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return CreateEmpty();
        }
    }

    public Task SaveAsync(T value, CancellationToken cancellationToken = default) =>
        AtomicFileWriter.WriteAsync(FilePath, JsonDefaults.Serialize(value), cancellationToken);

    /// <summary>文件缺失或内容为空时的文档（如空账本、出厂示例配置）。</summary>
    protected abstract T CreateEmpty();

    /// <summary>加载后的自愈 / 迁移钩子；默认原样返回。</summary>
    protected virtual ValueTask<T> NormalizeAsync(T value, CancellationToken cancellationToken) => new(value);
}
