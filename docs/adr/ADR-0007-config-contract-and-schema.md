# ADR-0007：配置契约稳定——横向切片 + JSON Schema 单一事实来源

- 状态：已接受（Accepted）
- 日期：2026-10-22
- 决策者：架构组
- 关联：ARCH P-09 / P-14；`Core/Options/RunOptions.*.cs`、`docs/config.schema.json`

## 背景

`RunOptions` 曾是 363 行的「God Config」，12 个产品配置域挤在一个类里；与此同时
`config.json` 是**对外契约**——用户会手工编辑、备份、跨版本复制，字段名与形状不能改。
另有一个更隐蔽的问题：配置项用什么类型、取值范围、枚举成员，只在 C# 里定义，
文档（`docs/CONFIG-配置项说明.md`）与代码靠人工同步，schema 无人可校验。

## 决策

1. **形状不变、按域切片**：`RunOptions` 保持 `partial class`，按 12 个配置域横向切为
   `RunOptions.01-Execution.cs` … `RunOptions.11-Derived.cs`（本体仅保留类型声明）。
   - 属性名、`[JsonPropertyName]` 键名、声明顺序（即 JSON 属性顺序）与落盘字节全部不变；
   - 切片顺序即落盘顺序，文件名以两位序号固定，调整须同步 `RunOptionsContractTests`。
2. **不做嵌套分组**：不接受 `options.execution.timeout` 这类层级化改造——那是破坏性契约变更，
   会读旧配置失败。
3. **JSON Schema 为机器可校验单一事实来源**：`docs/config.schema.json`（JSON Schema 2020-12）
   由 `RunOptions` 及其引用的领域类型派生，与代码同仓同 PR；`samples/config.example.json` 作为可校验样例。
4. **双向同步由测试锁死**：`ConfigSchemaTests` 断言 schema 顶层与各 `$defs` 的键集合
   **逐字等于** C# 类型的 `[JsonPropertyName]` 键集合；示例配置必须通过校验；畸形配置必须被拦截
   （防校验器退化为空操作）。
5. **契约风格约定**：字段 snake_case、派生属性 `[JsonIgnore]`、枚举以字符串持久化、
   金额为 decimal。由 `RunOptionsContractTests` 的命名/派生契约断言守卫。
6. **CI 校验**：`scripts/ci/Assert-ConfigSchema.ps1` 在 CI 中执行 schema 校验（复用同一套测试实现）。

## 后果

**正面**

- 旧 `config.json` 永远可读；新增字段默认值兼容，破坏性变更只能走主版本。
- 字段增减必须同时改 schema 与文档，否则 CI 红——「文档与代码同步」从人肉变成机器。
- 编辑者在 IDE 里可获得 schema 提示（`$schema` 指向），减少非法配置。

**负面 / 代价**

- 新增配置项要动三处（切片文件 + schema + 文档），单点改动成本上升（这是有意为之）。
- schema 为手写派生，仍可能漏写 `description`/边界；由键集合断言兜住结构、由文档兜住语义。
- `additionalProperties: true` 是刻意的前向兼容取舍：不误伤旧字段，代价是未知字段不会被报错。

## 替代方案

| 方案 | 为什么不选 |
| --- | --- |
| 嵌套分组重构 `RunOptions` | 破坏 `config.json` 形状，用户配置全部失效，不可接受 |
| 运行时从 C# 反射生成 schema 并只在 CI 落盘 | 生成器本身成为第二事实来源；且 schema 需可读、可手改描述 |
| 只维护 Markdown 配置文档 | 无法机器校验，必然漂移；且不能给编辑器提示 |
| 严格 `additionalProperties: false` | 会让旧版本配置（含已废弃字段）直接校验失败，违背前向兼容 |

## 关联

- `src/BatchAgentRunner.Core/Options/RunOptions.*.cs`
- `docs/config.schema.json`、`samples/config.example.json`
- `ConfigSchemaTests` / `RunOptionsContractTests` / `RunOptionsHygieneTests`
- `scripts/ci/Assert-ConfigSchema.ps1`
