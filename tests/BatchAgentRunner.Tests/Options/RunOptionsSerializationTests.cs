using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.Persistence;
using Xunit;

namespace BatchAgentRunner.Tests.Options;

/// <summary>
/// 配置序列化回归测试。
/// 重点覆盖：带免费时段（含星期过滤）的模型配置必须能被完整读回——
/// 历史缺陷是 <c>FreeTimeWindow.Days</c> 声明为 <c>IReadOnlySet&lt;&gt;</c>，
/// System.Text.Json 无法实例化接口类型，导致任何配了 days 的配置文件加载即抛异常。
/// </summary>
public class RunOptionsSerializationTests
{
    [Fact]
    public void 默认示例配置_可完整往返且不丢失时段与星期()
    {
        var sample = JsonConfigStore.CreateSample();
        var json = JsonDefaults.Serialize(sample);

        var back = JsonDefaults.Deserialize<RunOptions>(json);

        Assert.NotNull(back);
        Assert.Equal(sample.TaskFilePath, back.TaskFilePath);
        Assert.Equal(sample.AgentKind, back.AgentKind);
        Assert.Equal(sample.DegradationStrategy, back.DegradationStrategy);
        Assert.Equal(sample.Accounts.Count, back.Accounts.Count);
        Assert.Equal(sample.ModelCandidates.Count, back.ModelCandidates.Count);

        var window = back.ModelCandidates[1].FreeTimeWindow;
        Assert.NotNull(window);
        Assert.Equal(new TimeOnly(22, 0), window.Start);
        Assert.Equal(new TimeOnly(6, 0), window.End);
        Assert.True(window.IsCrossMidnight);
        Assert.NotNull(window.Days);
        Assert.Equal(5, window.Days.Count);
        Assert.Contains(DayOfWeek.Monday, window.Days);
        Assert.DoesNotContain(DayOfWeek.Saturday, window.Days);

        // 全天模型不带时段
        Assert.Null(back.ModelCandidates[0].FreeTimeWindow);
    }

    [Fact]
    public void 配置文件允许注释与尾随逗号()
    {
        const string annotated = """
        {
          // 任务文件
          "task_file_path": "E:/tasks.json",
          "agent_kind": "Mock",
          "model_candidates": [
            {
              "name": "model-x",
              "free_time_window": {
                "start": "08:00:00",
                "end": "12:00:00",
                "days": ["Monday", "Friday"],
              },
            },
          ],
        }
        """;

        var options = JsonDefaults.Deserialize<RunOptions>(annotated);

        Assert.NotNull(options);
        Assert.Equal("E:/tasks.json", options.TaskFilePath);
        Assert.Equal(AgentKind.Mock, options.AgentKind);
        var window = options.ModelCandidates[0].FreeTimeWindow;
        Assert.NotNull(window);
        Assert.Equal(new TimeOnly(8, 0), window.Start);
        Assert.Equal(2, window.Days!.Count);
    }

    [Fact]
    public void 派生属性不写入配置文件()
    {
        var json = JsonDefaults.Serialize(JsonConfigStore.CreateSample());

        // 这些是由分钟字段派生出来的便捷属性，写进配置只会让人困惑
        Assert.DoesNotContain("\"TaskTimeout\"", json);
        Assert.DoesNotContain("\"MaxTaskDuration\"", json);
        Assert.DoesNotContain("\"MaxWait\"", json);
        Assert.DoesNotContain("\"DefaultCooldown\"", json);
        Assert.DoesNotContain("\"IsFullDay\"", json);
        Assert.DoesNotContain("\"IsCrossMidnight\"", json);
    }

    [Fact]
    public void 仓库内注释模板可被解析()
    {
        // samples/config.example.json 是交付给用户的全字段模板，必须与代码同步且可加载
        var path = Path.Combine(FindRepoRoot(), "samples", "config.example.json");
        Assert.True(File.Exists(path), $"未找到模板文件：{path}");

        var options = JsonDefaults.Deserialize<RunOptions>(File.ReadAllText(path));

        Assert.NotNull(options);
        // v1.12：候选/兜底模型池挂在账号下；全局 model_candidates 为兼容字段（模板中为空）
        Assert.NotEmpty(options.Accounts);
        var acc1 = options.Accounts.Single(a => a.Id == "acc-1");
        Assert.NotEmpty(acc1.ModelCandidates!);
        Assert.NotEmpty(acc1.FallbackModels!);
        var acc2 = options.Accounts.Single(a => a.Id == "acc-2");
        Assert.NotEmpty(acc2.ModelCandidates!);
        Assert.Empty(options.ModelCandidates);
        Assert.NotEmpty(options.AutoAnswer.DangerousBlacklist);
        Assert.NotEmpty(options.AutoAnswer.AllowedWhitelist);
        Assert.Equal(10, options.TaskTimeoutMinutes);
        Assert.Equal(60, options.DefaultCooldownMinutes);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir?.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}