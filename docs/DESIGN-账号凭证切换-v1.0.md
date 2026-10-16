# 凭证备份与账号切换设计（Credential Vault）

**文档负责人：** 工程
**状态：** 🟢 已实现（2026-10-15：Core 端口 + Infrastructure 实现 + 引擎预检/任务边界集成 + MockAdapter 全链路 + 21 个测试；Live 路径仍以 M0 V-4a~V-4e 实测为准，实测后只改 `CredentialLocationRegistry`）
**最后更新：** 2026-10-15
**版本：** 1.1

**实现落点（2026-10-15）：**

| 设计条目 | 实现 |
|----------|------|
| §3 Live/Snapshot/Vault 模型 | `Core/Abstractions/ICredentialVault`（端口）+ `Infrastructure/CredentialVault/FileCredentialVault` |
| §4.2 manifest 契约 | `Core/Domain/CredentialVault.cs`（SnapshotManifest，snake_case） |
| §4.3 位置注册表 | `Infrastructure/CredentialVault/CredentialLocationRegistry`（WorkBuddy 未配置，V-4b 前拒绝 Vault 操作） |
| §5 引导登录 | `Infrastructure/CredentialVault/GuidedLoginOrchestrator` + `CliLoginLauncher`；配置页账号卡「引导登录」按钮 |
| §6 切换（互斥/保活回写/原子回滚） | `FileCredentialVault.SwitchToAsync`（②回写失败中止切换；③失败自动回滚）；配置页「切换到此账号」 |
| §7 引擎衔接 | `BatchEngine` 构造可选注入 `ICredentialVault`：预检前切换（无快照 → 停用并提示引导登录；通过 → 更新 `last_verified_at`）、任务边界 `EnsureLiveCredentialAsync`（发 `AccountSwitched` 事件）；并发>1 时预检告警 |
| §8 安全 | DPAPI（`System.Security.Cryptography.ProtectedData` 10.0.0，CurrentUser）加密快照；`vault\audit.log` 审计只记账号 id + 哈希前 8 位；manifest 不含凭证内容 |
| §11 测试 | `tests/CredentialVault/`：快照 6 + 切换 8 + 引导登录 4 + E2E 2 + DPAPI 往返（21 用例，MockAdapter 模拟 Live 凭证与 refresh token 轮换） |

**上游文档：**
- 技术设计：`docs/DESIGN-BatchAgentRunner-v1.0.md`（本设计为其第 6 章「Agent 适配层」账号凭据策略的展开，对应 M0 V-4）
- 配置说明：`docs/CONFIG-配置项说明.md`（`accounts[].credential_ref`）

---

## 1. 背景与目标

PRD 的核心差异化价值是「多账号接力」，但引擎层此前只约定了**切换策略**（DegradationMatrix 遍历顺序），没有解决**凭据从哪来、怎么切**的问题（V-4 开放问题）。

本设计采用**凭证快照 + 文件级切换**方案，两步闭环：

1. **引导登录 → 备份**：用户在界面发起引导登录，工具拉起 CLI 的交互式登录（浏览器 OAuth），登录成功后把 CLI 的登录凭证文件**备份出来，按账号命名**存入本地 Vault。
2. **切换 = 复制回填**：需要用账号 A 执行任务时，把 Vault 里 A 的备份凭证**复制到 CodeBuddy / WorkBuddy 的当前凭证位置（Live）**，CLI 即以 A 的身份运行。

方案成立的前提：两家 CLI 的登录态均为**文件型存储**（或可导出为文件），且不存在强设备绑定。

---

## 2. 本机侦查结论（2026-10-15 实测，Windows 11）

| 对象 | 结论 |
|------|------|
| CodeBuddy CLI（CodeBuddy Code） | 配置目录 `~/.codebuddy/`，当前**无** `.credentials.json` / auth 类文件 → 登录态应在首次 CLI 登录后生成（按 Claude Code 系产品惯例预期为 `~/.codebuddy/.credentials.json`，**V-4a 实测确认**） |
| CodeBuddy IDE（CodeBuddy CN 桌面版） | 登录态在 `%APPDATA%\CodeBuddy CN\User\globalStorage\`（SQLite vscdb + 扩展目录），结构复杂，**不作为切换对象**——本工具只切换 CLI 的登录态 |
| WorkBuddy CLI | `~/.workbuddy/` 下未发现文件型凭证（connectors 凭证按用户 UUID 目录存放，桌面主登录态存储方式未知），**V-4b 实测确认** |
| Windows 凭据管理器 | 无 WorkBuddy / CodeBuddy 条目 → 两家均**未使用系统密钥库**存登录态，文件级备份方案可行概率高 |
| CodeBuddy 手册 | CLI 首次启动需选登录站点（Chinese Site / International / Enterprise / iOA）→ 站点选择也属于账号差异的一部分，需一并备份 |

> ⚠️ 侦查只确认了「目前没有」，不等于「登录后也没有」。第 10 章验证项是本设计的硬前置。

---

## 3. 核心模型：Live / Snapshot / Vault

```
┌─────────────────────────────────────────────────────────┐
│  Vault（仓库，工具私有目录，按账号命名）                    │
│  %LOCALAPPDATA%\BatchAgentRunner\vault\                  │
│    ├─ codebuddy\                                         │
│    │   ├─ account-a\  ← 账号 A 的快照集                   │
│    │   │   ├─ files\          (凭证文件本体)              │
│    │   │   └─ manifest.json   (清单+哈希)                 │
│    │   └─ account-b\ ...                                 │
│    └─ workbuddy\                                         │
│        ├─ account-a\ ...                                 │
│        └─ account-b\ ...                                 │
└─────────────────────────────────────────────────────────┘
                 │ 复制（切换）          ▲ 复制（备份/保活回写）
                 ▼                     │
