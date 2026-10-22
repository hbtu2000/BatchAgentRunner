# 架构决策记录（ADR）

本目录存放 BatchAgentRunner 的**重大架构决策记录**（Architecture Decision Record）。
引入背景见 [ADR-0001](ADR-0001-adopt-adr.md)（ARCH P-14 / A-12）：

> 以前重大决策只活在提交信息、代码注释和人的记忆里——「为什么这样拆」「为什么不那样做」
> 无处可查，重构后半年再回看只能靠考古。ADR 把决策的**上下文与后果**固化进仓库，
> 与代码同版本、同评审、同回滚。

## 何时写 ADR

**写**（满足任一）：

- 改变模块边界 / 依赖方向 / 分层规则；
- 引入或移除一个跨模块的抽象、模式、第三方依赖；
- 改变对外契约（`config.json` 形状、CLI 参数、sidecar 格式、报告结构）；
- 改变工程化门禁（CI 阻断语义、覆盖率/格式基线、发布方式）；
- 决策的**后果难以回退**，或推翻已有 ADR。

**不写**：局部实现细节、命名、一次性的 bug 修复、性能微调（这些进提交信息 / PR 描述）。

## 怎么写

1. 复制 [`template.md`](template.md) 为 `ADR-NNNN-<kebab-slug>.md`（编号连续、不复用、不删号）；
2. 状态取值：`提议（Proposed）` / `已接受（Accepted）` / `已废弃（Deprecated，被 ADR-NNNN 取代）` / `已取代（Superseded by ADR-NNNN）`；
3. **决策一旦接受即不可改写**——要改就新开一篇并标注取代关系（保留决策演进轨迹）；
4. 在本文件「索引」表追加一行；
5. 与代码改动同一 PR 提交，PR 描述注明 `ADR-NNNN`。

> 机器校验：`tests/BatchAgentRunner.Tests/Architecture/AdrHygieneTests.cs` 强制
> ① 文件名编号连续且格式合规；② 必备章节（状态/背景/决策/后果）齐全；
> ③ 索引与本目录文件双向一一对应（漏登记、悬挂链接、编号断档均红）。
> 这就是 §5.8「设计文档与代码同步靠机器守卫，而非人肉」的落地。

## 索引

| 编号 | 标题 | 状态 | 日期 | 关联阶段 |
| --- | --- | --- | --- | --- |
| [ADR-0001](ADR-0001-adopt-adr.md) | 采用 ADR 记录重大架构决策 | 已接受 | 2026-10-22 | ARCH P-14 |
| [ADR-0002](ADR-0002-layering-and-dependency-direction.md) | 分层与单向依赖方向 | 已接受 | 2026-10-21 | ARCH P-02 |
| [ADR-0003](ADR-0003-orchestration-facade-400-lines.md) | 编排层 facade ≤400 行 + 协作者拆分 | 已接受 | 2026-10-21 | ARCH P-02 / P-10 |
| [ADR-0004](ADR-0004-scheduling-policy-objects.md) | 调度策略对象化 | 已接受 | 2026-10-21 | ARCH P-03 |
| [ADR-0005](ADR-0005-viewmodel-decomposition.md) | ViewModel 按 Coordinator / Presenter 分解 | 已接受 | 2026-10-21 | ARCH P-04 / P-05 / P-06 |
| [ADR-0006](ADR-0006-storage-and-path-single-source.md) | 存储与路径常量单一事实来源 | 已接受 | 2026-10-21 | ARCH P-07 / P-08 |
| [ADR-0007](ADR-0007-config-contract-and-schema.md) | 配置契约稳定：横向切片 + JSON Schema 单一事实来源 | 已接受 | 2026-10-22 | ARCH P-09 / P-14 |
| [ADR-0008](ADR-0008-observability-structured-logging.md) | 可观测性：单一日志落盘点 + RunId + 运行级指标 + 脱敏 | 已接受 | 2026-10-21 | ARCH P-10 |
| [ADR-0009](ADR-0009-ci-blocking-gates.md) | CI 门禁转阻断 + 覆盖率/格式基线 | 已接受 | 2026-10-22 | ARCH P-11 |
| [ADR-0010](ADR-0010-multi-platform-and-release.md) | 多平台矩阵与 Release 分发 | 已接受 | 2026-10-22 | ARCH P-13 |
| [ADR-0011](ADR-0011-fast-iteration-build-sop.md) | 构建 SOP：迭代 fast / 过关 strict | 已接受 | 2026-10-22 | AGENTS.md / ARCH P-13 |
