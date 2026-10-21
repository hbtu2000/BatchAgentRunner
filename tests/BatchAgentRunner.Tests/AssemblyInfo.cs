// 去 flaky：全量测试串行执行（DisableTestParallelization），消除跨类并行踩踏 / 测试宿主
// 满载崩溃导致的偶发红灯（见 TECH-DEBT §5.5「测试宿主退出崩溃」）。日常验证改用定向
// `--filter` 测试（2–7s 出结果），全量仅在收尾 / CI 跑，串行开销可接受。
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
