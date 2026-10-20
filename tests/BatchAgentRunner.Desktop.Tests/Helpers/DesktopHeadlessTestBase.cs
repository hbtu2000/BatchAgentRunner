namespace BatchAgentRunner.Desktop.Tests.Helpers;

/// <summary>
/// P1-19：Avalonia headless 契约测试基类（仅组织用途）。
/// 程序集经 <c>[assembly: AvaloniaTestFramework]</c> 自动初始化 headless Application 与 UI 线程消息循环，
/// 派生类用 <c>[AvaloniaFact]</c>/<c>[AvaloniaTheory]</c> 标记测试，测试体运行在 UI 线程上，
/// 使依赖 <c>Application.Current</c> / <c>Dispatcher.UIThread</c> 的代码路径（视图构造、线程收敛）可被测试。
/// </summary>
public abstract class DesktopHeadlessTestBase
{
}
