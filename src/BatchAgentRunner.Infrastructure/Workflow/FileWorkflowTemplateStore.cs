using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;

namespace BatchAgentRunner.Infrastructure.Workflow;

/// <summary>
/// 工作流/任务模板仓库（二期场景模板库 S1–S16）。
/// 内置模板首次访问时自动播种到 LocalApplicationData；用户自定义模板存于 user 子目录。
/// </summary>
public sealed class FileWorkflowTemplateStore : IWorkflowTemplateStore
{
    private readonly string _builtinDir;
    private readonly string _userDir;

    public FileWorkflowTemplateStore(string? baseDir = null)
    {
        var root = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchAgentRunner", "templates");
        _builtinDir = Path.Combine(root, "builtin");
        _userDir = Path.Combine(root, "user");
    }

    public async Task<IReadOnlyList<TemplateSpec>> ListAsync(CancellationToken cancellationToken = default)
    {
        await SeedIfNeededAsync(cancellationToken).ConfigureAwait(false);
        // 用户目录优先：同名 id 以用户模板覆盖内置模板，列表中只出现一次
        var byId = new Dictionary<string, TemplateSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in LoadFrom(_builtinDir))
        {
            byId[t.Id] = t;
        }

        foreach (var t in LoadFrom(_userDir))
        {
            byId[t.Id] = t;
        }

