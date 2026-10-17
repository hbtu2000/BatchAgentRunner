# BatchAgentRunner UI 设计系统 v1.0

> **角色**：UI Designer（像素君）｜面向开发的交付规范
> **配套文档**：`docs/PRD-AI批处理任务调度工具-v1.0.md`、`docs/DESIGN-BatchAgentRunner-v1.0.md`
> **平台**：Avalonia 12 跨平台桌面（Windows 优先，macOS / Linux 兼容）｜**双主题：浅色 + 深色**
> **版本**：1.0（2026-10-15）

---

## 0. 本文档用途

一份**可直接交给前端（Avalonia/XAML）开发**的设计系统，覆盖：

- 设计令牌（Design Tokens）——颜色 / 字体 / 间距 / 圆角 / 阴影 / 动效，以 CSS 变量与 Avalonia `ResourceDictionary` 双形态给出；
- 组件库规范——每个组件的状态、尺寸、间距、可访问性约束；
- 三大页面（配置 / 监控 / 报告）的布局与信息架构；
- 六种批级状态视图（Idle / Running / Waiting / Blocked / Terminated / Completed）；
- 关键交互模式（实时输出流、降级 toast、自动关机二次确认 + 倒计时、续跑 / 强制重跑）；
- 响应式、跨平台、WCAG AA 可访问性；
- 设计 → 开发对接清单。

> 本文档是**规范**，不是代码。所有 token 值已定稿，开发按第 9 章映射为 `Styles.xaml` / `Resources`。

---

## 1. 设计原则（美观 + 易用，无人值守调度场景）

| 原则 | 落地要求 |
|------|---------|
| **状态永远可见** | 批级状态、单任务状态、当前账号/模型/专家、等待倒计时、冷却恢复点——任一时刻用户都能回答"现在跑到哪了、为什么停、什么时候恢复"。 |
| **防误操作优先** | 危险操作（删除/格式化/`git push --force`）默认拒绝自动批准；自动关机、终止、强制重跑一律二次确认 + 可撤销倒计时。 |
| **信息密度可调，默认紧凑** | 专业工具用户需要高信息密度；列表默认紧凑行高，关键字段常驻，详情走抽屉而非新窗口。 |
| **预期行为 ≠ 错误** | "时段外跳过""额度冷却跳过"是预期调度，不报红、不弹错误、不污染误切换率；仅用中性 toast 提示。 |
| **一致性即信任** | 同一状态色、同一按钮语义、同一徽标形状贯穿三页；状态色速查表（§3.5）为唯一事实来源。 |
| **双主题无功能差异** | 浅色用于配置/白天的精细编排；深色用于长时间无人值守监控。两套主题仅换皮，布局与交互完全一致。 |

---

## 2. 设计基础 · Design Tokens

### 2.1 双主题策略

- 主题通过根级属性 `[Theme="Light"|"Dark"]`（或 Avalonia `ActualThemeVariant`）切换，组件**只引用 token、不写死颜色**。
- 所有 token 成对定义（`-light` / `-dark` 仅语义层区分，组件引用同名变量）。
- 深色主题背景取深蓝灰（非纯黑），降低长时间监控的眩光；状态色在深色下提高明度以保证 4.5:1 对比。

### 2.2 颜色系统

#### 2.2.1 中性与表面（浅色 / 深色）

| Token | 浅色值 | 深色值 | 用途 |
|-------|--------|--------|------|
| `--bg-app` | `#F5F7FA` | `#0F1419` | 应用最底层背景 |
| `--bg-surface` | `#FFFFFF` | `#1A2029` | 卡片 / 面板 |
| `--bg-surface-2` | `#F0F3F7` | `#222A35` | 次级面板 / 输入框底 |
| `--bg-hover` | `#EAF0F7` | `#2A3441` | 行/按钮 hover |
| `--bg-active` | `#DCE7F3` | `#33404F` | 选中 / 激活行 |
| `--border-subtle` | `#E3E8EF` | `#2C3542` | 分隔线 / 卡片描边 |
| `--border-strong` | `#C7D0DC` | `#3C4858` | 输入框描边 / 焦点环外圈 |
| `--text-primary` | `#1B2430` | `#E8EDF3` | 主文本 |
| `--text-secondary` | `#5A6675` | `#A6B2C0` | 次级文本 / 标签 |
| `--text-muted` | `#8A95A3` | `#6B7686` | 占位 / 禁用 / 时间戳 |

