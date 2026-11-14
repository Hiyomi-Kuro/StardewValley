# Phase 1.2.1：无 Root Mods 安全部署工具设计

- 日期：2026-11-14
- 状态：**设计已实现并在真机验收**（`Tools/deploy-mods-safe.ps1`，工具版本 `1.2.4`）。Phase 1.2.2 的逐项审计、
  实现映射与偏差记录见本文 §9 与 [Phase 1.2.2 报告](../audit/phase1.2.2-report.md)；Phase 1.2.3 的
  真机结果与两个 P0 修复见 [Phase 1.2.3 报告](../audit/phase1.2.3-report.md)；Phase 1.2.4 的
  REPLACE 真机验收与回滚报告修复见 [Phase 1.2.4 报告](../audit/phase1.2.4-report.md)
- 关联：[Phase 1.2 ARM64 真机验收报告](../audit/phase1.2-arm64-device-acceptance-report.md)、
  [Manifest 规范](../modding/manifest-spec.md)、[Loader 设计](phase1-loader-design.md)

---

## 1. 目标与非目标

### 1.1 目标

把 Phase 1.2 在真机上手工验证过的“无 Root、staging、逐文件 SHA-256、同文件系统 rename”流程固化为一个
**默认只读、apply 需显式授权、可审计、可恢复**的 Windows 主机侧工具：

- 只写游戏的应用专属外部目录 `files/Mods`；
- 不碰 `Saves`、不碰应用私有数据、不重装 APK、不使用 Root/su；
- 部署前给出完整计划（相对路径、字节数、SHA-256、现有冲突、备份位置）；
- apply 必须是两阶段：先写 staging 并逐文件校验，再逐个文件切换；
- 每个切换点都有 journal；失败/中断后可以从 journal 判断状态并回滚；
- 旧文件只备份、不自动删除；未知文件只报告、不删除；
- 输出稳定退出码、稳定错误类别和机器可读 JSON。

### 1.2 非目标

- 不做真实设备验收（本阶段只用 fake ADB；真机验收需要用户另行授权）。
- 不实现 SAF / `DocumentFile` / 系统文件选择器交互；也不声称 ADB 能替代 SAF 授权。
- 不安装、卸载、覆盖 APK，不修改签名配置，不写 `final/`。
- 不删除未知 Mod、不清理游戏生成的 `logs/`、`loader-report.txt`、`loader.log`、`.staging/`。
- 不做 Harmony、方法补丁、Mod API 或 P114 探针改动。
- 不实现 `renameat2(RENAME_EXCHANGE)` 或任何需要 NDK/原生 helper 的严格原子目录交换。

---

## 2. 事实来源与目标路径推导

### 2.1 Loader 实际使用的 Mods 根

`Mod/ModFramework.Runtime/Loader.cs`：

```csharp
internal const string ExternalModsRoot =
    "/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods";

internal static string DefaultModsRoot()
{
    // 正常路径：<ModFramework.Runtime.dll 所在目录>
    // 取不到 Location 时才回退到 ExternalModsRoot
}
```

`Tools/ModInjector/Program.cs` 把同一个字符串烘焙进 `ModFramework.Bootstrap.Start()`：

```csharp
DefaultModsDir = "/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods";
```

因此工具只接受一个目标：

```text
/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods
```

不接受调用方传入远端根路径，也不接受 `files`、`Saves`、`shared_prefs`、`databases` 或
`/data/data/<pkg>` 作为目标。

### 2.2 Mods 目录结构

`Mod/ModFramework.Runtime/ModDiscovery.cs` 与 `docs/modding/manifest-spec.md` 规定：

```text
Mods/
├── ModFramework.Runtime.dll      框架本体，必须在 Mods 根
├── <任意目录名>/manifest.json    每个 Mod 一个一层目录
├── <任意目录名>/<entryAssembly>
├── <任意目录名>/<privateAssemblies...>
├── loader.log / loader-report.txt   框架运行期生成（工具不部署、不删除）
└── logs/、.staging/                 框架运行期生成（工具不部署、不删除）
```

- 只扫一层子目录，不递归发现 Mod；
- 目录名以 `.` 或 `_` 开头会被 Loader 忽略；
- 裸 DLL 不会被执行；
- manifest 文件名固定、UTF-8、≤256 KiB、未知字段拒绝、`entryAssembly` 与
  `privateAssemblies` 只能是包内纯文件名。

工具会按这些约束做**部署前结构校验**，但 Loader 仍是 manifest 语义的最终权威。

### 2.3 设备与存储事实

