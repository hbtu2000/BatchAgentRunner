using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// P1-15 高危盲区：<see cref="BackupManager"/> 是启动前自动备份任务文件的唯一路径
/// （REQ-006）。备份失败 = 用户原任务文件无回滚点，且剪枝逻辑若算错会误删较新的备份，
/// 必须经断言锁死：① 源不存在直接短路返回 null、② 备份生成带时间戳副本且原文件保留、
/// ③ 剪枝保留最近 N 份、删除更旧的、④ retention≤0 与目录不存在时安全退避。
/// 使用临时目录，不碰真实任务文件。
/// </summary>
public class BackupManagerTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(),
        $"bar-backup-{Guid.NewGuid():N}");

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Backup_源文件不存在_返回null()
    {
        var dir = TempDir();
        try
        {
            var missing = Path.Combine(dir, "nope.json");

            var result = BackupManager.Backup(missing, retention: 5);

            Assert.Null(result);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Backup_源文件存在_生成带时间戳备份且原文件内容不变()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var source = Path.Combine(dir, "tasks.json");
            File.WriteAllText(source, "original-content");

            var backup = BackupManager.Backup(source, retention: 5);

            Assert.NotNull(backup);
            Assert.True(File.Exists(backup));
            Assert.Contains(".backup-", backup);
            Assert.Equal("original-content", File.ReadAllText(source));
            Assert.Equal("original-content", File.ReadAllText(backup));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Prune_retention为零_不删除任何备份()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var basePath = Path.Combine(dir, "tasks.json");
            for (var i = 1; i <= 3; i++)
            {
                File.WriteAllText($"{basePath}.backup-2026010{i:0}000000", "x");
            }

            var removed = BackupManager.Prune(basePath, retention: 0);

            Assert.Equal(0, removed);
            Assert.Equal(3, Directory.GetFiles(dir, "tasks.json.backup-*").Length);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Prune_保留最近N份并删除更旧的()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var basePath = Path.Combine(dir, "tasks.json");
            // 5 份，时间戳递增（文件名 ord 序即时间序）
            var stamps = new[] { "20260101000001", "20260101000002", "20260101000003",
                "20260101000004", "20260101000005" };
            foreach (var s in stamps)
            {
                File.WriteAllText($"{basePath}.backup-{s}", "x");
            }

            var removed = BackupManager.Prune(basePath, retention: 2);

            Assert.Equal(3, removed);
            var survivors = Directory.GetFiles(dir, "tasks.json.backup-*")
                .Select(Path.GetFileName).OrderBy(f => f).ToArray();
            // 保留最新的两份（时间戳最大）
            Assert.Equal(new[] { "tasks.json.backup-20260101000004",
                "tasks.json.backup-20260101000005" }, survivors);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Prune_目录不存在_返回0且不抛()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            $"bar-backup-missing-{Guid.NewGuid():N}");
        var basePath = Path.Combine(dir, "tasks.json");

        var removed = BackupManager.Prune(basePath, retention: 3);

        Assert.Equal(0, removed);
    }

    [Fact]
    public void Backup_超过retention_触发剪枝保留最新N份()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var source = Path.Combine(dir, "tasks.json");
            // 用不同时刻的“伪备份”预置 4 份（时间戳升序），再执行一次真实 Backup
            var stamps = new[] { "20260101000001", "20260101000002", "20260101000003",
                "20260101000004" };
            foreach (var s in stamps)
            {
                File.WriteAllText($"{source}.backup-{s}", "x");
            }

            File.WriteAllText(source, "fresh");
            BackupManager.Backup(source, retention: 3);

            // 真实备份时间戳最新，应保留它 + 既有最新 2 份 = 3 份
            var all = Directory.GetFiles(dir, "tasks.json.backup-*").Length;
            Assert.Equal(3, all);
            Assert.True(File.Exists(source));
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
