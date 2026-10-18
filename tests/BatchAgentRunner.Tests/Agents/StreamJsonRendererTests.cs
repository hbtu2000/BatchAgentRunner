using BatchAgentRunner.Agents.Internal;

namespace BatchAgentRunner.Tests.Agents;

/// <summary>
/// StreamJsonRenderer 回归测试（2026-10-16：CLI 改用 --output-format stream-json 后，
/// 需把逐行 JSON 事件翻译成可读进度，并正确抽取最终回答）。
/// </summary>
public class StreamJsonRendererTests
{
    [Fact]
    public void Feed_非Json行_原样透传()
    {
        var r = new StreamJsonRenderer();
        var outLines = r.Feed("Error: authentication failed");

        Assert.Single(outLines);
        Assert.Equal("Error: authentication failed", outLines[0]);
        Assert.Null(r.FinalResult);
    }

    [Fact]
    public void Feed_assistant文本块_透出文本行()
    {
        var r = new StreamJsonRenderer();
        var json = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"开始分析文件结构。"}]}}
            """;
        var outLines = r.Feed(json);

        Assert.Contains("开始分析文件结构。", outLines);
    }

    [Fact]
    public void Feed_assistant工具调用_翻译为工具调用可读行()
    {
        var r = new StreamJsonRenderer();
        var json = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet build"}}]}}
            """;
        var outLines = r.Feed(json);

        Assert.Contains(outLines, l => l.Contains("[工具调用]") && l.Contains("Bash") && l.Contains("dotnet build"));
    }

    [Fact]
    public void Feed_user工具结果_仅报状态不展开内容()
    {
        var r = new StreamJsonRenderer();
        var json = """
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":[{"type":"text","text":"超长输出省略"}],"is_error":false}]}}
            """;
        var outLines = r.Feed(json);

        Assert.Contains("[工具结果] 完成", outLines);
        Assert.DoesNotContain("超长输出省略", string.Join("\n", outLines));
    }

    [Fact]
    public void Feed_result事件_记录最终回答并输出()
    {
        var r = new StreamJsonRenderer();
        var json = """
            {"type":"result","subtype":"success","is_error":false,"result":"治理方案已落地，全部测试通过。","total_cost_usd":0.0123}
            """;
        var outLines = r.Feed(json);

        Assert.Equal("治理方案已落地，全部测试通过。", r.FinalResult);
        Assert.Contains("[结果] 治理方案已落地，全部测试通过。", outLines);
        Assert.Contains("[费用] $0.0123", outLines);
    }

    [Fact]
    public void Feed_result事件_isError_标注失败()
    {
        var r = new StreamJsonRenderer();
        var json = """
            {"type":"result","subtype":"error","is_error":true,"result":"鉴权失败，请重新登录"}
            """;
        var outLines = r.Feed(json);

        Assert.Equal("鉴权失败，请重新登录", r.FinalResult);
        Assert.Contains("[结果] 失败：鉴权失败，请重新登录", outLines);
    }

    [Fact]
    public void Feed_result事件_记录FinalIsError状态()
    {
        // 2026-10-16 回归：FinalIsError 供适配层在 exit 0 时识别 CLI 自报失败（429/额度耗尽）
        var r1 = new StreamJsonRenderer();
        r1.Feed("""{"type":"result","subtype":"error","is_error":true,"result":"429 限流"}""");
        Assert.True(r1.FinalIsError);

        var r2 = new StreamJsonRenderer();
        r2.Feed("""{"type":"result","subtype":"success","is_error":false,"result":"已完成"}""");
        Assert.False(r2.FinalIsError);

        var r3 = new StreamJsonRenderer();
        r3.Feed("非 JSON 文本");
        Assert.False(r3.FinalIsError);
    }

    [Fact]
    public void Feed_损坏Json_降级透传不抛异常()
    {
        var r = new StreamJsonRenderer();
        var outLines = r.Feed("{not valid json");
        Assert.Single(outLines);
        Assert.Null(r.FinalResult);
    }

    [Fact]
    public void Feed_streamEvent_textDelta_增量合并后由Flush刷出()
    {
        // --include-partial-messages 下，单行 delta 先进入合并缓冲，不足阈值时不会立即成行，
        // 需 Flush（或达到阈值/换行/完整事件到达）才刷出。
        var r = new StreamJsonRenderer();
        r.Feed("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"正在分析"}}}""");

        var flushed = r.Flush();
        Assert.Single(flushed);
        Assert.Equal("正在分析", flushed[0]);
        Assert.Null(r.FinalResult);
    }

    [Fact]
    public void Feed_连续delta_合并为单一连贯行()
    {
        var r = new StreamJsonRenderer();
        r.Feed("""{"type":"stream_event","event":{"delta":{"text":"短句一"}}}""");
        r.Feed("""{"type":"stream_event","event":{"delta":{"text":"短句二"}}}""");
        r.Feed("""{"type":"stream_event","event":{"delta":{"text":"短句三"}}}""");

        // 三段均不足阈值且无换行，仍留在缓冲，Flush 后合并为一行
        var flushed = r.Flush();
        Assert.Single(flushed);
        Assert.Equal("短句一短句二短句三", flushed[0]);
    }

    [Fact]
    public void Feed_streamEvent_遇换行_按行拆分刷出()
    {
        var r = new StreamJsonRenderer();
        var lines = new List<string>();
        lines.AddRange(r.Feed("""{"type":"stream_event","event":{"delta":{"text":"第一行\n第二行"}}}"""));
        lines.AddRange(r.Flush());

        Assert.Equal(2, lines.Count);
        Assert.Equal("第一行", lines[0]);
        Assert.Equal("第二行", lines[1]);
    }

    [Fact]
    public void Feed_streamEvent_达阈值_自动刷出连贯行()
    {
        var r = new StreamJsonRenderer();
        var longText = new string('中', 250);
        var outLines = r.Feed($"{{\"type\":\"stream_event\",\"event\":{{\"delta\":{{\"text\":\"{longText}\"}}}}}}");

        // 超过合并阈值，单个超长 delta 直接刷出（不再等 Flush）
        Assert.Single(outLines);
        Assert.Equal(longText, outLines[0]);
    }

    [Fact]
    public void Feed_先streamEvent后assistant_正文不重复输出()
    {
        // --include-partial-messages 下，正文已通过 stream_event 实时透出（合并刷出），
        // 完整 assistant 事件到达时不应再重复输出整段正文（整体只出现一次）。
        var r = new StreamJsonRenderer();
        var all = new List<string>();
        all.AddRange(r.Feed("""{"type":"stream_event","event":{"delta":{"type":"text_delta","text":"最终答案内容"}}}"""));
        all.AddRange(r.Feed("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"最终答案内容"}]}}"""));

        var count = 0;
        foreach (var l in all)
        {
            if (l.Contains("最终答案内容"))
            {
                count++;
            }
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void Feed_streamEvent非文本_不影响后续assistant正文()
    {
        // 非文本增量（如 message_start）不触发抑制标志，后续 assistant 正文仍正常透出。
        var r = new StreamJsonRenderer();
        r.Feed("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1"}}}""");
        var outLines = r.Feed("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"完整正文"}]}}""");

        Assert.Contains("完整正文", outLines);
    }

    [Fact]
    public void Feed_未知类型含文本_兜底透出()
    {
        var r = new StreamJsonRenderer();
        var outLines = r.Feed("""{"type":"system","subtype":"init","message":"会话已初始化"}""");

        Assert.Contains("会话已初始化", outLines);
    }
}
