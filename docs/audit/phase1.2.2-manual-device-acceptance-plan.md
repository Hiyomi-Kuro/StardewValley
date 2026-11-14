# Phase 1.2.2：无 Root Mods 部署工具 · 用户手动真机验收计划

| 项 | 值 |
| --- | --- |
| 状态 | **`AWAITING_USER_MANUAL_ACCEPTANCE`**（等待用户手动执行；未执行即为未验收） |
| 日期 | 2026-11-14 |
| 工具 | `Tools/deploy-mods-safe.ps1`（`-Action inspect\|plan\|deploy\|verify\|rollback`） |
| 适用设备 | 仅有既有基线：**vivo V1963A / PD1963 / Android 10 / API 29 / arm64-v8a** |
| 执行者 | **用户本人**。Codex 在本阶段不连接设备、不调用真实 `adb`、不执行下列任何命令 |
| 前置 | 主机已装 `adb`；目标设备已装 `com.chucklefish.stardewvalley` 239 / 1.6.15.0；**游戏已关闭** |
| 关联 | [Phase 1.2.2 报告](phase1.2.2-report.md)、[证据索引](phase1.2.2-evidence-index.md)、[设计文档](../architecture/phase1.2.1-deployment-design.md) |

> **本文件只是一份操作脚本模板。** 主机侧 fake-adb 测试通过、CI 通过，都不能替代这里的真机证据。
> 在用户实际执行并把证据登记进 [证据索引](phase1.2.2-evidence-index.md) 之前，Phase 1.2.2 的真机验收状态是
> **未验收**，不得写成 PASS。

> **历史说明（Phase 1.2.3 追加，不改写原文）**：本文件记录的是 Phase 1.2.2 当时的限制——“Codex 不连接设备、
> 必须由用户手动执行”。2026-11-14 的 Phase 1.2.3 已获得用户明确的 USB 真机授权，由 DSH 直接执行了等价流程
> （并额外覆盖 ADD/rollback），结果见 [Phase 1.2.3 真机验收报告](phase1.2.3-device-acceptance-report.md)。
> 本文件保留原样用于追溯；Phase 1.2.2 “没有连接设备、真机未验收”的历史事实不被改写。

---

## 0. 开始前的安全须知（请先读完再动手）

1. **先自行备份你自己的数据。** 工具的设计目标是不触碰 `Saves`、不删除未知文件、不重装 APK，
   但"设计目标"不等于"你已经在设备上验证过"。执行第 2 步时请顺便确认目标路径确实是
   `.../files/Mods`，而不是 `.../files/Saves`。
2. **不要卸载游戏、不要 `pm clear`、不要清除应用数据、不要解锁 Bootloader、不要改系统安全设置、
   不要使用 Root。** 本工具任何失败路径都不需要这些操作；如果某一步要求你这么做，请停止并反馈。
3. **游戏必须由你手动关闭。** 工具检测到游戏进程运行时会拒绝 apply，并且**永远不会**代替你 force-stop 或启动游戏。
4. **Android 11+ 不要尝试 apply。** 工具对 API 30+ 的 apply 是 fail-closed（退出码 2，
   `unverified-android-storage`）。本计划只覆盖 API 29 基线设备。
5. **任何一步失败：停止，保存输出，不要重复 apply 去"试一下"。** 先看第 8 步的检查清单。
6. 本计划中的 `<SERIAL>` 必须由你用 `adb devices` 的实际输出替换；不要沿用文档里的任何示例值。

---

## 1. 准备证据目录（主机侧）

```powershell
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$ev = "D:\Download\DSH\StardewValley\.tmp\phase1.2.2\device-acceptance\$stamp"
New-Item -ItemType Directory -Force -Path $ev | Out-Null
$ev
```

后面所有主机侧文件都写到 `$ev`。设备侧证据在第 6 步用只读命令导出。

---

## 2. 设备信息确认（只读）

```powershell
adb devices -l | Tee-Object "$ev\01-devices.txt"
```

从输出里确认：只有一台 `device` 状态的目标机，记下它的 **serial**，然后替换下面的 `<SERIAL>`：

