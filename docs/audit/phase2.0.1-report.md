# Phase 2.0.1 报告：事件框架正确性、性能声明与无 Root APK 安装收敛

| 项 | 值 |
| --- | --- |
| 阶段 | Phase 2.0.1（`GameLaunched` + `Tick` 的契约修正；不新增事件/内容管线/SMAPI 层） |
| 日期 | 2026-11-14（+08:00） |
| 开工核对 | `HEAD == origin/main == 97fdfa74a024784995922b1e4a3e3646f0da3933`；工作区干净（Phase 2.0 已由用户 commit/push） |
| 开工基线 CI | Actions Run **43596477121**（`97fdfa7`），**3/3 jobs success**，2026-11-14T12:46:47Z → 13:10:55Z |
| 本阶段 Git | **无新 commit / push**（没有新的 GitHub 写授权）；本阶段代码的远端 CI = **NOT_RUN** |
| 证据根 | `.tmp/phase2.0.1/20261114-2053/`（gitignore，不入库） |
| 关联 | [事件契约](../architecture/phase2-events.md) · [构建/安装验证](../architecture/phase2.0.1-build-install.md) · [真机验收](phase2.0.1-device-acceptance-report.md) · [证据索引](phase2.0.1-evidence-index.md) |

> 历史结果只作对照，不当作本轮结果。下面每个“修复前”数字都来自本轮真实运行
> （`.tmp/phase2.0.1/20261114-2053/logs/test_loader-pre-fix.log`），不是对旧报告的引用。

---

## 0. 结论

- 先用**新建的失败用例**确认了 4 个源码缺口（A 首次顺序、B ModCount、C 会话边界、D APK 验证器），
  再修复；修复后同一套用例全绿。没有修改任何既有测试期望来迎合实现。
- 事件调度器：
  - 首次顺序只记录一次，用**显式“已记录”标志**；首次无人订阅会记录空结果，后续注册不改写；
  - 稳定 Tick 不排序、不构造诊断字符串、不写磁盘；桌面实测 **0 B/tick**（空集合与 4 个固定订阅均如此）；
  - `ModCount` 用真正的去重（mod id 按 loader 的 ordinal-ignore-case 规则），交错注册 A→B→A 仍为 2；
  - 多根 Loader 语义决定为**替换 + 失败回退**：成功的新会话原子替换旧会话订阅；失败的新会话只丢弃自己，
    原来在服务的会话继续工作；
  - 旧 `IModContext` / staging scope / token 不能注册、提交或误删新会话订阅；
  - `GameLaunched` 是**进程级至多一次**，新会话不会把它重新“武装”；
  - 公开派发入口**拒绝重入**（不排队、不递归、不动计数器），并在诊断里计数；回调内注册/退订仍是下一轮生效。
- APK 身份拆成 **baseline / candidate / installed** 三个独立事实；准入只看 candidate 与 installed 的签名，
  baseline 签名只作内容参考。无 installed 证据 → `NOT_VERIFIED`（UNKNOWN），不再拿 baseline 冒充设备。
- 真实设备上验证了本阶段最重要的纠正：baseline 签名 `611c8fa4…` 与 candidate `858ef62d…` 不同，
  但 installed 也是 `858ef62d…`；旧验证器会 `BLOCKED`，新验证器给出 `INSTALL_CANDIDATE`，随后同签名原位覆盖成功。
- 安装后证明改用**普通 `adb shell pm path` + `sha256sum`**（不调用 `su`，不接受任意首个 token，split APK 明确拒绝），
  设备 `base.apk` SHA-256 与候选 APK 逐字节一致。
- 本机回归：host（含 `--refs`）**552/552**、CI 同口径 host **546/546**、InjectorTests **29/29**、
  pipeline **39/39**、modded-build **55/55**、offline **13/13**、隐私 `RESULT: OK`、
  fake-adb 部署门禁 **101/101**、P114 静态审计 **46/46**、APK 准入合成测试 **16/16**、`git diff --check` 0。
- 真机（V1963A / Android 10 / API 29 / USB `6d7200a`）：备份 57 文件双端一致；同签名原位安装成功；
  Loader 3/3、`GameLaunched=1`、Tick 600→1200、顺序 `Alpha,Beta`、线程 1、故障隔离/重复订阅/延迟订阅均未回归；
  Saves 前后 **0 文件且清单一致**；没有 Root、没有卸载、没有 `pm clear`、没有进入或创建存档。