#### 2.2.2 品牌主色（可替换为自定义品牌色，见 §9.4）

| Token | 浅色值 | 深色值 | 用途 |
|-------|--------|--------|------|
| `--color-primary` | `#2563EB` | `#4C8DFF` | 主按钮 / 链接 / 激活态 |
| `--color-primary-hover` | `#1D4FD8` | `#6BA0FF` | 主按钮 hover |
| `--color-primary-soft` | `#E8F0FE` | `#1E2C44` | 主色浅底（选中标签 / 软按钮） |
| `--color-primary-contrast` | `#FFFFFF` | `#0F1419` | 主色上的文字 |

> 主色默认为专业蓝（信任 / 技术感）。若需品牌化，仅替换 `--color-primary` 系列 6 个值，其余不变。

#### 2.2.3 语义状态色（成功 / 警告 / 错误 / 信息）

| Token | 浅色值 | 深色值 | 用途 |
|-------|--------|--------|------|
| `--color-success` | `#16A34A` | `#3FCB6E` | 完成 / 可用 |
| `--color-success-soft` | `#E6F6EC` | `#15301F` | 成功浅底 |
| `--color-warning` | `#D97706` | `#F5A623` | 等待 / 注意 / 额度冷却 |
| `--color-warning-soft` | `#FDF0DB` | `#33260F` | 警告浅底 |
| `--color-error` | `#DC2626` | `#F2655E` | 失败 / 危险 / 阻断 |
| `--color-error-soft` | `#FBE8E8` | `#341A1A` | 错误浅底 |
| `--color-info` | `#2563EB` | `#4C8DFF` | 信息 / 切换提示 |
| `--color-info-soft` | `#E8F0FE` | `#1E2C44` | 信息浅底 |

#### 2.2.4 任务状态色（§3.5 速查的唯一来源）

| 状态 | 浅色点色 | 深色点色 | 语义 |
|------|---------|---------|------|
| Pending（待处理） | `#8A95A3` | `#6B7686` | 灰，未开始 |
| Running（执行中） | `#2563EB` | `#4C8DFF` | 蓝，带脉冲动效 |
| Done（已完成） | `#16A34A` | `#3FCB6E` | 绿 |
| Failed（失败） | `#DC2626` | `#F2655E` | 红 |
| Interrupted（中断） | `#D97706` | `#F5A623` | 橙，可续跑 |

#### 2.2.5 批级状态色（§5 视图的唯一来源）

| 批级状态 | 主色 | 浅底 | 语义 |
|---------|------|------|------|
| Idle | `#8A95A3` | `--color-*-soft` 灰 | 空状态引导 |
| Running | `#2563EB` | info-soft | 进行中 |
| Waiting | `#D97706` | warning-soft | 倒计时等待（预期） |
| Blocked | `#DC2626` | error-soft | 阻塞，需人工 |
| Terminated | `#8A95A3` | 灰 soft | 已终止，可续跑 |
| Completed | `#16A34A` | success-soft | 完成（可能含失败） |

### 2.3 字体排印

| Token | 值 | 说明 |
|-------|----|------|
| `--font-ui` | `'Segoe UI', 'Inter', system-ui, sans-serif` | Windows 优先；mac 回退 SF Pro；Linux 回退 Noto Sans |
| `--font-mono` | `'JetBrains Mono', 'Cascadia Code', Consolas, monospace` | 日志流 / 模型名 / 账号 ID / 重置时间 |
| `--fs-xs` | 12px | 时间戳、脚注 |
| `--fs-sm` | 13px | 列表次级、标签 |
| `--fs-base` | 14px | 正文、表单 |
| `--fs-md` | 16px | 区块标题 |
| `--fs-lg` | 20px | 页面标题 |
| `--fs-xl` | 28px | 统计卡大数字 |
| 字重 | 400 / 500 / 600 / 700 | 正文 400，标签/按钮 500，标题 600 |
| 行高 | 1.4（正文）/ 1.25（标题）/ 1.5（日志多行） | — |

### 2.4 间距 / 圆角

