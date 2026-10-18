using System.Text;
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

    /// <summary>是否已通过 stream_event 实时透出过增量正文；为 true 后完整 assistant 正文不再重复输出。</summary>
    private bool _sawPartialText;

    /// <summary>增量正文合并缓冲：连续 stream_event 的 delta.text 先累积，按换行或阈值刷成连贯行，降低事件量（避免逐字成行压垮日志/UI）。</summary>
    private readonly StringBuilder _pending = new();

    /// <summary>增量合并阈值（字符）：缓冲达到该长度即刷出一条行。</summary>
    private const int CoalesceThreshold = 200;

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
            // 非 JSON（stderr 错误/告警、启动横幅等）→ 先刷出增量缓冲再原样透传
            var miss = new List<string>();
            FlushPending(miss);
            miss.Add(line);
            return miss;
        }

        try
        {
            if (!doc.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                var miss = new List<string>();
                FlushPending(miss);
                miss.Add(line);
                return miss;
            }

            var outputs = new List<string>();
            var type = typeEl.GetString();
            // 非 stream_event（回合边界/完整事件）到达时，先把累积的增量缓冲刷出，避免末尾正文滞留
            if (type != "stream_event")
            {
                FlushPending(outputs);
            }

            switch (type)
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
                case "stream_event":
                    EmitStreamEvent(doc, outputs);
                    break;
                // system / error 等未知类型：尽量抽取可读文本兜底，避免实时流静默
                default:
                    TryEmitAnyText(doc, outputs);
                    break;
            }

            return outputs;
        }
        catch
        {
            return new[] { line };
        }
    }

    private void EmitAssistant(JsonElement doc, List<string> outputs)
    {
        if (!doc.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            var t = content.GetString();
            // 已通过 stream_event 实时透出过增量正文时，跳过完整正文避免重复
            if (!string.IsNullOrEmpty(t) && !_sawPartialText)
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
                    if (t.Length > 0 && !_sawPartialText)
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

    private void EmitStreamEvent(JsonElement doc, List<string> outputs)
    {
        // --include-partial-messages 下，模型增量以 {"type":"stream_event","event":{...}} 实时推送。
        // 抽取文本增量（text_delta），并入合并缓冲，按换行或阈值刷成连贯行；
        // 并标记已见增量，避免随后完整 assistant 正文重复输出。
        if (!doc.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
        {
            FlushPending(outputs);
            return;
        }

        string? text = null;
        if (ev.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
        {
            text = txt.GetString();
        }

        if (string.IsNullOrEmpty(text))
        {
            // 非文本增量（如 input_json_delta / message_start）：不并入正文缓冲，
            // 先刷出已累积部分（避免前文滞留），再做兜底透出
            FlushPending(outputs);
            TryEmitAnyText(doc, outputs);
            return;
        }

        _sawPartialText = true;
        _pending.Append(text);

        var pendingStr = _pending.ToString();
        var nl = pendingStr.LastIndexOf('\n');
        if (nl >= 0)
        {
            // 遇到换行：刷出整行，余下部分继续累积
            outputs.Add(pendingStr.Substring(0, nl).TrimEnd('\r'));
            _pending.Clear();
            if (nl + 1 < pendingStr.Length)
            {
                // CA1846：StringBuilder.Append 有 ReadOnlySpan<char> 重载，免一次中间字符串分配
                _pending.Append(pendingStr.AsSpan(nl + 1));
            }
        }
        else if (_pending.Length >= CoalesceThreshold)
        {
            outputs.Add(pendingStr);
            _pending.Clear();
        }
    }

    private static void TryEmitAnyText(JsonElement doc, List<string> outputs)
    {
        // 兜底：CLI 协议演进/字段变动时，从未知类型事件中抽取短文本，避免实时流静默。
        foreach (var key in new[] { "text", "content", "message" })
        {
            if (doc.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()!;
                if (s.Length > 0 && s.Length <= 500)
                {
                    outputs.Add(s);
                    return;
                }
            }
        }

        foreach (var key in new[] { "event", "delta", "data" })
        {
            if (doc.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object)
            {
                foreach (var k2 in new[] { "text", "content", "message" })
                {
                    if (v.TryGetProperty(k2, out var v2) && v2.ValueKind == JsonValueKind.String)
                    {
                        var s = v2.GetString()!;
                        if (s.Length > 0 && s.Length <= 500)
                        {
                            outputs.Add(s);
                            return;
                        }
                    }
                }
            }
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

    /// <summary>
    /// 把增量合并缓冲中尚未刷出的部分作为一条行输出（在完整事件到达或任务结束时调用，避免末尾正文滞留）。
    /// </summary>
    private void FlushPending(List<string> outputs)
    {
        if (_pending.Length == 0)
        {
            return;
        }

        outputs.Add(_pending.ToString());
        _pending.Clear();
    }

    /// <summary>
    /// 任务结束时刷出残留增量（适配层在进程退出后调用），返回需转发的可读行。
    /// </summary>
    public IReadOnlyList<string> Flush()
    {
        var outputs = new List<string>();
        FlushPending(outputs);
        return outputs;
    }
}