| 事实 | 来源 | 对工具的影响 |
| --- | --- | --- |
| 已实测设备：vivo V1963A / Android 10 / API 29 / arm64-v8a | Phase 1.2 报告 | 只把 API 29 的无 Root 路径视为“已实测”；其他版本不自动宣称兼容 |
| 无 Root 部署已在 `files/Mods` 成功，11/11 SHA-256 一致 | Phase 1.2 报告 §2 | 目标目录成立；工具沿用 staging + rename |
| 应用非 debuggable，无 `run-as` | AGENTS.md §10.2 | 无法以应用 UID 执行探针；不能证明“应用一定能写” |
| `Saves` 位于 `files/Saves` | AGENTS.md §10.2 | 明确排除；工具任何写路径都不得以 `Saves` 开头 |
| Android 11+ 对 `Android/data` 的 ADB 访问因 OEM/版本而异 | 未在本项目实测 | apply 前用 staging 目录创建做能力探针；失败即停，不猜测、不绕过 |

**Scoped Storage / SAF 结论**：

- API 29 的 `files/Mods` 无 Root 部署已被本项目真机验证；
- API 30+ 是否允许 `adb shell` 写 `Android/data/<pkg>` **没有**本项目证据，工具不硬编码“支持”或“不支持”，
  而是在 apply 时真实创建 staging；创建失败即 `STORAGE_UNAVAILABLE` 并停止；
- SAF 需要用户在设备上交互授权，ADB 不能伪造该授权；本工具**不实现** SAF，也不会声称
  “ADB 写成功 == SAF 已授权”；
- 工具不会尝试 `MANAGE_EXTERNAL_STORAGE`、`appops`、`pm grant`、`su`、Magisk 或任何提权路径。

---

## 3. 安全边界

### 3.1 写白名单

远端只允许写以下两棵树：

```text
/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods/...
/sdcard/Android/data/com.chucklefish.stardewvalley/files/.mods-deploy/<txid>/...
```

其中 `<txid>` 必须匹配 `^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$`。

明确禁止：

- `files/Saves`、`files/shared_prefs`、`files/databases`、`files/` 下其他条目；
- `/data/data/<pkg>`、`/data/user/<n>/<pkg>`；
- `/sdcard` 其他目录、`/data/local/tmp`；
- APK、keystore、签名配置。

### 3.2 输入防护

源目录（主机侧）：

- 拒绝 reparse point / symlink / junction；
- 拒绝 `..`、绝对路径、盘符、反斜杠分隔符混淆、`:`（ADS）；
- 拒绝控制字符、换行、NUL、尾随空格/点、Windows 保留设备名；
- 拒绝大小写不敏感重复路径、Unicode NFC 归一化后重复路径；
- 单组件 UTF-8 字节数 ≤255，相对路径 ≤900 字符，文件数 ≤10,000，总字节 ≤4 GiB；
- 拒绝顶层 `.`/`_` 开头目录、`logs`、`.staging`、`loader.log`、`loader-report.txt`
  （这些是 Loader 生成物或会被忽略的目录）；
- 大文件不特殊处理，按普通文件分块计算 SHA-256 并 `adb push`。

Manifest：

- 严格 UTF-8、≤262,144 字节；
- 拒绝重复 JSON key（自写扫描器，含 `\uXXXX` 等价 key）；
- 拒绝未知顶层字段、错误类型、非法 `schemaVersion`；
- 校验 `id`、`name`、`version`、`entryAssembly`、`entryType`、`privateAssemblies`、
  `dependencies` 的基本 schema；
- `entryAssembly` / `privateAssemblies` 必须是纯文件名、`.dll` 结尾、无 `/` `\` `:` `..`；
- 声明文件必须存在且大小写完全匹配（Android 大小写敏感）；
- 同一包内声明重复、entry 与 private 重复、跨包 mod id 重复（大小写不敏感）→ 拒绝。

**ZIP Slip**：本工具不接收 ZIP、不调用 `unzip`、不在设备端解压，因此不存在 ZIP Slip 解压路径。
测试用“归档式相对路径”夹具（如 `a/../../evil.dll`）证明 manifest 与源树路径校验会拒绝它。

设备侧：

- Mods 根、`.mods-deploy` 根、`files` 根、目标文件及目标路径上的已存在父目录，
  逐一用 `test -L` 检查；发现 symlink → 拒绝；
- 目标文件已存在但内容不同 → 默认拒绝；只有 `-Overwrite <rel>` 明确列出的路径才允许替换；
- 未知文件/目录一律不删除、不移动、不 chmod；
- 所有远端 shell 参数经 POSIX 单引号转义；文件名中的 `'`、空格、括号、中文、Unicode 不参与
  shell 解释；