| Token | 值 | 用法 |
|-------|----|------|
| `--sp-1` | 4px | 图标与文字间距 |
| `--sp-2` | 8px | 控件内紧凑间距 |
| `--sp-3` | 12px | 列表项内边距 / 卡片内小间距 |
| `--sp-4` | 16px | 卡片内边距 / 区块间距 |
| `--sp-5` | 20px | 区块间 |
| `--sp-6` | 24px | 页面边距 / 大区块 |
| `--sp-8` | 32px | 页面大留白 |
| `--radius-sm` | 6px | 按钮、输入、徽标 |
| `--radius-md` | 10px | 卡片、抽屉 |
| `--radius-lg` | 14px | 对话框、大面板 |
| `--radius-pill` | 999px | 状态胶囊、标签 |

### 2.5 阴影与层级

| Token | 值（浅色） | 值（深色） | 用途 |
|-------|-----------|-----------|------|
| `--shadow-sm` | `0 1px 2px rgb(16 24 40 / .06)` | `0 1px 2px rgb(0 0 0 / .4)` | 卡片静态 |
| `--shadow-md` | `0 4px 12px rgb(16 24 40 / .10)` | `0 4px 12px rgb(0 0 0 / .5)` | hover / 抽屉 |
| `--shadow-lg` | `0 12px 32px rgb(16 24 40 / .16)` | `0 12px 32px rgb(0 0 0 / .6)` | 对话框 / toast 浮层 |
| `--z-base` | 0 | — | 页面 |
| `--z-drawer` | 40 | — | 右侧详情抽屉 |
| `--z-toast` | 60 | — | toast |
| `--z-modal` | 80 | — | 对话框 |

### 2.6 动效

| Token | 值 | 说明 |
|-------|----|------|
| `--motion-fast` | 120ms ease | 按钮、hover |
| `--motion-base` | 200ms ease | 抽屉滑入、卡片展开 |
| `--motion-slow` | 320ms ease | 页面切换、状态过渡 |
| `--pulse-running` | 1.2s 无限脉冲 | Running 状态点 |
| 降级规则 | **尊重 `prefers-reduced-motion`** | 开启时禁用脉冲 / 滑入，改为直接显隐（§8） |

---

## 3. 组件库规范

> 每个组件：默认态 + 交互态（hover/active/focus/disabled）+ 尺寸。所有组件在浅色/深色下仅换 token，结构一致。

### 3.1 基础输入组件

#### Button（按钮）
- 变体：`primary`（主色填充）/ `secondary`（描边）/ `ghost`（无底，仅文字+图标）/ `danger`（错误色，仅终止/删除/强制重跑）。
- 尺寸：`sm`（28px 高，13px）/ `md`（34px，14px，默认）/ `lg`（40px，16px）。
- 状态：hover（变深/浮起 1px）、active（下沉）、focus-visible（2px 主色焦点环，offset 2px）、disabled（opacity .5 + 禁指针）。
- 规则：主操作（开始/保存）每页至多 1 个 primary；破坏性操作必须 danger + 二次确认。

#### Input / TextArea
- 内边距 10–12px，圆角 `--radius-sm`，底 `--bg-surface-2`，描边 `--border-strong`。
- focus：去描边、加 3px 主色 soft 光环（`box-shadow: 0 0 0 3px var(--color-primary-soft)`）。
- 错误态：描边 `--color-error`，下方 12px 红字说明。

#### Select（下拉）
- 同 Input 外观；展开为浮层（`--shadow-md`），选项高 32px，hover `--bg-hover`。

#### Switch（开关）
- 44×24px 轨道，开态 `--color-primary`，关态 `--border-strong`；聚焦环；**危险开关（如自动关机）开启时轨道转 `--color-warning` 提示**。

#### Slider（滑块，用于超时/冷却/倒计时微调）
- 轨道 4px，拇指 16px 圆，拖拽时主色。

#### TagInput（标签输入，用于模型池 / 工作空间名 / 白名单）
- 一行可增删的胶囊（`--radius-pill`，primary-soft 底）；退格删最后一个；占位"输入后回车添加"。

### 3.2 数据展示组件

#### StatusPill（状态胶囊，贯穿全应用）
- 形态：左侧 8px 圆点（状态色，Running 带脉冲）+ 文字标签 + 可选计数。
- 变体：`task-*`（§2.2.4）/ `batch-*`（§2.2.5）。
- 禁止用纯文字颜色表达状态——**必须带点**，保证色盲可辨。

