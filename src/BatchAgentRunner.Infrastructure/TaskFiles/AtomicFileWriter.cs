namespace BatchAgentRunner.Infrastructure.TaskFiles;

/// <summary>
/// 原子写入：写同目录临时文件 → 刷新落盘 → 原子替换。
/// 保证强杀/断电不损坏任务文件（PRD 第 8 章可靠性）。
/// </summary>
public static class AtomicFileWriter
{
    public static async Task WriteAsync(string targetPath, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{targetPath}.tmp-{Guid.NewGuid():N}";
        var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        try
        {
            var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            try
            {
                await writer.WriteAsync(content).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                // CA2007：await using 声明无法挂 ConfigureAwait，改显式 DisposeAsync
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }
}