- 不使用 `su`、`sudo`、`root` 命令。

### 3.3 Saves 隔离

工具从不构造包含 `Saves` 的远端路径；`plan`/`verify`/`deploy`/`rollback` 的目标根都是常量。
自动测试会在 fake 设备里放一个 `Saves/save1.dat`，部署前后比较文件数与 SHA-256，并断言 ADB 日志中
没有 `Saves`、`shared_prefs`、`databases`、`/data/data` 等写入路径。

---

## 4. 事务模型与保证等级

### 4.1 为什么不是“原子部署”

Android 侧可用的是 `adb push`、`mv`、`mkdir`、`sha256sum` 等 toybox 命令，没有
`renameat2(RENAME_EXCHANGE)`，也不能保证多文件操作是一个系统调用。因此：

| 操作 | 保证等级 |
| --- | --- |
| 单个文件 `mv staging/x Mods/x`（同一文件系统） | **原子替换**（POSIX rename 语义） |
| `mv Mods/x backup/x` 后再 `mv staging/x Mods/x` | 两个原子操作；两步之间存在 x 短暂缺失的窗口 |
| 整个多文件部署 | **journal 两阶段事务**，不是原子事务 |
| 失败回滚 | **尽力回滚（best-effort）**；可证明归属的文件才恢复，未知状态保留现场 |
| 断电/进程被杀 | 由 journal + 哈希重新判定；不保证自动恢复，保证“不静默丢数据” |

**不把非原子操作称为原子操作**：本工具对外表述为
“逐文件原子 rename + journal 两阶段事务 + 尽力回滚”，不宣称整个 Mods 目录原子切换。

### 4.2 事务目录

```text
files/.mods-deploy/<txid>/
├── journal.json          事务状态（ASCII JSON，原子更新）
├── staging/<rel>         待部署文件
├── backup/<rel>          被替换文件的旧版本（只增不删）
└── rollback/<rel>        回滚时移出的“本事务新文件”（保留证据，不删除）
```

- `staging`、`backup`、`rollback` 与 `Mods` 在同一文件系统（同一 `files` 树），因此 `mv` 不跨挂载点；
- journal 更新方式：`adb push journal.json.new` → 校验 SHA-256 → `mv -f journal.json.new journal.json`；
- 成功提交后 `backup` 保留，供人工回退或审计；工具**不自动清理**；
- `committed` 事务不会阻止下一次部署；`prepared` / `staging-verified` / `applying` /
  `rolling-back` / `rollback-failed` / `recovery-required` 会阻止新部署。

### 4.3 状态机

```text
prepared
  └─ staging-verified
       └─ applying
            ├─ committed
            └─ rolling-back
                 ├─ rolled-back
                 └─ rollback-failed
```

- `prepared`：事务目录已创建，journal 已落盘；没有写 Mods。
- `staging-verified`：全部 staging 文件已上传并逐文件 SHA-256 通过；没有写 Mods。
- `applying`：即将/正在切换；journal 记录 expected 列表和 createdDirectories。
- `committed`：全部 expected 文件在最终位置逐文件校验通过。
- `rolling-back` / `rolled-back` / `rollback-failed`：回滚过程与结果。
- `recovery-required`：journal 缺失/损坏/哈希无法证明归属，必须人工处理。

**实现补充（Phase 1.2.2 审计记录）**：实现多了一个 `staging-failed` 状态，用于"staging 推送失败 /
staging 哈希不符 / staging 权限不可读"。它代替了本文原先写的"停在 `prepared`"。两者的区别是：
`prepared` 属于 active 状态会阻止下一次 `deploy`，而 `staging-failed` 不会。选择后者是因为这三条
失败路径都发生在**第一次切换 Mods 之前**（本事务尚未把任何字节写进 `Mods`），强制先做一次
"回滚一个从未写过 Mods 的事务"只是仪式。`inspect` 会把它列出来（`journalReadable=true`），
操作者仍能看到遗留事务目录；真正的风险窗口（`prepared` / `staging-verified` / `applying` /
`rolling-back` / `rollback-failed` / `recovery-required`）仍然 fail-closed。详见 §9.3 偏差 D-1。

### 4.4 每个文件的动作

