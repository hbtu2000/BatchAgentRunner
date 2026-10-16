# 项目长期记忆：AI 批处理任务调度工具（Batch Agent Runner）

## 项目定位
- 本地批处理任务调度工具：任务文件（JSON/Markdown）驱动 → 调用 WorkBuddy/CodeBuddy CLI 无头执行 → 结果原子回写 → 实时监控 + 随时终止/续跑
- 需求源：`docs/PRD-AI批处理任务调度工具-v1.0.md`（v1.12）；技术设计：`docs/DESIGN-BatchAgentRunner-v1.0.md`（v1.1）

## 技术栈与约定（用户已确认）
- 开发语言 C# / .NET 10；界面 **Avalonia 12 跨平台桌面**（非 Web UI）
- 交付范围一期 **P0 + P1**；CLI 适配层走抽象接口 + 内置 MockAdapter 仿真
- 解决方案 `BatchAgentRunner.slnx`，项目分层：Core / Agents / Infrastructure / Desktop / Tests
- 编码：源码 UTF-8（`Directory.Build.props` 已设 CodePage 65001）
- 枚举序列化用字符串（JsonDefaults 配 JsonStringEnumConverter）
- **配置字段命名一律 snake_case**，派生/计算属性必须标 `[JsonIgnore]`（否则会被写进 config.json 干扰阅读）
- **集合类型避免声明为接口**（如 `IReadOnlySet<T>`）—— System.Text.Json 无法实例化接口类型反序列化，会在加载配置时抛 `NotSupportedException`

## 核心领域语义（勿随意改动）
- **v1.12 需求变更（2026-10-15）：候选模型池与兜底模型池挂在账号下**——`accounts[].model_candidates` / `accounts[].fallback_models`，矩阵 = 各账号 × 自有池（不再全局笛卡尔积）；同一模型在不同账号可配不同免费时段（如 A 账号 Hy4 0:00–8:00、B 账号 Hy4 全天）；全局顶层 `model_candidates` 降级为兼容字段（仅对未配置自有池的账号生效）；`fallback.model`/`fallback.estimated_unit_price` 已废弃，`fallback.enabled`/成本上限保持全局
- 降级遍历：ModelFirst（默认）= 按账号顺序轮完自有池再换账号；AccountFirst = 按模型名聚合（同一模型名先在所有配置了它的账号上轮完，按首次出现顺序）
- 兜底选取：按「账号顺序 × 该账号兜底池顺序」取第一个可用兜底组合，不参与免费时段过滤
- 组合可用性两独立约束：免费时段（时间维度，组合级）∩ 额度冷却（额度维度，键仍为账号×模型）；恢复时间 = max(重置时间, 下一时段开始)
- 「时段外跳过」与「额度冷却跳过」均为预期调度行为：不计失败、不触发切换告警、不污染误切换率
- 临时限流先退避重试同一组合，额度耗尽才切换；切换/兜底后「继续当前任务」= 以完整指令重新发起全新会话（不继承上下文）
- Markdown 任务文件为只读输入，状态/结果写 sidecar `<name>.md.result.json`；JSON 原地原子回写
- 批级状态：Idle / Running / Waiting（独立状态，非 Running 子状态）/ Blocked / Terminated / Completed

## 配置文档入口
- 逐项说明：`docs/CONFIG-配置项说明.md`（9 个配置域速查表 + 机制解释 + 常见坑）
- 全字段带注释模板：`samples/config.example.json`（解析器允许 `//` 注释与尾随逗号，可直接复制为 config.json）
- 实际配置位置：`%LOCALAPPDATA%\BatchAgentRunner\config.json`（界面点保存会重写并**丢失注释**）

## 待解决
- M0 技术验证 V-1~V-10（CLI headless、模型参数、额度错误特征、多账号切换、专家参数、交互应答、用量数据、会话恢复、危险操作默认项、重置时间格式）
- 四个默认值需真实任务校准：单任务超时 10min / 总时长上限 60min / 最长可等待 60min / 默认冷却 60min
- 两个配置项已定义但引擎未消费（有意保留）：`terminate_after_current_task`（界面用两个独立按钮）、`expert.role_prompt`（M0 V-5 的 prompt 注入预留）
- ~~文档已按 v1.12 修订但源码尚未跟进~~ **已于 2026-10-15 完成源码跟进（79/79 测试通过）**：AccountConfig 新增 `model_candidates` / `fallback_models`（账号自有池，null 回退全局兼容列表）；DegradationMatrix 按各账号自有池展开（ModelFirst / AccountFirst 聚合语义，同名模型不同账号 = 不同组合）；兜底改为「账号顺序 × 账号内兜底池顺序」取第一个可用组合（`FallbackConfig.model` / `estimated_unit_price` 已从代码移除，报告 `fallback_summary.model` 记录实际用过的兜底模型列表）；预检改为「降级矩阵为空」报错 + 兜底已启用但无兜底池告警；配置页账号卡内直接编排该账号的候选/兜底池；配置向导把模型写入各账号自有池副本；示例配置与相关测试同步更新
- v1.12 收尾（同日下午）：修正 EffectiveModelPool 误导注释、清理 EarliestRestore 死代码、更新降级策略 ToolTip、修 6 处测试 CS8604（警告 15→9，余为 TaskFileStoreTests xUnit1031 P4 遗留）；实际运行配置已迁移为账号自有池（= 原全局列表深拷贝，语义等价；备份 `config.json.backup-20261015133535`）
