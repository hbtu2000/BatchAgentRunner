using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Desktop.Services;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// P-05：模板库页「预览 / 生成」展开逻辑收口（从 <see cref="TemplateEditorViewModel"/> 抽离的 Coordinator）。
/// 负责把界面变量输入写回模板副本、解析工作空间、调用 <see cref="TemplateExpander"/> 展开、写入产物文件；
/// VM 仅保留状态持有与绑定转发，不持有展开/落盘细节。
/// </summary>
public sealed class TemplateExpansionCoordinator
{
    /// <summary>变量多值输入分隔符（CA1861：常量数组提为 static readonly）。</summary>
    private static readonly char[] VarValueSeparators = { ',', '\n', ';' };

    private readonly IEngineHost _host;

    public TemplateExpansionCoordinator(IEngineHost host) => _host = host;

    /// <summary>把界面变量输入写回模板副本（不改动仓库里的模板定义）。</summary>
    public static TemplateSpec BuildSpec(TemplateSpec baseSpec, IReadOnlyList<TemplateVarInputViewModel> varInputs)
    {
        var spec = baseSpec.Clone();
        foreach (var input in varInputs)
        {
            var def = spec.Vars.FirstOrDefault(v => string.Equals(v.Name, input.Name, StringComparison.Ordinal));
            if (def is null)
            {
                continue;
            }

            def.Default = input.Value;
            def.Values = input.ValuesText
                .Split(VarValueSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return spec;
    }

    /// <summary>
    /// 把界面填写的「工作空间」（命名空间名或目录路径）解析为变量文件 glob / CSV 的实际目录根。
    /// 命中命名工作空间则用其 Path；否则按目录路径处理（相对路径就地展开为绝对路径）；
    /// 为空时退回当前目录（仅用于文件解析，任务级 workspace 仍按 <see cref="NormalizeWorkspaceForTask"/> 处理）。
    /// </summary>
    public string? ResolveWorkspaceRoot(string? ws)
    {
        if (string.IsNullOrWhiteSpace(ws))
        {
            return Directory.GetCurrentDirectory();
        }

        var named = _host.Options.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, ws, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return named.Path;
        }

        return Path.IsPathRooted(ws) ? ws : Path.GetFullPath(ws);
    }

    /// <summary>
    /// 规整为可直接落盘到任务的工作空间值：命中命名工作空间则存其 Name（便于随配置漂移重新解析），
    /// 否则存绝对目录路径（保证任务可直接运行）；空 = 不写，运行时由引擎套用默认 / 全局默认工作空间。
    /// </summary>
    public string? NormalizeWorkspaceForTask(string? ws)
    {
        if (string.IsNullOrWhiteSpace(ws))
        {
            return null;
        }

        var named = _host.Options.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, ws, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return named.Name;
        }

        return Path.IsPathRooted(ws) ? ws : Path.GetFullPath(ws);
    }

    /// <summary>把界面变量写回模板副本，并基于当前工作空间展开为任务列表（不落盘）。</summary>
    public List<TaskItem> ExpandFromInputs(
        TemplateSpec baseSpec,
        IReadOnlyList<TemplateVarInputViewModel> varInputs,
        string workspace)
    {
        var spec = BuildSpec(baseSpec, varInputs);
        return TemplateExpander.Expand(
            spec,
            ResolveWorkspaceRoot(workspace),
            NormalizeWorkspaceForTask(workspace));
    }

    /// <summary>把展开得到的任务列表序列化为 JSON 并原子写入输出路径。</summary>
    public static async System.Threading.Tasks.Task WriteTasksAsync(
        IReadOnlyList<TaskItem> tasks,
        string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(outputPath, JsonDefaults.Serialize(tasks)).ConfigureAwait(false);
    }
}
