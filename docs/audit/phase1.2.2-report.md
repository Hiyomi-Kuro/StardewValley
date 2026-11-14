# Phase 1.2.2：无 Root Mods 部署工具审计、CI 集成与设备验收准备

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-14（+08:00） |
| 分支 | `main` |
| 基线 HEAD | `62477fbe3ec8ade4888558ad1256679af219dc3b`（与 `origin/main` 一致，开工前核对） |
| 工作区状态 | 开工前干净；本阶段改动**未提交**（用户保留 commit/push 控制权） |
| 作用域 | 只动 `Tools/deploy-mods-safe.ps1`、其测试、CI 的 PowerShell gate、README/AGENTS/设计文档/审计文档 |
| 未触碰 | Loader、Mod API、APK 构建、签名、游戏资源、`final/`；**没有连接任何真实设备** |
| 关联 | [证据索引](phase1.2.2-evidence-index.md) · [手动真机验收计划](phase1.2.2-manual-device-acceptance-plan.md) · [设计文档](../architecture/phase1.2.1-deployment-design.md) |

---

## 0. 结论（先看这一段）

**主机侧完成；真机验收未开始，状态 `AWAITING_USER_MANUAL_ACCEPTANCE`。**

| 维度 | 状态 | 依据 |
| --- | --- | --- |
| 源码已实现 | 是（工具版本 1.2.2） | `Tools/deploy-mods-safe.ps1` |
| 静态审计通过 | 是 | 本文 §2、§3 的逐项矩阵 |
| fake-adb 自动化通过 | 是（48/48，fake adb，无设备） | §5.1 |
| 既有 PowerShell 门禁无回归 | 是（device-precheck 10/10、deploy-safe 95/95） | §5.3 |
| CI 已接入并通过本地静态校验 | 是（**未实际跑 GitHub Actions**，本阶段不推送） | §6 |
| 真机验收 | **未执行，等待用户** | §10 |

**必须先说清楚的三件事：**

1. Phase 1.2.1 提交的这个工具**当时是不可用的**：`$PID` 只读变量赋值 + PowerShell 5.1 的
   `@($List[object])` 缺陷，使 `plan`/`deploy`/`verify` 一律以退出码 5 结束；它的测试脚本自身也有
   3 个缺陷，所以那轮"新增测试文件"从未真正证明过任何东西。本阶段以**实际执行结果**为准，见 §3、§5。
2. **工具从未在任何真机上运行过。** Phase 1.2 的 11/11 哈希一致是无 Root **手工**流程的证据，
   不是这个工具的。本文任何 PASS 都只覆盖 fake adb 与静态审计。
3. 兼容性口径不变：只有一台 vivo V1963A / Android 10 / API 29 有真机证据；**Android 11+ 的 apply 仍然
   fail-closed**，本阶段没有引入 SAF、额外权限、绕过 scoped storage 或 Root。

---

## 1. 开工前状态核对

```text
$ git status --short --branch
## main...origin/main

$ git rev-parse HEAD
62477fbe3ec8ade4888558ad1256679af219dc3b

$ git rev-parse origin/main
62477fbe3ec8ade4888558ad1256679af219dc3b

$ git log -5 --date=iso --format="%h %ad %s"
62477fb 2026-11-14 10:23:37 +0800 2026-11-14 10:23:00
c548137 2026-11-14 09:35:36 +0800 2026-11-14 09:35:00
35c515a 2026-11-14 09:05:39 +0800 2026-11-14 09:05:00
f5c5821 2026-11-10 14:23:30 +0800 2026-11-10 14:23:00
8cbbb93 2026-11-09 19:06:45 +0800 2026-11-09 19:06:00
```

- 工作区干净，没有用户未提交修改，HEAD 与远端一致 → 满足"可以开始写操作"的前置条件。
- 本地 `AGENTS.md` 当时记录的 HEAD 是 `35c515a`，**已过期**；本轮按实际 Git 状态与最新报告为准，
  并在 §9 修正了该文件的当前状态段。
- 本阶段没有 `git commit`、`git push`，没有任何 GitHub 写操作。

---

## 2. 审计矩阵：设计要求 → 源码位置 → 测试用例 → 结论

