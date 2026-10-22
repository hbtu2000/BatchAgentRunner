using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>工作流/任务模板仓库端口（二期场景模板库 S1–S16）。</summary>
public interface IWorkflowTemplateStore
{
    /// <summary>列出所有可用模板（内置 + 用户自定义）。</summary>
    Task<IReadOnlyList<TemplateSpec>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>按 id 获取模板。</summary>
    Task<TemplateSpec?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>保存用户自定义模板（覆盖同名）。</summary>
    /// <param name="spec">模板定义（参数名避开 C# 关键字 <c>template</c>，CA1716）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveUserTemplateAsync(TemplateSpec spec, CancellationToken cancellationToken = default);
}
