// P2-8：FakeClock 等测试替身自测试项目供给（生产程序集不得出现，见 LayeringRulesTests）
global using BatchAgentRunner.Tests.Helpers;
global using Xunit;
// 与 System.Threading.Tasks.TaskStatus 区分：领域任务状态一律使用本别名
global using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;
