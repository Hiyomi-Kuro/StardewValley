# Phase 2.0 证据索引

| 项 | 值 |
| --- | --- |
| 阶段 | Phase 2.0 最小事件 API + 构建期注入 + ARM64 真机验收 |
| 基线 | `53f8171b9eb8faaee430ec8530d1eb942e5ade09` |
| 证据根 | `.tmp/phase2.0/`（gitignore；可整体删除，命令可重跑） |
| 设备证据 | `.tmp/phase2.0/20261114-2025/` |
| 结论 | 见 [阶段报告](phase2.0-report.md) · [真机验收](phase2.0-device-acceptance-report.md) |

---

## 1. 远端 CI（基线，非本阶段代码）

| 事实 | 值 | 来源 |
| --- | --- | --- |
| Run | `43589410109` | GitHub REST `/actions/runs?head_sha=53f8171…` |
| head_sha | `53f8171b9eb8faaee430ec8530d1eb942e5ade09` | 同上 |
| 结果 | `success`，3/3 jobs | `/actions/runs/43589410109/jobs` |
| 时间 | 11:18:43Z → 11:48:56Z | 同上 |
| 本阶段代码 | **NOT_RUN**（未 commit/push） | — |

`53f8171` 的作业级结果：`offline checks` success、`dotnet build + loader host tests` success、
`powershell gates` success。逐项测试计数无法从远端取得（公开仓库日志需认证），以本机同口径运行为准。

---

## 2. 主机测试

| 证据 | 路径 | 结果 |
| --- | --- | --- |
| Loader / 主机套件（CI 同口径，无 `--refs`） | `.tmp/phase2.0/logs/test_loader.log` | **519 checks, 519 passed, 0 failed**（2 项 hello-world SKIPPED） |
| 构建管道（真实 65 程序集，本地） | `.tmp/phase2.0/logs/test_mod_pipeline.log` | **39 checks, 39 passed, 0 failed** |
| 注入器合成夹具 | `Tools/tests/InjectorTests`（命令见 §6） | **23 checks, 23 passed, 0 failed** |
| 构建静态检查（CI 同口径） | `python Tools/tests/test_modded_build.py` | **55 checks, 55 passed, 0 failed** |
| 离线套件 | `python Tools/tests/test_ci_offline.py` | **13 checks, 13 passed, 0 failed** |
| 隐私扫描 | `python Tools/privacy-scan.py` | `RESULT: OK` |

Phase 2.0 主机断言清单（摘自 `Phase20Tests` 输出）：`events_ready_gate_*`、`events_game_launched_exactly_once`、
`events_tick_starts_at_one_and_increments`、`events_launch_observed_before_first_tick`、
`events_order_is_ordinal_mod_id`、`events_duplicate_registration_runs_twice`、`events_dispose_is_idempotent`、
`events_snapshot_*`、`events_fault_does_not_block_other_handlers`、`events_repeated_fault_logging_is_bounded`、
`events_staged_subscription_is_invisible`、`events_rollback_discards_subscription`、
`events_register_after_rollback_is_inert`、`events_abort_session_drops_subscriptions`、
`events_loader_*`、`phase20_legacy_mod_initialized`、`phase20_legacy_mod_gets_no_events`。

---

## 3. 构建产物