| 动作 | 条件 | 切换 | 回滚 |
| --- | --- | --- | --- |
| `ADD` | 目标不存在 | `mv staging/x Mods/x` | 仅当目标哈希 == 新哈希，移到 `rollback/x` |
| `REPLACE` | 目标存在、哈希不同、且 rel 在 `-Overwrite` 中 | `mv Mods/x backup/x` → `mv staging/x Mods/x` | 目标 == 新哈希则移到 `rollback/x`；校验 backup == 旧哈希后 `mv backup/x Mods/x` |
| `SKIP` | 目标存在且哈希相同 | 不写 | 不碰；若被外部改动只报告 |

### 4.5 失败与中断

- staging 写入失败或哈希不符：状态停在 `prepared`，Mods 未动；**保留 staging**，退出 4。
- 切换某文件失败：立即进入 `rolling-back`；按哈希反向恢复；成功则 `rolled-back`，失败则
  `rollback-failed`，退出 5。
- 进程被杀：journal 保留最后状态；下一次 `inspect` 显示 active/unknown；`deploy` 拒绝新事务；
  `rollback -TxId ... -Apply` 根据哈希恢复；`staging`/`backup`/`rollback` 不自动删除。
- journal 写失败：不再继续切换；能写则写 `recovery-required`，否则保留现场并提示人工检查。

---

## 5. CLI

```powershell
pwsh -NoProfile -File Tools/deploy-mods-safe.ps1 `
    -Action inspect|plan|deploy|verify|rollback `
    [-Serial <serial>] [-Adb <path>] `
    [-Source <complete Mods root>] `
    [-TxId <txid>] [-Overwrite <rel>[,<rel>...]] `
    [-Apply] [-StrictExtras] `
    [-Json] [-JsonOut <path>] [-ReportOut <path>]
```

（`-Action` 另接受别名 `-Command`，`-Source` 另接受别名 `-ModsSource`；两个别名来自
Phase 1.2.1 的初始提交，保留以免破坏已有调用。`-JsonOut` / `-ReportOut` 是 Phase 1.2.2 补齐的：
设计原文列出了它们，但初始实现只有 `-Json`。）

| 命令 | 默认 | 写设备 | 说明 |
| --- | --- | --- | --- |
| `inspect` | 只读 | 否 | 设备/包/进程/Mods 状态、未完成事务 |
| `plan` | 只读 | 否 | 校验源树与 manifest，输出逐文件计划/冲突 |
| `deploy` | dry-run | 否 | 不带 `-Apply` 等价 `plan`；带 `-Apply` 才执行事务 |
| `verify` | 只读 | 否 | 对源树或事务的 expected 集逐文件核对，报告缺失/额外/修改 |
| `rollback` | dry-run | 否 | 不带 `-Apply` 只打印回滚计划；带 `-Apply` 才恢复 |

硬性约束：

- `deploy -Apply` 和 `rollback -Apply` 必须显式 `-Serial`；单设备自动发现只用于只读命令。
- `deploy -Apply` 发现游戏进程运行 → 拒绝；不自动 `force-stop`。
- 包名固定 `com.chucklefish.stardewvalley`；版本必须是 `239 / 1.6.15.0`（可显式传参覆盖，
  但默认值来自 `app/apktool.yml`）。
- `-Overwrite` 必须精确覆盖全部冲突项；列出非冲突项也会拒绝（防止误操作）。
- `-Apply` 不接受空 `-ModsSource`；`verify` 至少需要 `-ModsSource` 或 `-TxId`。

---

## 6. 退出码与错误类别

### 6.1 退出码（稳定契约）

| 码 | 含义 |
| --- | --- |
| 0 | 成功：只读检查完成、dry-run 计划有效、apply 提交并校验通过 |
| 2 | BLOCKED：环境/设备/存储不可用（无 adb、无设备、serial 不匹配、offline/unauthorized、staging 无法创建） |
| 3 | REFUSED：策略/输入拒绝（包不符、游戏运行、路径/manifest 非法、冲突未授权、rollback 归属不符） |
| 4 | FAILED：已开始写入但未提交；工具已尽力回滚（看 journal 状态） |
| 5 | RECOVERY-REQUIRED：状态不明确或回滚失败；必须人工处理，工具不删 staging/backup |

### 6.2 错误类别（`category`，稳定字符串）

| category | 典型 reason |
| --- | --- |
| `EnvironmentBlocked` | `no-adb`、`no-device`、`ambiguous-device`、`serial-not-attached`、`serial-not-online` |
| `StorageUnavailable` | `package-data-missing`、`staging-create-failed`、`device-command-missing` |
| `PolicyRefused` | `package-not-installed`、`package-version-mismatch`、`game-running`、`unsafe-path`、`manifest-invalid`、`target-conflict`、`overwrite-list-mismatch`、`active-transaction`、`serial-required` |
| `DeployFailed` | `staging-hash-mismatch`、`switch-failed`、`final-verify-failed`、`journal-write-failed` |
| `RecoveryRequired` | `journal-unknown`、`rollback-ownership-unproven`、`rollback-failed` |
| `VerifyMismatch` | `missing-files`、`modified-files`、`unexpected-symlink`、`strict-extras` |

