# Phase 1.1.8 报告：CI 可移植性修复、原始签名密钥恢复审计与 Approval-Gated Android MonoVM 验证

**本阶段目标**：先修掉 Phase 1.1.7 之后那次手动 push 引出的红色 CI（Ubuntu 上唯一失败的
`modsroot_normalizes_separators`），再做一次**有边界**的原始签名 keystore 恢复审计，
在"原始 key 不可恢复"成立后把现有 test key 持久化到项目外长期密钥库、用它重建 mod-enabled APK，
并为后续的 A（Mods 推送）/ B（安装）/ C（启动取证）准备好可审批的计划。
**本阶段没有写设备**：CI 未绿之前不安装、不推送 Mods、不启动游戏。

**一句话结论**：Linux/Windows 路径分隔符回归已在本机修复并全量回归通过（9/9 suites、host
486/486），原始签名 key（`611c8fa4…`）确认**不可恢复**（它是建库时自动生成、只存在于已删除的
`.tmp/build`），现有 test key（`858ef62d…`）已复制到 `%USERPROFILE%/.android/keystores/StardewValley/`
并逐字节/逐指纹复核，候选 mod-enabled APK 已用该长期密钥重建并通过 35 项静态验收
（1 项安装准入 BLOCK 属预期）。**下一步只能由用户手动 commit/push 让 Actions 变绿**，
DSH 不 push。

---

## 0. 总状态

| 项 | 值 |
| --- | --- |
| 阶段主状态 | **`CI_FIX_READY_AWAITING_USER_PUSH_AND_CI`** |
| 签名密钥状态 | **`TEST_SIGNING_KEY_PRESERVED`**（原始 key 不可恢复，见 §3） |
| 候选 APK 状态 | **`BUILT_WAITING_FOR_INSTALL_APPROVAL`**（静态验收 35 PASS / 0 FAIL / 1 BLOCK） |
| A（Mods 推送批准） | **NOT REQUESTED**（前置：CI 3/3 绿 + 本阶段有 delta） |
| B（安装批准） | **NOT REQUESTED**（前置：CI 3/3 绿 + E0 + 备份校验，见 §6） |
| C（启动/取证批准） | **NOT REQUESTED**（前置：B 完成） |
| 设备写入次数 | **0**（无 `install` / `uninstall` / `pm clear` / `push` / `am start` / `force-stop`） |
| 设备读取次数 | **0**（E0 未执行：§5 的门禁要求 CI 先绿） |
| Saves 访问 | **0 次**（未定位、未列出、未 pull；规则与备份方案见 §9） |
| 本地测试 | **9/9 suites PASS**，host 486/486（含 5 条新增路径断言） |
| 工作区 | 6 modified + 2 added，**未 commit、未 push** |

状态选择依据（任务书 §10）：本地修复完成、CI 尚未重跑 → `CI_FIX_READY_AWAITING_USER_PUSH_AND_CI`；
原始 key 找不到但 test key 已安全持久化 → `TEST_SIGNING_KEY_PRESERVED`；APK 已验收但安装被
"CI 未绿 + 未获批"阻断 → `BUILT_WAITING_FOR_INSTALL_APPROVAL`。

---

## 1. Gate 0：无损核对（P118-01）

仓库 `D:\Download\DSH\StardewValley`；`origin` = `https://github.com/Hiyomi-Kuro/StardewValley.git`。

| 项 | 实测值 | 与任务书 §0.1 对照 |
| --- | --- | --- |
| branch | `main` | 一致 |
| `HEAD` | `8cbbb93358698ab66f702ca0b33e13011c1ac0b2` | 一致（= 用户手动 push 的提交） |
| `origin/main` | `8cbbb93358698ab66f702ca0b33e13011c1ac0b2` | 一致 |
| ahead / behind | `0 / 0` | — |
| `git fetch origin` 后 | `origin/main` 仍为 `8cbbb93…` | 远程未前进，无新提交需要审查 |
| 开库时 `git status --short` | 空（工作区干净） | 无用户未提交改动 |
| `git diff --check` | 干净 | 无空白错误 |