- **下一阶段**：可以进入“存档生命周期事件”的**可行性研究**，但不能把本轮当成 `SaveLoaded` 已可用；
  真正的 `SaveLoaded` 验收需要创建/进入存档（本轮明确未做），并需要新的 Saves 备份与非空存档基线。

---

## 1. 审查发现的复现、根因与修复

### A. 每 Tick 重建首次顺序字符串

**复现（修复前）**

```text
FAIL p201_stable_tick_zero_alloc_no_subscriptions   subscriptions=0 bytes=5200040 per-tick=104.001
FAIL p201_stable_tick_zero_alloc_with_subscriptions subscriptions=4 bytes=7200040 per-tick=144.001
```

`DispatchTick` 每次都 `DispatchTo(..., recordOrder:true)`；`RecordOrderLocked` 无条件 `new StringBuilder()`、
遍历订阅、`ToString()`，再用“结果字符串长度是否为 0”判断是否已经记录过。

**根因**

1. 没有“已记录”状态，只有 `s_firstTickOrder.Length == 0` 这个**值**判断，既是重复工作又是错误完成条件；
2. 每次 Tick 都调用记录函数，而不是只在首次调用。

**修复**

- `s_firstTickOrderRecorded` / `s_firstGameLaunchedOrderRecorded` 两个显式标志；
- 派发前在锁内计算 `recordOrder = !recorded`，稳定 Tick 传 `false`，`RecordOrderLocked` 在已记录时直接返回；
- 首次派发即使 0 订阅也写 `""` 并置位，之后不再改写；
- 诊断字符串只在首次构造。

**回归**

```text
PASS p201_stable_tick_zero_alloc_no_subscriptions   subscriptions=0 bytes=0 per-tick=0
PASS p201_stable_tick_zero_alloc_with_subscriptions subscriptions=4 bytes=0 per-tick=0
PASS p201_first_tick_order_recorded_even_when_empty order=""
PASS p201_first_tick_order_not_rewritten_after_registration calls=1 order=""
PASS p201_first_tick_order_stable                   Zeta,alpha
```

### B. ModCount 按相邻 ModId 去重

**复现（修复前）**：`ResetForTests` 后同一会话注册 `Mod.Alpha → Mod.Beta → Mod.Alpha`（三个订阅）：

```text
FAIL p201_mod_count_distinct_interleaved   mods=3 subscriptions=3     （应为 2）
FAIL p201_mod_count_after_dispose          mods=1                     （旧逻辑的连带结果）
FAIL p201_mod_count_case_insensitive_identity mods=2                 （A 与 a 被当成两个）
```

**根因**：`Active` 是**追加顺序**，不保证按 ModId 排序；`CaptureDiagnostics` 只比较“当前项与前一项”。

**修复**：捕获诊断时用 `HashSet<string>(StringComparer.OrdinalIgnoreCase)` 去重。
大小写规则选 **ordinal-ignore-case**，与 Loader 的 mod 身份规则（`byId`）一致；派发顺序仍是
`String.CompareOrdinal`（这是顺序，不是身份）。诊断捕获本来就会分配快照，这个 HashSet 只在 `Capture()` 时分配，
不在派发路径上。

**回归**：交错 2、同 Mod 多事件 1、Dispose 后 1、大小写 1、空集合 0；真机 `diagnostics_mods=2` 与
loader 的 `event-mods=2` 一致。

### C. 会话边界

**复现（修复前）**

```text
FAIL p201_session_replacement_drops_previous_subscriptions old=2 new=1 subscriptions=2 session=2
FAIL p201_session_abort_restores_previous_session          live=1 failed=0 ready=False subscriptions=1
FAIL p201_stale_scope_commit_rejected                      stale=1 winner=1 subscriptions=2
FAIL p201_game_launched_once_per_process_across_sessions   first=2 second=1 count=1 session=2
FAIL p201_loader_second_root_replaces_previous_subscriptions subscriptions=6 mods=2
FAIL p201_loader_second_root_drops_old_subscriptions       before=4 after=6
FAIL p201_loader_failed_run_restores_previous_session      ready=False
FAIL p201_loader_failed_run_keeps_dispatching              before=1 after=1
FAIL p201_loader_same_root_still_idempotent                session=3 subscriptions=6
```

**根因**

