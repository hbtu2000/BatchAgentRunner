# ADR-0006：存储与路径常量单一事实来源

- 状态：已接受（Accepted）
- 日期：2026-10-21
- 决策者：架构组
- 关联：ARCH P-07 / P-08；`Infrastructure/Persistence/JsonStoreBase.cs`、`Infrastructure/AppPaths.cs`

## 背景

三个 JSON 仓储（config / cooldown / credits-claim）的加载-校验-归一化-原子写回逻辑各写一遍，
差异只在类型与文件名；路径拼接散落 9 处魔法串（`%APPDATA%` 下的目录名、文件名、`run-*.log` 前缀等）。
后果：改一处漏两处，文件名/目录调整必然产生孤儿文件；新增第四个仓储时又会复制第三份。

## 决策

1. `JsonStoreBase<T>` 作为同构仓储唯一基类：子类只提供 `CreateEmpty()` 与 `NormalizeAsync()`，
   加载/容错/原子写回/备份逻辑共用。
2. `AppPaths` 作为**路径布局唯一事实来源**：目录、文件名、日志/报告/指标前缀全部常量化，
   业务代码禁止字面量拼接路径。
3. 领域原语同样收敛（P-08）：文本截断 `TextTruncate`、终态集合 `TaskStatusExtensions.IsTerminal`、
   `Clone` 手写唯一实现、依赖失败分派单一入口。
4. 守卫测试锁死：
   - `AppPathsHygieneTests`：路径字面量不得在 `AppPaths` 之外出现；
   - `DomainPrimitivesHygieneTests`：公共工具只允许单份实现；
   - 拦截「第 4 份同构仓储」的复制。

## 后果

**正面**

- 新增仓储/新路径只改一处；原子写回与容错行为天然一致。
- 路径布局可被测试断言，迁移目录时有单一改动点。

**负面 / 代价**

- 基类抽象对极端异构仓储未必合适；出现真正异构需求时需显式说明并另立实现（守卫会拦「悄悄复制」）。
- `AppPaths` 成为热点文件，新增路径需集中评审（这也是目的之一）。

## 替代方案

| 方案 | 为什么不选 |
| --- | --- |
| 保持三份实现 + 复制模板 | 修复要改三处，必然漂移；守卫无法有效拦截 |
| 引入通用 ORM / 数据库 | 本地单机批处理工具，JSON 文件即产品契约（可手工编辑、可 diff），引入 DB 破坏可运维性 |
| 用依赖注入配置路径字符串 | 字符串仍会散落；常量类比注入更易被静态断言 |

## 关联

- `src/BatchAgentRunner.Infrastructure/Persistence/*`、`src/BatchAgentRunner.Infrastructure/AppPaths.cs`
- `src/BatchAgentRunner.Core/Text/TextTruncate.cs`、`Core/Domain/TaskStatusExtensions.cs`
- `AppPathsHygieneTests` / `DomainPrimitivesHygieneTests` / `TaskItemCloneTests`
