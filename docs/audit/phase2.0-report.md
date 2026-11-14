# Phase 2.0 报告：最小事件 API、构建期注入与 ARM64 真机验收

| 项 | 值 |
| --- | --- |
| 阶段 | Phase 2.0（`GameLaunched` + `Tick` 最小实现） |
| 日期 | 2026-11-14（+08:00） |
| 开工基线 | `53f8171b9eb8faaee430ec8530d1eb942e5ade09`（本地 HEAD == origin/main，工作区干净） |
| 基线 CI | Actions Run **43589410109**，`53f8171`, **3/3 success**（详见 §1） |
| 本阶段 commit/push | **无**（没有新的 GitHub 写操作授权）；本阶段代码的远端 CI = **NOT_RUN** |
| 关联 | [事件 API](../architecture/phase2-events.md) · [可行性修订](../architecture/phase2-feasibility.md) §12 · [真机验收](phase2.0-device-acceptance-report.md) · [证据索引](phase2.0-evidence-index.md) |

---

## 0. 结论

- 事件契约按 `phase2-feasibility.md` 的路线 B 实现：`GameLaunched` 注入 `GameRunner.Initialize()` 的**每个正常 `ret` 前**，
  `Tick` 注入 `GameRunner.Update(GameTime)` 的**方法头**；两者都经 `ModInjector` 构建期注入，无 Harmony、无 NDK。
- 线程、顺序、快照、异常隔离、初始化失败回滚、有界日志、版本与二进制兼容、构建开关、
  A/B/C 产物隔离与反向校验均已实现，并在主机测试 + 合成夹具 + 真实 DLL 管道测试中验证。
- 真机（V1963A / Android 10 / API 29）：C 版 `GameLaunched=1`、`Tick` 1800→3600、顺序 `Alpha,Beta`
  （Beta 先初始化）、Loader/事件线程都是 managed thread 1；B 版 `diagnostics_dispatch=0`（无派发证据）；
  Saves 保持 0 文件。详见 [真机验收报告](phase2.0-device-acceptance-report.md)。
- **基线 CI（53f8171）为 3/3 success**；本阶段新代码**没有任何远端 CI 结果**，不得用基线 Run 冒充。

---

## 1. 基线 CI 真实状态

| 项 | 值 |
| --- | --- |
| Run | `43589410109`（workflow `ci`，event `push`） |
| head_sha | `53f8171b9eb8faaee430ec8530d1eb942e5ade09`（与本地/远端一致） |
| 创建 → 完成 | 2026-11-14T11:18:43Z → 11:48:56Z（约 30 分钟） |
| 结果 | `conclusion = success`，**3/3 jobs success** |

| Job | 结果 |
| --- | --- |
| `offline checks (python, powershell, manifests, privacy)` | success |
| `dotnet build + loader host tests` | success |
| `powershell gates (adb resolution, apk deploy safety, no-root mods deploy, no device)` | success（约 19 分钟量级，与 1.2.4 观察一致） |

查询方式是只读 GitHub REST（`/actions/runs?head_sha=…` 与 `/actions/runs/{id}/jobs`）。
公开仓库的 job 日志下载需要认证，因此**逐项测试计数不可从远端取得**；计数以本机真实运行为准（§3）。

**本次开工时该 Run 仍在 `in_progress`，不是“已完成后再补记”。**

---

## 2. 本阶段改动

### 2.1 运行时（`Mod/ModFramework.Runtime/`）