1. `BeginSession` 只重置计数、**保留旧 `Active`**；
2. `Subscribe` / `CommitScope` / `RollbackScope` 不校验 `owner.SessionId`；
3. `AbortSession` 只移除本会话订阅，不恢复上一会话，也不阻止旧 context 再注册；
4. `BeginSession` 把 `GameLaunched` 计数清零，新会话可以再次派发“首次启动”。

**决定的契约（并实现）**

| 场景 | 契约 |
| --- | --- |
| 成功的 session1 → session2 | session2 `CommitSession` 时**原子替换**：所有非本会话订阅被标记 Disposed 并移出 Active |
| session2 失败 | 只丢弃 session2 的订阅；session1 恢复 `ready`，计数保持原值 |
| session1 abort 后旧 context 再注册 | 该 context 的 SessionId 不等于当前会话（或会话已 abort）→ 返回惰性 token，不进入 Active |
| 新会话开始后旧 scope 再 `Commit()` | 校验失败 → no-op |
| 旧 token `Dispose()` | 只按引用移除自己；新会话订阅不受影响 |
| 连续 `Loader.Run` 不同根 | 每次成功运行是新会话并替换；同根重复仍是 `already-initialized`（不建新会话） |
| 中途会话失败 | 失败会话无幽灵订阅；服务中的会话不被误伤 |

`GameLaunched` 计数/首次线程/首次顺序/“早于首个 Tick”是**进程事实**，`BeginSession` 与
`CommitSession` 都不重置；只有主机测试 seam `ResetForTests()` 才重置。Tick 计数、故障计数、dispatch 计数
是**会话计数**，在 `CommitSession` 时归零（不是 `BeginSession`，否则失败会话会毁掉服务中会话的计数）。

> 主机测试接口与产品生命周期的区别：`ResetForTests()` 是 `internal` 的进程级测试 seam，产品代码从不调用；
> 它存在只是为了让一个测试进程能模拟“新进程”。产品里没有“重新开始首次启动”的 API——这正是
> `GameLaunched` 每进程至多一次的含义。

### D. APK 验证器把 baseline 当 installed

**复现（合成数据，新测试）**

```text
PASS baseline_differs_candidate_installed_same_is_installable legacy=False -> INSTALL_CANDIDATE
PASS baseline_same_candidate_installed_differs_is_blocked     legacy=True  -> UPDATE_INCOMPATIBLE_RISK
```

旧判定是 `candidate_signer == baseline_signer`：

- baseline 与 candidate 签名不同、但设备已装的就是 candidate 同签名 → 旧逻辑错误 `BLOCKED`（真实设备正是这一种）；
- baseline 与 candidate 相同、但设备签名不同 → 旧逻辑错误放行。

**根因**：把“内容基线”当成了“当前设备上的包”。baseline 只证明“除 blob 外的内容没变”，
它的签名与本机升级准入无关。旧代码还把 `--installed-apk-sha256` 直接和 `baseline` 哈希比较，
同样是把两个概念混在一起。

**修复**

- 新增纯函数模块 `Tools/apk_admission.py`：`evaluate(candidate, baseline, installed, ...)`；
- `verify_modded_apk.py` 接受独立的 installed 证据：
  `--installed-apk <pulled base.apk>`、`--installed-signer-sha256`、`--installed-apk-sha256`、
  `--installed-absent`、`--installed-unreadable`；
- 状态：`INSTALL_CANDIDATE` / `FIRST_INSTALL` / `UPDATE_INCOMPATIBLE_RISK` / `DOWNGRADE_RISK` /
  `PACKAGE_MISMATCH` / `UNSUPPORTED_MULTI_SIGNER` / `SIGNATURE_UNREADABLE` / `NOT_VERIFIED`；
  只有前两种 `adb_install_allowed = true`；
- 没有 installed 证据 → 检查状态 `UNKNOWN`，报告 `NOT_VERIFIED`，**不是** PASS、也不是 FAIL；
- 多 signer（候选或设备）→ `UNSUPPORTED_MULTI_SIGNER`；v3 轮换 lineage 未实现，明确拒绝而不是只比较第一个摘要；
- 安装后哈希是独立证据：`post_install_hash_matches`，与准入分开；
- 历史 baseline 哈希门禁（`baseline_apk_unchanged`）保留，没有把 BLOCKED 改成 PASS 来消警。

**真实设备证据**

