# AGENTS.md — WorkAgent（BatchAgentRunner）仓库协作指南

> 本文件供 AI 智能体（CodeBuddy / WorkBuddy 等）在本仓库工作时自动遵循。
> 重点解决一个高频坑：**用 AI 还技术债 / 修 BUG 时，单次任务耗时 2h+**。

> 🔒 **核心纪律**：针对会反复编译的代码改动类任务，**迭代阶段用快速模式，提交前再跑严格构建**。
> 即「迭代 fast / 过关 strict」——干活时只查真实编译错误（`-p:NEXYAW_FAST_BUILD=1`），
> 交付 / 提交 / 门禁前再跑一次不带 fast 参数的严格构建，把可空 / 样式告警一次性逼出并就地修掉。详见第三节。

## 一、仓库画像
- **项目**：BatchAgentRunner —— 本地批处理任务调度工具（C# / .NET 10 / Avalonia 12 跨平台桌面）。
- **形态**：单仓多工程巨石（`src/` 数百个 .cs、多个测试工程、`BatchAgentRunner.slnx`）。
- **约定**：源码 UTF-8（`Directory.Build.props` 已设 CodePage 65001）；中文注释为主。

## 二、为什么 AI 改代码会反复返工、单任务 2h+（根因）
`Directory.Build.props` 全局开启 **`TreatWarningsAsErrors=true`** + **`EnforceCodeStyleInBuild=true`** +
`AnalysisLevel=latest-recommended`。对巨石仓库，AI 每改一处，任何**可空(CS86xx) / 样式告警**都会被打成编译错误，
被迫反复回改十几轮；再叠加 pdb 全量拷贝进 bin、每次 `dotnet build` 重算还原图、串行构建多工程，单 TD 任务量级 **2h+**。

## 三、构建 SOP：迭代 fast / 过关 strict（务必遵守）
把「**迭代期快速验证**」与「**提交前严格门禁**」解耦：

### 3.1 迭代期（你干活、反复验证时）→ 用 fast 模式
只查**真实编译错误**，跳过分析器 / 可空告警当错误 / pdb 拷贝：
```bash
# 只构建你改动涉及的工程，并复用已还原/已编译产物
dotnet build <你改动的工程.csproj> -p:NEXYAW_FAST_BUILD=1 --no-restore
# 若需连依赖一起编，再加 --no-dependencies 跳过重编项目引用
```
- 变量 `NEXYAW_FAST_BUILD=1`（或别名 `WORKAGENT_FAST_BUILD=1`）触发，见 `Directory.Build.props` 末尾的 opt-in 段；**不设变量即原 strict 行为，CI 零影响**。
- ⚠️ **致命坑：多数 shell 不保留环境变量**，`export NEXYAW_FAST_BUILD=1` 往往只对当条命令有效。**务必把 `-p:NEXYAW_FAST_BUILD=1` 直接写进 `dotnet build` 命令本身**，不要依赖 `export`。

### 3.2 过关期（完成修复、交付 / 提交 / 门禁前）→ 用 strict 模式
**不带** fast 参数、跑一次严格构建，把可空 / 样式告警一次性逼出（**延后、非绕过**，不会丢失）：
```bash
# 末次严格构建：不带 -p:NEXYAW_FAST_BUILD=1，也不加 --no-restore（确保干净）
dotnet build <受影响工程集或全量 sln>
```
- 严格构建若冒出 CS86xx / 样式告警，**就地修掉再交付**；fast 期间被压下的告警不得带病入库。

## 四、范围最小化（收益常常大于构建优化本身）
- **绝不默认构建全量主 sln / 全部工程**——这是耗时爆炸主因之一。
- 改到哪只建哪：单工程用 `dotnet build <proj>`；多工程只建受影响子集。
- 首次还原后加 `--no-restore`；依赖已编译加 `--no-dependencies`。
- 多组 / 内部包无变更加对应跳过重打包开关（若仓库有封装构建脚本，加 `--fast` / `--no-restore` 透传）。

## 五、动手前先摸清治理状态
本仓库有活跃的技术债治理（见 `docs/TECH-DEBT-技术债务治理方案-v2.0.md`），并发会话常同时改仓。
动手前先跑治理 / 健康检查脚本确认当前状态，避免在过期结论上反复返工。

## 六、速查
| 场景 | 命令 |
|------|------|
| 迭代验证（fast） | `dotnet build <改到的工程> -p:NEXYAW_FAST_BUILD=1 --no-restore` |
| 末次过关（strict） | `dotnet build <受影响集/全量>`（不带 fast 参数）|
| 关键原则 | 不靠 `export`，把 `-p:NEXYAW_FAST_BUILD=1` 写进命令；范围越小越快 |

> 权威跨项目手册：`NexYaw3D/docs/偿还技术债耗时优化_具体解决办法_跨项目手册.md`
> （办法一~七 + 落地清单；本仓库的 `Directory.Build.props` 快速迭代段即照该手册办法一实现）。
