# ADR-0003：编排层 facade ≤400 行 + 协作者拆分

- 状态：已接受（Accepted）
- 日期：2026-10-21
- 决策者：架构组
- 关联：ARCH P-01 / P-02 / P-03 / P-08 / P-10

## 背景

`BatchEngine` 曾是一个 953 行的 God Class：预检、等待唤醒、派发、降级、兜底、计费、
持久化、事件上报、报告生成全部内联。后果：任何改动都要在巨型方法间跳转，
并发/等待逻辑与业务策略交错，回归只能靠端到端。

## 决策

1. 编排层保留 **facade**：`BatchEngine` 只做流程编排与协作，**硬上限 400 行**（守卫测试锁死）。
2. 职责按「变化原因」拆为协作者，各自单文件：
   `Preflight` / `WaitCoordinator` / `DispatchCoordinator` / `TaskDispatcher` / `TaskRunner` /
   `AccountSwitchCoordinator` / `AutoShutdownCoordinator` / `BatchPersistence` / `BatchNotifier` /
   `BatchReportBuilder` / `GitWorkflow` / `WorkspaceResolver` / `TaskStepExecutor` / `TaskTerminalWriter`。
3. 协作者通过构造注入依赖，facade 只持有并按序调用；**facade 内禁止承载可复用的判断/计算**。
4. 行数超限时不做「压缩行数」的伪优化：把职责下沉到协作者，或拆出策略对象（见 ADR-0004）。
5. 行数守卫以机器断言固化，防止增量堆回。

## 后果

**正面**

- 变更局部化：改等待逻辑只看 `WaitCoordinator`，改派发只看 `DispatchCoordinator`。
- 协作者可独立单测（含并发场景），回归成本从 E2E 降到单测。
- 阅读入口清晰：facade 就是一份可执行的流程说明书。

**负面 / 代价**

- 类型数量增加，跨协作者的数据需经参数/请求对象传递，偶有「传参变长」。
- 行数上限会带来「为了 400 行而拆」的机械感，需以职责内聚为准（拆完的协作者必须自身有意义）。

## 替代方案

| 方案 | 为什么不选 |
| --- | --- |
| 保持单类，靠 region 分区 | region 不改变耦合，方法间仍共享可变状态，测试仍难 |
| 按「任务阶段」切成 Engine 子类继承 | 继承把状态耦合进层级，且难以组合；协作者组合更灵活 |
| 引入 Mediator / 事件总线解耦 | 引入隐式调用链，调试与顺序保证变难；本规模不必要 |

## 关联

- `src/BatchAgentRunner.Core/Engine/`（facade + 协作者）
- `LayeringRulesTests`（facade 行数硬上限断言）
- `BatchCollaboratorTests` / `TaskRunnerTests` / `TaskDispatcherTests` 等协作者契约测试
