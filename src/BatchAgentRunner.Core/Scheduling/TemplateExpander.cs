using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 模板展开器（REQ-016：任务模板 / 变量批量生成任务清单）。
/// 支持三类变量来源：Literal（笛卡尔积）、FileList（按文件清单每文件一任务）、CsvColumn（CSV 每行一任务）。
/// 展开产物与手写任务字节级等价（不引入专属运行时格式）。可链式生成 depends_on。
/// </summary>
public sealed class TemplateExpander
{
    public static List<TaskItem> Expand(TemplateSpec template, string? baseDir = null)
    {
        var instances = BuildInstances(template, baseDir);
        var tasks = new List<TaskItem>();

        for (var i = 0; i < instances.Count; i++)
        {
            var vars = instances[i];
            var id = $"{template.Id}-{i + 1:d3}";

            var ctx = new WorkflowRenderContext();
            foreach (var (k, v) in vars)
            {
                ctx.SetVar(k, v);
            }

            var instruction = string.IsNullOrWhiteSpace(template.InstructionTemplate)
                ? string.Empty
                : StageTemplateRenderer.Render(template.InstructionTemplate, ctx).Text;
            ctx.SetTaskField("instruction", instruction);

            var task = new TaskItem
            {
                Id = id,
                Instruction = instruction,
                TemplateVars = new TemplateVars
                {
                    TemplateId = template.Id,
                    Values = new Dictionary<string, string>(vars)
                }
            };

            if (template.Workflow is not null)
            {
                task.Workflow = RenderWorkflow(template.Workflow, ctx);
            }

            tasks.Add(task);
        }

        if (template.ChainDependency && tasks.Count > 1)
        {
            for (var i = 1; i < tasks.Count; i++)
            {
                tasks[i].DependsOn = new List<string> { tasks[i - 1].Id };
            }
        }

        return tasks;
    }

    private static WorkflowSpec RenderWorkflow(WorkflowSpec source, WorkflowRenderContext ctx)
    {
        var clone = source.Clone();
        foreach (var stage in clone.Stages)
        {
            if (!string.IsNullOrWhiteSpace(stage.InstructionTemplate))
            {
                stage.InstructionTemplate = StageTemplateRenderer.Render(stage.InstructionTemplate, ctx).Text;
            }
        }

        return clone;
    }

    private static List<Dictionary<string, string>> BuildInstances(TemplateSpec template, string? baseDir)
    {
        // 1) CSV 模式：任一变量为 CsvColumn 且 FileGlob 指向 csv
        var csvVar = template.Vars.FirstOrDefault(v => v.Source == VarSource.CsvColumn);
        if (csvVar is not null && template.FileGlob is { Count: > 0 })
        {
            var csvPath = template.FileGlob.FirstOrDefault(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            if (csvPath is not null)
            {
                return ExpandFromCsv(template, ResolvePath(csvPath, baseDir));
            }
        }

        // 2) 文件清单模式
        if (template.FileGlob is { Count: > 0 })
        {
            var files = new List<string>();
            foreach (var glob in template.FileGlob)
            {
                files.AddRange(GlobFiles(ResolvePath(glob, baseDir)));
            }

            if (files.Count > 0)
            {
                var literal = LiteralCartesian(template);
                var instances = new List<Dictionary<string, string>>();
                foreach (var file in files)
                {
                    foreach (var lit in literal)
                    {
                        var dict = new Dictionary<string, string>(lit, StringComparer.OrdinalIgnoreCase)
                        {
                            ["file"] = file
                        };
                        instances.Add(dict);
                    }
                }

                return instances;
            }
        }

        // 3) 纯字面量笛卡尔积
        return LiteralCartesian(template);
    }

    private static List<Dictionary<string, string>> ExpandFromCsv(TemplateSpec template, string csvPath)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!File.Exists(csvPath))
        {
            return rows;
        }

        var lines = File.ReadAllLines(csvPath);
        if (lines.Length == 0)
        {
            return rows;
        }

        var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var cells = lines[i].Split(',').Select(c => c.Trim()).ToArray();
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Length && c < cells.Length; c++)
            {
                dict[headers[c]] = cells[c];
            }

            // 填入模板中未在 CSV 出现的字面量变量默认值
            foreach (var v in template.Vars.Where(v => v.Source == VarSource.Literal))
            {
                dict.TryAdd(v.Name, v.Default ?? string.Empty);
            }

            rows.Add(dict);
        }

        return rows;
    }

    private static List<Dictionary<string, string>> LiteralCartesian(TemplateSpec template)
    {
        var result = new List<Dictionary<string, string>> { new(StringComparer.OrdinalIgnoreCase) };
        foreach (var varDef in template.Vars.Where(v => v.Source == VarSource.Literal))
        {
            var values = (varDef.Values is { Count: > 0 } ? varDef.Values : new List<string> { varDef.Default ?? string.Empty });
            var next = new List<Dictionary<string, string>>();
            foreach (var combo in result)
            {
                foreach (var val in values)
                {
                    var copy = new Dictionary<string, string>(combo, StringComparer.OrdinalIgnoreCase)
                    {
                        [varDef.Name] = val
                    };
                    next.Add(copy);
                }
            }

            result = next;
        }

        if (result.Count == 0)
        {
            result.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    private static string ResolvePath(string path, string? baseDir)
    {
        if (Path.IsPathRooted(path) || baseDir is null)
        {
            return path;
        }

        return Path.Combine(baseDir, path);
    }

    private static IEnumerable<string> GlobFiles(string pattern)
    {
        try
        {
            if (Directory.Exists(pattern))
            {
                return Directory.EnumerateFiles(pattern, "*", SearchOption.AllDirectories);
            }

            var dir = Path.GetDirectoryName(pattern) ?? ".";
            var name = Path.GetFileName(pattern);
            if (Directory.Exists(dir))
            {
                return Directory.EnumerateFiles(dir, string.IsNullOrWhiteSpace(name) ? "*" : name,
                    SearchOption.TopDirectoryOnly);
            }
        }
        catch
        {
            // 忽略非法路径
        }

        return Enumerable.Empty<string>();
    }
}
