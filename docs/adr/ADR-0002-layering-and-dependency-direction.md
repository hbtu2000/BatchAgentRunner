# ADR-0002：分层与单向依赖方向

- 状态：已接受（Accepted）
- 日期：2026-10-21
- 决策者：架构组
- 关联：ARCH P-02 / A-7；`tests/BatchAgentRunner.Tests/Architecture/LayeringRulesTests.cs`；`docs/ARCH-…§3.4` / 附录 C

## 背景

重构前 `Core` 直接 `new` 基础设施实现（文件仓储、进程启动器、通知器），Desktop 又直接
`new` Infrastructure/Agents 具体类型。后果：

- 引擎层无法脱离文件系统做单测，测试必须真跑磁盘与进程；
- 换实现（如换 Vault/换 CLI 适配器）要改核心编排代码；
- 依赖方向混乱，「谁依赖谁」只能靠读代码猜。

## 决策

依赖方向固定为单向：

```
Desktop ──▶ Agents ──▶ Infrastructure ──▶ Core
   │                                        ▲
   └────────── Composition（组合根）─────────┘
```

1. `Core` 只定义抽象与领域模型，**禁止引用** `Infrastructure` / `Agents` / `Desktop` 具体命名空间。
2. `Infrastructure` / `Agents` 不得出现在 `Core` 的 `ProjectReference` 中（只允许 `Core.Abstractions` 形态的抽象）。
3. `Desktop` 除 `Composition`（组合根）外，**禁止 `new`** Infrastructure / Agents 具体类型。
4. 规则由 IL 扫描断言机器校验，违反即测试红，不依赖人肉评审。
5. 例外只允许登记在治理方案 §4.7 台账，四要素齐全且有到期日（`EscapeValveHygieneTests` 双向校验）。

## 后果

**正面**

- 引擎/协作者可在无 IO 环境做单测；端口实现可替换（`IFileSystem` / `IProcessLauncher` / `ICredentialVault` 等）。
- 依赖图可以被自动断言，架构腐化在 CI 阶段即暴露。

**负面 / 代价**

- 抽象数量增加，新增能力需先在 Core 定义端口，短期代码量变多。
- 组合根成为唯一「知道全部具体类型」的地方，其体量需持续控制。

## 替代方案

| 方案 | 为什么不选 |
| --- | --- |
| 允许 Core 直接依赖 Infrastructure | 单测必须真 IO；实现替换需改核心；依赖环风险高 |
| 只在文档约定、不做断言 | 巨石仓库里约定必然被打破，且发现时已晚 |
| 引入第三方 DI 容器 + 约定扫描 | 依赖体积与学习成本高于收益；本仓库抽象数量可用手写组合根管理 |

## 关联

- `LayeringRulesTests`（IL 扫描断言）
- `src/BatchAgentRunner.Desktop/Composition/`（唯一组合根）
- 治理方案 §4.7 逃逸阀台账