### 6.3 JSON Schema（`schemaVersion: 1`）

顶层字段固定：

```json
{
  "schemaVersion": 1,
  "tool": "deploy-mods-safe",
  "command": "inspect|plan|deploy|verify|rollback",
  "mode": "read-only|dry-run|apply",
  "generatedAt": "ISO-8601",
  "exitCode": 0,
  "status": "ok|refused|blocked|failed|recovery-required",
  "category": "one of §6.2 or empty",
  "reason": "stable-reason-or-empty",
  "detail": "human detail, no host profile path",
  "device": {
    "serial": "...", "state": "device|offline|unauthorized|...",
    "apiLevel": 29, "androidRelease": "10", "model": "...",
    "explicitSerial": true
  },
  "package": {
    "name": "com.chucklefish.stardewvalley", "installed": true,
    "versionCode": "239", "versionName": "1.6.15.0",
    "running": false, "pid": ""
  },
  "storage": {
    "modsRoot": "/sdcard/Android/data/.../files/Mods",
    "transactionRoot": "/sdcard/Android/data/.../files/.mods-deploy",
    "scopedStorageRisk": "verified-api29|unverified-apiXX",
    "safSupported": false,
    "writeProbe": "not-run|ok|failed"
  },
  "plan": {
    "fileCount": 0, "totalBytes": 0,
    "files": [{"path":"...","bytes":0,"sha256":"...","action":"ADD|REPLACE|SKIP","existingSha256":"..."}],
    "directories": ["..."],
    "conflicts": [{"path":"...","existingSha256":"...","newSha256":"..."}],
    "overwriteApproved": ["..."]
  },
  "journal": {
    "txid": "...", "path": "...", "state": "...",
    "createdAt": "...", "committedAt": "..."
  },
  "transactions": [{"txid":"...","state":"...","journalReadable":true}],
  "verification": {
    "expectedFiles": 0, "matchedFiles": 0,
    "missing": [], "modified": [], "extra": [],
    "symlinks": [], "strictExtras": false
  },
  "safety": {
    "deviceTouched": false, "writesSaves": false, "usesRoot": false,
    "installsApk": false, "forceStopsGame": false, "deletesUnknownFiles": false
  }
}
```

`deviceTouched` 在 dry-run/只读命令中必须为 `false`；在 apply 中表示“本次进程是否执行过写操作”
（Phase 1.2.2 修正：初始实现从未把它置为 `true`，属于自证字段失真；现在创建事务目录与进入
rollback 写路径时会置 `true`）。
`writesSaves` / `usesRoot` / `installsApk` / `forceStopsGame` / `deletesUnknownFiles` 是工具自证的
常量 `false`，并由静态测试和 fake ADB 日志交叉验证。

实现比本文多一个字段 `verification.unparseable`：`find … -exec sha256sum` 的输出若有无法解析、或
不在 `Mods` 之下的行，会记到这里并让 `verify` fail-closed，而不是静默当作匹配。`-JsonOut` /
`-ReportOut` 只是报告副本；权威 journal 始终在设备端（见 §8.6）。

---

## 7. 自动化验收（fake ADB）

测试文件：`Tools/tests/test_deploy_mods_safe.ps1`（Phase 1.2.1 原文误写为 `test_deploy_mods.ps1`）。
它不调用真实 `adb`，不连接设备；每个用例在独立
子进程中运行工具，fake adb 模拟：

- `devices -l`（device/offline/unauthorized 多设备）；
- `getprop`、`pm path`、`dumpsys package`、`pidof`、`df`；
- `test -e/-d/-L`、`mkdir -p`、`mv [-f]`、`rmdir`、`sha256sum`、`stat -c`、`ls`、`cat`、`chmod`、
  `find -type f/-type l`、`command -v`；
- `push`（复制到模拟设备树）；
- 故障注入：staging 哈希错误、指定 `mv` 失败、journal 写入失败、回滚 `mv` 失败；
- 进程中断：fake adb 在匹配命令后杀掉工具父进程（`FAKE_PARENT_PID`），保留设备树现场。

覆盖矩阵见 `docs/audit/phase1.2.1-report.md` 与测试输出；至少覆盖任务书第 4 节的 15 类场景。

---

## 8. 未解决项与风险