```powershell
$serial = "<SERIAL>"     # ← 用 01-devices.txt 里的实际值替换，不要猜
adb -s $serial shell getprop ro.product.model    | Tee-Object "$ev\02-model.txt"
adb -s $serial shell getprop ro.build.version.sdk     | Tee-Object "$ev\03-sdk.txt"
adb -s $serial shell getprop ro.build.version.release | Tee-Object "$ev\04-release.txt"
adb -s $serial shell getprop ro.product.cpu.abi  | Tee-Object "$ev\05-abi.txt"
adb -s $serial shell dumpsys package com.chucklefish.stardewvalley |
    Select-String 'versionCode|versionName' | Tee-Object "$ev\06-package.txt"
```

判定：

| 检查 | 期望 | 不满足时 |
| --- | --- | --- |
| SDK | `29` | 停止。本计划不覆盖其它 API（尤其不要用 30+ 试 apply） |
| ABI | `arm64-v8a` | 停止，记录实际值 |
| versionCode / versionName | `239` / `1.6.15.0` | 停止；工具会以 `package-version-mismatch` 拒绝 |
| 游戏进程 | 未运行（下一步 `inspect` 会显示） | 手动关闭游戏 |

---

## 3. `inspect`（只读，零写入）

```powershell
pwsh -NoProfile -File Tools\deploy-mods-safe.ps1 -Action inspect -Serial $serial `
    -Json -JsonOut "$ev\10-inspect.json" -ReportOut "$ev\10-inspect.txt"
```

（`-Json` 把机器可读结果打到标准输出；`-JsonOut` 另存一份副本；`-ReportOut` 保存人读日志。以**退出码**为判定依据，文件只是证据。）

期望：

- 退出码 **0**；
- `status=ok`，`safety.deviceTouched=false`，`storage.writeProbe=not-run`；
- `package.installed=true`，`versionCode=239`，`package.running=false`；
- `transactions` 为空，或其中每一项都是 `state=committed`。

如果退出码是 **5** 且 `reason=active-transaction`：**不要继续部署**。先保存
`$ev` 下的输出和第 6 步的 `.mods-deploy` 列表，反馈给维护者。

---

## 4. `plan`（只读部署计划）

```powershell
pwsh -NoProfile -File Tools\deploy-mods-safe.ps1 -Action plan -Serial $serial `
    -Source "D:\path\to\complete\Mods" `
    -Json -JsonOut "$ev\11-plan.json" -ReportOut "$ev\11-plan.txt"
```

`-Source` 必须是**完整的 Mods 根目录**（`ModFramework.Runtime.dll` 在根，每个 Mod 一个一层目录带 `manifest.json`）。

逐项核对 `$ev\11-plan.txt`：

| 期望 | 说明 |
| --- | --- |
| 每个文件有 `action` 与 64 位小写 `sha256` | `ADD` / `REPLACE` / `SKIP` |
| 有 `REPLACE` 时，该相对路径必须出现在你稍后显式传的 `-Overwrite` 中 | 否则 apply 会被拒绝（这是设计行为） |
| `conflicts` 与 `plan.overwriteApproved` 一致 | 不允许多列、漏列 |
| 退出码 0 | 有冲突且未授权时是 3 `target-conflict`，属正常拒绝 |

> 首次在原设备上跑这个流程时，通常全部是 `ADD`（Mods 为空）。若出现 `REPLACE`，
> 说明设备上已有不同内容的同名文件——请先自行确认这是你要替换的旧版本。

---

## 5. `deploy`（先 dry-run，再显式 apply）

### 5.1 dry-run（不带 `-Apply`，零写入）

```powershell
pwsh -NoProfile -File Tools\deploy-mods-safe.ps1 -Action deploy -Serial $serial `
    -Source "D:\path\to\complete\Mods" `
    -Json -JsonOut "$ev\12-deploy-dryrun.json" -ReportOut "$ev\12-deploy-dryrun.txt"