| 角色 | SHA-256 | signer |
| --- | --- | --- |
| 内容 baseline（`app-debug.apk`） | `65e1e2af…f9d` | `611c8fa4…5de` |
| candidate（本轮 C） | `c571a25b…02a` | `858ef62d…5ff` |
| installed（安装前，Phase 2.0 C） | `d4f60d57…5dd` | `858ef62d…5ff` |

安装前验证：`40 passed / 0 failed / 0 blocked / 1 unknown`（唯一 UNKNOWN 是“还没安装，安装后哈希无法证明”），
准入 **INSTALL_CANDIDATE**；安装后（拉回新 `base.apk`）验证：`41/41`，`post_install_hash_matches_candidate = true`。

---

## 2. 事件契约的准确表述

| 主题 | 准确契约 |
| --- | --- |
| 首次顺序 | 每种事件**第一次派发**时记录一次；0 订阅时记录空字符串并置“已记录”，之后注册不改写。诊断字符串不参与完成判断。 |
| 稳定 Tick | 不重建快照、不排序、不构造诊断字符串、不写盘；桌面 200k Tick 当前线程分配 0 B（见 §3）。 |
| 排序 | mod id `CompareOrdinal` 升序，同 mod 内注册顺序；快照稳定插入排序，集合变化才重建。 |
| 快照 | 本轮派发持有开始时的数组；回调内注册/退订/Dispose 只影响下一轮。 |
| ModCount | 活跃订阅中**不同 mod id** 的数量；身份按 ordinal-ignore-case；交错注册不会重复计数。 |
| 会话 | 成功的新 Loader 会话替换旧会话订阅；失败的新会话被丢弃并恢复旧会话；旧 context/scope/token 不污染新会话。 |
| GameLaunched | 每进程至多一次；新会话不重置；`launched_at_tick_count=0` 表示派发时还没有 Tick。 |
| 重入 | handler 内调用公开 `DispatchTick`/`DispatchGameLaunched` → 立即返回、不增加 Tick/Launch/Dispatch 计数、不递归；`ReentrantDispatchRejections` 计数。 |
| 并发 | 派发按单线程设计；另一个线程在派发进行中调用会被当作不可重入而拒绝（不排队、不并行执行 handler）。 |
| 线程 | 仍是“进入被注入游戏方法的线程”，不做线程切换；本轮真机仍为 managed thread 1。 |

`TickCount` 仍是 1 起、每次 +1、`long` 饱和不回绕；`CommitSession` 会把会话 Tick 计数从 0 开始
（新会话的新计数），但同一进程不会因此再派发 `GameLaunched`。

---

## 3. 稳定 Tick 的桌面分配实测

**方法**：独立的 `--perf-report` 模式；固定订阅集合（1 个 GameLaunched + 3 个 Tick handler，空 handler）、
预热 5000 Tick、测量 200000 Tick，使用 `GC.GetAllocatedBytesForCurrentThread()`，测量区间内**不调用**
`EventDiagnostics.Capture()`、不写日志、不写文件。计时用 `Stopwatch.GetTimestamp()`（避免 `Stopwatch` 对象本身被计入）。

**环境**（`host/perf.json`）：.NET 8.0.11，Windows 10.0.26100，X64，8 逻辑处理器，Workstation GC。

| 订阅数 | 测量 Tick | 分配字节 | B/Tick | 参考耗时 |
| ---: | ---: | ---: | ---: | ---: |
| 0 | 200,000 | **0** | **0** | 9.5 ms |
| 4（1 launch + 3 tick） | 200,000 | **0** | **0** | 20.5 ms |

对照修复前同一测量：0 订阅 104 B/Tick、4 订阅 144 B/Tick（每次 Tick 一个 `StringBuilder` + 一个 string）。

**边界**：这是桌面 .NET 8 的当前线程分配，不是 Android 帧率结论，也不是“低端机没有影响”。
Android 上仍只记录“进入 Update 的线程/计数”，不承诺 60 Hz；本轮没有做设备端帧率或 CPU 剖析。

---

## 4. 注入校验的拒绝边界

**问题**：`HasCompleteCompletionHook` 只看“每个正常 `ret` 的前一条非 nop 指令是不是 hook call”，
以及 `CountCalls == normalReturns`；`CountCalls` / `HasEntryHook` 只按“类型 + 方法名”匹配。
因此 `br ret; call Hook; ret` 这种“call 紧邻 ret、但有边直接跳到 ret”的形状会被误判为完整补丁；
`Hook(int)` 也会被误当成 `Hook()`。