行内"测试"列给出 fake-adb 用例名（`Tools/tests/test_deploy_mods_safe.ps1` 的 `Check` 名称）。
所有用例均在本机 Windows PowerShell 5.1 上真实执行。

### 2.1 命令与授权边界

| 设计要求 | 源码位置 | 测试用例 | 审计结论 |
| --- | --- | --- | --- |
| 五个命令存在且互斥 | 脚本尾部 `if ($Command -eq ...)` 分派 | `unknown action refused`、`no-argument help exit 0` | 通过。未知命令退出码 3 `unknown-action` |
| 默认只读；`deploy` 不带 `-Apply` 等价 plan | `Invoke-PlanOrDeploy` | `dry-run deploy plan` | 通过 |
| `deploy -Apply` 必须显式 `-Serial` | `$script:SerialExplicit`、`Invoke-Deploy` | `apply requires explicit serial` | 通过，退出码 3 `serial-required` |
| `rollback -Apply` 必须显式 `-Serial` | `Invoke-RollbackCommand` | §5.1 的 `rollback dry-run changes nothing` + 代码审计 | 通过（dry-run 不需要 serial，apply 需要） |
| 多设备必须 fail-closed | `Get-DeviceContext` | `multiple devices fail closed` | 通过，退出码 2 `ambiguous-device` |
| 离线/未授权设备拒绝 | 同上 | `offline device refused`、`unauthorized device refused` | 通过，退出码 2 `serial-not-online` |
| serial 未连接拒绝 | 同上 | `unknown serial refused` | 通过，退出码 2 `serial-not-attached` |
| 包名固定，覆盖必须拒绝 | `$script:FixedPackage` + `Get-PackageState` | `package name override refused` | **1.2.2 修复后**通过（原来参数被脚本自身覆盖，拒绝分支不可达） |
| 游戏运行中拒绝 apply 且不 force-stop | `Get-PackageState` | `running game blocks apply` | 通过，退出码 3 `game-running` |
| `-Overwrite` 只授权指定相对路径 | `Get-RemotePlan`、`Get-ApprovedOverwrites` | `overwrite list must name a real conflict`、`authorized overwrite replaces the file` | 通过 |
| `plan`/`inspect`/`verify` 不接受 `-Apply` | 脚本尾部参数校验 | 代码审计（无独立用例） | 通过（退出码 3 `apply-not-supported`） |
| JSON 稳定且可解析 | `$script:Result`、`Exit-Tool` | `inspect JSON contract and self-attested safety flags`、`JsonOut writes a parseable report copy` | 通过 |
| 退出码可区分 | `Exit-Tool` | 各用例的 `exit=` 断言 | 通过（0/2/3/4/5 均有覆盖） |

### 2.2 路径与文件安全

| 设计要求 | 源码位置 | 测试用例 | 审计结论 |
| --- | --- | --- | --- |
| 源目录必须是完整 Mods 根 | `Get-SourcePlan` | `dry-run deploy plan` | 通过 |
| 拒绝 reparse point / symlink / junction | `Get-SourcePlan` 显式栈遍历 | `source reparse point (junction) refused`、`reparse rejection names the source boundary` | **1.2.2 修复后**通过（原来先递归枚举再检查） |
| manifest 路径穿越拒绝 | `Get-SourcePlan` 的 `^[^/\\:.]+\.dll$` | `manifest path traversal refused` | 通过 |
| 同一包内重复声明拒绝 | 同上（`$declared`） | `duplicate manifest path refused` | 通过 |
| Android 大小写敏感（声明必须逐字符匹配） | `-ceq` 匹配 | `manifest assembly name must match case exactly` | 通过 |
| 中文/空格路径不破坏参数边界 | `Quote-Posix` + argv 传参 | `Chinese and spaces survive deployment` | **1.2.2 修复后**通过（原来设备输出按 OEM 代码页解码会乱码） |
| 写范围仅 `Mods/**` 与 `.mods-deploy/<txid>/**` | `Test-RemoteAllowed` / `Assert-RemoteAllowed` | `no su, install, stop, clear, or sensitive paths in adb trace`、`Saves, keystore and APK sentinels remain present` | 通过（`files` 根只读例外见设计文档 D-4） |
| 设备侧 symlink 检查 | `Test-RemotePathChains` | `OS-owned /sdcard symlink does not block apply` | **1.2.2 修复后**通过（原来把 `/sdcard` 当不安全链接） |
| Saves 不被触碰 | 路径常量 + 白名单 | `Saves, keystore and APK sentinels remain present` | 通过（fake 设备里 `Saves/save1.dat`、keystore、APK 哨兵前后一致） |

