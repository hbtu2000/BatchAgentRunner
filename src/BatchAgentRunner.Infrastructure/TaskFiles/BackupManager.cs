using System.Globalization;

namespace BatchAgentRunner.Infrastructure.TaskFiles;

/// <summary>启动前自动备份任务文件，保留最近 N 份（REQ-006）。</summary>
public static class BackupManager
{
    public static string? Backup(string path, int retention)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupPath = $"{path}.backup-{stamp}";
        File.Copy(path, backupPath, overwrite: true);
        Prune(path, retention);
        return backupPath;
    }

    /// <summary>保留最近 retention 份，删除更旧的备份。</summary>
    public static int Prune(string path, int retention)
    {
        if (retention <= 0)
        {
            return 0;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var pattern = fileName + ".backup-";
        var backups = Directory.GetFiles(directory, pattern + "*")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToList();

        var removed = 0;
        foreach (var old in backups.Skip(retention))
        {
            try
            {
                File.Delete(old);
                removed++;
            }
            catch
            {
                // 忽略删除失败
            }
        }

        return removed;
    }
}