**修复**（`Tools/ModInjector/Injection.cs`）

- `IsHookCall` 精确校验：declaring type、方法名、返回类型 `System.Void`、参数个数 0；
- `CountCalls` 只用该精确签名；
- `HasCompleteCompletionHook` 额外要求：**没有任何指令的操作数（分支目标/switch 表项）指向该 `ret`**。
  真正的注入会把这些边改指 hook call，所以正常补丁仍通过；
- `HasEntryHook` 同样使用精确签名。

**负向测试**（`Tools/tests/InjectorTests/Program.cs`）

```text
PASS bypass_edge_rejected            call-before-ret with a branch straight at ret must not count as complete
PASS bypass_control_accepted         injector output has no edge to the ret and is accepted
PASS completion_wrong_overload_rejected
PASS count_calls_ignores_wrong_overload
PASS entry_wrong_overload_rejected
```

**边界声明**：这组规则证明的是“本项目支持的 IL 形状”内不会被误判为完整补丁，不是通用 IL 控制流验证器；
`catch` 区域内的 `ret` 仍按“错误路径、不插钩子”处理，没有声称覆盖所有异常路径语义。
真实 `GameRunner.Initialize` 目前只有 15 条指令、单一正常 `ret`（本轮 pipeline/注入结果再次显示
`game-launched-hook: present (1 normal-completion call)`），但“真实方法简单”只是当前事实，不是普遍保证。

**幂等/混合状态**没有回归：pipeline 39/39 中 `patch_is_idempotent`、`events_idempotent_keeps_exactly_one_hook`、
`events_refuses_mixed_state_upgrade/downgrade` 全部通过；对已注入产物再 patch 仍只有一组钩子。

---

## 5. 安装脚本的职责收敛

| 脚本 | 定位 | 本阶段改动 |
| --- | --- | --- |
| `Tools/deploy-apk-safe.ps1` | **默认无 Root 原位安装门禁**（默认只读预检） | 安装成功后新增普通 shell 证明：`pm path` 必须恰好一条 `package:`（split APK → `UNSUPPORTED_SPLIT_APK`，不猜第一条），再对完整路径 `sha256sum`；要求退出码 0 + 完整 64-hex 行 + 与本地哈希相等。不调用 `su`，不取“输出第一个 token”。 |
| `Tools/deploy-apk.ps1` | 历史破坏式脚本（保留可审计） | 删除 `su -c sha256sum`，改为普通 shell + 退出码 + 完整哈希 + split 拒绝；`-LegacyDestructiveInstall` 之外**还要求** `-SavesBackupRoot <已存在且非空目录>` 与 `-SavesBackupConfirmed`，否则不写设备。仍然永不作为默认入口。 |
| `Tools/deploy-mods-safe.ps1` | 无 Root 外部 Mods 部署（未改） | 本轮只作为执行工具使用：plan → deploy（1 REPLACE）→ verify 15/15；没有改代码，所以没有重跑两套长部署套件。 |

没有新增第三套安装工具。`test_deploy_safe.ps1` 从 76 项扩展到 **101 项**，覆盖：安装后哈希匹配、
split 布局拒绝、设备哈希不匹配拒绝、首装后仍能证明、以及所有原有“不 uninstall / 不 pm clear /
不 force-stop / 不启动”断言。`test_p114_probe.py` 的静态“无破坏式命令”审计仍通过。

---

## 6. 回归结果（本轮真实计数）

| 套件 | 结果 | 证据 |
| --- | --- | --- |
| `test_loader.py --refs`（完整 host，含 example mod / P114 / Phase 2.0 / 2.0.1） | **552 checks, 552 passed, 0 failed**（driver 53/53） | `logs/test_loader-final-refs.log` |
| `test_loader.py`（CI 同口径，无 `--refs`） | **546 checks, 546 passed, 0 failed**（driver 52，2 skipped） | `logs/test_loader-final-ci.log` |
| `InjectorTests` | **29/29** | `logs/injector-tests.log` |
| `test_mod_pipeline.py --refs` | **39/39** | `logs/test_mod_pipeline.log` |
| `test_modded_build.py` | **55/55** | `logs/test_modded_build.log` |
| `test_ci_offline.py` | **13/13** | `logs/test_ci_offline.log` |
| `privacy-scan.py` | `RESULT: OK` | 同次运行 |
| `test_deploy_safe.ps1`（fake adb） | **101/101** | `logs/test_deploy_safe2.log` |
| `test_p114_probe.py` | **46/46** | 同次运行 |
| `test_apk_admission.py` | **16/16** | 同次运行 |
| `git diff --check` | 0 | 同次运行 |