### 2.3 事务与恢复

| 设计要求 | 源码位置 | 测试用例 | 审计结论 |
| --- | --- | --- | --- |
| staging 写入 → 逐文件 SHA-256 → 切换 | `Invoke-Deploy` | `apply deploy commits` | 通过（journal `committed`） |
| staging push 失败停在 Mods 之前 | 同上 | `staging push failure stops before Mods` | 通过，退出码 4 `staging-write-failed` |
| staging 哈希不符同理 | `Get-StagingVerification` | `staging hash mismatch stops before Mods` | 通过，退出码 4 `staging-hash-mismatch` |
| 切换失败进入回滚 | `Move-Remote` + `Invoke-Rollback` | `switch failure rolls back` | 通过，退出码 4 `switch-failed` |
| 最终字节不可证明时 fail-closed | `Test-FinalState` | `unprovable final bytes fail closed` | **1.2.2 修复后**通过（单元素结果被解包导致失败被忽略） |
| 进程被杀后 journal 可恢复 | journal 状态机 | `journal interruption leaves recoverable state`、`journal interrupted transaction recovers` | 通过 |
| 未完成事务必须阻止新 apply | `Assert-NoActiveTransaction` | `unfinished transaction blocks a new apply`、`inspect reports an unfinished transaction` | **1.2.2 修复后**通过（原来单事务时 `.Count` 为 `$null`，检查被跳过） |
| 重复 apply 幂等（全部 SKIP） | `Get-RemotePlan` | `second apply is a no-op (all SKIP)` | 通过 |
| `REPLACE` 保留旧字节 | `backup/` + `Move-Remote` | `replaced bytes are kept in backup` | 通过 |
| rollback 不覆盖事务外修改 | `Invoke-Rollback` 哈希归属判定 | `rollback refuses bytes not owned by transaction` | 通过，退出码 5 `rollback-failed` |
| rollback dry-run 不写设备 | `Invoke-RollbackCommand` | `rollback dry-run changes nothing` | 通过 |
| `verify` 报告 missing/modified/extra | `Invoke-Verify`、`Get-RemoteTree` | `verify reports modified and extra files`、`verify lists the extra file separately`、`verify reports missing files` | 通过 |
| 不把多文件切换当原子 | 设计文档 §4.1 + 报告口径 | —（口径项，无代码可测） | 保持 |

### 2.4 副作用审计（静态）

| 检查项 | 方法 | 结论 |
| --- | --- | --- |
| 无 Root / `su` / `run-as` 路径 | 全文静态审计 + fake adb 命令日志正则 | 未发现 |
| 不安装/卸载 APK、不 `pm clear`、不 force-stop、不 `am start` | 同上 | 未发现 |
| dry-run 不创建远端目录/不推送 | fake 设备树前后快照对比 | `dry-run performed no device writes`、`inspect is read-only` 通过 |
| 不删除未知文件、不清理 backup/journal | 全文无 `rm` / `rm -rf`；远端动词只有读/建/移/校验类（见下） | 未发现 |
| Android 11+ apply fail-closed | `Get-StorageState` | `Android 11+ apply fail-closed` 通过（退出码 2 `unverified-android-storage`） |

静态提取的工具行为清单（2026-11-14，`Select-String` 审计）：

- `adb` 动词只有 **`shell`** 与 **`push`**（外加 `devices -l` 只读列举）。
- 远端 shell 动词只有 `cat`、`chmod`、`command`、`df`、`find`、`ls`、`mkdir`、`mv`、
  `sha256sum`、`stat`、`test`；只读查询另有 `getprop`、`pm path`、`dumpsys package`、`pidof`。
- 全文没有 `rm`、`rm -rf`、`su`、`run-as`、`force-stop`、`pm clear`、`uninstall`、`am start`、
  `install`、`chown`、`pm grant`、`appops`、Magisk 或任何提权/卸载路径
  （唯一命中 `install` 的是提示文案 "pass -Adb or install adb"）。

---

## 3. 发现的问题与处置

