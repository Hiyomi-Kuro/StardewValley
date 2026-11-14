# Phase 1.2.1：无 Root Mods 安全部署工具设计

- 日期：2026-11-14
- 状态：实现前设计（随后由 `Tools/deploy-mods-safe.ps1` 落实）
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
    -Command inspect|plan|deploy|verify|rollback `
    [-Serial <serial>] [-Adb <path>] `
    [-ModsSource <complete Mods root>] `
    [-TxId <txid>] [-Overwrite <rel>[,<rel>...]] `
    [-Apply] [-StrictExtras] `
    [-JsonOut <path>] [-ReportOut <path>]
```

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

`deviceTouched` 在 dry-run/只读命令中必须为 `false`；在 apply 中表示“本次进程是否执行过写操作”。
`writesSaves` / `usesRoot` / `installsApk` / `forceStopsGame` / `deletesUnknownFiles` 是工具自证的
常量 `false`，并由静态测试和 fake ADB 日志交叉验证。

---

## 7. 自动化验收（fake ADB）

测试文件：`Tools/tests/test_deploy_mods.ps1`。它不调用真实 `adb`，不连接设备；每个用例在独立
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