修复前同一 host 套件为 `551 checks, 532 passed, 19 failed`；其中 1 项是 Phase 2.0 遗留的
`hello_world_log_versions`（example mod 日志的 framework 版本仍断言 1.0.0，而 Phase 2.0 已把 API 版本
定为 1.1.0；该检查只在 `--refs` 下执行，所以 Phase 2.0 的 CI 口径没暴露）。这是过时断言，不是实现回归；
本轮改为断言 **1.1.0**，并在本报告中记录。

> 本轮 host 直接运行时曾出现 **553/553**：比 `test_loader` 口径多 1 项 `p201_perf_report_written`
> （因为传了 `--perf-report`）。三个数字不是矛盾，是不同参数下的检查集合不同。

---

## 7. 真机验收摘要

完整记录见 [Phase 2.0.1 真机验收报告](phase2.0.1-device-acceptance-report.md)。

| 项 | 结果 |
| --- | --- |
| 备份（项目外） | `%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-2053\`；Mods 27 文件 + `.mods-deploy` 29 文件 + base.apk = **57 文件 / 389,971,296 B**，设备侧与主机侧 **57/57** SHA-256 一致 |
| 安装前身份 | installed `d4f60d57…` / signer `858ef62d…`；baseline `65e1e2af…` / signer `611c8fa4…` |
| 准入 | candidate `c571a25b…` / signer `858ef62d…` → `INSTALL_CANDIDATE`；`adb install -r` = Success；设备 `base.apk` = `c571a25b…`（普通 shell 证明） |
| Mods | 事务 `20261114T133014Z-78bc0c3a`，1 REPLACE（Runtime `c92be121…` → `d2604b67…`），15/15 远端哈希一致，旧 Runtime 保留在 `backup/` |
| Loader | 3 discovered / 3 initialized / 0 failed；`events=ready`、session 1、subscriptions 7、mods 2；P114 仍初始化 |
| 事件 | `GameLaunched=1`、`launched_at_tick_count=0`；Tick **600 → 1200**（两次快照），dispatch 601 → 1201；顺序 `P20.Acceptance.Alpha,P20.Acceptance.Beta`；Loader/Launch/Tick 全部 thread 1 |
| 隔离/订阅 | 故障 3 次、健康 handler 1200/1200；重复订阅 2/1/0；tick3 内新增订阅本轮 0、之后 1196；double dispose 1；log/bridge faults 0；故障日志有界 |
| P114 探针 | `cases=14 observed=12 pass=12 fail=0 blocked=0 not-run=2` |
| 标题界面 | 正常（`device/title.png`），logcat 无 FATAL/AndroidRuntime/MissingMethod/TypeLoad/InvalidProgram |
| Saves | 前后均 **0 文件**，目录 mtime 09:15 未变，清单一致 |
| 结束状态 | 进程已停止；没有 Root、没有卸载、没有 `pm clear`、没有进入/创建存档 |

`Tick` 与渲染帧率的关系仍**未测量**；本轮只记录“进入 `GameRunner.Update` 的计数”。

---

## 8. 构建/安装产物与哈希

| 产物 | 路径 | SHA-256 |
| --- | --- | --- |
| A 内容基线 | `app/build/outputs/apk/debug/app-debug.apk` | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| C 本轮 candidate | `app/build/outputs/apk/modded/app-modded-debug.apk` | `c571a25b8e5fa89afd9d2aef4ca4d8af336257f07517ca3795d0552d8a68302a` |
| Runtime（本轮设备部署） | `.tmp/phase2.0.1/20261114-2053/artifacts-runtime/…/ModFramework.Runtime.dll` | `d2604b67548defa898cae51fa404dcf95d70114db5695d62c6e05881b67db8ce`（130,560 B） |
| 安装后设备 base.apk | `.tmp/phase2.0.1/20261114-2053/device/installed-after.apk` | `c571a25b…`（= candidate） |

> Runtime 部署了两次：第一次用 `80877ff1…` 完成安装与首轮验收；随后加入仅注释的源码改动，
> 确定性 MVID 变化为 `d2604b67…`。为了不让“设备上的字节”与最终源码脱节，重新部署最终构建
> （事务 `20261114T133014Z-78bc0c3a`）并重跑真机验收；报告中的运行证据均来自最终这次。
> 这也再次证明：本项目的程序集构建**不是**字节可复现的，哈希只能绑定“这一次构建”。

构建命令（本地）：

```powershell
.\gradlew.bat --no-daemon :app:assembleModdedDebug `
  -Pdotnet=.tmp\dotnet-sdk\dotnet\dotnet.exe -Ppython=python -Pkeystore=.work/debug.keystore
```