┌─────────────────────────────────────────────────────────┐
│  Live（CLI 实际读取的当前凭证位置）                        │
│    CodeBuddy: ~/.codebuddy/.credentials.json 等           │
│    WorkBuddy: （V-4b 确认）                               │
└─────────────────────────────────────────────────────────┘
```

- **Snapshot（快照）**：某账号某时刻全部凭证文件的集合 + 清单。
- **Vault**：所有快照的持久化仓库，目录名 = `accounts[].id`（与冷却记录同键，要求稳定）。
- **Live**：CLI 正在使用的凭证文件。**同一时刻每个 CLI 只有一个身份。**

---

## 4. 目录与清单契约

### 4.1 快照目录

```
vault\<cli>\<account_id>\
  ├─ files\                    # 按原始相对路径镜像（保持子目录结构）
  │    └─ .credentials.json    # 例：CodeBuddy CLI
  └─ manifest.json
```

### 4.2 manifest.json（snake_case，与项目约定一致）

```jsonc
{
  "account_id": "account-a",          // 必须与配置中 accounts[].id 一致
  "cli": "codebuddy",                 // codebuddy | workbuddy
  "display_name": "A 账号",
  "site": "chinese",                  // 登录站点（CodeBuddy CLI 首启选择，切换时若不一致需提示）
  "backed_up_at": "2026-10-15T14:00:00+08:00",
  "last_verified_at": "...",          // 最近一次预检通过时间
  "files": [
    { "relative_path": ".credentials.json", "sha256": "…", "size": 1234 }
  ],
  "schema_version": 1
}
```

### 4.3 Live 位置注册表（代码内 `CredentialLocationRegistry`）

每个 CLI 注册：凭证文件路径列表（含相对路径映射）、进程名（用于互斥检测）、可选的「logout 清理动作」。**路径全部来自注册表，不散落硬编码**——M0 实测后只改注册表，不动流程代码。

---

## 5. 引导登录流程（登录成功 → 按账号备份）

```
界面「引导登录」
  → ① 选/新建 account_id（Vault 目录名）
  → ② 清场：确认无该 CLI 运行中的进程；删除/移走当前 Live 凭证（logout 或手工清理，V-4e 确认）
  → ③ 拉起 CLI 交互式登录（如 codebuddy login，浏览器 OAuth）
  → ④ 用户在浏览器完成登录，回到界面点「我已登录」
  → ⑤ 检测：Live 凭证文件出现 / mtime 变化（轮询 + 超时，如 5 分钟）
  → ⑥ 校验：调用 CLI 做一次极简请求（复用 REQ-010 预检逻辑），确认凭证有效
  → ⑦ 备份：Live 文件集 → vault\<cli>\<account_id>\files\，写 manifest（含 sha256）
  → ⑧ 界面提示成功；该账号进入可用账号池
```

关键点：
- **⑤ 的变化检测**以「文件出现或哈希变化」为准，不依赖 CLI 的退出码。
- **⑦ 备份的是文件副本，不是移动**——Live 始终保留一份，保证备份后 CLI 立即可用。

---

## 6. 切换流程（把备份复制回 Live）

```
引擎请求切换到账号 X（或用户手动「切换到此账号」）
  → ① 前置互斥：无该 CLI 的运行中进程（批处理空闲时才允许；运行中切换只发生在任务边界）
  → ② 保活回写：当前 Live 若属于账号 Y 且其文件哈希 ≠ Y 快照记录 → 先把 Live 复制回 Y 的快照
       （理由：CLI 运行期间会自动刷新 access/refresh token，最新 token 在 Live 里；
        不回写则 Y 的快照会随时间失效）
  → ③ 原子覆盖：X 快照 files → Live 位置（先写临时文件 + rename，失败自动回滚②的副本）
  → ④ 校验：极简请求确认身份 = X（对比 CLI 输出的账号标识，V-4c 确认可获取）
  → ⑤ 发出 AccountSwitched 事件（原账号、新账号、触发原因）