#### StatCard（统计卡，监控页顶部）
- 结构：大数字（`--fs-xl`，状态色）+ 标签（`--text-secondary`，13px）+ 可选小趋势。
- 一组 5 张：Pending / Running / Done / Failed / Interrupted；另有"已跳过"中性卡。
- 间距：卡间 `--sp-4`，整组与下方列表 `--sp-5`。

#### ProgressBar（总进度条，监控页顶）
- 堆叠分段：done(绿) / failed(红) / interrupted(橙) / running(蓝, 动态) / pending(灰)。
- 高度 8px，圆角 `--radius-pill`；右侧显示 `已完成 X / 总数 Y`（如 `23 / 50`）。

#### Card（卡片 / 面板）
- 白/深色面，`--radius-md`，`--shadow-sm`，hover 升 `--shadow-md` + 上移 1px（仅可点击卡）。
- 标题区 14px/600 + 右侧操作区。

#### Table（报告页 / 账号用量表）
- 斑马纹关（紧凑专业风用行 hover 代替）；表头 13px/600 `--text-secondary`；行高 36px；数字列右对齐、等宽字体。

#### TaskRow（任务列表项，监控页核心）
- 布局（左→右）：状态点｜指令摘要（截断 1 行，tooltip 全文）｜当前账号·模型·专家（mono，次级色）｜耗时（mono，xs）｜徽标（兜底/重试/部分输出）。
- 行高 44px（紧凑）；hover `--bg-hover`；点击开详情抽屉。
- 右键/悬浮显示"跳过/重跑本条"等（P1 受控）。

### 3.3 反馈组件

#### Toast（轻提示，右下浮层）
- 四类样式：info（切换/兜底）/ warning（时段外/冷却跳过，中性）/ error（危险操作命中、失败）/ success（完成）。
- 内容：`图标 + 标题 + 一行详情`（如"账号A·Hy4 额度冷却，恢复 02:14"）。
- 自动消失 4s（error 6s，可手动关）；同一来源 5s 内合并，避免刷屏。
- **时段外/冷却跳过用 warning 中性色，不报红**（原则：预期行为≠错误）。

#### Dialog（对话框，模态，`--shadow-lg`）
- 两种刚需：
  1. **自动关机确认**：标题"即将自动关机"+ 60s 可撤销倒计时条 + "取消关机"主按钮 + "立即关机"次按钮。倒计时结束前默认不关。
  2. **危险操作 / 强制重跑确认**：列出影响（如"将清空 N 条部分输出"），勾选"我已知晓"才可确认。
- 遮罩 `--bg-app` + 50% 透明；ESC 关（取消类）；focus 锁在弹窗内。

#### Drawer（任务详情抽屉，右侧，`--z-drawer`）
- 宽 420px（≥1280）/ 全宽以下自适应；滑入 `--motion-base`。
- 内容分区：指令（只读，mono 块）/ 实时输出流（mono，可滚动，自动追底）/ 结果（成功绿框）/ 错误（红框，含失败归因）/ 元数据（账号·模型·专家·耗时·重试）。
- 底部常驻"续跑本条 / 强制重跑本条"。

#### Tooltip
- 12px，深色底（`--text-primary` 反相），延迟 300ms；用于状态色含义、问号图标解释降级策略等。

#### Skeleton（加载占位）
- 启动预检 / 加载任务时，列表行以 `--bg-surface-2` 脉冲占位，禁用交互。

### 3.4 导航组件

#### Sidebar（左侧主导航，应用框架）
- 宽 200px（收起 56px）；三项：⚙ 配置 / ▶ 监控 / 📊 报告；监控项显示批级状态胶囊。
- 底部：主题切换（浅/深图标）、版本号。

#### Tabs（配置页分组 / 监控子视图）
- 下划线式，激活态主色 + 2px 底线；分组：任务与工具 / 工作空间 / 账号池 / 模型池 / 专家 / 兜底 / 危险白名单 / 调度策略 / 自动关机。
- 移动/窄屏降级为下拉选择。

### 3.5 状态色速查（唯一事实来源）

```
任务： Pending=灰  Running=蓝(脉冲)  Done=绿  Failed=红  Interrupted=橙
批级： Idle=灰  Running=蓝  Waiting=橙(倒计时)  Blocked=红  Terminated=灰  Completed=绿
语义： success=绿  warning=橙  error=红  info=蓝
规则： ① 状态必须带点，不只靠颜色  ② 时段外/冷却跳过=中性 warning，不报红
```

---

## 4. 页面设计

### 4.1 应用框架（三页共用）