### 3.1 P0（工具不可用 / 核心路径崩溃）

| # | 问题 | 证据 | 处置 |
| --- | --- | --- | --- |
| P0-1 | `Get-PackageState` 给只读自动变量 `$PID` 赋值，抛 `SessionStateUnauthorizedAccessException`，被顶层捕获为退出码 5 | 基线执行 14 通过 / 15 失败，失败项全部 `unexpected-exception`；`.tmp/phase1.2.1/test-results.log`（10:11 的历史记录）同样如此 | 改为 `$processState` |
| P0-2 | `@($list)` 包装 `List[object]` 在 Windows PowerShell 5.1 抛 "Argument types do not match"（`List[string]` 不受影响） | `plan`/`deploy` dry-run 直接退出码 5；`Get-RemotePlan`/`Get-Transactions` 断言失败 | 4 处改用 `.ToArray()`；同类缺陷在测试脚本里也有（见 §5.4） |

### 3.2 P1（安全边界失效或真机不可用）

| # | 问题 | 影响 | 处置 |
| --- | --- | --- | --- |
| P1-1 | 脚本第 20 行用常量覆盖了 `-PackageName` 参数本身 | `-PackageName <其它包>` 被静默忽略，`package-name-mismatch` 永远不可达 | 拆出 `$script:FixedPackage`；用例 `package name override refused` 现在真的会拒绝 |
| P1-2 | 路径链检查把 `/sdcard`、`/sdcard/Android` 等 OS 前缀当普通路径做 `test -L` | Android 上 `/sdcard` 是符号链接 → **真机会拒绝所有部署** | 边界下移到应用专属 `files` 根（`Test-RemotePathChains`）；新增 `FAKE_SDCARD_LINK=1` 回归用例 |
| P1-3 | 源树用 `Get-ChildItem -Recurse` 一次性枚举后再逐项检查 reparse point | Windows PowerShell 5.1 的 `-Recurse` 会跟随 junction，链接环可能在检查前就被遍历 | 改为显式栈遍历：取一层 → 检查 → 只对非 reparse 目录下降 |
| P1-4 | 设备输出按 OEM 代码页解码，而 `adb shell` 输出是 UTF-8 | 非 ASCII 设备路径变乱码 → 误报 `staging-hash-mismatch`（方向对，原因错，用户会误判） | 工具与测试宿主统一按 UTF-8 解码 |
| P1-5 | 单元素数组被 PowerShell 解包后 `.Count` 为 `$null` | `inspect` 看不到未完成事务（退出码 0）；`Test-PreState`/`Test-FinalState`/`Test-StagingModes` 的**单个**问题会被静默忽略 | 调用点改 `@(...)`；新增 `unprovable final bytes fail closed` 用例 |

### 3.3 P2（契约/质量）

| # | 问题 | 处置 |
| --- | --- | --- |
| P2-1 | `safety.deviceTouched` 在 apply 中始终为 `false` | 写路径置 `true`；用例 `apply reports mode=apply and deviceTouched=true` |
| P2-2 | 设计列出的 `-JsonOut`/`-ReportOut` 未实现 | 实现（原子写；报告副本，非权威 journal） |
| P2-3 | 临时 journal 文件写进项目 `.tmp/phase1.2.1` | 改到系统临时目录 |
| P2-4 | `Get-RemotePlan` 里有一段空 `if` 死代码、`$args`/`$matches` 被当临时变量 | 清理（`$args` 赋值虽可用，但会遮蔽自动变量） |
| P2-5 | 测试桩 `mv` 拒绝覆盖已存在文件，违背 POSIX 语义 | 修正为 POSIX 语义；工具对 journal 替换显式用 `mv -f` |
| P2-6 | 设计 §3.2 要求工具自写重复 JSON key 扫描器 | **不实现**，记录为偏差 D-2（Loader `Json.cs` 已在加载前拒绝 `MF0106`） |

### 3.4 明确记录为"未修复/不修"的偏差

见设计文档 §9.3 的 D-1…D-4（`staging-failed` 状态、重复键扫描器、manifest 语义校验深度、
`files` 根只读白名单）。每一条都写了理由与残余风险，没有把未验证的事写成已通过。

---

## 4. 修改文件清单

