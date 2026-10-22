namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// 文件系统探测端口（P1-10）：引擎不再直接调用 <see cref="File"/> / <see cref="Directory"/>
/// 静态 API，全部经此抽象，使「任务文件是否存在 / 工作空间目录是否存在 / 兜底工作目录」
/// 可被测试替身替换，且与 <see cref="SystemClock"/> 一样保留 Core 内 BCL 默认实现。
/// </summary>
public interface IFileSystem
{
    /// <summary>文件是否存在。</summary>
    bool FileExists(string path);

    /// <summary>目录是否存在。</summary>
    bool DirectoryExists(string path);

    /// <summary>当前工作目录（工作空间解析失败时的兜底）。</summary>
    string CurrentDirectory { get; }
}

/// <summary>
/// 默认实现：直接委派 BCL（真实运行环境）。与 <see cref="IClock"/> 的 <c>SystemClock</c> 同模式：
/// 端口与默认适配器同驻 Core，生产经 <see cref="IEngineFactory"/> 组合，测试注入假实现。
/// </summary>
public sealed class SystemFileSystem : IFileSystem
{
    /// <summary>文件是否存在（直接委派 BCL）。</summary>
    /// <param name="path">待探测的文件路径（相对或绝对）。</param>
    public bool FileExists(string path) => File.Exists(path);

    /// <summary>目录是否存在（直接委派 BCL）。</summary>
    /// <param name="path">待探测的目录路径（相对或绝对）。</param>
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <summary>当前工作目录（直接委派 BCL）。</summary>
    public string CurrentDirectory => Directory.GetCurrentDirectory();
}
