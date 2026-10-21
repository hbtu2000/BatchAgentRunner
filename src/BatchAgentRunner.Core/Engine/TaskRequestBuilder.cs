using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 请求构建协作器（A-2 / P-03）：从 <see cref="TaskRunner"/> 下沉的 <see cref="AgentRequest"/> 构造。
/// 解析工作区 + 组装请求 + 绑定输出分块回调（经上下文广播）。
/// </summary>
internal static class TaskRequestBuilder
{
    public static AgentRequest BuildRequest(TaskRunnerContext ctx, TaskItem task, AgentCombo combo, bool isFallback)
    {
        var workspace = ctx.ResolveWorkspace(task.Workspace ?? ctx.Options.DefaultWorkspace)
                        ?? ctx.FileSystem.CurrentDirectory;

        return new AgentRequest(
            task.Id,
            task.Instruction,
            combo.Account,
            combo.Model,
            ctx.Options.Expert,
            workspace,
            ctx.Options.TaskTimeout,
            ctx.Options.AutoAnswer)
        {
            IsFallback = isFallback,
            OnOutputChunk = chunk =>
                ctx.Raise(new BatchEvent(BatchEventKind.TaskOutput, ctx.Clock.Now, chunk, task.Id))
        };
    }
}