| 文件 | 变更 | 说明 |
| --- | --- | --- |
| `Tools/deploy-mods-safe.ps1` | 修改（版本 1.2.1 → 1.2.2） | P0/P1/P2 修复；`-JsonOut`/`-ReportOut`；UTF-8 输出解码；`mv -f`；显式栈遍历；批量 symlink 检查；系统临时目录 |
| `Tools/tests/test_deploy_mods_safe.ps1` | 修改 | 路径改到 `.tmp/phase1.2.2`；修 3 个测试桩缺陷；新增 14 项检查（JSON 契约、只读性、幂等、`/sdcard` 链接、最终哈希、`-Overwrite`、missing 文件等） |
| `.github/workflows/ci.yml` | 修改 | `powershell-gates` 新增 `test_deploy_mods_safe.ps1` 一步；job 名与注释更新；`timeout-minutes` 15 → 45 |
| `README.md` | 修改 | 目录树、功能边界、无 Root 部署用法、测试表、CI 说明（仍保持四节） |
| `docs/architecture/phase1.2.1-deployment-design.md` | 修改 | 状态、CLI 参数、状态机补充、JSON 字段、新增 §9 审计矩阵/偏差/风险现状 |
| `docs/audit/phase1.2.2-report.md` | 新增 | 本文件 |
| `docs/audit/phase1.2.2-evidence-index.md` | 新增 | 证据索引 |
| `docs/audit/phase1.2.2-manual-device-acceptance-plan.md` | 新增 | 用户手动真机验收计划 |
| `AGENTS.md` | 修改 | 本地文件（`.gitignore` 排除，不入库）：当前状态、第 12 节 PowerShell/Android 事实与坑 |

`git diff --stat`（不含未跟踪的新文档）：

```text
 .github/workflows/ci.yml                          |  16 +-
 README.md                                         |  53 +++++-
 Tools/deploy-mods-safe.ps1                        | 176 +++++++++++++-----
 Tools/tests/test_deploy_mods_safe.ps1             | 214 +++++++++++++++++++---
 docs/architecture/phase1.2.1-deployment-design.md |  97 +++++++++-
 5 files changed, 468 insertions(+), 88 deletions(-)
```

`git diff --check` 无问题（退出码 0）。两个 `.ps1` 工作区换行保持 LF（与 `HEAD` 版本一致；
`.gitattributes` 的 `*.ps1 eol=crlf` 只在 checkout 时生效，本阶段没有改动换行风格）。

---

## 5. 测试执行结果

所有命令都在 `D:\Download\DSH\StardewValley` 下执行；日志在 `.tmp/phase1.2.2/logs/`。
**没有调用真实 adb、没有连接设备**（fake adb 由测试脚本自己编译，所有设备文件都在
`.tmp/phase1.2.2/<case>-<rand>/` 的模拟树里）。

### 5.1 新工具 fake-adb 套件

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_mods_safe.ps1
```

- 结果：**48 项全部通过（48 passed / 0 failed / 0 skipped），退出码 0，耗时约 1209 秒**
- 日志：`.tmp/phase1.2.2/logs/run5-test_deploy_mods_safe.txt`
- 逐项结果：`.tmp/phase1.2.2/logs/test-results.log`、`.tmp/phase1.2.2/logs/test-summary.json`
- 基线（未修改的提交状态，同一命令）：29 项已执行 → 14 通过 / 15 失败，且套件在写 summary 时
  因 `[ordered]@{ tests = @($list) }` 崩溃（日志 `.tmp/phase1.2.2/logs/baseline-test_deploy_mods_safe.txt`）

### 5.2 迭代过程（说明为什么基线数字不能当结论）

| 轮次 | 结果 | 关键发现 |
| --- | --- | --- |
| 基线 | 29 项 / 14 通过 15 失败 + 收尾崩溃 | `$PID` 只读、测试桩序列化缺陷 |
| run1 | 46 项 / 22 通过 24 失败 | `List[object]` 解包、`-PackageName` 被覆盖 |
| run2 | 45 项 / 27 通过 17 失败 | UTF-8 编码链、fake `mv` 语义 |
| run3 | 46 项 / 29 通过 17 失败 | `/sdcard` 符号链接、journal 覆盖语义 |
| run4 | 47 项 / 46 通过 1 失败 | 单元素事务列表导致 `inspect` 漏报 |
| run5 | 48 项 / 48 通过 0 失败，退出码 0 | 全量回归（最终结果） |

### 5.3 既有套件回归

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_device_precheck.ps1
```

