# Phase 2.0 真机验收报告

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-14（+08:00） |
| 设备 | vivo **V1963A** / PD1963，Android **10** / API **29** / arm64-v8a，security patch 2022-05-01，非 debuggable、无 Root |
| serial | `6d7200a`（USB 物理机；MuMu 的 `127.0.0.1:16384`、`emulator-5554` 全程未选择、未写入） |
| 包名 | `com.chucklefish.stardewvalley`（唯一操作对象） |
| 游戏 | 1.6.15.0 / versionCode 239 |
| 证据根 | `.tmp/phase2.0/20261114-2025/`（gitignore） |
| 结果 | **PASS（限本次设备与已执行路径）** |

---

## 0. 结论

| 验收项 | 结果 | 证据 |
| --- | --- | --- |
| C（事件版）安装且设备字节 == 目标 APK | PASS | `device/install-c`、`logs/install-c.txt` |
| Loader 初始化 3 个 Mod、0 失败、framework 1.1.0 | PASS | `device/run-c/loader-report.txt` |
| `GameLaunched` 恰好 1 次，且早于首次 `Tick` | PASS | `device/run-c/probe/Alpha/phase2.0-alpha.txt` |
| `Tick` 单调增长（两次快照 1800 → 3600） | PASS | 同上 + `…snapshot2.txt` |
| 线程归属（Loader / GameLaunched / Tick） | PASS（记录，非假设） | 两份报告 + `device/run-c/threads.txt` |
| 确定顺序（Ordinal mod id） | PASS（`Alpha,Beta`，且 Beta 先初始化） | Alpha 报告 `first_tick_order` |
| 一个 handler 抛异常不影响健康 handler | PASS（1800/1800） | Beta 报告 `healthy_ticks=1800`、`fault_attempts=3` |
| 重复订阅 / 退订 / Dispose 幂等 | PASS（2 → 1 → 0；double_dispose=1） | Beta 报告 |
| 回调中新增订阅下一轮生效 | PASS（tick3 内注册，tick3 内仍为 0，之后 1796 次） | Beta 报告 |
| 异常日志有界 | PASS（3 次故障 → 2 条 ERROR 记录，无第 3 条） | `device/run-c/logs/logs/P20.Acceptance.Beta.log` |
| 原有 Mod（P114 探针）仍初始化 | PASS | loader 报告 `initialized=P114.RuntimeProbe` |
| 标题界面正常、无原生崩溃 | PASS | `device/run-c/title-c.png`、logcat 无 FATAL |
| B（Loader-only）无任何事件派发证据 | PASS（`diagnostics_dispatch=0`、只有 initialize 快照） | `device/run-b/` |
| Saves 无副作用 | PASS（0 文件、目录 mtime 09:15 未变） | `device/*/saves-*.txt` |
| 最终恢复 C 并核对哈希 | PASS | `device/run-final/` |

---

## 1. 备份（项目外，逐文件双端校验）