        return byId.Values.ToList();
    }

    public async Task<TemplateSpec?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await SeedIfNeededAsync(cancellationToken).ConfigureAwait(false);
        // 用户目录优先：用户保存同名模板即覆盖内置模板（覆盖语义）
        return LoadFrom(_userDir).FirstOrDefault(t =>
            string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? LoadFrom(_builtinDir).FirstOrDefault(t =>
                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public Task SaveUserTemplateAsync(TemplateSpec spec, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_userDir);
        var path = Path.Combine(_userDir, $"{spec.Id}.json");
        File.WriteAllText(path, JsonDefaults.Serialize(spec));
        return Task.CompletedTask;
    }

    private static List<TemplateSpec> LoadFrom(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return new List<TemplateSpec>();
        }

        var list = new List<TemplateSpec>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var spec = JsonDefaults.Deserialize<TemplateSpec>(File.ReadAllText(file));
                if (spec is not null)
                {
                    list.Add(spec);
                }
            }
            catch
            {
                // 跳过损坏的模板文件
            }
        }

        return list;
    }

    private async Task SeedIfNeededAsync(CancellationToken cancellationToken)
    {
        if (Directory.Exists(_builtinDir) && Directory.EnumerateFiles(_builtinDir, "*.json").Any())
        {
            return;
        }

        Directory.CreateDirectory(_builtinDir);
        foreach (var t in BuiltinTemplates.All())
        {
            var path = Path.Combine(_builtinDir, $"{t.Id}.json");
            await File.WriteAllTextAsync(path, JsonDefaults.Serialize(t), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>内置 S1–S16 模板定义（结构化的阶段序列 + 门禁 + 默认参数）。</summary>
    internal static class BuiltinTemplates
    {
        public static List<TemplateSpec> All() => new()
        {
            S1(), S2(), S3(), S4(), S5(), S6(), S7(), S8(),
            S9(), S10(), S11(), S12(), S13(), S14(), S15(), S16()
        };

        private static WorkflowStage Stage(string id, string name, string tmpl, QualityGate? gate = null, int? maxLoops = null) =>
            new() { Id = id, Name = name, InstructionTemplate = tmpl, Gate = gate, MaxLoops = maxLoops };

        private static QualityGate Review(string checklist, string? expert = null) =>
            new() { Kind = GateKind.Review, ReviewChecklist = checklist, ReviewExpertId = expert };

        private static QualityGate Command(string command, string? expectedExit = "0", string? regex = null) =>
            new() { Kind = GateKind.Command, Command = command, ExpectedExitCode = expectedExit, OutputMatchRegex = regex };

        // S1 需求文档生成与审查
        private static TemplateSpec S1() => new()
        {
            Id = "S1", Name = "需求文档生成与审查",
            Vars = Vars(("topic", "待梳理的需求主题")),
            Workflow = new WorkflowSpec
            {
                Name = "需求文档生成与审查", DefaultMaxLoops = 3,
                Stages = new List<WorkflowStage>
                {
                    Stage("gen", "生成需求文档", "根据主题「{var:topic}」生成结构化的需求文档（背景/目标/范围/功能点/验收标准）。"),
                    Stage("review1", "审查需求", "审查需求文档是否完整、可测、范围清晰。", Review("需求完整性/可测性/范围清晰"), maxLoops: 3),
                    Stage("fix", "修订需求", "根据审查遗留问题修订需求文档：\n{output:review1}", null),
                    Stage("review2", "再审需求", "复审修订后的需求文档。", Review("是否解决上轮遗留问题"), maxLoops: 3)
                }
            }
        };

        // S2 设计文档生成与审查
        private static TemplateSpec S2() => new()
        {
            Id = "S2", Name = "设计文档生成与审查",
            Vars = Vars(("requirement", "需求文档内容或路径")),
            Workflow = new WorkflowSpec
            {
                Name = "设计文档生成与审查", DefaultMaxLoops = 3,
                Stages = new List<WorkflowStage>
                {
                    Stage("gen", "生成设计文档", "基于「{var:requirement}」生成设计文档（架构/模块/接口/数据模型/可行性/成本）。"),
                    Stage("review", "审查设计", "审查设计文档的架构合理性、可行性与成本。", Review("架构/可行性/成本"), maxLoops: 3),
                    Stage("fix", "修订设计", "根据审查遗留问题修订设计文档：\n{output:review}", null),
                    Stage("review2", "再审设计", "复审修订后的设计文档。", Review("是否解决上轮遗留问题"), maxLoops: 3)
                }
            }
        };

        // S3 自动排期与逐任务推进
        private static TemplateSpec S3() => new()
        {
            Id = "S3", Name = "自动排期与逐任务推进",
            Vars = Vars(("requirement", "需求文档内容")),
            Workflow = new WorkflowSpec
            {
                Name = "自动排期与逐任务推进", DefaultMaxLoops = 1,
                Stages = new List<WorkflowStage>
                {
                    Stage("plan", "生成排期(WBS)", "根据「{var:requirement}」拆解为带依赖的任务清单（WBS），输出任务 id 与依赖关系。"),
                    Stage("execute", "逐任务推进", "按排期逐个执行任务（每任务独立会话）：\n{output:plan}", null)
                }
            }
        };

        // S4 批量代码审查与分批修复
        private static TemplateSpec S4() => new()
        {
            Id = "S4", Name = "批量代码审查与分批修复",
            Vars = Vars(("scope", "待审查的模块/目录")),
            DefaultBatchSize = 5,
            Workflow = new WorkflowSpec
            {
                Name = "批量代码审查与分批修复", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("review", "分批审查", "审查「{var:scope}」代码，输出问题清单（含严重度）。"),
                    Stage("triage", "问题分级", "将问题按严重度分级：\n{output:review}", null),
                    Stage("fix", "分批修复", "按高优先级修复问题：\n{output:triage}", null),
                    Stage("verify", "复审", "重新检查修复是否引入问题。", Command("dotnet build"), maxLoops: 2)
                }
            }
        };

        // S5 批量 E2E 测试与修复
        private static TemplateSpec S5() => new()
        {
            Id = "S5", Name = "批量E2E测试与修复",
            Vars = Vars(("scope", "E2E 测试目录")),
            DefaultBatchSize = 5,
            Workflow = new WorkflowSpec
            {
                Name = "批量E2E测试与修复", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("run", "分批跑E2E", "编写/执行「{var:scope}」的 E2E 用例。"),
                    Stage("summarize", "汇总失败", "汇总失败用例：\n{output:run}", null),
                    Stage("fix", "定位修复", "定位并修复失败用例：\n{output:summarize}", null),
                    Stage("rerun", "重跑失败", "仅重跑失败用例验证修复。", Command("dotnet test --filter Failed"), maxLoops: 2)
                }
            }
        };

        // S6 批量可用性测试与修复
        private static TemplateSpec S6() => new()
        {
            Id = "S6", Name = "批量可用性测试与修复",
            Vars = Vars(("scope", "待走查的页面/功能")),
            DefaultBatchSize = 5,
            Workflow = new WorkflowSpec
            {
                Name = "批量可用性测试与修复", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("audit", "分批走查", "对「{var:scope}」做可用性走查（WCAG/交互/文案）。"),
                    Stage("list", "问题清单", "输出可用性问题清单：\n{output:audit}", null),
                    Stage("fix", "修复", "修复可用性问题：\n{output:list}", null),
                    Stage("recheck", "复检", "复检修复是否通过。", Review("WCAG/交互/文案"), maxLoops: 2)
                }
            }
        };

        // S7 批量 Bug 修复流水线
        private static TemplateSpec S7() => new()
        {
            Id = "S7", Name = "批量Bug修复流水线",
            Vars = Vars(("bugs", "Bug 清单（每行一个）")),
            DefaultBatchSize = 5,
            Workflow = new WorkflowSpec
            {
                Name = "批量Bug修复流水线", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("locate", "逐个定位", "独立会话逐个定位 Bug：\n{var:bugs}"),
                    Stage("fix", "修复", "修复定位到的问题。", null),
                    Stage("verify", "验证", "验证修复是否生效。", Command("dotnet test"), maxLoops: 2)
                }
            }
        };

        // S8 单元测试批量补齐
        private static TemplateSpec S8() => new()
        {
            Id = "S8", Name = "单元测试批量补齐",
            Vars = Vars(("scope", "低覆盖率模块")),
            DefaultBatchSize = 5,
            Workflow = new WorkflowSpec
            {
                Name = "单元测试批量补齐", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("scan", "扫描低覆盖", "扫描「{var:scope}」的覆盖率缺口。"),
                    Stage("gen", "分批生成用例", "为缺口模块生成单元测试：\n{output:scan}", null),
                    Stage("run", "运行测试", "运行生成的测试。", Command("dotnet test"), maxLoops: 2),
                    Stage("fix", "修复失败", "修复失败用例：\n{output:run}", null)
                }
            }
        };

        // S9 依赖升级流水线
        private static TemplateSpec S9() => new()
        {
            Id = "S9", Name = "依赖升级流水线",
            Vars = Vars(("deps", "待升级依赖清单")),
            DefaultBatchSize = 3,
            Workflow = new WorkflowSpec
            {
                Name = "依赖升级流水线", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("plan", "依赖清单", "列出「{var:deps}」的升级计划。"),
                    Stage("upgrade", "分批升级", "分批升级依赖：\n{output:plan}", null),
                    Stage("build", "编译", "升级后编译验证。", Command("dotnet build"), maxLoops: 2),
                    Stage("test", "测试", "运行测试套件。", Command("dotnet test"), maxLoops: 2)
                }
            }
        };

        // S10 代码规范批量整改
        private static TemplateSpec S10() => new()
        {
            Id = "S10", Name = "代码规范批量整改",
            Vars = Vars(("scope", "待整改代码范围")),
            DefaultBatchSize = 5,
            Workflow = new WorkflowSpec
            {
                Name = "代码规范批量整改", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("scan", "lint扫描", "扫描「{var:scope}」的规范问题：\n{output:scan}", null),
                    Stage("triage", "问题分级", "对规范问题分级。", null),
                    Stage("fix", "分批自动修复", "自动修复可修复的规范问题：\n{output:triage}", null),
                    Stage("rescan", "复扫", "重新扫描确认清零。", Command("dotnet format --verify-no-changes"), maxLoops: 2)
                }
            }
        };

        // S11 安全漏洞批量修复
        private static TemplateSpec S11() => new()
        {
            Id = "S11", Name = "安全漏洞批量修复",
            Vars = Vars(("report", "扫描报告路径/内容")),
            DefaultBatchSize = 3,
            Workflow = new WorkflowSpec
            {
                Name = "安全漏洞批量修复", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("scan", "漏洞分级", "对「{var:report}」的漏洞按严重度分级。"),
                    Stage("fix", "分批修复", "分批修复高危漏洞：\n{output:scan}", null),
                    Stage("rescan", "复扫", "复扫确认高危清零。", Command("dotnet build"), maxLoops: 2),
                    Stage("regress", "回归", "回归测试。", Command("dotnet test"), maxLoops: 2)
                }
            }
        };

        // S12 性能问题批量优化
        private static TemplateSpec S12() => new()
        {
            Id = "S12", Name = "性能问题批量优化",
            Vars = Vars(("report", "profiling 报告")),
            DefaultBatchSize = 3,
            Workflow = new WorkflowSpec
            {
                Name = "性能问题批量优化", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("profile", "热点分级", "对「{var:report}」的性能热点分级。"),
                    Stage("optimize", "分批优化", "分批优化热点：\n{output:profile}", null),
                    Stage("benchmark", "基准对比", "运行基准测试对比优化前后。", Command("dotnet run --configuration Release -- --benchmark"), maxLoops: 2)
                }
            }
        };

        // S13 代码注释/文档批量补全
        private static TemplateSpec S13() => new()
        {
            Id = "S13", Name = "代码注释/文档批量补全",
            Vars = Vars(("scope", "待补全模块")),
            DefaultBatchSize = 5,
            Workflow = new WorkflowSpec
            {
                Name = "代码注释/文档批量补全", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("scan", "扫描缺失", "扫描「{var:scope}」缺注释/缺文档的模块。"),
                    Stage("fill", "分批补全", "补全 JSDoc/README/API 文档：\n{output:scan}", null),
                    Stage("review", "抽查复审", "抽查补全质量是否达标。", Review("准确性/完整性"), maxLoops: 2)
                }
            }
        };

        // S14 多语言翻译与校对
        private static TemplateSpec S14() => new()
        {
            Id = "S14", Name = "多语言翻译与校对",
            Vars = Vars(("content", "待翻译内容"), ("target", "目标语言")),
            DefaultBatchSize = 5,
            Workflow = new WorkflowSpec
            {
                Name = "多语言翻译与校对", DefaultMaxLoops = 3,
                Stages = new List<WorkflowStage>
                {
                    Stage("translate", "分批翻译", "将「{var:content}」翻译为「{var:target}」。"),
                    Stage("proof", "审校", "审校术语一致性：\n{output:translate}", Review("术语一致/通顺"), maxLoops: 3),
                    Stage("revise", "修订", "根据审校修订译文：\n{output:proof}", null),
                    Stage("proof2", "再审校", "再审校修订后译文。", Review("术语一致/通顺"), maxLoops: 3)
                }
            }
        };

        // S15 发布流水线
        private static TemplateSpec S15() => new()
        {
            Id = "S15", Name = "发布流水线",
            Vars = Vars(("version", "待发布版本号")),
            Workflow = new WorkflowSpec
            {
                Name = "发布流水线", DefaultMaxLoops = 1,
                Stages = new List<WorkflowStage>
                {
                    Stage("bump", "版本号更新", "将版本号更新为「{var:version}」。"),
                    Stage("changelog", "changelog", "生成 changelog。", null),
                    Stage("notes", "发布说明", "生成发布说明：\n{output:changelog}", null),
                    Stage("checklist", "回归清单", "生成回归清单：\n{output:notes}", null),
                    Stage("release", "发布检查", "执行发布前检查（构建 + 关键测试）。", Command("dotnet build"), maxLoops: 1)
                }
            }
        };

        // S16 UI 走查与视觉回归
        private static TemplateSpec S16() => new()
        {
            Id = "S16", Name = "UI走查与视觉回归",
            Vars = Vars(("scope", "待走查页面")),
            DefaultBatchSize = 5,
            Workflow = new WorkflowSpec
            {
                Name = "UI走查与视觉回归", DefaultMaxLoops = 2,
                Stages = new List<WorkflowStage>
                {
                    Stage("capture", "分批截图", "对「{var:scope}」截图对比。"),
                    Stage("diff", "差异分级", "对截图差异分级：\n{output:capture}", null),
                    Stage("fix", "修复", "修复视觉差异：\n{output:diff}", null),
                    Stage("recheck", "复检", "复检是否通过。", Review("视觉一致性"), maxLoops: 2)
                }
            }
        };

        private static List<TemplateVarDef> Vars(params (string Name, string Default)[] items) =>
            items.Select(i => new TemplateVarDef { Name = i.Name, Default = i.Default, Source = VarSource.Literal }).ToList();
    }
}