未执行 `reset --hard`、`git clean`、强制 checkout、stash/pop、merge/rebase。
未删除或覆盖 `.work/debug.keystore`、65 refs、baseline APK、历史证据。

任务目录（唯一，位于项目外）：
`D:\Download\DSH\tasks\20261109-p118-signing-runtime-a7f3\`，日志在 `logs\`，产物在 `outputs\`，
临时文件在 `.tmp\`。

---

## 2. Phase 1.1.8-A：Linux/Windows 路径分隔符回归修复（P118-02）

### 2.1 失败事实（Actions #9）

| 项 | 值 |
| --- | --- |
| run | `42606913384`（#9），head `8cbbb93…` |
| 失败 job | `dotnet build + loader host tests` → 步骤 `loader host tests (no game assemblies)` |
| host tests | 475 checks / 474 passed / **1 failed** |
| 失败检查 | `modsroot_normalizes_separators` |
| 输入 / 期望 / 实际 | `C:\mods\test\ModFramework.Runtime.dll` / `C:/mods/test` / 外部 fallback |

### 2.2 根因

`Loader.ResolveModsRoot()` 的**顺序**错了：先 `Path.GetDirectoryName(location)`，后
`PathRules.Normalize(directory)`。Windows 把 `\` 当分隔符，所以本机 475/475 通过；
Ubuntu 的 `Path.GetDirectoryName` 只认 `/`，对纯 Windows 路径返回空 → 提前落入
`ExternalModsRoot`。**规范化发生在取父目录之后，太晚了。**

这是跨平台可测性缺陷，不是测试过严：生产 Android 路径永远用 `/`，所以设备行为不变；
但"同一段纯函数在两个平台上给出不同答案"本身就是缺陷（任务书 §2.1 的要求是平台无关）。

### 2.3 修改

`Mod/ModFramework.Runtime/Loader.cs`（+8 / −1）：在调用宿主 API 之前先
`PathRules.Normalize(location)`，再取父目录，结果再归一化一次；异常/空/无父目录仍
Fail-Closed 到 `ExternalModsRoot`。`PathRules.Normalize` 只替换分隔符，不触碰根、UNC、URI 语义，
所以不需要新增 helper，也没有引入新依赖。

`Tools/tests/ModFramework.Tests/HostTests.cs`（+29）：`ModsRootConsistency` 从 8 条断言扩到 13 条，
新增/强化：

| 检查 | 输入 | 期望 |
| --- | --- | --- |
| `modsroot_normalizes_separators` | `C:\mods\test\ModFramework.Runtime.dll` | `C:/mods/test` |
| `modsroot_accepts_forward_slashes` | `C:/mods/test/ModFramework.Runtime.dll` | `C:/mods/test` |
| `modsroot_windows_trailing_separator` | `C:\mods\test\` | `C:/mods/test` |
| `modsroot_unix_style_path` | `/opt/app/Mods/ModFramework.Runtime.dll` | `/opt/app/Mods` |
| `modsroot_drive_relative_falls_back` | `C:` | `ExternalModsRoot` |
| `modsroot_root_only_falls_back` | `/` | `ExternalModsRoot` |
| `modsroot_never_private_fallback` | 11 个分支 | 都不含 `/data/data/` |

没有删除任何原有断言，也没有把 expected 改成 fallback。期望值全部是**平台无关字面量**，
契约记录在 `docs/architecture/phase1-loader-design.md` §13.5。

### 2.4 本地验收（Windows）

| 命令 | 结果 |
| --- | --- |
| `test_loader.py --dotnet <SDK 8.0.404> --refs <.tmp/build-modded/refs>` | 45/45 驱动 + **486/486 host**（原 481 + 新增 5） |
| `test_mod_pipeline.py`（经 run-all） | 27/27 |
| `test_modded_build.py` | 49/49 |
| `test_ci_offline.py` | 13/13 |
| `test_p114_probe.py` | 46/46 |
| `privacy-scan.py --include-untracked` | `RESULT: OK`（CRITICAL/HIGH 0，MEDIUM 0，INFO 28 为机器路径） |
| `test_device_precheck.ps1` | 10/10 |
| `test_deploy_safe.ps1` | 95/95 |
| `run-all.ps1 -Dotnet <SDK> -Refs <refs>` | **9 suites, 9 passed, 0 failed, 0 skipped** |
| `git diff --check` | 干净 |

### 2.5 Linux/Ubuntu 侧

本机没有可用的 WSL/Linux `dotnet`（只有 Windows SDK 8.0.404），因此
**Ubuntu 复跑：NOT RUN（本地不可执行）**，不由我伪造。判定依据只能是用户 push 后新的
Actions run；在 3/3 jobs SUCCESS 之前，本阶段不进入任何设备操作。

---

## 3. Phase 1.1.8-B：原始签名 keystore 的受限恢复审计（P118-03 / P118-04）

### 3.1 搜索范围（有边界，未全盘扫描）

| 范围 | 结果 |
| --- | --- |
| `%USERPROFILE%\.android\` | 1 个 `debug.keystore`（2,618 B，2026-05-26） |
| `%USERPROFILE%\.android\keystores\StardewValley\` | 阶段开始时**不存在** |
| `StardewValley\.work\` | 1 个 `debug.keystore`（2,666 B） |
| `StardewValley\.tmp\`（含 `build`、`build-modded`、`phase0.5*`） | **无** `*.keystore` / `*.jks` / `*.p12` / `*.pfx` |
| `StardewValley\app\build\` | 无 |
| `D:\Download\DSH\tasks\`（历史 Stardew/APK/Android 任务） | 无 |
| `D:\Download\AndroidStudioProjects\` | 仅其它项目资产：`ADB\...\autojs.keystore`、若干 `*.bks` truststore |
| `D:\Download\Codex\` | 仅 Edge/Chrome profile 的 `.store` 元数据（94 B），非密钥库 |
| `D:\Download\GitHub\` | 同上（`ChromeExtMalware.store.store`） |
| `C:\$Recycle.Bin` / `D:\$Recycle.Bin` | **无**被删的 keystore 残留 |

元数据搜索只做文件名匹配（`debug.keystore`、`*.keystore`、`*.jks`、`*.p12`、`*.pfx`），
没有解压任何 ZIP/7z，也没有读取其它应用的私有数据。

### 3.2 精确比对（不只比 DN）

对每个真正的 keystore 候选做两步独立核验：

1. `keytool -list -v`（`-storepass:env`，密码不落在命令行/日志）拿 SHA-256 指纹；
2. `keytool -exportcert` 导出 DER → 独立算 SHA-256（证书 DER 摘要，与 apksigner 的
   `signer-sha256` 同一口径）。

| 候选（脱敏） | alias | 证书 SHA-256 | 与 `611c8fa4…` |
| --- | --- | --- | --- |
| `%USERPROFILE%\.android\debug.keystore` | `androiddebugkey` | `b8849d6e0146f3fa8c95e5911f444372b6693162142e1aa93d1700a2da3be088` | **不匹配** |
| `StardewValley\.work\debug.keystore` | `androiddebugkey` | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` | **不匹配**（= 已知 test key） |
| `AndroidStudioProjects\ADB\...\autojs.keystore` | — | 未作为 signer 候选 | 它属于 AutoJs 项目资产，不是 Android 标准 debug keystore，密码未知；按规则不猜密码 |
| `*.bks`（ADB / WakeUP） | — | 不适用 | BKS 是**信任库**，不是签名私钥库 |
| `*.store`（Edge/Chrome） | — | 不适用 | 浏览器 profile 元数据，不是密钥库 |