```

期望退出码 0 且 `mode=dry-run`、`safety.deviceTouched=false`；设备上**没有**新建 `.mods-deploy` 目录。

### 5.2 apply（需要你明确同意后才执行）

**确认清单（请逐条打勾后再运行）：**

- [ ] 第 3、4 步都通过，且我看过计划里的每个路径和 SHA-256；
- [ ] 游戏已关闭（`inspect` 显示 `running=false`）；
- [ ] 我已经自行备份了我在意的数据；
- [ ] 我接受：多文件切换**不是原子事务**，中途断电/进程被杀会留下需要恢复的 journal；
- [ ] 我接受：旧版本文件会被备份保留、不会被自动删除；
- [ ] 目标路径是 `.../files/Mods`，不是 `Saves`。

```powershell
pwsh -NoProfile -File Tools\deploy-mods-safe.ps1 -Action deploy -Serial $serial `
    -Source "D:\path\to\complete\Mods" -Apply `
    -Overwrite "<只在计划里出现 REPLACE 时填写，多个用逗号分隔>" `
    -Json -JsonOut "$ev\13-deploy-apply.json" -ReportOut "$ev\13-deploy-apply.txt"
```

（没有 `REPLACE` 时不要传 `-Overwrite`。）

期望：

- 退出码 **0**，`status=ok`，`mode=apply`，`safety.deviceTouched=true`；
- `journal.state=committed`，`journal.txid` 形如 `<yyyyMMddTHHmmssZ>-<8 hex>`；
- `detail` 里给出 "N added, M replaced"；
- 文本日志里每个文件一行 `switched`，最后有 `verify-remote` 与 `backup-kept`。

失败时按退出码处置：

| 退出码 | 含义 | 你要做的 |
| --- | --- | --- |
| 2 | BLOCKED（无设备/serial 不符/storage 不可用/API 30+） | 修环境；**不要**想办法绕过 |
| 3 | REFUSED（包不符、游戏在跑、路径/manifest 非法、冲突未授权） | 按 `reason` 修正输入；**不要**加 `-Overwrite` 硬闯 |
| 4 | FAILED（已写入但未提交，工具已尽力回滚） | 看 `$ev\13-deploy-apply.json` 的 `journal.state`；执行第 7 步 rollback 或反馈 |
| 5 | RECOVERY-REQUIRED（状态不明/回滚失败） | **停止**，保存设备端 `.mods-deploy` 与日志，反馈；不要重复 apply |

---

## 6. `verify` 与设备端只读取证

### 6.1 verify（只读）

```powershell
pwsh -NoProfile -File Tools\deploy-mods-safe.ps1 -Action verify -Serial $serial `
    -Source "D:\path\to\complete\Mods" `
    -Json -JsonOut "$ev\14-verify.json" -ReportOut "$ev\14-verify.txt"
```

期望退出码 0，`verification.missing=0`、`modified=0`；`extra` 只应是 Loader 运行期生成物
（`loader.log`、`loader-report.txt`、`logs/`、`.staging/`），这些**不算失败**。

### 6.2 设备端文件与哈希（只读）

```powershell
adb -s $serial shell "find '/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods' -type f -exec sha256sum {} +" |
    Tee-Object "$ev\20-device-mods-sha256.txt"
adb -s $serial shell "find '/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods' -maxdepth 2 -exec ls -ld {} +" |
    Tee-Object "$ev\21-device-mods-modes.txt"
adb -s $serial shell "ls -la '/sdcard/Android/data/com.chucklefish.stardewvalley/files/.mods-deploy'" |
    Tee-Object "$ev\22-device-tx-list.txt"
adb -s $serial shell "cat '/sdcard/Android/data/com.chucklefish.stardewvalley/files/.mods-deploy/<TXID>/journal.json'" |
    Tee-Object "$ev\23-journal.json"
```

（`<TXID>` 用第 5 步 `journal.txid`。）

**Saves 未被触碰的对照证据**（只读）：

```powershell
adb -s $serial shell "ls -la '/sdcard/Android/data/com.chucklefish.stardewvalley/files/Saves'" |
    Tee-Object "$ev\24-saves-before-after.txt"
```

请在第 5 步**之前**也跑一次同样的命令并把输出存成 `$ev\24a-saves-before.txt`，这样 `before/after` 才有意义。
两次数目与时间戳应完全一致。

### 6.3 主机侧逐文件对照

把 `20-device-mods-sha256.txt` 与 `13-deploy-apply.json` 的 `plan.files[].sha256` 对照。
所有 `ADD`/`REPLACE` 项必须一一相等；`SKIP` 项也应在设备列表里存在。把对照结论写进报告表格。

---

## 7. （可选，需你另行决定）启动游戏并读取 Loader 报告

**这一步会启动游戏，属于你自己的决定**，本工具不会做。若你决定执行：