`-Pkeystore` 必须显式指向既有的 `.work/debug.keystore`：mod 构建拒绝自动生成新 key，
否则就变成“签名不同、装不上去”的假升级。

`final/` 目录**没有在本阶段刷新**：它仍是 Phase 1.1.9 的交付副本（`97c38ed7…`）。
本阶段的权威产物与哈希是上表这两个路径；如果要发布新副本，应在用户确认后单独更新
`final/*.apk`、`SHA256SUMS.txt` 与 `DELIVERY.md`。

Loader-only 回退**没有重做**：本阶段 `Injection.cs` 的改动只影响 verify/补丁完整性判定，不改变注入的 IL 形状；
桥接方法、`Bootstrap.Start/Tick/GameLaunched` 与 loader-only 产物均未变。真实注入验证仍为
`event-hooks: enabled`、`game-launched-hook: present (1 normal-completion call)`、`tick-hook: present (enter)`。

---

## 9. 远端 CI 覆盖情况

| 代码 | 远端 CI |
| --- | --- |
| `97fdfa7`（开工基线，Phase 2.0） | Run **43596477121**：**3/3 success**（offline 19s、dotnet 1m19s、powershell 约 24m）。该 run 的 workflow **不含**本轮新增的 APK 准入测试步骤。 |
| 本阶段工作区代码 | **NOT_RUN**（未 commit / 未 push；没有新的 GitHub 写授权）。不能用 `97fdfa7` 的绿色结果证明本轮代码。 |

本轮在 `.github/workflows/ci.yml` 的 offline job 中新增了 `python Tools/tests/test_apk_admission.py`；
该步骤要等用户提交推送后才会在远端执行。

---

## 10. 下一阶段（存档生命周期事件）的进入条件

**可以进入“研究/可行性”**，但不能直接宣称 `SaveLoaded`/`DayStarted` 已实现或已验收：

1. 需要先确认保存/读取路径的注入点（`SaveGame`/`loadGame` 一类方法）及其真实 IL 形状；
   本轮只验证了 `GameRunner.Initialize/Update`。
2. 真正验收 `SaveLoaded` 必须**创建或进入一个存档**；本轮按要求没有进入/创建存档，Saves 仍是 0 文件。
   下一步需要用户明确授权创建存档，并对非空 Saves 做新的项目外备份与前后哈希。
3. 仍不扩大 Android 11+、Harmony、NDK、危险原生探针；本阶段没有改变这些边界。
4. 任何新事件都必须沿用本轮修正后的契约：首次顺序只记一次、稳定期零分配、会话替换/回退、重入拒绝、
   进程级 once-only 语义要与新事件的会话生命周期一致。

---

## 11. 已知限制与残留未知

- 只覆盖 `StardewValley` 1.6.15.24354 / versionCode 239 + V1963A / Android 10 / API 29 这一台设备。
- 桌面 0 B/tick 不等于 Android 帧率或低端机 CPU 结论；设备端没有做分配/CPU 剖析。
- `HasCompleteCompletionHook` 的边检查只针对本项目支持的 IL 形状；不是完整控制流证明。
- v3 签名轮换 lineage 没有实现；多于一个 signer 摘要一律 `UNSUPPORTED_MULTI_SIGNER`（明确拒绝）。
- 多根 Loader 的“替换 + 失败回退”是进程内语义；跨进程、并发 Loader 调用不在覆盖范围。
- 真机只验证了标题界面路径；没有进入存档，因此没有 `SaveLoaded`/`DayStarted`/存档内容相关证据。
- 设备 `.staging` 目录每次 Loader 运行会换随机名（运行期行为），不属于 Mods 载荷；本轮已把“载荷哈希”
  与“运行期产物”分开比较。
