# Phase 2.0.1 真机验收报告

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-14（+08:00） |
| 设备 | vivo **V1963A** / PD1963，Android **10** / API **29** / arm64-v8a，非 debuggable、无 Root |
| serial | `6d7200a`（USB 物理机；MuMu 的 `127.0.0.1:16384`、`emulator-5554` 全程未选择、未写入） |
| 包名 | `com.chucklefish.stardewvalley`（唯一操作对象） |
| 游戏 | 1.6.15.0 / versionCode 239 |
| 证据根 | `.tmp/phase2.0.1/20261114-2053/device/`（gitignore） |
| 结果 | **PASS（限本次设备与已执行路径）** |

---

## 0. 结论

| 验收项 | 结果 | 证据 |
| --- | --- | --- |
| 备份先于写设备，且双端逐文件一致 | PASS（57/57） | `logs/backup.log`、项目外 `host.sha256.txt` / `device.sha256.txt` |
| 安装前 installed 身份（APK + signer）独立采集 | PASS | `device/installed-before.apk`（= 备份 base.apk）、`logs/verify-pre-install.log` |
| 升级准入用 installed（不是 baseline） | PASS：`INSTALL_CANDIDATE` | `host/verify-pre-install.json` |
| 同签名原位覆盖安装，未卸载 | PASS | `device/install-modded.txt` |
| 安装后普通 shell 哈希证明 | PASS：设备 `base.apk` = `c571a25b…` | 同上 + `device/installed-after.apk` |
| 安装后准入复核 | PASS：41/41，`post_install_hash_matches=true` | `host/verify-post-install.json` |
| Runtime 替换事务 | PASS：`20261114T133014Z-78bc0c3a`，committed，15/15 | `device/deploy-journal.txt`、`device/mods-deploy.txt` |
| Loader 3/3、P114 仍在 | PASS | `device/loader-report.txt` |
| `GameLaunched` 恰好一次、早于首个 Tick | PASS（`launched_at_tick_count=0`） | `device/alpha-snapshot1/2.txt` |
| Tick 连续增长 | PASS（600 → 1200，dispatch 601 → 1201） | 同上 |
| 顺序（Ordinal mod id） | PASS（`Alpha,Beta`） | 同上 |
| 异常隔离 / 重复订阅 / Dispose / 延迟订阅 | PASS（无回归） | `device/beta-snapshot2.txt`、`device/beta-log.txt` |
| 有界诊断可核实 ModCount | PASS（`diagnostics_mods=2`，与 `event-mods=2` 一致） | `device/alpha-snapshot2.txt` |
| 标题界面正常、无原生/托管崩溃 | PASS | `device/title.png`、`device/logcat.txt` |
| Saves 前后清单与哈希一致 | PASS（0 文件，mtime 09:15 未变） | `device/saves-before.txt` / `saves-after.txt` |
| 结束进程 | PASS（pid 12601 → 无） | `device/pid.txt` / `pid-after-stop.txt` |

---

## 1. 备份（项目外，逐文件双端校验）