- 结果：**10 checks, 10 passed, 0 failed**，退出码 0
- 日志：`.tmp/phase1.2.2/logs/phase122-device-precheck.txt`

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_safe.ps1
```

- 结果：**95 checks, 95 passed, 0 failed**，退出码 0（日志中 2 项能力探测标 `SKIPPED`，按该套件既有约定计为 PASS）
- 日志：`.tmp/phase1.2.2/logs/phase122-deploy-safe.txt`
- 备注：该套件有 2 项因本机不允许创建符号链接而 `SKIPPED`（不是通过，也不是失败）

### 5.4 离线检查（与本次改动相关）

| 命令 | 结果 | 日志 |
| --- | --- | --- |
| `python Tools/tests/test_ci_offline.py` | 13 checks, 13 passed（含 PowerShell 语法解析、UTF-8/mojibake、CI workflow 静态校验） | `.tmp/phase1.2.2/logs/phase122-ci-offline.txt` |
| `python Tools/privacy-scan.py` | 退出码 0 | `.tmp/phase1.2.2/logs/phase122-privacy.txt` |
| `python Tools/tests/test_p114_probe.py` | 退出码 0 | `.tmp/phase1.2.2/logs/phase122-p114.txt` |

### 5.5 静态检查

| 检查 | 结果 |
| --- | --- |
| PowerShell AST 解析（`Tools/deploy-mods-safe.ps1`、`test_deploy_mods_safe.ps1`） | 0 错误（套件内 `Parser ...` 两项 + `test_ci_offline.py` 的 `powershell_syntax`） |
| 工具内"本地函数引用必须解析"审计 | 通过（`AST local function references resolve`） |
| `git diff --check` | 退出码 0 |
| workflow YAML 语法（PyYAML `safe_load`） | 通过；三 job、四步 gate 正确 |
| PSScriptAnalyzer | **未安装，未运行**（按任务要求没有擅自安装新依赖） |

### 5.6 测试隔离性核对

- fake adb 由测试脚本用本机 `csc.exe` 现编（`FakeAdbLauncher` C# + PowerShell worker），
  通过 `-Adb <path>` 显式传给工具，**不读 PATH 上的真实 adb**。
- 每条命令的路径都经 `Device-Path` 限制在 `FAKE_DEVICE_ROOT` 之下；越界即抛异常。
- worker 对命令做黑名单拦截（`su`、`force-stop`、`pm clear`、`uninstall`、`install`、`Saves`、
  `shared_prefs`、`databases`、`/data/data`、`keystore`、`.apk`），命中即退出码 9。
- 每个用例前后对虚拟设备树做 SHA-256 快照；只读用例断言树完全不变。
- 最终日志扫描断言 adb trace 里没有上述禁用词（`no su, install, stop, clear, or sensitive paths in adb trace`）。

---

## 6. CI 改动与本地校验

`.github/workflows/ci.yml` 的 `powershell-gates` job：

```yaml
      - name: no-root mods deploy fake-adb suite
        shell: pwsh
        run: ./Tools/tests/test_deploy_mods_safe.ps1