```
┌──────────┬───────────────────────────────────────────────┐
│ Sidebar  │  TopBar: [批级状态胶囊]  搜索/快捷    [主题] [开始/停止] │
│ ⚙配置    ├───────────────────────────────────────────────┤
│ ▶监控 ●  │                                                 │
│ 📊报告    │            当前页面内容（配置/监控/报告）          │
│ ──────   │                                                 │
│ [主题]   │                                                 │
│ v1.0     │                                                 │
└──────────┴───────────────────────────────────────────────┘
```
- TopBar 右侧主按钮随页面变：配置页=「保存配置」，监控页=「开始/停止/续跑」，报告页=「导出」。
- 批级状态胶囊常驻 TopBar，点击跳转监控页。

### 4.2 配置页（最高复杂度，分组 Tabs）

目标：把 9 个配置域拆成**可独立理解的分组**，账号池为核心。

- **账号池**（重点）：账号卡列表，支持**拖拽排序 + 启停 Switch + 删除**。
  - 每张卡内：DisplayName、CredentialRef（显示为 `profile:xxx` 掩码，不显明文）、Note。
  - 卡内折叠区「编排自有模型池」：TagInput / 列表增删，每项可设**免费时段**（Start/End 时间 + 星期多选），并实时预览「周日 22:00 → 周一 06:00」跨零点命中说明。
  - 卡内「兜底模型池」：独立有序列表（不参与时段过滤）。
- **模型池（全局兼容）**：仅对未配置自有池的账号生效的共享列表，标注"兼容字段"。
- **任务与工具**：任务文件路径、agent_kind（WorkBuddy/CodeBuddy/Mock）、cli_path。
- **工作空间**：多空间 CRUD + 全局默认 + 本批默认（三级覆盖提示）。
- **专家**：专家/专家团选择（不支持时降级为角色 prompt 注入的说明）。
- **兜底**：全局开关（默认关）+ 单任务/单批成本上限。
- **危险白名单**：TagInput，黑名单命中永不自动批准（红字说明）。
- **调度策略**：并发(默认1)/重试/超时/总上限/降级策略(ModelFirst/AccountFirst 带 tooltip 解释)。
- **自动关机**：开关 + 仅失败率0 选项 + "60s 可撤销倒计时"说明。
- 底部固定操作条：「保存配置」（primary，重写 config.json）+「配置向导」（首次推荐）。

### 4.3 执行监控页（主页面）

```
TopBar: [批级:Running●]                        [停止] [续跑]
──────────────────────────────────────────────────────────
[总进度条 ▓▓▓░░ 23/50]   [Pending 12][Running 1][Done 23][Failed 2][Interr 1][跳过 5]
──────────────────────────────────────────────────────────
任务列表（TaskRow ×N，可搜索/筛选状态）：
 ● 重写登录页文案……        A·Hy4·expertSEO    00:42  [重试1]
 ● 生成周报摘要             B·Hy3·—            ——     [兜底]
 ● 清理临时文件(危险)       —·—·—              失败✗   危险操作已拦截
──────────────────────────────────────────────────────────
[底部实时输出流（当前任务，mono，自动追底）…………]
```
- 顶部：进度条 + 6 张 StatCard（含"已跳过"中性卡）。
- 中部：任务列表（默认按文件顺序；筛选 chip：全部/待处理/失败/已跳过）。
- 底部：当前任务实时输出流（折叠可展开；点击任意 TaskRow 开右侧 Drawer 看完整指令+输出+结果）。
- Toast 区：右下，切换/兜底/时段/冷却/危险操作提示。
- 批级状态变化时 TopBar 胶囊与背景色随之变化（Waiting 转橙 + 显示倒计时）。

### 4.4 报告页（P1 汇总）

- 顶部：成功/失败/跳过计数卡 + 总成本（兜底花费，标注"估算"口径）+ 总耗时。
- 切换时间线：竖向时间轴，区分四类原因（时段外/冷却/额度/兜底），每段标注账号·模型·耗时。
- 账号/模型用量表：每账号调用次数、成功/失败、花费。
- 失败清单：可点击跳转原任务 / 复制错误归因。
- 导出：JSON / Markdown（复用引擎报告 writer）。

---

## 5. 六种批级状态视图

批级状态是监控页的"总开关"，决定 TopBar、主区与可操作按钮。