| 项 | 值 |
| --- | --- |
| 备份根 | `%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-2053\` |
| Mods | 27 文件 / 257,240 B（含 `.staging`、`logs`、loader 报告等运行期产物） |
| Saves | **0 文件**（显式空目录基线） |
| `.mods-deploy` | 29 文件 / 137,872 B（9 个历史事务 + 本轮之前的全部记录） |
| base.apk | 389,561,500 B，`d4f60d577edb57a66fa2ce68a04786f3b9cd650e12a5c6c9302688a529d4a5dd` |
| 合计 | **57 文件 / 389,971,296 B**，设备侧与主机侧 **57/57** SHA-256 一致 |

第一次拉取时游戏进程仍在运行，两个 `probe-output/*.txt` 在拉取与设备端重新计算之间继续被写入，
导致 2 个文件不一致（已记录在 `logs/backup.log` 的前一次运行中）。随后 `am force-stop` 停止游戏，
重新拉取并复核，最终 **57/57 一致**；报告只采用最终这份。

Saves 为空是**显式记录的空目录基线**，不是“没有检查”。

---

## 2. 安装准入与安装

### 2.1 三个不同的 APK

| 角色 | 路径/来源 | SHA-256 | signer SHA-256 |
| --- | --- | --- | --- |
| 内容 baseline | `app/build/outputs/apk/debug/app-debug.apk` | `65e1e2af…f9d` | `611c8fa4…5de` |
| candidate | `app/build/outputs/apk/modded/app-modded-debug.apk` | `c571a25b…02a` | `858ef62d…5ff` |
| installed（安装前） | 备份的 `apk/base.apk`（设备 `/data/app/.../base.apk`） | `d4f60d57…5dd` | `858ef62d…5ff` |

**关键事实**：baseline 与 candidate 签名不同，但 installed 与 candidate 相同。
旧验证器（candidate signer == baseline signer）会误报 `BLOCKED`；本阶段新模型根据 installed 证据
判定 `INSTALL_CANDIDATE`：

```text
admission  : INSTALL_CANDIDATE
reason     : candidate signer equals the INSTALLED signer; a same-signature in-place replace is admissible
checks     : 40 passed, 0 failed, 0 blocked, 1 unknown
unknown    : post_install_hash_matches_candidate（安装前，正确为 UNKNOWN）
```

### 2.2 安装

`Tools/deploy-apk-safe.ps1 -Serial 6d7200a -ApkPath <candidate> -InstallApproved`：

```text
signature-gate  : SAME - installed and local certificates match
installing      : adb -s 6d7200a install -r --no-incremental <apk>
Success
post-install    : MATCH - c571a25b8e5fa89afd9d2aef4ca4d8af336257f07517ca3795d0552d8a68302a
verdict         : INSTALLED (same-signature replace; device base.apk SHA-256 == local APK)
```

- 证明使用普通 `adb shell pm path` + `sha256sum`，**不调用 `su`**；
- 没有卸载、没有 `pm clear`、没有清数据；
- 安装后重新拉取 `base.apk`（`device/installed-after.apk`）并复核：`41/41`、`post_install_hash_matches=true`。

---

## 3. Mods 部署

`Tools/deploy-mods-safe.ps1 -Action deploy -Apply -Serial 6d7200a -Overwrite ModFramework.Runtime.dll`：

| 项 | 值 |
| --- | --- |
| 计划 | 15 文件；1 REPLACE + 14 SKIP |
| 事务 | `20261114T133014Z-78bc0c3a` |
| 替换 | `ModFramework.Runtime.dll`：`c92be121…2464` → `d2604b67…b8ce`（130,560 B） |
| 验证 | staged 1/1、remote 15/15、旧 Runtime 保留在 `backup/` |
| 结果 | `committed`；`safety.usesRoot=false`、`writesSaves=false`、`installsApk=false`、`forceStopsGame=false` |

> 本轮共有两次 Mods 部署：先用 `80877ff1…` 的 Runtime 完成安装与首轮运行；随后加入文档注释，
> 确定性 MVID 从 `80877ff1…` 变为 `d2604b67…`。为保证“设备上是最终源码的构建”，用最终构建重新部署
> （事务 `20261114T133014Z-78bc0c3a`）并重跑本节全部验收。第一次事务 `20261114T131519Z-e5b43656`
> 作为历史保留在设备 `.mods-deploy/`；下表与证据文件均指最终这次运行。

设备端 Mods 载荷（8 个文件）哈希与部署源一致；运行期产物（loader 报告/日志、`probe-output`、
`.staging` 随机目录）按预期变化，不当作载荷差异。

---

## 4. 运行结果

启动：`am start -W -n com.chucklefish.stardewvalley/.MainActivity`，约 24 秒，未进入/创建存档。

### 4.1 Loader

```text
mods-discovered=3  mods-initialized=3  mods-failed=0
framework-version=1.1.0  framework-version-state=known
events=ready  event-session=1  event-subscriptions=7  event-mods=2
event-loader-thread-id=1
initialized=P114.RuntimeProbe / P20.Acceptance.Beta / P20.Acceptance.Alpha
events-subscribed=P20.Acceptance.Beta count=5
events-subscribed=P20.Acceptance.Alpha count=2
result=ok
```

### 4.2 GameLaunched / Tick

```text
observed_launches=1
launched_at_tick_count=0
first_launch_order=P20.Acceptance.Alpha
first_tick_order=P20.Acceptance.Alpha,P20.Acceptance.Beta
```

| 快照 | 时刻 | 观察 Tick | 诊断 Tick | dispatch |
| --- | --- | ---: | ---: | ---: |
| 1 | 启动后约 14 s | 600 | 600 | 601 |
| 2 | 再过约 10 s | 1200 | 1200 | 1201 |

线程：Loader / GameLaunched / 首个 Tick / 探针写文件均为 managed thread **1**；进程 `pid=12601`。

### 4.3 隔离与订阅（Beta）

```text
fault_attempts=3
healthy_ticks=1200
duplicate_calls_tick1=2
duplicate_calls_tick2=1
duplicate_calls_tick3=0
late_calls_tick3=0
late_calls=1196
double_dispose=1
diagnostics_handler_faults=3
diagnostics_log_faults=0
diagnostics_bridge_faults=0
```

故障日志有界：3 次故障只写 2 条 ERROR（第 1 次带堆栈、第 2 次汇总，之后限流），没有每 Tick 刷日志。

### 4.4 ModCount

`diagnostics_subscriptions=6`、`diagnostics_mods=2`：

- 初始 7 个订阅（Beta 5 + Alpha 2）；
- tick1/tick2 各 Dispose 一个重复订阅（-2）；
- tick3 内新增一个延迟订阅（+1）；
- mod 身份去重后仍是 **2**（Alpha、Beta），不是“订阅数”也不是相邻项计数。

### 4.5 P114 与稳定性

- P114 探针：`cases=14 observed=12 pass=12 fail=0 blocked=0 not-run=2`，仍通过 Loader 初始化。
- 标题界面正常（`title.png`），logcat 无 `FATAL` / `AndroidRuntime` / `MissingMethodException` /
  `TypeLoadException` / `InvalidProgramException`。

---

## 5. Saves 与最终状态

| 项 | 值 |
| --- | --- |
| Saves 文件数 | 运行前 **0**，运行后 **0** |
| 目录 mtime | `2026-11-14 09:15`（前后一致） |
| 前后清单 | `saves-before.txt` 与 `saves-after.txt` 内容完全相同 |
| 进程 | `pid=12601`，`am force-stop` 后为空 |
| APK | 设备 `base.apk` = `c571a25b…`（= 本轮 candidate） |
| Runtime | `d2604b67…`（= 部署源） |
| Mods 载荷 | P114 / Alpha / Beta 与部署源一致；运行期产物按预期更新 |
| 事务 | 13 个（11 历史 + 本轮两次部署 `20261114T131519Z-e5b43656`、`20261114T133014Z-78bc0c3a`），均 `committed` |
| 最终版本身份 | 事件版 C（Loader + GameLaunched/Tick） |

没有 `pm clear`、没有卸载、没有 Root、没有修改 MuMu、没有进入或创建存档。

---

## 6. 回退路径（本轮未触发）

1. 事件相关异常 → 重新安装 Phase 2.0 的 B（Loader-only）APK，Mods 不变；
2. 框架整体异常 → 安装 A（baseline）APK（注意：A 的签名与设备当前签名不同，原位升级不可行；
   需要显式授权 + 备份后卸载重装，本轮没有做）；
3. Mods 回滚 → `deploy-mods-safe.ps1 -Action rollback -TxId 20261114T133014Z-78bc0c3a -Serial 6d7200a -Apply`
   （恢复旧 Runtime `c92be121…` 并保留事务记录）。

---

## 7. 边界

- 只覆盖 V1963A / Android 10 / API 29 这一台设备；不外推到 Android 11+、其它 OEM、其它 GPU。
- 只验证标题界面路径；没有进入存档，`SaveLoaded`/`DayStarted`（本阶段未实现）不在覆盖范围。
- Tick 计数与渲染帧率/CPU 占用的关系没有测量；本轮只记录“进入 `Update` 的计数”。
- 桌面 0 B/tick 是 .NET 8 当前线程分配，不是设备端性能结论。