```

**保活回写（②）是本设计的灵魂**：把「Live 永远是最被频繁刷新的那份」这个事实，转化为「每次离开某账号时把 Live 还给它」，Vault 里的快照因此始终是各账号最新一次使用时的凭证。

失效兜底：refresh token 若有「闲置天数上限」，久未使用的账号切回时可能 AuthExpired → 预检发现 → 界面提示对该账号重新「引导登录」（流程同第 5 章，覆盖旧快照）。**不做自动续命。**

---

## 7. 与引擎 / 配置 / 界面的衔接

| 现有约定 | 本设计的落点 |
|----------|-------------|
| `accounts[].credential_ref` | 语义明确化为 **Vault 内账号目录名**（= `accounts[].id`， credential_ref 保留做显式覆盖，默认取 id） |
| `IAccountValidator` / REQ-010 预检 | 预检前先执行「切换到该账号」；预检即验证快照有效性，通过后更新 `manifest.last_verified_at` |
| `AuthExpired` 错误分类 | 触发「该账号需重新引导登录」提示 + 停用该账号走既有降级逻辑 |
| `AccountSwitched` 事件/日志 | 切换成功后发出，日志**只记账号 id 与文件哈希前 8 位，不记凭证内容** |
| 配置页账号卡 | 新增「引导登录」「切换到此账号」「快照状态（备份时间/最近验证/已失效）」 |
| MockAdapter | 模拟同一套 Vault 流程（假凭证文件），全链路可先行联调 |

---

## 8. 安全设计

1. Vault 目录 DPAPI 加密（Windows `CryptProtectData`，按当前用户绑定），明文只存在于内存与 Live 位置。
2. Vault 与 Live 均设置 ACL：仅当前用户可读。
3. 凭证内容不进日志、不进任务文件、不进报告（PRD 安全约定）。
4. 备份/切换操作写审计日志（时间、账号 id、动作、结果），不含内容。
5. 删除账号时同时删除其 Vault 快照，需界面二次确认。

---

## 9. 约束、风险与开放问题

| 风险 | 等级 | 应对 |
|------|------|------|
| 凭证非文件型（存 keychain/加密 SQLite） | 高（待 V-4b） | 降级方案：环境变量注入 token（若 CLI 支持）或改用 CLI 自带的多 profile 机制；实在不行该 CLI 仅支持单账号 |
| 设备指纹绑定（machineid / device-id 参与鉴权） | 中 | V-4c 实测：切换后是否需要同步备份 machineid 类文件；若强绑定则此方案不可行 |
| refresh token 闲置过期 | 中 | 预检拦截 + 引导重登；文档明示「长期不用的账号切回前先预检」 |
| 多账号高频轮换触发平台风控 | 中 | PRD 第 14 章已有约定：仅用户自有合法账号免费额度；切换频率跟随任务失败/额度耗尽，不做无意义轮换 |
| 切换时 CLI 进程仍存活导致文件被覆盖 | 中 | 互斥检测（进程名 + 任务状态机仅 Idle/任务边界允许切换） |
| 站点/租户不一致（A 账号国际站、B 账号国内站） | 低 | manifest 记录 site，切换时校验，不一致则提示并按账号携带站点配置 |

**开放问题：** WorkBuddy CLI 是否存在独立于桌面端的 `login` 命令；桌面端登录态是否与 CLI 共享（V-4b 一并确认）。

---

## 10. M0 验证项细化（V-4 拆分）

| 编号 | 验证内容 | 通过标准 |
|------|---------|---------|
| V-4a | CodeBuddy CLI 登录后凭证文件路径、格式、是否含 refresh token | 找到文件且为可复制的自包含 JSON |
| V-4b | WorkBuddy CLI 登录态存储位置与形态 | 同上；确认是否文件型 |
| V-4c | 备份→清空→回填后，CLI 无感使用回填身份 | CLI 正常调用，且能从输出/配置确认当前账号身份 |
| V-4d | refresh token 是否轮换、闲置多久过期 | 得到轮换行为与过期时长的结论，确定保活回写必要性 |
| V-4e | logout / 删除凭证后是否回到「干净未登录」态 | 可作为引导登录的可重复起点 |

---

## 11. 测试要点

- 快照备份：文件集哈希与 manifest 一致；重复备份覆盖并更新时间戳。
- 切换：保活回写触发条件（哈希不一致）；覆盖失败回滚；进程存活时拒绝切换。
- 预检集成：快照缺失账号预检直接失败并提示引导登录。
- 安全：日志/报告/任务文件中 grep 不到凭证片段。
- MockAdapter 全流程：登录模拟 → 备份 → 切换 → AuthExpired → 重登覆盖。