1. **应用可写性不可直接证明**：非 debuggable、无 `run-as`、无 Root。工具只能保证文件/目录
   世界可读、shell 可写，并依赖应用专属外部存储的 FUSE/sdcardfs 语义；Phase 1.2 的真机成功是
   目前唯一的“应用可读且 Loader 能落报告”证据。
2. **Android 11+ 未实测**：工具不硬编码 API 上限，但 apply 的 staging 创建失败会 fail closed；
   不声称 SAF 或所有 OEM 可用。
3. **多文件非原子**：见 §4.1；断电窗口内可能短暂缺少某个文件，但 journal + backup 可恢复。
4. **不删除旧文件**：更新后旧版本的额外文件会留在 Mods；这是安全取舍，需要人工清理时另行处理。
5. **备份不自动清理**：`.mods-deploy` 会随部署次数增长；工具不自动删除，避免误删唯一回滚凭据。
6. **journal 在设备端**：设备被清数据或目录被外部删除后无法恢复；主机侧 `-JsonOut` 只是报告，
   不是权威 journal。

---

## 9. Phase 1.2.2 审计结果（实现映射与偏差）

本节由 Phase 1.2.2 的源码审计追加。§1–§8 是原始设计，**不修改**；实现与设计的差异集中记在这里，
原始风险项也原样保留（只在 §9.4 标注当前状态）。

### 9.1 设计要求 → 实现位置

| 设计要求 | 实现位置（`Tools/deploy-mods-safe.ps1`） | 审计结论 |
| --- | --- | --- |
| 只写固定外部 `Mods` 与 `.mods-deploy/<txid>` 两棵树 | `Test-RemoteAllowed` / `Assert-RemoteAllowed` | 已实现（`files` 根的只读例外见 D-4） |
| 默认只读；apply 需显式 `-Apply` + 显式 `-Serial` | `$script:SerialExplicit`、`Invoke-Deploy`、`Invoke-RollbackCommand` | 已实现 |
| 多设备/离线/未授权/缺 serial/包不符/游戏运行 fail-closed | `Get-DeviceContext`、`Get-PackageState` | 已实现；`-PackageName` 覆盖检查在 1.2.2 修复（见 §9.2） |
| `-Overwrite` 只授权指定相对路径 | `Get-RemotePlan`、`Get-ApprovedOverwrites` | 已实现（漏列/多列都拒绝） |
| 稳定 JSON、稳定 `category`、可区分退出码 | `$script:Result`、`Exit-Tool` | 已实现；1.2.2 补齐 `-JsonOut`/`-ReportOut` |
| 源目录规范化、reparse/symlink、大小写/Unicode 重复、保留名、边界与限额 | `Get-SourcePlan`、`Assert-RelativePath` | 已实现；1.2.2 改为显式栈遍历（原实现先递归枚举再检查） |
| Manifest 结构校验与声明文件存在性 | `Get-SourcePlan` 内联校验 | 部分实现（D-2、D-3） |
| 设备侧路径链 symlink 检查 | `Test-RemotePathChains` | 已实现；1.2.2 修正 `/sdcard` 前缀误判（见 §9.2） |
| staging → 逐文件哈希 → 切换 → journal | `Invoke-Deploy` | 已实现 |
| 失败/中断按 journal 恢复；回滚只恢复可证明归属的字节 | `Invoke-Rollback` | 已实现 |
| 不使用 Root / `su` / `run-as`；不安装、不 `pm clear`、不 force-stop | 全文静态审计 + fake adb 命令日志断言 | 已实现 |
| Android 11+ apply fail-closed | `Get-StorageState` | 已实现（`unverified-android-storage`，退出码 2） |
| 不把多文件切换说成原子 | §4.1 与报告口径 | 保持 |

### 9.2 Phase 1.2.2 修复的实现缺陷

