# ADR-0005：ViewModel 按 Coordinator / Presenter 分解

- 状态：已接受（Accepted）
- 日期：2026-10-21
- 决策者：桌面组 + 架构组
- 关联：ARCH P-04 / P-05 / P-06；`src/BatchAgentRunner.Desktop/ViewModels/`

## 背景

`MonitorViewModel`（983 行）、`LoginViewModel`（751 行）、`ConfigViewModel`、`TemplateEditorViewModel`
曾同时承担状态持有、绑定转发、流程编排、IO 调用四类职责。后果：

- 绑定属性与流程逻辑混在一起，headless 测试只能整体构造 VM；
- Avalonia 的 `[ObservableProperty]` 生成属性使文件更长，测试更难定位问题；
- 任何人加一个功能都在同一个文件追加。

## 决策

1. VM 只保留三类内容：**状态持有**（可绑定属性）、**绑定转发**、**流程入口**；
2. 流程编排下沉为 Coordinator / Presenter：
   - Monitor：`MonitorOperationsCoordinator` / `OutputStreamCoordinator` / `TaskListCoordinator` + `BatchStatistics` / `BatchStatsState` / `EventToastBoard` 等；
   - Login：登录流程协作者 + 状态对象；
   - Config：`ConfigSaveCoordinator` + 按配置域拆分的 Section VM（账号/降级/通知/Git/通用/任务/流程/关机/安全/时段）；
   - TemplateEditor：模板加载 / 变量展开 / 流程示意三个 Coordinator + 行视图模型独立文件。
3. **最大单个 VM ≤400 行**（硬上限，测试锁死）；行视图模型各自独立文件。
4. 协作者与行 VM 必须可 headless 构造（不依赖真实窗口/CLI），供契约测试使用。
5. XAML 绑定名由守卫测试校验，避免拆分后绑定断链。

## 后果

**正面**

- VM 变成「薄绑定层」，逻辑可在 headless 下测；显示器/流程可分别回归。
- 按配置域拆 Section VM 后，新增配置项只动对应域，冲突面显著变小。

**负面 / 代价**

- 类型与文件数量增加，跨协作者共享状态需显式传递（部分场景引入状态对象）。
- XAML 绑定路径变长，需要绑定守卫测试兜底。

## 替代方案

| 方案 | 为什么不选 |
| --- | --- |
| 保持大 VM + region | 绑定与流程仍耦合，headless 测试仍需构造整个 VM |
| 引入 MVVM 框架（ReactiveUI/Prism 等） | 增加依赖与学习成本；本项目手写 MVVM 已足够 |
| 只拆 partial 文件 | 仍是同一个类型同一份状态，耦合未解，测试粒度不变 |

## 关联

- `src/BatchAgentRunner.Desktop/ViewModels/**`
- `tests/BatchAgentRunner.Desktop.Tests/`（headless 契约 + 绑定守卫 + 行数守卫）