### 3.3 结论：原始 key 不可恢复

- 全部候选的证书 SHA-256 都不等于 `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de`
  → 搜索结论 **`SIGNING_KEY_CANDIDATES_MISMATCH`**；
- 结合 Phase 1.1.7 已确认的事实（`Signing.kt` 在 keystore 缺失时**自动生成**新 key，
  baseline 的 key 只存在于 `.tmp/build/debug.keystore`，而 `.tmp/build` 整个目录已不存在，
  回收站也无残留），可判定：**原始 key 是建库时自动生成的、随 `.tmp` 一起被删除，不可恢复**；
- 不可能从 APK 反推私钥，也不下载任何"现成 key"、不猜密码、不暴力破解。

→ 转入任务书授权的替代路径：**持久化现有 test key**，并在备份校验后对指定 MuMu 测试环境
**卸载重装**。绝不把它称作原始 key，也绝不用它 `install -r`。

### 3.4 长期密钥库持久化（已完成）

| 项 | 值 |
| --- | --- |
| 目录 | `%USERPROFILE%\.android\keystores\StardewValley\`（项目外，不在 `.tmp`/`.work`/任务目录） |
| 文件 | `stardewvalley-test-signing.keystore` |
| 大小 / 文件 SHA-256 | 2,666 B / `0f696561b66b50d825cff235c3c12983a87dc82a20ac7e43fa850bb6467732ed` |
| 与来源的关系 | 与 `StardewValley\.work\debug.keystore` **逐字节相同**（同一 SHA-256），来源保留未动 |
| alias / 条目数 | `androiddebugkey` / 1（`PrivateKeyEntry`） |
| 证书 DER SHA-256（复制前） | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` |
| 证书 DER SHA-256（复制后） | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` |
| keytool SHA-256 指纹（复制后） | `85:8E:F6:2D:…:FA:E5:FF`（与复制前逐段相同） |
| NTFS ACL | 继承已断开；仅 `BUILTIN\Administrators`、`NT AUTHORITY\SYSTEM`、当前用户（`(OI)(CI)F`） |
| BitLocker / EFS | **BLOCKED**：`Get-BitLockerVolume` 需管理员权限，本会话被拒（见 §10） |
| 加密离线备份 | **PENDING USER**：需要用户自选口令与介质，见 §10 |

复制前该目录的 ACL 还包含 `CodexSandboxUsers:(RX)`（继承而来）；已通过
`icacls /inheritance:r` + 显式授权收紧到三个主体。

### 3.5 密码边界

- 该 test key 使用 Android 默认 debug 密码（与仓库既有 `Signing.kt` 一致），未做无关重构；
- 构建日志里会出现 `--ks-pass pass:android`：这是既有脚本把默认密码写进命令行参数的行为，
  记录为**已知观察项**（默认 debug 密码不是密钥材料，但"把口令放进命令行/日志"本身不理想）；
- 未在报告、日志、Git、任务目录写入任何密码或私钥；`keytool` 一律用 `-storepass:env`；
- 若将来匹配 key 的密码不是默认值，必须先按任务书 §3.4 改成 env/stdin + Fail-Closed 测试，
  再谈使用。

---

## 4. Phase 1.1.8-C：用长期密钥库重建 mod-enabled APK（P118-05）

### 4.1 构建

```powershell
# 强制重建注入链（新 MVID），产物仍在 .tmp/build-modded
.\gradlew.bat --offline :app:assembleModdedDebug --rerun-tasks `
    "-Pdotnet=<SDK 8.0.404>" `
    "-Pkeystore=%USERPROFILE%\.android\keystores\StardewValley\stardewvalley-test-signing.keystore"