| 文件 | 变更 |
| --- | --- |
| `Contracts.cs` | `IModContext` 增加 `IModEvents Events { get; }`（唯一的加法式接口变更） |
| `Events.cs`（新） | `IModEvents`、`GameLaunchedEventArgs`、`TickEventArgs`（只读 struct）、`EventDiagnosticsSnapshot`、`EventDiagnostics` |
| `EventDispatcher.cs`（新） | 订阅表、per-mod staging/commit/rollback、会话生命周期、稳定快照（稳定插入排序）、Ordinal 顺序、逐 handler 隔离、有界故障日志、桥接入口、诊断计数 |
| `ModContext.cs` | 传入选定的 `IModEvents` |
| `Loader.cs` | 事件会话（`BeginSession`/`CommitSession`/`AbortSession`）、`Initialize` 失败回滚、成功才提交、报告新增 `events=`/`event-*` 行；`FrameworkApiVersion = 1.1.0` |
| `ModFramework.Runtime.csproj` | `Version`/`FileVersion` 1.1.0，**`AssemblyVersion` 保持 1.0.0.0** |

### 2.2 注入器（`Tools/ModInjector/`）

| 文件 | 变更 |
| --- | --- |
| `Injection.cs`（新） | 精确签名解析；正常完成路径注入（每个正常 `ret`）；分支/switch/leave 目标改指；EH 边界搬移；`catch` 内 `ret` 跳过/全错误路径拒绝；短分支展开；`HasCompleteCompletionHook`/`HasEntryHook` |
| `Program.cs` | Bootstrap 新增 `GameLaunched()`/`Tick()`/`WriteEventsDisabled()` 与两个 `Action` 字段；`Start()` 内一次性绑定委托；三个游戏方法注入；`--no-event-hooks`；幂等/混合状态拒绝 |
| `IlChecks.cs` | 校验新桥接方法（单个 catch、调用 `Action::Invoke`）、`Start` 确实绑定委托且**没有** `ModFramework.Runtime` 静态引用、`Initialize` 每个正常 `ret` 前都有钩子、无任何边直跳 `ret`、EH 边界不是 `ret`、`Update` 首指令是 `Tick`、loader-only 构建反向检查 |

### 2.3 构建与校验

| 文件 | 变更 |
| --- | --- |
| `Tools/build_modded_apk.py` | `--event-hooks enabled/disabled`（+`--no-event-hooks` 别名）；manifest 记录 `event_hooks` 与钩子状态；正向/反向校验 |
| `Tools/verify_modded_apk.py` | `--expect-events auto/enabled/disabled`；从**最终 APK 解出的程序集**重新 verify，检查“应有/应无” |
| `app/build.gradle.kts` | `assembleModdedDebug`（C）+ `assembleLoaderOnlyDebug`（B），独立输出/工作目录，`--event-hooks` 贯通，事件开关声明为 Gradle input |
| `.github/workflows/ci.yml` | 新增 synthetic injector tests 步骤 |

### 2.4 测试与夹具

| 文件 | 用途 |
| --- | --- |
| `Tools/tests/InjectorTests/`（新） | 合成 Cecil 夹具：多返回、分支直跳 `ret`、`catch` 内 `ret`、handler 边界、错误签名、歧义重载、部分补丁、短分支展开；**写出后真的加载并执行** |
| `Tools/tests/ModFramework.Tests/Phase20Tests.cs`（新） | 调度器契约（12 组）+ loader 级事件（跨 Mod 顺序、重复运行、失败 Mod 无订阅）+ 二进制兼容 |
| `Tools/tests/FixtureMods/EventFixtureMod/`（新） | 参数化 A/Z/Fault 三个事件 fixture |
| `Tools/tests/LegacyContract/` + `FixtureMods/LegacyMod/`（新） | 冻结 1.0.0 契约桩 + 按其编译的旧 Mod |
| `Tools/tests/test_loader.py` | 构建/传递新夹具；Phase 2.0 结果断言 |
| `Tools/tests/test_mod_pipeline.py` | 事件模式正向/反向、loader-only、混合状态拒绝、幂等保持唯一钩子 |
| `Tools/tests/test_modded_build.py` | 新 Gradle 结构与事件开关静态校验 |

### 2.5 验收 Mod

| Mod | 作用 |
| --- | --- |
| `P20.Acceptance.Alpha` | 记录 `GameLaunched`/`Tick`、顺序、线程、两次快照；**声明依赖 Beta**，使 Beta 先初始化，用于证明顺序来自 mod id |
| `P20.Acceptance.Beta` | 有界故障注入（前 3 个 Tick 抛异常，且**先于**健康 handler 注册）、重复注册、回调内退订、Dispose 幂等、回调内新增订阅的下一轮生效 |