| 状态 | 视觉 | 主区内容 | 可操作 |
|------|------|---------|--------|
| **Idle** | 灰胶囊 | 空状态引导插画 + 「配置向导」「选择任务文件并开始」CTA | 开始 |
| **Running** | 蓝胶囊 + 脉冲 | 进度条 + 实时 + 任务流 | 停止（协作式，可选"等当前任务完成再停"） |
| **Waiting** | 橙胶囊 | 倒计时条"下一恢复 02:14" + 原因（时段外/冷却） | **取消等待**（转 Blocked）/ 停止 |
| **Blocked** | 红胶囊 | 提示"全部组合暂不可用，下一恢复点 HH:MM" + 已跳过计数 | **续跑**（到点或恢复后）/ 强制重跑 |
| **Terminated** | 灰胶囊 | "已被终止，N 条未完成" + 部分输出保留提示 | **续跑**（跳过 done）/ **强制重跑** |
| **Completed** | 绿胶囊 | "全部到达终态" + 含失败数（若有）+ 报告入口 | 查看报告 / 重新运行 |

> 规则：Waiting / Blocked / Terminated 都不是"错误"——用中性/对应语义色，提供明确下一步（续跑/取消等待），杜绝"卡死无出口"。

---

## 6. 关键交互模式

### 6.1 实时输出流
- 事件驱动（引擎 `BatchEvent` → `Dispatcher.UIThread` 合并，≤2s 刷新，非轮询）。
- 当前任务输出在底部流 + Drawer 内完整流，mono 字体，自动追底（用户上滚则暂停追底，显示"回到最新"）。
- 截断：`ResultMaxKB` 超 100KB 截断并提示全量日志路径。

### 6.2 降级 / 切换 Toast
- 触发：model_switched / account_switched / fallback_used / model_window_skipped / model_cooldown_skipped。
- 文案模板：`[账号]·[模型] [原因]，[下一动作]`。
  - 例：`A·Hy4 额度冷却，恢复 02:14（已切 B·Hy3）`
  - 例：`B·Hy3 时段外（22:00–06:00），已跳过，不计失败`
- 中性 warning 不报红；error 仅用于危险操作命中 / 任务失败。

### 6.3 自动关机二次确认 + 倒计时
- 触发：批级 Completed 且满足条件（默认允许带失败；勾选"仅失败率0"时存在失败则阻止并标注原因）。
- 弹 Dialog：标题"即将自动关机" + 60s 可撤销倒计时条（warning 色）+ 「取消关机」(primary) + 「立即关机」(danger)。
- 硬约束顺序：先全部落盘 + 回收子进程 → 再关机；权限不足降级提示手动关机并记日志。
- 倒计时内点"取消关机"→ 关闭 Dialog，批级回 Completed 静置。
- **实现状态（2026-10-17 补齐，债务 P0-10）**：`Views/ShutdownCountdownWindow` 即本节对话框;
  标题区 + 剩余秒数 + 「取消关机」(primary) / 「立即关机」(danger)，倒计时秒数取 `auto_shutdown.countdown_seconds`。
  策略下沉在 `Core/Engine/AutoShutdownCoordinator`：只有"倒计时走完未被取消"才调用系统关机；
  窗口弹不出来 / 进程异常 → 一律不关机并把原因写进报告 `shutdown` 段。

### 6.4 取消等待 / 续跑 / 强制重跑
- 取消等待：Waiting → 用户主动，转 Blocked（当前任务 interrupted）。
- 续跑：以任务文件/sidecar `status` 为准，`done` 跳过并计数"已跳过 N"；`Interrupted` 重跑清旧部分输出（旧值留日志）。
- 强制重跑：二次确认（列出影响），`ForceRerun=true` 重跑全部。

---

## 7. 响应式与跨平台

- **桌面优先**：推荐最小布局宽 1280px（Sidebar 200 + 内容）；下限 1024px（Sidebar 收起 56，抽屉全宽）。
- 断点：`≥1280` 完整；`1024–1279` 紧凑（统计卡 5→横向滚动 / 换行）；`<1024` 适配（Tabs 转下拉，Drawer 全宽）。
- 字体回退链已定义（§2.3），mac/Linux 不依赖 Segoe UI。
- 高 DPI：所有尺寸用与设备无关单位（Avalonia 默认），图标用矢量 / 字体图标。
- 快捷键：Ctrl/Cmd+R 开始、Ctrl/Cmd+. 停止、Ctrl/Cmd+K 全局搜索任务（一致性跨平台）。