| 项 | 值 |
| --- | --- |
| 备份根 | `%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-2025\` |
| Mods | 17 文件 / 204,316 B |
| Saves | **0 文件**（空目录基线；目录 mtime 09:15 未变） |
| `.mods-deploy` | 27 文件 / 17,522 B（部署前 10 个事务） |
| 合计 | 44 文件 / 221,838 B，**设备侧与主机侧 44/44 SHA-256 完全一致**（`device/files-before.sha256.txt`） |
| 安装前 base.apk | `97c38ed7eb6ac719d7a5ee941abac3766307ad03205569bed19db5a48e7d5d90`（389,561,500 B） |

备份先于任何写入完成；`Saves` 为空是**显式记录的空目录基线**，不是“没有检查”。

---

## 2. 安装与部署

| 项 | 值 |
| --- | --- |
| C APK | `app/build/outputs/apk/modded/app-modded-debug.apk`，SHA-256 `d4f60d577edb57a66fa2ce68a04786f3b9cd650e12a5c6c9302688a529d4a5dd` |
| 安装方式 | `adb -s 6d7200a install -r`（同签名原位升级，未卸载、未 `pm clear`） |
| 安装后设备哈希 | `sha256sum /data/app/…/base.apk` = `d4f60d57…a5dd` → **与目标 APK 一致** |
| Mods 部署 | `Tools/deploy-mods-safe.ps1 -Action deploy -Apply … -Overwrite ModFramework.Runtime.dll` |
| 事务 | `20261114T122056Z-6d61246d`，`committed`，4 ADD + 1 REPLACE，5/5 远端哈希一致，旧 Runtime 留在 `backup/` |
| 设备端 Mods（最终） | `ModFramework.Runtime.dll` `c92be121…2464`、`P20.Acceptance.Alpha/*`、`P20.Acceptance.Beta/*`、原有 `P114.RuntimeProbe/*` 未改动 |

说明：`Tools/deploy-apk.ps1` 的哈希证明依赖 `su -c sha256sum`（需要 Root），`Tools/deploy-apk-safe.ps1` 同样要求 Root 才能读取安装路径。
本机无 Root，因此改用**设备上 `/data/app/.../base.apk` 对 shell 用户可读**这一事实，直接 `sha256sum` 证明
“装上的字节就是本轮产物”。这比只看 `pm path` 更强，且不引入 Root。

---

## 3. C（事件版）运行结果

启动：`am start -n com.chucklefish.stardewvalley/.MainActivity`，停留约 38 秒，未进入/创建存档。

### 3.1 Loader

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

**原有的 `P114.RuntimeProbe`（Phase 1.2.4 的探针）仍然初始化成功**，说明新 Runtime 与旧 Mod 在设备上共存。

### 3.2 `GameLaunched` 与 `Tick`

```text
reason=on-tick
observed_launches=1
launched_at_tick_count=0          <- 派发 GameLaunched 时还没有任何 Tick
launch_before_first_tick=true
observed_ticks=1800               <- 首次快照
diagnostics_ticks=3600            <- 8 秒后的第二次快照（持续增长）
diagnostics_dispatch=1801         <- 1800 Tick + 1 次 GameLaunched
```

### 3.3 线程（实测，不是推断）

| 事件 | 记录值 |
| --- | --- |
| Loader `Initialize`（全部 Mod） | managed thread **1** |
| `GameLaunched` 派发 | managed thread **1** |
| `Tick` 派发 | managed thread **1** |
| 探针写文件 | managed thread **1** |

进程 `pid=4587`，`ps -T` 显示其**主线程 TID = 4587**（进程第一个线程，即 Android 主/UI 线程），
另有 `Jit thread pool`、`RenderThread`、`.NET Long Runni…`、`Binder:*` 等独立线程。
**没有任何事件在这些线程上派发。**

结论：本设备上 Loader、`GameLaunched` 与 `Tick` 都运行在**进程主线程**上；框架不做线程切换。
契约只承诺“进入游戏方法的那个线程”，不承诺 UI 线程——这条实测是本阶段的证据，不是契约来源。

### 3.4 顺序

```text
first_launch_order=P20.Acceptance.Alpha
first_tick_order=P20.Acceptance.Alpha,P20.Acceptance.Beta
```

Alpha 的 manifest **依赖 Beta**，因此 Loader 先初始化 Beta（`events-subscribed=Beta` 出现在 Alpha 之前）。
派发顺序仍然是 **Alpha → Beta**，证明顺序来自 Ordinal mod id 而不是初始化/注册顺序。

### 3.5 异常隔离、重复订阅、快照语义（Beta）

```text
fault_attempts=3          <- 故障 handler 只在前 3 个 Tick 抛异常（有界注入）
healthy_ticks=1800        <- 健康 handler 收到每一个 Tick
duplicate_calls_tick1=2   <- 同一 delegate 注册两次 = 两个独立订阅
duplicate_calls_tick2=1   <- tick1 回调内退订一个
duplicate_calls_tick3=0   <- tick2 回调内退订第二个
double_dispose=1          <- 重复 Dispose 是 no-op
late_calls_tick3=0        <- tick3 回调内新增的订阅本轮不生效
late_calls=1796           <- 下一轮起开始收到
diagnostics_handler_faults=3
diagnostics_log_faults=0  diagnostics_bridge_faults=0
```

日志有界性（`logs/P20.Acceptance.Beta.log`）：

```text
ERROR … event handler for Tick threw (subscription #1); the remaining handlers still run   <- 第 1 次（带堆栈）
ERROR … event handler for Tick is still throwing: 2 failures for subscription #1 (further logs are rate limited)
```

**3 次连续故障只写了 2 条记录**，第 3 次按限流规则不写；没有每 Tick 刷日志。

### 3.6 标题界面与稳定性

- 截图 `device/run-c/title-c.png`：正常的 Stardew Valley 标题界面（NEW / LOAD），无弹窗、无黑屏。
- logcat（`logs/logcat-run-c.txt`）没有 `FATAL` / `AndroidRuntime` / `MissingMethodException` / `TypeLoadException` / `InvalidProgramException`。
- 进程持续运行到停止前（`pid` 稳定）。

---

## 4. B（Loader-only 回退）运行结果

| 项 | 值 |
| --- | --- |
| B APK | `app/build/outputs/apk/loader-only/app-loader-only-debug.apk`，`b67816857c0c08c5ce2bb2c60c5db22c172b60fa8d2c0eb3418f5180c9eeffd4` |
| 安装后设备哈希 | 与目标一致（同签名原位升级） |
| 启动前清理 | 删除 `probe-output/`、`loader-report.txt`、`loader.log`，并 `logcat -c`，排除旧报告干扰 |

```text
loader: mods-initialized=3 mods-failed=0 events=ready event-subscriptions=7
Alpha : reason=initialize-before-first-event
        observed_ticks=0 observed_launches=0 diagnostics_ticks=0 diagnostics_dispatch=0
Beta  : reason=initialize — 所有计数为 0
```

- 探针**只有初始化快照**，之后没有再写任何文件 → 本进程**没有任何事件派发证据**。
- 没有任何 `on-tick` 报告，也没有 `bootstrap-events.txt`（说明不是“桥接禁用”，而是构建里根本没有事件钩子）。
- 标题界面正常（`device/run-b/title-b.png`），logcat 无崩溃。
- 注意：Loader-only 只是“没有游戏钩子”，Runtime 的 `IModEvents` 仍然存在（`events=ready`、订阅数 7），
  所以这是**事件不派发**而不是“事件 API 不存在”。

---

## 5. 最终状态（恢复事件版）

| 项 | 值 |
| --- | --- |
| 重新安装 C | 成功；设备 `base.apk` = `d4f60d57…a5dd`，与目标一致 |
| 启动 | 标题界面正常（`device/run-final/title-final.png`） |
| 事件 | `observed_launches=1`、`observed_ticks=1800`、顺序 `Alpha,Beta`、线程 1、故障 3、日志 0 失败 |
| Loader | `mods-initialized=3  mods-failed=0  events=ready  event-subscriptions=7` |
| Mods 最终哈希 | Runtime `c92be121…`、Alpha `00d4da77…`、Beta `12902638…`，与部署源逐文件一致 |
| 事务 | 11 个（10 个历史 + 本轮 `20261114T122056Z-6d61246d`），journal `committed`，旧 Runtime 备份保留 |
| Saves | **0 文件**，目录 mtime 09:15 未变（空目录基线保持） |

没有 `pm clear`、没有卸载、没有 Root、没有修改 MuMu、没有进入或创建存档。

---

## 6. 失败与回退

本轮没有失败项，因此没有触发回退。已就绪的回退路径：

1. 事件相关异常 → 装 B（Loader-only）APK，Mods 不变；
2. 框架整体异常 → 装 A（baseline APK）；
3. Mods 回滚 → `Tools/deploy-mods-safe.ps1 -Action rollback -TxId 20261114T122056Z-6d61246d -Serial 6d7200a -Apply`
   （会把 Runtime 恢复成部署前字节，并删除两个新增 Mod 目录）。

---

## 7. 边界

- 只覆盖这一台 V1963A / Android 10 / API 29；不外推到 Android 11+、其它 OEM、其它 GPU。
- 只验证标题界面路径；没有进入存档，所以 `DayStarted`/`SaveLoaded` 类事件（本阶段未实现）不在覆盖范围。
- `Tick` 计数与渲染帧率的关系没有测量（`Update` 每次进入计数，真机上 38 秒约 3600 次，接近 60 Hz 但**不作为契约**）。
- 事件线程只在本次设备/本次运行上记录；框架不做线程切换，也不承诺 UI 线程。