| 产物 | 路径 | SHA-256 | 大小 |
| --- | --- | --- | ---: |
| A 基线 | `app/build/outputs/apk/debug/app-debug.apk` | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` | 388,967,580 |
| B Loader-only | `app/build/outputs/apk/loader-only/app-loader-only-debug.apk` | `b67816857c0c08c5ce2bb2c60c5db22c172b60fa8d2c0eb3418f5180c9eeffd4` | 389,561,500 |
| C Loader+events | `app/build/outputs/apk/modded/app-modded-debug.apk` | `d4f60d577edb57a66fa2ce68a04786f3b9cd650e12a5c6c9302688a529d4a5dd` | 389,561,500 |

A 的哈希与历史记录（`BASELINE_APK_SHA256`）一致，说明基线 APK 未被本阶段改动。

### 3.1 构建 manifest 与注入结果

| 证据 | 路径 |
| --- | --- |
| C 构建 manifest | `.tmp/build-modded/modded-build-manifest.json`（`event_hooks=enabled`） |
| B 构建 manifest | `.tmp/build-loader-only/modded-build-manifest.json`（`event_hooks=disabled`） |
| C 注入日志 | `.tmp/phase2.0/logs/gradle-modded.log`、`.tmp/build-modded/logs/injector-*.log` |
| B 注入日志 | `.tmp/phase2.0/logs/gradle-loader-only.log`、`.tmp/build-loader-only/logs/injector-*.log` |
| 注入后 IL 审计 | `.tmp/phase2.0/analysis/patched-events-il.txt` |

C 的关键行：

```text
event-hooks    : enabled
bridge-game-launched: present
bridge-tick    : present
game-launched-hook: present (1 normal-completion call)
tick-hook      : present (entry call)
tick-is-first  : yes
RESULT: OK
```

B 的关键行：

```text
event-hooks    : disabled
bridge-game-launched: absent
bridge-tick    : absent
game-launched-hook: absent (0 calls)
tick-hook      : absent (0 calls)
RESULT: OK
```

### 3.2 最终 APK 反向校验（从 APK 解出程序集，不只信 manifest）

| 证据 | 路径 | 结果 |
| --- | --- | --- |
| C | `.tmp/phase2.0/analysis/verify/modded-verify.json` | 38 passed / 0 failed / 1 BLOCKED（signer 与 **baseline** 不同，见下） |
| B | `.tmp/phase2.0/analysis/verify/loader-only-verify.json` | 38 passed / 0 failed / 1 BLOCKED（同上） |

两个 BLOCKED 都是 `candidate_signer_equals_baseline`：模组构建使用 `.work/debug.keystore`
（signer `858ef62d…`），`app/build/outputs/apk/debug` 的基线 APK 使用项目外长期密钥（signer `611c8fa4…`）。
**设备上已安装的是同签名的模组构建（`97c38ed7…`）**，因此原位升级可行，且真机上已实际验证（见 §4）。

校验同时覆盖：容器条目数、只有 `assemblies/assemblies.blob` 变化、57/57 其它条目字节不变、
描述符索引 7、blob 与构建产物一致、源 blob/manifest 未被修改、zipalign、v1+v2+v3 签名、
以及“事件钩子应有/应无”。

---

## 4. 真机证据（`.tmp/phase2.0/20261114-2025/`）

| 证据 | 路径 | 说明 |
| --- | --- | --- |
| 备份前设备文件哈希 | `device/files-before.sha256.txt` | 44 文件，双端一致 |
| 安装前 base.apk | `device/installed-apk-before.txt` | `97c38ed7…` |
| C 安装 | `logs/install-c.txt`、`device/after-install-c.txt` | Success + 设备哈希 `d4f60d57…` |
| C 部署 | `device/deploy-c.json`、`logs/deploy-c.log` | tx `20261114T122056Z-6d61246d` committed |
| C 运行 | `device/run-c/` | loader 报告、两份探针报告、mod 日志、线程表、截图、logcat |
| C 第二次快照 | `device/run-c/probe/*/phase2.0-*.snapshot2.txt` | Tick 1800 → 3600 |
| B 安装 | `logs/install-b.txt` | 设备哈希 `b6781685…` |
| B 运行 | `device/run-b/` | 只有 initialize 快照、`diagnostics_dispatch=0`、截图 |
| 最终恢复 | `device/run-final/` | 重新安装 C、标题界面、最终 Mods 哈希、Saves |
| 事务 journal | `device/run-final/tx-journal-final.json` | `state=committed`，5 个文件全部 done，旧 Runtime 在 `backup/` |
| 项目外备份 | `%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-2025\` | 44 文件 / 221,838 B |

关键数字（详见 [真机验收报告](phase2.0-device-acceptance-report.md)）：

- `GameLaunched=1`，`launched_at_tick_count=0`，`launch_before_first_tick=true`；
- `Tick` 1800 → 3600（两次快照）；
- `first_tick_order=P20.Acceptance.Alpha,P20.Acceptance.Beta`（Beta 先初始化）；
- Loader / GameLaunched / Tick 的 managed thread id 都是 **1**，与进程主线程 TID 4587 对应；
- Beta：`fault_attempts=3`、`healthy_ticks=1800`、重复订阅 2/1/0、`late_calls_tick3=0` 后 1796；
- 3 次故障 → 2 条 ERROR 日志（第 3 次被限流）；
- B：`diagnostics_dispatch=0`、`diagnostics_ticks=0`，无 `on-tick` 报告；
- 最终 Saves 0 文件，目录 mtime 未变。

---

## 5. 代码与文档

| 类别 | 路径 |
| --- | --- |
| 运行时契约与调度器 | `Mod/ModFramework.Runtime/Contracts.cs`、`Events.cs`、`EventDispatcher.cs`、`ModContext.cs`、`Loader.cs` |
| 注入器 | `Tools/ModInjector/Program.cs`、`Injection.cs`、`IlChecks.cs` |
| 构建/校验 | `Tools/build_modded_apk.py`、`Tools/verify_modded_apk.py`、`app/build.gradle.kts` |
| 测试 | `Tools/tests/InjectorTests/`、`Tools/tests/ModFramework.Tests/Phase20Tests.cs`、`Tools/tests/test_loader.py`、`test_mod_pipeline.py`、`test_modded_build.py` |
| 夹具 | `Tools/tests/FixtureMods/EventFixtureMod/`、`Tools/tests/LegacyContract/`、`Tools/tests/FixtureMods/LegacyMod/` |
| 验收 Mod | `Mod/P20.Acceptance.Alpha/`、`Mod/P20.Acceptance.Beta/` |
| 文档 | `docs/architecture/phase2-events.md`、`docs/architecture/phase2-feasibility.md` §12、本目录三份 Phase 2.0 报告 |

---

## 6. 重跑命令（无需设备）

```powershell
$dotnet = (Resolve-Path .tmp\dotnet-sdk\dotnet\dotnet.exe).Path

# 主机/加载器套件（CI 同口径）
python Tools/tests/test_loader.py --dotnet $dotnet `
    --artifacts .tmp\phase2.0\rerun\loader-artifacts --workdir .tmp\phase2.0\rerun\loader-work

# 注入器合成夹具
dotnet build Tools/tests/InjectorTests/InjectorTests.csproj -c Release --artifacts-path .tmp\phase2.0\rerun\itests -v q
dotnet .tmp\phase2.0\rerun\itests\bin\InjectorTests\release\InjectorTests.dll

# 构建静态检查 + 离线 + 隐私
python Tools/tests/test_modded_build.py
python Tools/tests/test_ci_offline.py
python Tools/privacy-scan.py
```

需要真实游戏程序集的本地检查：`python Tools/tests/test_mod_pipeline.py --dotnet $dotnet --refs .tmp/build-modded/refs`。
这些输入（65 个程序集、APK、存档、私有程序集）**没有**上传到仓库或 CI。

---

## 7. 未收集/不可得

| 项 | 原因 |
| --- | --- |
| 远端 CI 的逐项测试计数 | 公开仓库日志下载需要认证（REST 403） |
| 本阶段代码的远端 CI | 未 commit/push（没有授权） |
| 低端机帧率影响 | 未测量；只在本设备记录 Tick 计数 |
| Android 11+ / 其它 OEM | 超出本阶段边界，Mods apply 仍 fail-closed |
| `Tick` 与渲染帧的精确对应 | 未测量，也不作为契约 |
