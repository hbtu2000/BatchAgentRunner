using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Infrastructure.Persistence;

/// <summary>运行配置持久化。安全约定：账号仅存标识 + 凭据引用，不明文保存凭据。</summary>
public sealed class JsonConfigStore : IConfigStore
{
    private readonly string _filePath;

    public JsonConfigStore(string? directory = null)
    {
        ConfigDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchAgentRunner");
        Directory.CreateDirectory(ConfigDirectory);
        _filePath = Path.Combine(ConfigDirectory, "config.json");
    }

    public string ConfigDirectory { get; }

    public async Task<RunOptions> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return CreateSample();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RunOptions>(json, JsonDefaults.Options) ?? CreateSample();
        }
        catch (JsonException)
        {
            return CreateSample();
        }
    }

    public async Task SaveAsync(RunOptions options, CancellationToken cancellationToken = default)
    {
        await AtomicFileWriter.WriteAsync(_filePath, JsonDefaults.Serialize(options), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 首次启动的默认示例配置（便于用户直接改）。
    /// 设计原则：<b>没有任何一项留空到「看不懂」</b>——能用默认值的都给出可直接跑起来的值，
    /// 需要用户替换的（任务文件路径 / CLI 路径 / 凭据引用 / 模型标识）也在 note 里写明改什么。
    /// 字段逐项说明见 <c>docs/CONFIG-配置项说明.md</c>。
    /// </summary>
    public static RunOptions CreateSample() => new()
    {
        // ① 任务文件与执行工具：任务文件必须由用户选择，此处留空并由预检拦截提示
        TaskFilePath = string.Empty,
        AgentKind = AgentKind.WorkBuddy,
        CliPath = null,
        CliArgumentsTemplate = null,

        // ② 工作空间：默认落在程序当前目录，等价于不配置时的行为，但让用户看得见、可改
        Workspaces = new List<WorkspaceConfig>
        {
            new()
            {
                Name = "默认工作区",
                Path = Environment.CurrentDirectory,
                IsGlobalDefault = true
            }
        },
        DefaultWorkspace = null,

        // ③ 账号池：凭据只存引用（PRD 第 8 章），明文一律不落盘。
        //    v1.12：候选模型池与兜底模型池都挂在账号下（accounts[].model_candidates / fallback_models）
        Accounts = new List<AccountConfig>
        {
            new()
            {
                Id = "acc-1",
                DisplayName = "账号 1",
                CredentialReference = "profile:default",
                Enabled = true,
                ModelCandidates = new List<ModelCandidate>
                {
                    new()
                    {
                        Name = "model-a",
                        FreeTimeWindow = null,
                        EstimatedUnitPrice = null,
                        Discount = 0.65m,
                        Note = "该账号自有候选池的第一项（首选模型）。全天可用，折扣 0.65x；把 name 换成真实模型标识"
                    },
                    new()
                    {
                        Name = "model-b",
                        FreeTimeWindow = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0),
                            new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }),
                        EstimatedUnitPrice = null,
                        Note = "仅工作日 22:00 → 次日 06:00 免费；其余时段会被「时段外跳过」（不计失败、不告警）"
                    },
                    new()
                    {
                        Name = "hy3",
                        FreeTimeWindow = null,
                        EstimatedUnitPrice = null,
                        Discount = 0.65m,
                        Note = "示例：hy3 带 0.65x 折扣（点「拉取该账号可用模型」后下拉可见 0.65x）。"
                    },
                    new()
                    {
                        Name = "hy4",
                        FreeTimeWindow = null,
                        EstimatedUnitPrice = null,
                        Discount = 1.20m,
                        Note = "示例：hy4 带 1.20x 折扣（高于原价，仅展示、不参与调度判定）。"
                    }
                },
                FallbackModels = new List<FallbackModel>
                {
                    new()
                    {
                        Name = "paid-model",
                        EstimatedUnitPrice = 0.5m,
                        Note = "该账号自有的兜底模型（不参与免费时段过滤）。兜底默认关闭，启用需打开 fallback.enabled"
                    }
                },
                Note = "凭据引用：改成你实际的 CLI 登录 profile / 环境变量名（形如 profile:xxx、env:XXX）"
            },
            new()
            {
                Id = "acc-2",
                DisplayName = "账号 2（示例，默认停用）",
                CredentialReference = "profile:backup",
                Enabled = false,
                Note = "演示账号池的多账号降级：启用后账号 1 的自有模型池轮完会自动切到这里。" +
                       "未配置自有 model_candidates，将回退使用下面的全局共享列表"
            }
        },

        // 全局共享候选模型列表（兼容字段）：仅对未配置自有候选池的账号（如示例中的 acc-2）生效
        ModelCandidates = new List<ModelCandidate>
        {
            new()
            {
                Name = "model-a",
                FreeTimeWindow = null,
                EstimatedUnitPrice = null,
                Discount = 0.65m,
                Note = "全局共享列表（仅对未配置自有池的账号生效）。全天可用，折扣 0.65x；把 name 换成真实模型标识"
            },
            new()
            {
                Name = "model-b",
                FreeTimeWindow = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0),
                    new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }),
                EstimatedUnitPrice = null,
                Note = "仅工作日 22:00 → 次日 06:00 免费；其余时段会被「时段外跳过」（不计失败、不告警）"
            },
            new()
            {
                Name = "hy3",
                FreeTimeWindow = null,
                EstimatedUnitPrice = null,
                Discount = 0.65m,
                Note = "示例：hy3 带 0.65x 折扣（共享模式下点「拉取可用模型」后下拉可见）。"
            },
            new()
            {
                Name = "hy4",
                FreeTimeWindow = null,
                EstimatedUnitPrice = null,
                Discount = 1.20m,
                Note = "示例：hy4 带 1.20x 折扣。"
            }
        },
        DegradationStrategy = DegradationStrategy.ModelFirst,

        // ④ 专家：未指定时用 CLI 默认智能体
        Expert = ExpertRef.Default(),

        // ⑤ 兜底：默认关闭，打开前请先填模型名与成本上限
        Fallback = new FallbackConfig(),

        // ⑥⑦⑧⑨⑩ 全部沿用 RunOptions 的内置默认值（重试 2 / 限流退避 3 / 超时 10 分钟 等）
        AutoAnswer = AutoAnswerPolicy.CreateDefault()
    };
}
