using System.Text.Json;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>
/// 把 CodeBuddy CLI 的 <c>--output-format stream-json</c> 输出（逐行 JSON 事件）翻译成可读文本行，
/// 供应用内实时输出流展示；同时抽取最终 answer（<c>result</c> 事件）供结果回写。
/// 设计原则：解析失败的任意行原样透传，保证错误/告警等非 JSON 文本不丢失。
/// 解析异常一律降级为透传，绝不让渲染器炸掉主流程。
/// </summary>
public sealed class StreamJsonRenderer
{
    /// <summary>最终回答文本（来自 <c>result</c> 事件；为 null 表示尚未收到或解析失败）。</summary>
    public string? FinalResult { get; private set; }

    /// <summary>
    /// 最终 <c>result</c> 事件是否带 <c>is_error=true</c>（CLI 自报执行失败，
    /// 如 429 限流/额度耗尽）。供适配层作为 exit 0 时的失败兜底信号。
    /// </summary>
    public bool FinalIsError { get; private set; }

    /// <summary>喂入一行原始输出（CLI 的 stdout/stderr 单行），返回要转发的可读行。</summary>
    public IReadOnlyList<string> Feed(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
        {
            return Array.Empty<string>();
        }

        JsonElement doc;
        try
        {
            using var d = JsonDocument.Parse(line);
            doc = d.RootElement.Clone();
        }
        catch (JsonException)
        {
            // 非 JSON（stderr 错误/告警、启动横幅等）→ 原样透传
            return new[] { line };
        }

        try
        {
            if (!doc.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                return new[] { line };
            }

            var outputs = new List<string>();
            switch (typeEl.GetString())
            {
                case "assistant":
                    EmitAssistant(doc, outputs);
                    break;
                case "user":
                    EmitUser(doc, outputs);
                    break;
                case "result":
                    EmitResult(doc, outputs);
                    break;
                // system / stream_event / error 等：不翻译，避免噪声淹没进度
                default:
                    break;
            }

            return outputs;
        }
        catch
        {
            return new[] { line };
        }
    }

    private static void EmitAssistant(JsonElement doc, List<string> outputs)
    {
        if (!doc.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            var t = content.GetString();
            if (!string.IsNullOrEmpty(t))
            {
                outputs.Add(t!);
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt) || bt.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var btype = bt.GetString();
            if (btype == "text")
            {
                if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                {
                    var t = txt.GetString()!;
                    if (t.Length > 0)
                    {
                        outputs.Add(t);
                    }
                }
            }
            else if (btype == "tool_use")
            {
                var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()!
                    : "tool";
                var summary = SummarizeToolInput(block);
                outputs.Add($"[工具调用] {name}{(summary.Length > 0 ? "：" + summary : string.Empty)}");
            }
        }
    }

    private static void EmitUser(JsonElement doc, List<string> outputs)
    {
        // 工具结果：只报状态，不展开大段内容
        if (doc.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var bt)
                    && bt.ValueKind == JsonValueKind.String
                    && bt.GetString() == "tool_result")
                {
                    var err = block.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    outputs.Add(err ? "[工具结果] 失败" : "[工具结果] 完成");
                }
            }
        }
    }

    private void EmitResult(JsonElement doc, List<string> outputs)
    {
        var isError = doc.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        FinalIsError = isError;

        if (doc.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
        {
            FinalResult = r.GetString();
        }

        if (isError)
        {
            outputs.Add($"[结果] 失败：{FinalResult ?? "(无详情)"}");
        }
        else if (!string.IsNullOrEmpty(FinalResult))
        {
            outputs.Add($"[结果] {FinalResult}");
        }

        if (doc.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
        {
            outputs.Add($"[费用] ${cost.GetDouble():F4}");
        }
    }

    private static string SummarizeToolInput(JsonElement toolUse)
    {
        if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        // 优先取常见「一句话描述」字段
        foreach (var key in new[] { "command", "file_path", "description", "prompt", "path" })
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()!;
                return s.Length <= 120 ? s : s[..120] + "…";
            }
        }

        var json = input.GetRawText();
        return json.Length <= 120 ? json : json[..120] + "…";
    }
}
