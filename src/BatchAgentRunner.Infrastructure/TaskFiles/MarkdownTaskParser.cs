using System.Globalization;
using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Infrastructure.TaskFiles;

/// <summary>
/// Markdown 任务文件解析（REQ-001 + 附录 A）：
/// 每个二级标题 <c>## &lt;task-id&gt;</c> 为一条任务；其下 <c>- instruction:</c> 行为指令起始；
/// 指令可跨多行，至下一个二级标题（或文件结尾）结束，区间内容全部合并为完整指令。
/// Markdown 为只读输入源，状态与结果走 sidecar（REQ-006）。
/// </summary>
public static class MarkdownTaskParser
{
    private static readonly Regex HeadingRegex = new(@"^##\s*(?<id>.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex InstructionRegex = new(@"^\s*[-*]\s*instruction\s*:\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<TaskItem> Parse(string content, List<TaskFileIssue> issues)
    {
        var tasks = new List<TaskItem>();
        var lines = content.Replace("\r\n", "\n").Split('\n');

        string? currentId = null;
        var instructionLines = new List<string>();
        var instructionStarted = false;
        var headingLine = 0;

        void Flush()
        {
            if (currentId is null)
            {
                return;
            }

            var instruction = string.Join("\n", instructionLines).Trim();
            tasks.Add(new TaskItem
            {
                Id = currentId,
                Instruction = instruction,
                Status = TaskStatus.Pending
            });

            if (!instructionStarted)
            {
                issues.Add(new TaskFileIssue("warning", currentId, $"任务 {currentId} 未找到 `- instruction:` 行，指令为空"));
            }

            instructionLines.Clear();
            instructionStarted = false;
            currentId = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd();
            var heading = HeadingRegex.Match(line);
            if (heading.Success)
            {
                Flush();
                headingLine = i + 1;
                var idText = heading.Groups["id"].Value.Trim();
                if (string.IsNullOrWhiteSpace(idText))
                {
                    idText = GenerateId(tasks.Count + 1);
                    issues.Add(new TaskFileIssue("info", idText,
                        $"第 {headingLine} 行二级标题缺少任务 ID，已自动生成 {idText}"));
                }

                currentId = idText;
                continue;
            }

            if (currentId is null)
            {
                continue;
            }

            var instructionMatch = InstructionRegex.Match(line);
            if (instructionMatch.Success)
            {
                instructionStarted = true;
                var text = instructionMatch.Groups["text"].Value;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    instructionLines.Add(text);
                }

                continue;
            }

            // 指令区间内（已遇到 instruction 行）的其余内容全部并入指令
            if (instructionStarted)
            {
                instructionLines.Add(raw);
            }
        }

        Flush();

        if (tasks.Count == 0)
        {
            throw new TaskFileFormatException("未找到任何任务（需要至少一个 `## <task-id>` 二级标题）");
        }

        return tasks;
    }

    public static string GenerateId(int index) => string.Create(CultureInfo.InvariantCulture, $"task-{index:000}");
}
