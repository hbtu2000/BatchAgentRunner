using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Models;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Models;

/// <summary>
/// P2-15 偿还守卫：配置编辑器的「字段 → 字符串 → 解析回写」三段式收敛为
/// <see cref="FieldEditor{TValue}"/>，解析失败显式报错（<see cref="FieldEditor{TValue}.HasError"/> /
/// <see cref="FieldEditor{TValue}.Error"/>），编辑器 <c>Apply()</c> 对错误态保留原值而非静默覆盖。
/// 锁死：① 字段级解析语义（空 = 未配置 / 合法 = 取值 / 非法 = 错误态）；
/// ② 编辑器级回写语义（正常回写 / 错误保留原值）；③ 端口回落语义（空 = 587 / 非法 = 587 且报错）。
/// </summary>
public class ConfigEditorsTests
{
    [Fact]
    public void 候选模型编辑_时段非法输入_保留原时段且错误可见()
    {
        // P-09：起止时刻原本手写 TryParse，解析失败时 Apply() 会把用户已配置的免费时段静默抹掉。
        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0), null);
        var model = new ModelCandidate { Name = "m1", FreeTimeWindow = window };
        var editor = new ModelEditor(model);
        var previewBefore = editor.Preview;
        editor.StartTime.Text = "25:99";

        Assert.True(editor.StartTime.HasError);
        Assert.Equal("起始时刻格式无效", editor.StartTime.Error);
        Assert.Equal("时段无效", editor.Preview);
        Assert.NotEqual(previewBefore, editor.Preview); // P-09：文本变更经回调驱动 Preview 刷新

        editor.Apply();

        Assert.Same(window, model.FreeTimeWindow); // 非法输入不清空已配置时段
    }

    [Fact]
    public void 候选模型编辑_时段合法输入_按输入回写()
    {
        var model = new ModelCandidate { Name = "m1" };
        var editor = new ModelEditor(model) { WindowEnabled = true };
        editor.StartTime.Text = "08:30";
        editor.EndTime.Text = "12:00";

        editor.Apply();

        var window = model.FreeTimeWindow;
        Assert.NotNull(window);
        Assert.Equal(new TimeOnly(8, 30), window.Start);
        Assert.Equal(new TimeOnly(12, 0), window.End);
    }

    [Fact]
    public void 候选模型编辑_关闭时段_回写为空且不留残值()
    {
        var model = new ModelCandidate
        {
            Name = "m1",
            FreeTimeWindow = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0), null)
        };
        var editor = new ModelEditor(model) { WindowEnabled = false };

        editor.Apply();

        Assert.Null(model.FreeTimeWindow);
        Assert.Equal("全天可用", editor.Preview);
    }

    private static ModelCandidate NewModel() => new() { Name = "m1" };

    [Fact]
    public void 字段_空文本_未配置且无错误()
    {
        var field = new FieldEditor<decimal?>("预估单价", string.Empty, FieldParsers.DecimalOrNull);

        Assert.False(field.HasError);
        Assert.Null(field.Error);
        Assert.Null(field.Value);
    }

    [Fact]
    public void 字段_合法文本_解析取值且无错误()
    {
        var field = new FieldEditor<decimal?>("预估单价", "0.35", FieldParsers.DecimalOrNull);

        Assert.False(field.HasError);
        Assert.Equal(0.35m, field.Value);
    }

    [Fact]
    public void 字段_非法文本_错误态且取值默认()
    {
        var field = new FieldEditor<decimal?>("预估单价", "abc", FieldParsers.DecimalOrNull);

        Assert.True(field.HasError);
        Assert.Equal("预估单价格式无效", field.Error);
        Assert.Null(field.Value);
    }

    [Fact]
    public void 字段_改文本_错误态联动刷新()
    {
        var field = new FieldEditor<decimal?>("预估单价", "0.35", FieldParsers.DecimalOrNull);
        Assert.False(field.HasError);

        field.Text = "不是数字";

        Assert.True(field.HasError);
        Assert.Equal("预估单价格式无效", field.Error);

        field.Text = "1.5";

        Assert.False(field.HasError);
        Assert.Null(field.Error);
        Assert.Equal(1.5m, field.Value);
    }

    [Fact]
    public void 候选模型编辑_正常输入_回写领域对象()
    {
        var model = NewModel();
        var editor = new ModelEditor(model) { Name = "m2" };
        editor.UnitPrice.Text = "0.35";
        editor.Discount.Text = "0.65";

        editor.Apply();

        Assert.Equal("m2", model.Name);
        Assert.Equal(0.35m, model.EstimatedUnitPrice);
        Assert.Equal(0.65m, model.Discount);
        Assert.Equal("0.65x", editor.DiscountDisplay);
    }

    [Fact]
    public void 候选模型编辑_留空_清除可选字段()
    {
        var model = new ModelCandidate { Name = "m1", EstimatedUnitPrice = 0.35m, Discount = 0.65m };
        var editor = new ModelEditor(model);
        editor.UnitPrice.Text = string.Empty;
        editor.Discount.Text = string.Empty;

        editor.Apply();

        Assert.Null(model.EstimatedUnitPrice);
        Assert.Null(model.Discount);
        Assert.Equal(string.Empty, editor.DiscountDisplay);
    }

    [Fact]
    public void 候选模型编辑_非法输入_保留原值且错误可见()
    {
        // P2-15 核心行为变更：原实现解析失败静默置 null（用户无从得知输入未被采纳）；
        // 现为错误态可见 + Apply 保留原值。
        var model = new ModelCandidate { Name = "m1", EstimatedUnitPrice = 0.35m };
        var editor = new ModelEditor(model);
        editor.UnitPrice.Text = "免费";
        editor.Discount.Text = "五折";

        Assert.True(editor.UnitPrice.HasError);
        Assert.True(editor.Discount.HasError);

        editor.Apply();

        Assert.Equal(0.35m, model.EstimatedUnitPrice); // 保留原值，不清空
        Assert.Null(model.Discount); // 原值即未配置
    }

    [Fact]
    public void 兜底模型编辑_非法输入_保留原值且错误可见()
    {
        var model = new FallbackModel { Name = "paid-1", EstimatedUnitPrice = 0.12m };
        var editor = new FallbackModelEditor(model);
        editor.UnitPrice.Text = "x";

        Assert.True(editor.UnitPrice.HasError);

        editor.Apply();

        Assert.Equal("paid-1", model.Name);
        Assert.Equal(0.12m, model.EstimatedUnitPrice);
    }

    [Fact]
    public void 通知规则_SMTP端口_留空回落587()
    {
        var editor = new NotificationRuleEditor(new NotificationRule
        {
            Channel = NotificationChannel.Smtp,
            Smtp = new SmtpConfig { Host = "smtp.example.com" }
        });
        editor.SmtpHost = "smtp.example.com";
        editor.SmtpPort.Text = string.Empty;

        var rule = editor.Apply();

        Assert.Equal(587, rule.Smtp!.Port);
        Assert.False(editor.SmtpPort.HasError);
    }

    [Fact]
    public void 通知规则_SMTP端口_非法输入_回落587且错误可见()
    {
        var editor = new NotificationRuleEditor(new NotificationRule
        {
            Channel = NotificationChannel.Smtp,
            Smtp = new SmtpConfig { Host = "smtp.example.com", Port = 25 }
        });
        editor.SmtpHost = "smtp.example.com";
        editor.SmtpPort.Text = "端口";

        Assert.True(editor.SmtpPort.HasError);
        Assert.Equal("SMTP 端口格式无效", editor.SmtpPort.Error);

        var rule = editor.Apply();

        Assert.Equal(587, rule.Smtp!.Port); // 回落默认值，但错误态已对用户显式呈现
    }

    [Fact]
    public void 通知规则_SMTP端口_合法输入_按输入回写()
    {
        var editor = new NotificationRuleEditor(new NotificationRule
        {
            Channel = NotificationChannel.Smtp,
            Smtp = new SmtpConfig { Host = "smtp.example.com", Port = 25 }
        });
        editor.SmtpHost = "smtp.example.com";
        editor.SmtpPort.Text = "465";

        var rule = editor.Apply();

        Assert.Equal(465, rule.Smtp!.Port);
        Assert.False(editor.SmtpPort.HasError);
    }
}