```powershell
adb -s $serial shell am start -n com.chucklefish.stardewvalley/.MainActivity
Start-Sleep -Seconds 20
adb -s $serial shell "cat '/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods/loader-report.txt'" |
    Tee-Object "$ev\30-loader-report.txt"
adb -s $serial shell "cat '/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods/loader.log'" |
    Tee-Object "$ev\31-loader-log.txt"
```

期望：`mods-discovered` 与本次部署的 Mod 数一致，`mods-failed=0`，`final result=ok`。
**注意**：本阶段的目标是"部署工具可用"，不是"Mod 功能验收"；Loader 报告的结论不能代替 Mod 行为验收。

---

## 8. `rollback`（只有你明确决定回滚时才执行）

先 dry-run 看计划：

```powershell
pwsh -NoProfile -File Tools\deploy-mods-safe.ps1 -Action rollback -Serial $serial -TxId "<TXID>" `
    -Json -JsonOut "$ev\40-rollback-dryrun.json" -ReportOut "$ev\40-rollback-dryrun.txt"
```

确认输出里的 `expected` 列表与你要回滚的内容一致后，再显式 apply：

```powershell
pwsh -NoProfile -File Tools\deploy-mods-safe.ps1 -Action rollback -Serial $serial -TxId "<TXID>" -Apply `
    -Json -JsonOut "$ev\41-rollback-apply.json" -ReportOut "$ev\41-rollback-apply.txt"
```

语义（不要误解）：

- 只回滚**本事务拥有且能被哈希证明**的内容；
- 事务外被修改的文件不会被覆盖；此时退出码 5 `rollback-failed`，属**正确**的拒绝；
- 回滚后的新字节被移到事务目录的 `rollback/` 下保留，不删除；
- `backup/`、`staging/`、`rollback/` 都不会被自动清理。

---

## 9. 失败时的取证清单（不要跳过）

| 情况 | 必须保存 |
| --- | --- |
| 任意非 0 退出 | `$ev` 下的 `.json` / `.txt`（`ExitCode`、`status`、`category`、`reason`、`detail`、`journal`） |
| 退出码 4 或 5 | 设备端 `journal.json`、`.mods-deploy/<TXID>/` 目录树列表、`staging/`、`backup/`、`rollback/` 的 `ls -la` |
| 怀疑路径问题 | `find '.../files' -maxdepth 3 -exec ls -ld {} +` 的输出（只读） |
| 怀疑 Saves 被动过 | 第 6.2 步的 before/after 两份输出 |

不要为了"让它过"而删除 journal、清空 `.mods-deploy`、`pm clear` 或卸载应用。

---

## 10. 结果记录表（请填完再交给维护者）

| 步骤 | 命令（可简写） | 退出码 | 关键字段 | 判定 |
| --- | --- | --- | --- | --- |
| 2 设备信息 | `adb devices -l` 等 | – | model / sdk=29 / abi / 239·1.6.15.0 | ☐ |
| 3 inspect | `-Action inspect` | | `status`, `transactions` | ☐ |
| 4 plan | `-Action plan -Source …` | | `fileCount`, `conflicts`, `actions` | ☐ |
| 5.1 deploy dry-run | `-Action deploy`（无 `-Apply`） | | `mode=dry-run` | ☐ |
| 5.2 deploy apply | `-Action deploy -Apply` | | `journal.state=committed` | ☐ |
| 6.1 verify | `-Action verify` | | `missing/modified/extra` | ☐ |
| 6.2 设备取证 | `find … sha256sum` | – | 逐文件哈希一致 | ☐ |
| 6.2 Saves 对照 | `ls -la …/Saves` before/after | – | 完全一致 | ☐ |
| 7 Loader（可选） | `am start` + `cat loader-report.txt` | – | `mods-failed=0` | ☐ |
| 8 rollback（可选） | `-Action rollback -Apply` | | 前态恢复 | ☐ |

### 判定准则

- **只有上表全部为"通过"且证据已登记**，才能把 Phase 1.2.2 的真机验收写为 PASS。
- 主机侧 fake-adb 通过、CI 通过、本文件存在，**都不构成**真机证据。
- 结论只覆盖这一台 V1963A / Android 10 设备，不得外推为所有 ARM64 设备或 Android 11+ 兼容。
