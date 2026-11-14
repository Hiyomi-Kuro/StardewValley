# Phase 1.2.4 证据索引

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-14（+08:00） |
| 基线 | `4221283ee73e7a1bc26408fc7709431a9c004d40`（用户已 push） |
| 阶段报告 | [phase1.2.4-report.md](phase1.2.4-report.md) |
| 真机记录 | [phase1.2.4-device-acceptance-report.md](phase1.2.4-device-acceptance-report.md) |
| Phase 2 | [phase2-feasibility.md](../architecture/phase2-feasibility.md) |
| 原始证据根 | `D:\Download\DSH\StardewValley\.tmp\phase1.2.4\20261114-142000\`（gitignore，可整体删除） |
| 备份根 | `%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-141147\`（项目外） |

---

## 1. `4221283` 的远端 CI

| 证据 | 位置 | 说明 |
| --- | --- | --- |
| Run 元数据 | `.tmp/phase1.2.4/ci-run.json` | REST `/actions/runs/43563074341`，`status=completed`、`conclusion=success`、`head_sha=4221283…` |
| Job/步骤元数据 | `.tmp/phase1.2.4/ci-jobs.json` | 3/3 jobs success；每步 `started_at` / `completed_at` / `conclusion` |
| 日志 | **不可得** | 公开仓库日志下载要求认证；未配置 token，REST 返回 **403**。报告中不使用远端日志的测试计数 |

| Job | 结论 | 耗时（由时间戳计算） |
| --- | --- | ---: |
| offline checks | success | 18 s |
| dotnet build + loader host tests | success | 73 s |
| powershell gates | success | 1171 s（19m31s） |
| └ `no-root mods deploy fake-adb suite` | success | 1105 s（18m25s） |

**边界**：该 Run 只覆盖 `4221283`，**不覆盖本阶段未提交修改**。

---

## 2. 工具 / 测试最终字节

| 文件 | SHA-256 |
| --- | --- |
| `Tools/deploy-mods-safe.ps1`（1.2.4） | `8d0863f806a5e9ae1542997dc5aa63e584945946194aa583e783421bd602b1dd` |
| `Tools/tests/test_deploy_mods_safe.ps1` | `a05d2630844ae34a1d6869bf8ba157fa697aa34095d7705d491f833706cc39f2` |

复现：

```powershell
(Get-FileHash Tools\deploy-mods-safe.ps1 -Algorithm SHA256).Hash.ToLower()
(Get-FileHash Tools\tests\test_deploy_mods_safe.ps1 -Algorithm SHA256).Hash.ToLower()
```

---

## 3. 主机回归

| 证据 | 位置 | 结果 |
| --- | --- | --- |
| fake-adb 套件（Windows PowerShell 5.1） | `.tmp/phase1.2.4/suite-final-ps51.log`、`logs/93-suite-ps51.txt`、`.tmp/phase1.2.2/logs/test-summary.json` | **76/76，0 failed / 0 skipped，退出码 0**（35m09s；29 个夹具设备纳入 sentinel 检查） |
| fake-adb 套件（PowerShell 7.4.6 便携版） | `.tmp/phase1.2.4/suite-final-ps7.log`、`logs/94-suite-ps7.txt` | **76/76，0 failed / 0 skipped，退出码 0**（50m19s；29 个夹具设备） |
| 离线检查 | `logs/90-ci-offline.txt` | 13/13，退出码 0 |
| 隐私扫描 | `logs/91-privacy.txt` | `RESULT: OK`，退出码 0 |
| P114 静态审计 | `logs/92-p114.txt` | 46/46，退出码 0 |
| 空白检查 | — | `git diff --check` 退出码 0（LF→CRLF 提示不算失败） |

PowerShell 7 是 GitHub Releases 的便携 zip：`.tmp/phase1.2.4/pwsh-7.4.6/`，未安装 MSI、未改系统 PATH。

---

## 4. 回滚报告修复的证据

| 证据 | 位置 |
| --- | --- |
| fake adb 回归 | 套件日志中的 `rollback stdout JSON reports the persisted rolled-back state`、`rollback -JsonOut agrees with stdout and the device journal`、`failed final journal write does not claim rolled-back`、`failed rolling-back write still reports the committed state` |
| 真机 stdout JSON | `.tmp/phase1.2.4/20261114-142000/json/70-rollback-apply.stdout.txt` |
| 真机 `-JsonOut` | 同上目录 `70-rollback-apply.json` |
| 真机设备 journal | `device/20261114T063834Z-aed22504.journal.json` |
| 真机 dry-run | `json/60-rollback-dry-run.*`（`state=committed`） |
| 幂等 | `json/80-rollback-again.*` |
| 初始 ADD 回滚 | `json/90-rollback-add.*` + `device/90-mods-after-all-rollbacks.sha256` |

---

## 5. 测试断言修复的证据

| 证据 | 位置 |
| --- | --- |
| 每个设备初始化 sentinel 快照 | 测试脚本 `Get-SentinelSnapshot` / `$script:sentinels`（静态可查） |
| 全部夹具设备检查 | 套件日志 `Saves, keystore and APK sentinels unchanged in every fixture device devices=N` |
| 负向控制 | 套件日志 `sentinel checker detects a modified Saves byte (negative control)` |

---

## 6. 真机 REPLACE 证据

| 证据 | 位置 |
| --- | --- |
| 每步 stdout / JsonOut | `.tmp/phase1.2.4/20261114-142000/json/10-… … 91-…` |
| 每步 `-ReportOut` 文本 | `logs/10-… … 91-…` |
| 设备逐文件哈希 | `device/00-mods-before.sha256`、`20-mods-after-v1-add.sha256`、`90-mods-after-all-rollbacks.sha256`、`00-saves-before.sha256`、`99-saves-after.sha256` |
| 设备 journal 副本 | `device/20261114T063751Z-c8bba640.journal.json`、`device/20261114T063834Z-aed22504.journal.json` |
| 汇总 | `device-replace-summary.json`（步骤、退出码、耗时、断言） |
| 控制台日志 | `device-replace.log`、`device-replace-console4.log` |
| 夹具 | `fixtures/phase124-v1/`、`fixtures/phase124-v2/`、`fixtures.sha256.json` |
| 备份校验 | `.tmp/phase1.2.4/20261114-142000/backup-Mods.sha256`、`compare-Mods.txt`、`backup-run3.log` |

备份目录（项目外）：

```text
%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-141147\
├── Mods\          17 文件 / 203,328 B（逐文件设备哈希一致）
├── Saves\         0 文件
└── mods-deploy\   3 文件 / 6,496 B（写入前历史事务）
```

---

## 7. Loader / 探针 / 截图

| 证据 | 值 |
| --- | --- |
| 启动 | `logs/launch-final2.txt`：`Status: ok`、`WaitTime: 882` |
| 前台 | `device/foreground-final2.txt`：`mResumedActivity` 为本游戏 |
| 旧报告 | `device/loader-report-final-before.txt`：1368 B / `b75b883d…25e4` / 14:28:24 |
| 新报告 | `device/loader-report-final2.txt`：1368 B / `21e1fad8a8811e2c042d502b445a1314ad2e4ae6a882a7c7137bdde7f811f1ba` / **14:39:53** |
| 探针 | `device/probe-output-final2.txt`：`cases=14 observed=12 pass=12 fail=0 not-run=2` |
| 截图 | `device/72-title-screen.png`：2400×1080 / 397,158 B / `af12b55899685d99257bf84eff71b2c755b71f50f89e7e7acc69e428b3ac5035` |
| logcat | `device/logcat-final2.txt`：4 行，无 FATAL/SIGSEGV/UnsatisfiedLinkError |

---

## 8. Phase 2 研究证据

| 证据 | 值 / 位置 |
| --- | --- |
| 程序集身份 | `StardewValley.dll` 1.6.15.24354 / SHA-256 `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047` / MVID `573bf1e5-3245-46d6-925a-1c3713afb677` |
| REA 元数据摘要 | `ev_5d1f1c354b00a4cd1c2952a9065cc1570c830aa41213ecbc93c55a3595cb1493` |
| REA 成员选择（含 `ModHooks` 不存在） | `ev_403b49461d899ba9375b9fd48c7dc8dac5f5a4f0df7a4b2ed068ee50de0f983d`（`unmatched_type_names=["StardewValley.ModHooks"]`） |
| REA Game1 | `ev_b4e0ef62ebb50746227e15d20bf88cfe1a32bfd8a4e64e5ba84922e17a1f8995` |
| REA InstanceGame | `ev_c9339fb4f517e3fc10a0dda2e73e6604921fd9203231744a5c7462a408c81118` |
| REA InstanceGame 方法页 | `ev_10adf08567c868dfda384ac498ecf1b2d495a472f9c3678f422240b952a96600` |
| 全部事件（34 条）与 selected 类型 | `device/meta-stardew/meta-dump.txt` |
| MonoGame `Game` 事件 | `device/meta-monogame/meta-dump.txt`（`Activated`/`Deactivated`/`Disposed`/`Exiting`） |
| 注入点/事件点 IL | `device/il2/il-dump.txt`（`OnCreatePartTwo`、`GameRunner`、`Game1` 选中方法、`OnDayStarted` 调用点） |
| 临时取证工具 | `.tmp/phase1.2.4/meta-dump/`、`.tmp/phase1.2.4/il-dump/`（不属于交付代码） |

关键 IL（`device/il2/il-dump.txt`）：

```text
MainActivity::OnCreatePartTwo
  IL_0015 newobj StardewValley.GameRunner::.ctor
  IL_003c stsfld StardewValley.GameRunner::instance
  IL_004e callvirt Microsoft.Xna.Framework.Game::Run

GameRunner : Microsoft.Xna.Framework.Game
  .ctor → call Microsoft.Xna.Framework.Game::.ctor; callvirt Game::add_Exiting
  Update(GameTime) / Initialize()  ← 首批事件注入点

Game1 : StardewValley.InstanceGame
  OnDayStarted() 调用点：Game1._update、Game1.set_activeClickableMenu
```

---

## 9. 不覆盖的证据

- Android 11+ / 其它 OEM / 多设备：**无**。
- 非空 Saves：**无**（只验证空目录基线）。
- 断电/进程被杀故障注入：**无**。
- 远端 CI 的日志文本与逐项测试计数：**不可得**（403）。
- 生产 mod 的 REPLACE：**未单独执行**（夹具走同一代码路径）。
- Phase 2 事件框架的运行时行为：**未实现、未验收**。