| 级别 | 缺陷 | 影响 | 修复 |
| --- | --- | --- | --- |
| P0 | `Get-PackageState` 给只读自动变量 `$PID` 赋值 | 所有命令在读包状态时抛异常 → 退出码 5 `unexpected-exception`，工具实际不可用 | 改用 `$processState` |
| P0 | 用 `@($list)` 包装 `List[object]`（Windows PowerShell 5.1 会抛 "Argument types do not match"） | `plan`/`deploy` dry-run、事务列表、`-Overwrite` 列表构造失败 | 改用 `.ToArray()` |
| P1 | 脚本用常量覆盖了 `-PackageName` 参数本身 | 传入其它包名被静默忽略，`package-name-mismatch` 永远不会触发 | 拆出 `$script:FixedPackage`，参数保持独立 |
| P1 | 路径链检查把 `/sdcard`、`/sdcard/Android` 等 OS 前缀当普通路径做 `test -L` | Android 上 `/sdcard` 通常是符号链接 → 真机会拒绝**所有**部署 | 检查边界下移到应用专属 `files` 根；fake adb 增加该形状回归用例 |
| P1 | 源目录用 `Get-ChildItem -Recurse` 一次性枚举后再逐项检查 | Windows PowerShell 5.1 的 `-Recurse` 会跟随 junction，链接环可能被遍历后才报错 | 改为显式栈遍历，下降前逐项检查 |
| P1 | 设备输出按 OEM 代码页解码，而 `adb shell` 是 UTF-8 | 非 ASCII 设备路径变乱码 → 误报 `staging-hash-mismatch`（fail-closed 但错误原因） | 工具与测试宿主统一按 UTF-8 解码 |
| P2 | `safety.deviceTouched` 在 apply 中始终为 `false` | 自证字段失真 | apply/rollback 写路径置 `true` |
| P2 | 设计列出的 `-JsonOut` / `-ReportOut` 未实现 | 设计契约缺口 | 实现（报告副本，非权威 journal） |
| P2 | 临时 journal 文件写进项目 `.tmp/phase1.2.1` | 仓库内临时产物 | 改到系统临时目录 |
| P2 | 测试桩 `mv` 拒绝覆盖已存在文件 | fake adb 违背 POSIX 语义 → 第二次 journal 写入假失败 | 修正为 POSIX 语义；工具对 journal 替换显式用 `mv -f` |

测试桩自身另有三个缺陷（`[ordered]@{ tests = @($list) }` 序列化崩溃、launcher 用 OEM 代码页解码子进程
输出、reparse 场景依赖需要特权的 symlink）已在同一轮修复，详见
[Phase 1.2.2 报告](../audit/phase1.2.2-report.md)。

### 9.3 有意保留的偏差

| 编号 | 偏差 | 理由 | 风险 |
| --- | --- | --- | --- |
| D-1 | 新增 `staging-failed` 状态，且**不**阻止下一次部署（设计原文是停在会阻止的 `prepared`） | 该状态只出现在首次切换 `Mods` 之前，重试安全；强制先回滚一个从未写过 `Mods` 的事务没有收益 | 遗留事务目录需要人工清理；`inspect` 会列出 |
| D-2 | 工具不自行拒绝重复 JSON key（设计 §3.2 要求自写扫描器） | Loader 的 `Json.cs` 在加载前拒绝重复 key（`MF0106`），工具接受而 Loader 拒绝是 fail-closed 方向 | 纵深防御缺口，不构成绕过；不新增解析器以避免引入新风险 |
| D-3 | Manifest 语义校验只覆盖 `id`/`entryAssembly`/`privateAssemblies`/未知字段/声明文件存在性；`version`/`name`/`schemaVersion` 取值与 id 字符集未校验 | 同上，Loader 是 manifest 语义的最终权威且 fail-closed | 工具可能把 Loader 会拒绝的包部署上去（Mod 不加载）。1.2.3 已把可选元数据字段加入白名单，见 §9.5 |
| D-4 | 写白名单函数也接受 `files` 根本身 | 只用于 `test -d` 与 `df -k` 只读探测；没有任何写调用点传入它 | 需要将来拆分"读探测白名单"与"写白名单" |

### 9.4 §8 风险项的当前状态

| 原风险 | 当前状态（2026-11-14，Phase 1.2.2 / 1.2.3） |
| --- | --- |
| 8.1 应用可写性不可直接证明 | Phase 1.2.3 已由工具在 V1963A 上真实写入 staging/ADD 文件并用 Loader 加载佐证；**仍未**由第三方文件浏览器或 `run-as` 直接读取 |
| 8.2 Android 11+ 未实测 | **仍成立**。API 30+ apply 继续 fail-closed；本阶段未引入 SAF / 权限 / Root 路径 |
| 8.3 多文件非原子 | **仍成立**。逐文件 rename + journal 两阶段，不声称原子 |
| 8.4 不删除旧文件 | **仍成立**（安全取舍） |
| 8.5 备份不自动清理 | **仍成立**；Phase 1.2.3 保留了两个事务目录 |
| 8.6 journal 在设备端 | **仍成立**；主机侧文件只是报告副本。1.2.3 的 rollback JSON 状态字段与设备 journal 不一致，见 §9.5 |
| 新增：OS 前缀 `/sdcard` 是符号链接 | 代码已按该事实处理，但**只有真机证据能确认**；见手动验收计划第 3–5 步 |