```

- job 名更新为 `powershell gates (adb resolution, apk deploy safety, no-root mods deploy, no device)`；
- `timeout-minutes: 15` → `45`（该套件在本机实测约 20 分钟：每次 fake adb 调用都要现起一个 PowerShell 进程，
  但留足余量避免超时误判）；
- `dotnet-tools` job 的 SKIPPED 说明补上了新套件与真机验收计划；
- 新步骤只用 fake adb：**不需要真实设备、ADB server、Root、凭据或 secrets**，也**不上传 artifact**。

本地校验：PyYAML `safe_load` 通过、`test_ci_offline.py` 的 `ci_workflow_static` 通过
（含 trigger/permissions/runs-on/timeout-minutes/action 版本固定检查）。

**没有实际触发 GitHub Actions**（本阶段不 push）。CI 状态在用户推送后才产生，不能预先宣称绿。

---

## 7. 新旧部署脚本职责矩阵

| 维度 | `Tools/deploy-apk-safe.ps1` | `Tools/deploy-mods-safe.ps1` |
| --- | --- | --- |
| 主职责 | APK 安装门禁 + 签名比对 + （Root）Mods 推送 | 无 Root Mods 部署（staging/哈希/切换/journal/回滚） |
| 默认行为 | 预检只读，打印将执行的命令 | 只读或 dry-run |
| 写入路径 | `/data/data/<pkg>/files/Mods`（app 私有）+ `/data/local/tmp/svmods-*` staging | `/sdcard/Android/data/<pkg>/files/Mods` + `files/.mods-deploy/<txid>` |
| 需要 Root | **是**（`su -c mkdir/cp/chown/chmod/sha256sum/stat/rm`） | **否** |
| 是否安装/卸载 APK | 可以（`-InstallApproved`，仍禁止 uninstall/`pm clear`） | **从不** |
| 授权参数 | `-InstallApproved` / `-PushModsApproved` / `-OverwriteModsApproved` | `-Apply` + 显式 `-Serial` + `-Overwrite <rel>` |
| 哈希校验 | staging + 最终 + owner/mode | staging + 每次切换后 + 最终 | 
| 回滚能力 | 无（失败即停，保留 staging 供排查） | journal 两阶段 + 尽力回滚 + `-TxId` 显式回滚 |
| 适用设备 | 已 Root、可 `su` 的测试机 | 无 Root 的普通设备（外部存储可写） |
| 当前状态 | 保留兼容行为，**未改动** | 1.2.2 审计并修复；fake-adb 通过；真机待验收 |

结论：**两条链路保持分离，不合并、不互相 fallback**。无 Root 失败不会自动改用 Root；
旧脚本的 Root 推送入口通过 help/README 明确标注"仅适用于已 Root 设备"来降低误用风险，
其代码行为本阶段未改动（改动它需要单独的测试覆盖与设备验证）。

---

## 8. Android 11+ 决策门

- 现状：`Get-StorageState` 在 `$Apply` 且命令为 `deploy`/`rollback` 时，若 `apiLevel >= 30` 直接
  退出码 2 `unverified-android-storage`，**失败发生在创建 staging 目录之前**，因此不会留下半成品
  （只有 `test -d`/`getprop` 等只读调用已经发生）。
- 只读命令（`inspect`/`plan`/`verify`、以及不带 `-Apply` 的 `deploy`）在 API 30+ **仍可用**，
  因为它们不写设备；`storage.scopedStorageRisk` 会在 JSON 里报 `unverified-api30…`。
- 本阶段**没有**引入 SAF / `DocumentFile` / `MANAGE_EXTERNAL_STORAGE` / `appops` / `pm grant` /
  `su` / Magisk 等任何绕过手段，也没有移除该保护。
- 结论：Android 11+ 的可行性仍是**未验证**状态。要做这件事，至少需要：设备证据（不同 OEM）、
  系统 API 证据、以及"失败时如何回滚/如何不留半成品"的方案；本阶段不满足条件，因此保持拒绝。
- `test Android 11+ apply fail-closed` 用例锁定该行为，避免后续被"顺手放开"。

---

## 9. 文档更新

| 文档 | 更新内容 |
| --- | --- |
| `README.md` | 目录树加工具；功能边界表加 Phase 1.2.2 行（⚠️ 主机侧完成/真机待验收）；新增"无 Root Mods 部署"小节（用法、权限、行为、限制、与旧脚本区别、真机未验收提示）；测试表加一行；CI 说明改为三个 PowerShell 门禁 |
| `AGENTS.md`（本地、不入库） | 当前状态段整体重写（HEAD `62477fbe`、Phase 1.2.1 工具当时不可用、真机待验收、下一步顺序）；新增第 12 节（两种部署路径、PowerShell 5.1 四个陷阱、设备输出编码、`/sdcard` 是符号链接、POSIX `mv` 语义、本阶段产物） |
| `docs/architecture/phase1.2.1-deployment-design.md` | 状态改为"设计已实现"；CLI 参数与实现对齐；状态机补充 `staging-failed`；JSON 说明补 `unparseable`/`deviceTouched`/`-JsonOut`；新增 §9（实现映射、修复缺陷、偏差 D-1…D-4、§8 风险现状） |
| `docs/audit/phase1.2.2-report.md` | 本文件 |
| `docs/audit/phase1.2.2-evidence-index.md` | 证据索引（本地日志 + 未产生哪些证据） |
| `docs/audit/phase1.2.2-manual-device-acceptance-plan.md` | 用户手动真机验收计划（占位 serial、只读优先、证据清单、失败处置、结果表） |

README 仍严格保持四个顶层章节（目录 / 前置条件 / 构建 / 二次开发），新增内容都放在"构建"之下。

---

## 10. 真机验收状态

**`AWAITING_USER_MANUAL_ACCEPTANCE`。**

- 本阶段**没有**连接设备、没有调用真实 `adb`、没有 `adb devices`、没有安装/启动/停止任何应用。
- 工具的 device-facing 行为只由 fake adb 验证；**fake adb 不是设备**。
- 用户需要按 [手动真机验收计划](phase1.2.2-manual-device-acceptance-plan.md) 在既有
  V1963A / Android 10 测试机上执行，并把证据登记到 [证据索引](phase1.2.2-evidence-index.md)。
- 在该计划执行完之前：不得声称"Phase 1.2.2 真机通过"，不得把本报告的 fake-adb PASS 当作设备证据，
  也不得把结论外推到 Android 11+ 或其它 OEM。

---

## 11. 未解决风险与 Phase 2 准入

### 11.1 仍未解决

1. **应用可写性不可直接证明**：应用非 debuggable、无 `run-as`、无 Root；唯一真实证据仍是 Phase 1.2 的
   手工部署。工具的 fake-adb 通过不改变这一点。
2. **Android 11+ 未验证**：保持 fail-closed，没有实测。
3. **多文件切换非原子**：逐文件 `mv` + journal；断电窗口内可能短暂缺文件，靠 backup/journal 恢复。
4. **不删除旧文件 / 不清理备份**：`.mods-deploy` 会随部署增长，需要人工清理（本阶段没有加自动清理）。
5. **journal 在设备端**：清数据或外部删除后无法恢复；主机侧 JSON 只是报告副本。
6. **`/sdcard` 符号链接**：代码已按系统事实处理，但只有真机能最终确认（手动计划第 3–5 步覆盖）。
7. **性能**：单次 apply 在 fake adb（每次调用都要现起 PowerShell）下约 200 次设备调用、1–2 分钟；
   真机 adb 单次约几十毫秒，实际部署耗时可接受，但没有大 Mod 树（数千文件）的实测数据。
8. **CI 时长未实跑验证**：新套件本机约 20 分钟，`powershell-gates` 现在同一 job 里跑三个套件，
   超时设为 45 分钟。首次真实 Actions 运行的耗时应被记录；若逼近上限，正确做法是给新套件单独 job，
   而不是删减用例。

### 11.2 Phase 2 准入建议

本阶段**未启动** Phase 2（没有 Mod API、事件模型、Harmony/detour 改动）。建议准入顺序：

1. 用户 commit/push 本轮改动，并确认 CI `powershell-gates` 三步全绿（真实 Actions 结果，不是本地推断）。
2. 用户按手动计划完成一次真机验收（至少覆盖 inspect → plan → deploy → verify + Saves 对照），
   把证据登记进索引；再决定是否需要一次 `rollback` 演练。
3. 真机通过后，Phase 2 的第一件事仍应是**可行性研究**（最小 Mod API / 事件模型 / Harmony-detour 的
   风险与回滚方案），而不是直接实现方法补丁。
4. 在任何"支持 Android 11+"的说法之前，先拿到设备与 API 证据；没有证据就继续 fail-closed。

---

## 12. 复现方式

```powershell
cd D:\Download\DSH\StardewValley

# 1) 新工具套件（本机实测约 20 分钟，fake adb，无设备）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_mods_safe.ps1

# 2) 既有门禁回归
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_device_precheck.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_safe.ps1

# 3) 离线检查
python Tools/tests/test_ci_offline.py
python Tools/privacy-scan.py
python Tools/tests/test_p114_probe.py

# 4) 只读状态核对
git status --short --branch
git diff --check
```

本阶段没有修改 `.git`，没有 commit / push / GitHub 写操作；所有中间产物在 `.tmp/phase1.2.2/`
（`.gitignore` 已排除，可整体删除）。
