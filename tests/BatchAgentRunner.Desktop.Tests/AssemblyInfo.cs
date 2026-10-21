// 去 flaky：与 BatchAgentRunner.Tests 一致，全量测试串行执行（DisableTestParallelization），
// 消除跨类并行踩踏 / 测试宿主满载崩溃导致的偶发红灯。日常验证用定向 `--filter` 测试，
// 全量仅在收尾 / CI 跑，串行开销可接受。
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
