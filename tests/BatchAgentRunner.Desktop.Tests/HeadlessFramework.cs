using Avalonia.Headless.XUnit;

// P1-19：本程序集统一使用 Avalonia headless 测试框架——每个测试自动初始化 headless Application
// 与 UI 线程消息循环，使 [AvaloniaFact]/[AvaloniaTheory] 测试运行在 UI 线程上（视图构造、
// UiThread.Post 真实 marshal 等依赖 Application.Current / Dispatcher.UIThread 的路径可被覆盖）。
[assembly: AvaloniaTestFramework]
