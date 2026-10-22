# ADR-0011：构建 SOP——迭代 fast / 过关 strict

- 状态：已接受（Accepted）
- 日期：2026-10-22
- 决策者：架构组
- 关联：`AGENTS.md` §3；`Directory.Build.props` 快速迭代段

## 背景

`Directory.Build.props` 全局开启 `TreatWarningsAsErrors=true` + `EnforceCodeStyleInBuild=true` +
`AnalysisLevel=latest-recommended`。对巨石仓库，任何一处可空/样式告警都会把迭代构建打成红色，
AI / 开发者被迫反复回改十几轮；叠加 pdb 全量拷贝、依赖图重算、多工程串行构建，
单个债务任务的验证耗时可达 2h+。同时 CI 必须保持严格——不能为了迭代顺手把门禁放松。

## 决策

1. **迭代期 fast**：设置 `NEXYAW_FAST_BUILD=1`（或别名 `WORKAGENT_FAST_BUILD=1`）时，
   `Directory.Build.props` 的 opt-in 段生效：
   - 跳过 Roslyn 分析器通道与 `EnforceCodeStyleInBuild`；
   - `TreatWarningsAsErrors=false`（只保留 csc 硬错误）；
   - 引用相关文件只带 `.xml`，不拷贝 `.pdb/.config`。
2. **过关期 strict**：交付 / 提交 / 门禁前，**不带** fast 参数跑严格构建（不加 `--no-restore`），
   把可空与样式告警一次性逼出并就地修掉。
3. **不依赖环境变量**：多数 shell 不保留 `export`，必须把 `-p:NEXYAW_FAST_BUILD=1` 直接写进 `dotnet build` 命令。
4. **范围最小化**：只构建改动涉及的工程；依赖已编译可加 `--no-dependencies`，已还原加 `--no-restore`。
5. **不设变量即基线行为，CI 零影响**：fast 是本地迭代加速器，绝不进入 CI 与发布。

## 后果

**正面**

- 迭代循环从「十几轮告警回改」降到「只查真实错误」，单任务耗时显著下降。
- 严格门禁完全保留：fast 期间的告警**只是延后，不会丢失**，strict 构建会全部逼出。
- CI 与正式构建行为不变，不存在「本地绿、CI 红」的分裂。

**负面 / 代价**

- 需要纪律：忘记跑 strict 就提交，会把告警带入仓库（CI 会拦住，但返工在远端）。
- fast 掩盖分析器问题，可能让人误以为「已经干净」；文档与 AGENTS.md 反复强调这一点。

## 替代方案

| 方案 | 为什么不选 |
| --- | --- |
| 全局关闭 `TreatWarningsAsErrors` | 直接放弃门禁，债务立刻回潮 |
| 用 `-warnaserror:false` 命令行参数 | 仍跑分析器与代码样式，耗时不减；且无法裁剪 pdb 拷贝 |
| 拆分解决方案分治 | 仓库是单仓多工程巨石，拆分成本远高于收益 |
| 只靠增量构建 | 增量缓存会掩盖问题，且首次仍需全量 |

## 关联

- `AGENTS.md` §3（构建 SOP）、`Directory.Build.props`（opt-in 段）
- 跨项目手册：`NexYaw3D/docs/偿还技术债耗时优化_具体解决办法_跨项目手册.md`