---

## 3. 本机验证结果

| 套件 | 结果 | 证据 |
| --- | --- | --- |
| `Tools/tests/test_loader.py`（无 `--refs`，即 CI 同口径） | **519 checks, 519 passed, 0 failed**（2 项 hello-world 按设计 SKIPPED） | `.tmp/phase2.0/logs/test_loader.log` |
| `Tools/tests/test_mod_pipeline.py --refs` | **39 checks, 39 passed, 0 failed** | `.tmp/phase2.0/logs/test_mod_pipeline.log` |
| `Tools/tests/InjectorTests` | **23 checks, 23 passed, 0 failed** | 本报告 §4 |
| `Tools/tests/test_modded_build.py`（CI 同口径） | **55 checks, 55 passed, 0 failed** | 同上 |
| `test_ci_offline.py` | **13 checks, 13 passed, 0 failed** | CI 同口径 |
| `privacy-scan.py` | `RESULT: OK` | CI 同口径 |
| 运行时双编译 | 桌面 net8.0 与 `-p:AndroidRefsDir=<65 refs>` 均 0 warning / 0 error | |

Phase 2.0 新增的主机检查（`Phase20Tests`）覆盖：

- ready 前不派发；提交后才派发；
- `GameLaunched` 恰好一次、且早于首个 `Tick`；
- `TickCount` 首值 1、逐次 +1；
- Ordinal 顺序（`Zeta` 在 `alpha` 前，与常见文化排序相反）；
- 重复注册 = 独立订阅；退订一个仍有一个；Dispose 幂等且不再计入活跃订阅；
- 回调内增删 → 本轮冻结、下一轮生效；
- 一个 handler 抛异常不影响其它 handler；故障计数正确；
- 100 次连续故障 → 日志记录 **4** 条（1/2/10/100），有界；
- staging 不可见、rollback 丢弃、rollback 后注册惰性；
- session abort 移除本会话全部订阅；
- Loader 级：三个 fixture 的订阅数为 6、失败 Mod 为 0 订阅、跨 Mod 顺序 `EventMod.A,EventMod.Z`、
  重复运行 `already-initialized` 且不重复派发、失败 Mod 从未收到事件；
- 旧（1.0.0 契约桩编译）Mod 初始化成功且拿到 `framework=1.1.0`、0 订阅。

---

## 4. 注入验证（合成夹具，非“一份游戏 DLL 成功”）

`Tools/tests/InjectorTests` 每个用例都**写出程序集、`Assembly.LoadFrom`、反射执行被注入的方法**，
用 hook 计数器证明控制流真的走到钩子：

| 用例 | 断言 |
| --- | --- |
| 单返回 | 插入 1 次，执行后 hook=1 |
| 三返回 + 分支直跳 `ret` | 插入 3 次，三条路径执行后 hook=1/2/3，且**没有任何边仍指向 `ret`** |
| 分支直跳 `ret`（唯一路径） | 边被改指钩子，执行后 hook=1 |
| `catch` 内 `ret` | 只给正常 `ret` 插钩子（1 次），handler 内 `ret` 前没有调用 |
| handler 边界 = `ret` | `HandlerEnd` 搬到钩子调用，执行 hook=1 |
| 入口钩子 | 每次进入都执行（两次调用 → hook=2） |
| 错误返回类型 / 错误参数列表 | `RequireMethod` 抛异常（fail-closed） |
| 两个同签名重载 | 抛异常（不猜“第一个”） |
| 部分补丁 | `HasCompleteCompletionHook == false`（patch 路径据此拒绝） |
| 短分支 | `br.s` → `br`，展开后仍可执行 |

真实 `StardewValley.dll` 的注入结果（`ModInjector verify`）：

