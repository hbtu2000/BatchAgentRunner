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
    public void Feed_损坏Json_降级透传不抛异常()
    {
        var r = new StreamJsonRenderer();
        var outLines = r.Feed("{not valid json");
        Assert.Single(outLines);
        Assert.Null(r.FinalResult);
    }
}