### 9.5 Phase 1.2.3 真机验收发现与修复

Phase 1.2.3 在这台 V1963A 上第一次用**项目自己的完整 Mods 根**运行工具，暴露了两个会让真机 plan 直接失败、
但 fake-adb 夹具没有覆盖的缺陷：

| 级别 | 缺陷 | 真机表现 | 修复 |
| --- | --- | --- | --- |
| P0 | manifest 字段白名单漏掉 schemaVersion 1 的可选元数据 `author`/`description`/`website` | 项目真实 manifest 被 `manifest-unknown-field:author` 拒绝（exit 3 `manifest-invalid`），plan/deploy 无法执行 | 白名单对齐 `ModManifest.KnownFields` 与 `docs/modding/manifest-spec.md` |
| P0 | `entryAssembly`/`privateAssemblies` 校验用 `^[^/\\:.]+\.dll$`，字符类把 `.` 也排除了 | `P114.RuntimeProbe.dll` 被 `unsafe-manifest-path` 拒绝（exit 3），几乎所有真实 .NET 程序集名都过不了 | 镜像 `ModManifest.IsValidEntryAssemblyName`：只拒绝分隔符、`:`、`..`、控制字符、非 `.dll` 后缀和超过 128 字符 |
| P2 | `rollback -Apply` 的 JSON `journal.state` 保留回滚前读取的值 | 设备 journal 已是 `rolled-back`，JSON 仍显示 `committed`；退出码与 detail 正确 | 未改代码，记录为报告字段偏差；**设备端 journal 是权威** |

工具版本 1.2.2 → **1.2.3**；fake-adb 套件新增两项回归（可选元数据接受、未知字段仍拒绝），并把夹具
`entryAssembly` 改成带点号的 `Metadata.Assembly.dll`。Phase 1.2.3 的真机执行结果见
[真机验收报告](../audit/phase1.2.3-device-acceptance-report.md)。

### 9.6 Phase 1.2.4：回滚后的 JSON `journal.state` 以设备持久化状态为准

§9.5 记录的 P2 偏差（`rollback -Apply` 的 JSON 仍是回滚前读取的 `committed`）在本阶段修复。根因不是序列化顺序，
而是**报告值在回滚前就被复制**：`Invoke-RollbackCommand` 先把 `journal.state` 写入 `$script:Result`，再调用
`Invoke-Rollback`；后者修改并写入最终 journal 后直接 `Exit-Tool`，JSON 因此保留旧值。自动回滚分支（`Invoke-Deploy`
内 9 处）同样把 `applying` 写进结果后不再更新。

修复后的契约：

| 场景 | `journal.state`（stdout 与 `-JsonOut`） | 依据 |
| --- | --- | --- |
| rollback dry-run | 回滚前读取的状态（如 `committed`） | 没有写设备，报告读到的就是权威值 |
| rollback -Apply 成功 | `rolled-back` | 回滚后**重新读取设备 journal**；读不到时使用本次已证明写入的状态 |
| 已回滚事务再次 apply | `rolled-back` | 幂等分支直接读设备 journal |
| 最终 journal 写入失败 | `rolling-back`（不是 `rolled-back`） | 本次只成功持久化了 `rolling-back`；文件可能已恢复，但事务未完成 |
| 连 `rolling-back` 都写不进 | 保持回滚前状态 | 没有新的持久化证据，不能声称状态变化 |
| 自动回滚（deploy 失败） | 设备 journal 的实际状态 | 同一条 `Sync-JournalResultState` 路径 |

实现：`Invoke-Rollback` 返回 `JournalState`（本进程已证明持久化的最后一个状态）；`Sync-JournalResultState`
优先重新读取设备 journal，读取失败才回退到该值。所有自动回滚分支统一经 `Invoke-AutomaticRollback`。
另外，staging 失败分支在写 `staging-failed` / `staging-verified` 后也会同步报告状态，避免同一个“报告旧值”问题。

退出码与 `category`/`reason` 语义不变；设备 journal 仍是唯一权威。回归用例：
`approved REPLACE commits`、`rollback -Apply restores the pre-state`、`rollback -JsonOut agrees with stdout and the
device journal`、`failed rolling-back write still reports the committed state`、`failed final journal write does not
claim rolled-back`、`refused rollback reports the persisted journal state, not a rolled-back claim`。

工具版本 1.2.3 → **1.2.4**。真机 REPLACE 验收见
[Phase 1.2.4 真机验收记录](../audit/phase1.2.4-device-acceptance-report.md)。