```text
bootstrap-type : present
bootstrap-start: present
hook-call      : present at IL_0000
hook-is-first  : yes
event-hooks    : enabled
bridge-game-launched: present
bridge-tick    : present
game-launched-hook: present (1 normal-completion call)
tick-hook      : present (entry call)
tick-is-first  : yes
RESULT: OK
```

真实 IL 形状（注入后）：

- `GameRunner.Initialize`：原 15 条指令 → 16 条，`IL_002e call Bootstrap::GameLaunched` + `IL_0033 ret`；
- `GameRunner.Update`：首指令为 `call Bootstrap::Tick()`，其余 319 条指令整体后移 5 字节；
- `Bootstrap.Tick()`：`ldsfld → brtrue → ldsfld → callvirt Action::Invoke → leave → ret`，外覆单个 `catch`；
- `Bootstrap.Start()`：新增 3 个空检查分支 + 2 次 `Delegate.CreateDelegate` + 2 次 `stsfld`，
  全部在既有 `try` 内；新增 `AssemblyRef` 只有 `System.Private.CoreLib`（Cecil 导入 BCL 类型时的既有行为，
  原始 loader-only 构建同样存在；**没有** `ModFramework.Runtime` 静态引用）。

---

## 5. 构建产物与反向校验

| 产物 | 路径 | SHA-256 | 说明 |
| --- | --- | --- | --- |
| A 基线 | `app/build/outputs/apk/debug/app-debug.apk` | `65e1e2af…1f9d` | 与历史记录一致，未改动 |
| B Loader-only | `app/build/outputs/apk/loader-only/app-loader-only-debug.apk` | `b6781685…ffd4` | 无事件钩子 |
| C Loader + events | `app/build/outputs/apk/modded/app-modded-debug.apk` | `d4f60d57…a4dd` | 有事件钩子 |

`verify_modded_apk.py` 对 B 与 C 分别是 **38 passed / 0 failed / 1 BLOCKED**；唯一的 BLOCKED 是
`candidate_signer_equals_baseline`：模组构建用 `.work/debug.keystore`（`858ef62d…`），而
`outputs/apk/debug` 的基线 APK 用项目外长期密钥（`611c8fa4…`）。设备上已安装的正是同签名的模组构建，
因此原位升级可行，并已在真机上实际验证（设备 `base.apk` 与目标 APK 哈希逐字节相等）。

校验内容：容器条目数、只有 `assemblies/assemblies.blob` 变化、其余 57 个条目字节不变、描述符索引 7、
blob 是本次构建产物、源 blob/manifest 未改、zipalign、v1/v2/v3 签名，以及**从最终 APK 解出的程序集**的
事件钩子“应有/应无”（C：present；B：absent）。具体数字见 [证据索引](phase2.0-evidence-index.md)。

---

## 6. 与基线 CI 的区分

| 项 | 状态 |
| --- | --- |
| `53f8171` 的远端 CI | **3/3 success（Run 43589410109）** |
| 本阶段新代码的远端 CI | **NOT_RUN**（未 commit/push） |
| 本阶段新代码的本机验证 | 见 §3（全部通过，含 CI 同口径套件） |

**不得**把 `53f8171` 的绿色结果当作本阶段代码的 CI 结果。

---

## 7. 已知限制

- 只覆盖 `StardewValley` 1.6.15.24354 / versionCode 239 与一台 V1963A / Android 10 / API 29。
- 事件只有两个；无 `DayStarted`/`SaveLoaded`/内容管线/存档 API。
- 每 Tick 一次静态字段读 + 一次缓存委托调用；未量化低端机帧率影响。
- Android 11+ 的 Mods `apply` 仍 fail-closed。
- 事件线程由游戏循环决定，框架不做保证也不切换线程。
- `Tools/deploy-apk-safe.ps1` 的“安装后哈希证明”依赖 root（`su -c sha256sum`）；本机无 root，
  因此本阶段改用**设备 `/data/app/.../base.apk` 直接可读**的 `sha256sum` 作为等价证明（见真机报告）。