---

## 8. 可访问性（WCAG AA）

- **对比度**：正文文本 ≥ 4.5:1，大文本/状态点 ≥ 3:1（深色主题已提亮状态色校验）。
- **状态不只靠颜色**：所有状态配形状/文字/图标（StatusPill 带点 + 标签）。
- **键盘可达**：Tab 顺序 = 视觉顺序；所有可操作元素 focus-visible 环；Dialog 焦点陷阱；Sidebar/抽屉可 `Esc` 关。
- **屏幕阅读器**：Avalonia AutomationProperty 暴露名称；状态变化（批级/任务）发 `Notification`；实时输出流标记 `live region`（polite）。
- **动效敏感**：检测 `prefers-reduced-motion` → 禁用脉冲/滑入，改为瞬时显隐；倒计时保留数字不依赖动画。
- **触控**：若触屏，目标 ≥ 44px（按钮/行已 ≥ 34，关键操作 40+）。
- **错误预防**：所有破坏性操作二次确认 + 影响清单；凭据不明文、脱敏显示。

---

## 9. 设计 → 开发对接

### 9.1 Token → Avalonia ResourceDictionary
- 颜色：`<Color x:Key="Primary">#2563EB</Color>` + `[Theme=Dark]` 覆盖；语义色同名。
- 间距/圆角/字体：`<Thickness>` / `<x:Double>` / `<FontFamily>` 资源。
- 阴影：`<BoxShadows>`（Avalonia 12 支持）；动效：`Transition` / `Animation`。

### 9.2 组件命名与文件结构（实际落地）
```
src/BatchAgentRunner.Desktop/
  Styles/
    AppStyles.xaml      资源字典：Design Token（Light/Dark 双主题字典）+ 关键 ControlTheme（Card，显式引用）
                         + EqualsTextConverter 实例。经 App.axaml 的 MergedDictionaries → ResourceInclude 合并进 Application.Resources
    GlobalStyles.axaml  <Styles> 文件：Button/TextBox 全局 <Style>（全站自动应用，含 :primary/:danger 变体）
                         + 状态点全局 <Style>（Ellipse.st-* / Ellipse.batch-*，按 class 着色，双主题自动切换）。
                         经 App.axaml 的 Application.Styles → StyleInclude 引入。
                         ⚠ Avalonia 12 约束：Styles 集合不允许放顶层 ControlTheme（编译通过但运行时抛
                         InvalidOperationException），控件级主题须用普通 <Style> 选择器或放在资源字典内带键 ControlTheme
  Converters/
    StatusDotHelper.cs  附加属性（TaskStatus / BatchStatusText / NavPage / NavActivePage）：
                         XAML 无法直接绑定 Classes，故用附加属性把状态映射为 CSS 类，由 GlobalStyles 命中着色
    StatusClassConverters.cs  EqualsTextConverter（条件可见性用）
  Views/   MainWindow  ConfigView  MonitorView  ReportView
  ViewModels/ 如上 DESIGN §3.2
```
> **实现约束（Avalonia 12）**：① 全局无 key 的 `<Style>` / 隐式 `ControlTheme` 只能放在 `<Styles>`（Application.Styles），
> 不能放在 ResourceDictionary；② `Classes` 属性不接受 `{Binding}`，状态着色改用 `StatusDotHelper` 附加属性；
> ③ `ResourceInclude` 须置于 `Application.Resources` 的 `MergedDictionaries` 内，而非直接作为 Resources 子项。

### 9.3 交付清单（开发自测用）
- [ ] 双主题切换无布局错位、对比度达标
- [ ] 六种批级状态视图均按 §5 渲染且按钮正确
- [ ] StatusPill / StatCard / ProgressStack 三页复用同一组件
- [ ] 自动关机 Dialog 60s 倒计时可撤销、落盘优先
- [ ] 降级 toast 中性不报红、5s 合并
- [ ] 键盘全可达、reduced-motion 生效
- [ ] 账号卡拖拽排序 + 免费时段跨零点预览正确

### 9.4 品牌化替换点
仅需替换 `--color-primary` 系列 6 值（§2.2.2）；其余语义/状态色保持稳定以保证可访问性。

---

**UI Designer · 像素君**　｜　设计系统 v1.0　｜　可直接交付 Avalonia 开发