```

`BUILD SUCCESSFUL`（3 actions executed，3m23s）。构建日志确认
`keystore: …\StardewValley\stardewvalley-test-signing.keystore (require-existing=true)`：
`--require-existing-keystore` 生效，没有生成新 key。

### 4.2 候选 APK 身份

| 项 | 值 |
| --- | --- |
| 路径 | `app/build/outputs/apk/modded/app-modded-debug.apk` |
| 大小 | **389,561,500 B**（371.51 MiB） |
| SHA-256 | **`794e0e7f88f278feb49af76d2ddaac9974f1a0c1fbe59c8ce11f7630ab282290`** |
| package / version | `com.chucklefish.stardewvalley` / versionCode **239** / versionName **1.6.15.0**（未降级） |
| signer（apksigner） | **`858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff`** |
| 签名方案 | v1 + v2 + v3 全开，`apksigner verify` rc=0 |
| zipalign | 35.0.0 与 36.0.0 的 `-c -p 4` 均通过 |
| 注入 blob | `.tmp/build-modded/modded-assemblies.blob`，6,183,615 B，SHA-256 `1662f0f2b06a3f4d12e0f80a0d0f6cd81f8652c81be90051523aea6ddd8199d5` |
| 注入后 MVID | `e7202336-b6c5-46fd-ab3a-5d2f7bde0184`（构建前 `573bf1e5-…-1c3713afb677`） |
| patched DLL | 9,261,568 B，SHA-256 `58a592364145c555b175e34bc198b7a48e1af3da8971d9fad8c062c56638272f`，`descriptor_index=7`，≤ 上限 9,280,000 |
| baseline APK | `app-debug.apk` 388,967,580 B / `65e1e2af…051f9d`，**未被覆盖**（mtime 仍为 2026-11-04） |

### 4.3 静态验收

`python Tools/verify_modded_apk.py --candidate … --baseline … --blob-manifest … --keystore <长期密钥库> …`

| 项 | 结果 |
| --- | --- |
| checks | **35 passed / 0 failed / 1 blocked**（退出码 0） |
| Bootstrap hook | `verify` 从 **APK 里的 blob 重新解出** DLL 校验：hook 是首条有效 IL、`present (51 instr, 182 bytes)` |
| Loader API / Mods 根 | `loadfrom (Assembly.LoadFrom(string))` / 外部 `/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods` |
| 容器 | `entry_count=58`、`total_count=65`、`descriptor_index=7`、gap 2600、57/57 其它条目未变 |
| APK 条目 | 3,672 条目逐 CRC 比对，只有 `assemblies.blob` + `META-INF` 变化 |
| 包名/版本 | `com.chucklefish.stardewvalley` / 239 / 1.6.15.0 |
| 安装准入 | **BLOCK `candidate_signer_equals_baseline`** → `UPDATE_INCOMPATIBLE_RISK` |

**这个 BLOCK 是预期结果，不是缺陷**：候选 signer（test key）与已安装/ baseline signer
（`611c8fa4…`）不同。脚本内置的说明文字仍写着"uninstall is not permitted (it deletes saves)"，
那是**没有拿到本次授权的静态口径**；按用户 2026-11-09 修订，MuMu 测试环境在
"Saves/Mods 已备份并校验"之后**允许**卸载重装。因此本阶段把它记为"安装门禁需要 B 批准 +
备份验收"，而不是"永久禁止"。

---

## 5. Phase 1.1.8-D：E0 / Saves 备份 / Mods delta —— 本阶段**未执行**

任务书 §5 的三个前置条件：

| 前置 | 状态 |
| --- | --- |
| A 本地测试通过 | ✅ 已满足（§2.4） |
| 用户手动 push 后 Actions 对最新 HEAD 3/3 SUCCESS | ❌ **未满足**（修复尚未 push） |
| 候选 APK 由长期密钥库签名并通过 §4 | ✅ 已满足（§4） |

因此 E0（设备基线）、Saves/Mods 备份、Mods delta 一律**未执行**：本阶段
`adb` 调用次数为 0，设备读取次数为 0，Saves 访问次数为 0。这不是遗漏，而是门禁顺序。

下一轮（CI 绿之后）将按下列计划执行，届时仍需 A/B/C 各自的明确批准：

1. **E0（只读）**：重新核验 `127.0.0.1:16384` 的 serial/型号/Android 版本/package；
   记录已安装 APK 的 SHA-256 与 signer、外部 Mods 根是否仍为 11 文件 / 172,191 B、
   `ModFramework.Runtime.dll` 等文件当前 SHA-256、空间、UID/GID、权限、staging 残留。
2. **Saves 备份（只读 pull）**：`/sdcard/Android/data/com.chucklefish.stardewvalley/files/Saves`
   → `%USERPROFILE%/Documents/StardewValley-TestBackups/MuMu12/<YYYYMMDD-HHMMSS>/`，
   逐文件记录相对路径/大小/SHA-256，核对文件数与总字节数，抽查可读性并做整体 hash 复核；
   原始备份只读保留，测试只操作恢复出来的工作副本。
3. **Mods delta**：重新编译当前 Runtime（含本阶段 `ResolveModsRoot` 修正）与 P114，
   生成"相对路径 / 本地 SHA-256 / 设备当前 SHA-256 / 是否需要更新"清单，
   只推送真正变化的文件（预计核心变化是 `ModFramework.Runtime.dll`）。

---

## 6. A / B / C 门禁：状态与待批准计划

### 6.1 A（Mods 推送）

**NOT REQUESTED**。前置未满足（CI 未绿），且 delta 清单尚未生成。
未获 A 批准前：设备写入 0 次、不 `adb push`、不创建/修改/删除设备文件。

### 6.2 B（安装）——下一轮要批准的具体路径

因为候选 signer（`858ef62d…`）**不等于**已安装 signer（`611c8fa4…`），本阶段**不走原位升级**，
计划走"备份校验后卸载重装"。B 批准请求会明确列出：

| 项 | 计划 |
| --- | --- |
| 采用方式 | **卸载重装**（不是 `install -r`，不是 `pm clear`） |
| 目标设备 | 仅 `127.0.0.1:16384`（MuMu 12 / Android 12 / API 32 / x86_64 + libnb.so），执行前重新核验 |
| 命令 | `adb -s 127.0.0.1:16384 uninstall com.chucklefish.stardewvalley` → `adb -s 127.0.0.1:16384 install app-modded-debug.apk` |
| 备份位置 | `%USERPROFILE%/Documents/StardewValley-TestBackups/MuMu12/<timestamp>/`（逐文件 SHA-256 清单） |
| 影响 | 卸载会清除应用私有数据（含 Saves 与外部 `files/Mods`）；装完按备份清单恢复 Mods 工作副本与 Saves 工作副本 |
| 回滚 | 用同一备份 + baseline APK（signer `611c8fa4…`）恢复；baseline 的 key 已不可得，所以**恢复只能靠已装包不回滚签名**——因此备份是唯一的存档保险 |
| 不做的事 | 不对非指定设备执行；不重复 `pm clear`；不删除唯一备份 |

### 6.3 C（启动/取证）

**NOT REQUESTED**。C 只允许：启动到标题界面、读本次生成的 `loader-report.txt` /
`loader.log` / P114 `probe-output/phase1.1.4.txt` 与限定窗口的 logcat；不 `force-stop`、
不重启、不循环重启；需要读档时只用备份恢复出的工作副本。

---

## 7. R-01 … R-14：本阶段真实状态

前置（Bootstrap 实际运行 + Loader 实际启动 + P114 输出文件真实生成）一个都没有发生，
因此按任务书定义：

| 编号 | 内容 | 本阶段状态 |
| --- | --- | --- |
| R-01 / R-02 | SHA-256 标准向量 | **NOT RUN**（需设备运行；桌面 486 项不等于 MonoVM） |
| R-03 / R-12 | preflight → staging → assembly-load → Initialize 顺序 | **NOT RUN** |
| R-04 / R-05 / R-06 | 同路径 / 同身份同字节 / 同身份不同字节 `LoadFrom` | **NOT RUN** |
| R-07 … R-10 | SimpleName / identity / Location / AppDomain | **NOT RUN** |
| R-11 | private dependency 预加载与调用 | **NOT RUN** |
| R-13 | 生产路径 `LoadFrom` 文件 SHA-256 | **NOT RUN** |
| R-14 | 损坏 fixture 与异常隔离 | **NOT RUN** |

设备分类固定记为 **Emulator（x86_64 + libnb.so 翻译）**，不冒充 ARM64 physical；
ARM64 真机仍然 **BLOCKED**。

---

## 8. 测试 / CI 矩阵

| 套件 | 本地 | CI | 说明 |
| --- | --- | --- | --- |
| `test_ci_offline.py` | PASS 13/13 | 跑（`offline checks`） | 含 UTF-8 / mojibake / 历史证据口径检查 |
| `test_modded_build.py` | PASS 49/49 | 跑 | 静态 + 合成 XABA 往返 |
| `test_p114_probe.py` | PASS 46/46 | 跑 | 源文本安全审计 |
| `privacy-scan.py` | PASS（0 高危） | 跑 | 机器路径仅 INFO |
| `test_device_precheck.ps1` | PASS 10/10 | 跑（windows） | 无设备 |
| `test_deploy_safe.ps1` | PASS 95/95 | 跑（windows） | 假 adb |
| `test_loader.py` | PASS 45 驱动 + **486 host** | 跑（ubuntu） | 新增 5 条路径断言 |
| `test_mod_pipeline.py` | PASS 27/27 | **SKIPPED**（需游戏程序集） | local-only |
| `test_loader.py --refs`（example mod + 65 refs 差分） | PASS | **SKIPPED** | local-only，145/145 |
| Ubuntu 侧 `modsroot_*` 断言 | **NOT RUN** | 待用户 push 后判定 | 本机无 Linux dotnet |
| 设备 E0 / Saves 备份 / Mods push / install / 启动 | **NOT RUN** | — | 门禁未到 |

CI 结论只能来自用户 push 后的新 run；在 3/3 SUCCESS 之前，本阶段不声明 CI 通过，也不做设备操作。

---

## 9. Saves：访问范围与备份规则（本阶段未访问）

- 本阶段**没有**定位、列出、读取、pull 或修改任何 Saves；`adb` 调用 0 次。
- 已确定的规则（下一轮执行）：真实路径只按 Stardew Valley 本应用定位
  （`/sdcard/Android/data/com.chucklefish.stardewvalley/files/Saves`，必要时只读查找私有目录）；
  备份必须落到项目外的
  `%USERPROFILE%/Documents/StardewValley-TestBackups/MuMu12/<YYYYMMDD-HHMMSS>/`；
  逐文件记录相对路径 / 大小 / SHA-256，核对文件数与总字节数；
  原始备份只读，测试只用恢复出的工作副本；卸载或 `pm clear` 之前必须完成并校验备份；
  无法访问的位置记 `BLOCKED`，不伪造"备份完成"。

---

## 10. 未决项

1. **CI 未绿**：必须由用户手动 commit/push；DSH 不 commit、不 push。
2. **原始签名 key 不可恢复**：任何"签名连续"的原位升级都做不到；只能备份 + 卸载重装。
3. **BitLocker/EFS 与加密离线备份 BLOCKED/PENDING**：`Get-BitLockerVolume` 在本会话被拒
   （需要管理员或交互式提升）；加密离线备份需要用户自选口令与介质。建议用户自行执行，
   例如 `7z a -p -mhe=on <离线介质>\SV-keystore-<date>.7z "<长期库目录>"`（口令只由用户掌握）。
4. **apksigner 命令行含默认密码**：既有行为，已记录；若将来 key 密码不是 `android`，
   必须先改成 env/stdin 并补 Fail-Closed 测试。
5. **构建非字节可复现**：MVID 每次随机，所以每次 `assembleModdedDebug` 的 blob/APK SHA-256 都不同
   （本轮 `794e0e7f…` 只对应当前这次构建）。
6. **设备上的 `ModFramework.Runtime.dll` 仍是旧版**：本阶段 `ResolveModsRoot` 的修正只在源码里，
   要生效必须先获 A 批准做一次 delta 推送。
7. **Phase 1.2 未启动**：没有 Harmony/detour、SMAPI 兼容层、事件 Hook、Content/Data API、
   热重载、游戏内 Mod 管理 UI 或玩法功能。

---

## 11. 修改文件清单

| 文件 | 类型 | 摘要 |
| --- | --- | --- |
| `Mod/ModFramework.Runtime/Loader.cs` | 修改 | `ResolveModsRoot` 先归一化分隔符再取父目录（+8/−1） |
| `Tools/tests/ModFramework.Tests/HostTests.cs` | 修改 | `ModsRootConsistency` 扩到 13 条断言，期望值平台无关（+29） |
| `docs/architecture/phase1-loader-design.md` | 修改 | 新增 §13.5 路径规范化契约 + §13 keystore 长期化说明 + 变更记录（+36） |
| `docs/audit/phase1.1.7-report.md` | 修改 | §0 计数更正为 14+5；新增 §15 用户手动 push 与 Actions #9 失败后记（+41/−1） |
| `README.md` | 修改 | mod-enabled 构建改为长期密钥库绝对路径 + 签名准入说明（+9/−4） |
| `Tools/README.md` | 修改 | 同上（+9/−3） |
| `docs/audit/phase1.1.8-report.md` | **新增** | 本文件 |
| `docs/audit/phase1.1.8-evidence-index.md` | **新增** | 证据索引 |

未修改（有意）：`Tools/build_modded_apk.py`、`Tools/AssemblyStore/store.py`、`Tools/ModInjector`、
`Tools/verify_modded_apk.py`、`Tools/deploy-apk-safe.ps1`、`app/build.gradle.kts`、
`app/src/main/kotlin/**`、`Signing.kt`、`.github/workflows/ci.yml`、任何游戏资源、
Phase 1.1.6 及更早的历史报告。

工作区 diffstat（不含两个新增文件）：**6 files changed, 132 insertions(+), 9 deletions(-)**。
**DSH 没有 `git commit`，也没有 `git push`。**

---

## 12. Definition of Done 对照

| 验收项 | 状态 | 证据 |
| --- | --- | --- |
| P118-01 Git 无损基线 | **PASS** | §1；HEAD = origin/main = `8cbbb93…`，0/0，工作区干净 |
| P118-02 Linux 路径修复 | **PASS（本地）** | §2；代码 + 5 条新断言；host 486/486 |
| P118-03 keystore 受限搜索 | **PASS** | §3.1；有边界范围，无全盘扫描 |
| P118-04 指纹精确比对 / 持久化 | **PASS** | §3.2–3.4；`SIGNING_KEY_CANDIDATES_MISMATCH` → `TEST_SIGNING_KEY_PRESERVED` |
| P118-05 候选 APK 重建 + 静态验收 | **PASS** | §4；35 PASS / 0 FAIL / 1 BLOCK（准入） |
| P118-06 签名准入判定 | **PASS（判定正确）** | §4.3；signer 不一致 → 走卸载重装路径，不 `install -r` |
| P118-07 E0 设备基线 | **BLOCKED** | §5；CI 未绿 |
| P118-08 Saves/Mods 备份 | **BLOCKED** | §5；CI 未绿；规则见 §9 |
| P118-09 A / B / C 门禁 | **PASS（未越权）** | §6；均为 NOT REQUESTED，设备写入 0 |
| P118-10 R-01…R-14 | **NOT RUN** | §7；前置未发生 |
| P118-11 测试 | **PASS（本地）** | §8；9/9 suites、486/486 host；Ubuntu 侧 NOT RUN |
| P118-12 文档/证据 | **PASS** | 本文件 + `phase1.1.8-evidence-index.md` |
| P118-13 安全 | **PASS** | 未碰 Saves、未卸载/清数据、未启动；密钥未入库 |
| P118-14 Git 工作流 | **PASS** | 未 commit、未 push，`AWAITING_USER_PUSH_AND_CI` |

**总原则复核**：先修红色 CI，再处理签名密钥——已做到；原始 key 优先，不可恢复时用已持久化的
test key 走 MuMu 卸载重装——已做到（安装仍待 B）；任何 key 都放项目外持久位置——已做到；
允许访问 Saves，但卸载/`pm clear` 前必须完成并校验项目外备份——本轮未执行此类操作；
A/B/C 门禁继续有效——遵守；非指定设备不执行破坏性操作——遵守；DSH 永不自动 push——遵守。
