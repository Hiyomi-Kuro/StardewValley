# Phase 2.1-P.1：标题重置边界与加载代次（reset/epoch）内部原型增量

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-24 / 2026-11-25（+08:00） |
| 阶段 | Phase 2.1-P.1（内部原型增量 + 合成执行 + 目标程序集静态检查；未接触设备） |
| 分支 / HEAD | `main` / `d76f20eb57731f4975b63e10ad1ed08052dcdbed` |
| 本地 `origin/main` / 远端 `main` | 同上（`git ls-remote` 复核） |
| 开工工作区 | 干净（已跟踪/未跟踪均无改动） |
| 游戏程序集 | `StardewValley` 1.6.15.24354；SHA-256 `587FF5FF…E047`；MVID `573bf1e5-3245-46d6-925a-1c3713afb677`；原始、未注入 |
| APK 身份 | `com.chucklefish.stardewvalley` / versionCode **239** / versionName **1.6.15.0** |
| 结论 | **原型增量 PASS**；**目标静态检查 PASS（仅静态）**；**设备准备度 HOLD**；公开发布 NOT_IN_SCOPE |

> 证据分级：**CURRENT_STATIC**（本轮对目标程序集的静态观察）、**SYNTHETIC_EXEC**（写出到磁盘、重新加载并真实执行合成程序集）、
> **HOST_MODEL**（协调器模型行为）、**HISTORICAL**（历史文档/历史 CI）、**NOT_RUN**、**UNKNOWN**。
> 本轮**没有任何**“真机已验证”“最终 APK 已验证”或“真实游戏 reset 已覆盖”的结论。

---

## 0. 结论摘要

| 项 | 判定 | 一条最强证据 | 一条最关键限制 |
| --- | --- | --- | --- |
| 原型增量（协调器 + 三信号注入） | **PASS** | 本机 **65/65**（开工基线 34/34 → 新增 31 项，0 删除）；reset 生命周期、代次拒绝、窗口拒绝、信号顺序均有断言 | 只证明模型与合成程序集；不证明真实游戏提供 reset 覆盖、线程权威或尝试令牌 |
| 合成执行（写盘 → 重载 → 执行） | **PASS** | 两条竞态用例完整经过**注入 IL → bridge → 协调器**，信号序列被逐项断言（`…ResetBegan>StartRefused:ResetEpochChanged>StartAccepted>CompletionAccepted`） | 合成夹具不是 `Game1`；重置体内是测试同步点，不是真实 543 条 IL 的语义 |
| 目标程序集静态改写 | **PASS（仅静态）** | 临时副本：1 reset 入口 + 1 reset 正常退出 + 1 开始 + 1 完成；**3 个方法变化、5 个 bridge 方法新增、AssemblyRef 28→28 未变、输入哈希未变** | 未执行任何游戏方法、未构建/安装 APK、未从最终 APK 反验 |
| 反例 C 的合成链路 | **已关闭一种交错（两种具体竞态）** | 旧 epoch 开始被 `ResetEpochChanged` 拒绝；epoch 已增、字段未清理的窗口开始被 `ResetInProgress` 拒绝 | 真实 reset 是否覆盖所有取消路径、工作线程是否真与 reset 并发、异常退出后的自愈语义均未运行时验证 |
| 反例 B（旧完成是否污染 B） | **仍可污染 → HOLD** | `late_completion_reads_b_loader` 证明：完成边读到的全局 loader 是 L_B 时，A 的迟到完成会被**接受为 B** | 两个/三个钩子都不提供“这次完成属于哪次开始”的令牌；引用相等不是拥有证明 |
| 线程权威（反例 D） | 模型 PASS / 真实 **NOT_RUN** | 未知线程/错误线程/正确线程均有可断言结果；静态链只到 `GameRunner.Update`/`Game1.Update` 的虚分派 | managed thread id 的运行时归属未测；权威输入未接线 |
| 设备试验准备 | **HOLD** | 三个已识别阻塞项（C 的真实覆盖、B 的令牌、D 的线程测量）都还没有运行时证据 | 不是“合成全绿所以 GO”，也不是 NO-GO：补证路径明确 |
| 公开发布 | **NOT_IN_SCOPE** | 未加 `IModEvents` 成员、未加公开 EventArgs、未改 AssemblyVersion 1.0.0.0 / Framework API 1.1.0 | 本轮不决定公开命名与语义 |

---

## 1. 开工基线

### 1.1 Git / 远端 / CI

| 查询 | 实际结果 | 类别 |
| --- | --- | --- |
| `git rev-parse HEAD` | `d76f20eb57731f4975b63e10ad1ed08052dcdbed` | CURRENT_STATIC |
| `git rev-parse origin/main` | 同上 | CURRENT_STATIC |
| `git ls-remote origin refs/heads/main` | 同上 | CURRENT_STATIC |
| `git status --porcelain`（开工） | 空（工作区干净） | CURRENT_STATIC |
| 已跟踪原型文件 | `Tools/prototype/WorldReady/*`、`Tools/tests/PrototypeTests/*` 均在 `d76f20e` 中已提交 | CURRENT_STATIC |

**远端既有结果（历史，不属于本轮改动）**

| 项 | 值 |
| --- | --- |
| Actions Run | **`45340372641`**（run_number 18） |
| `head_sha` | `d76f20eb57731f4975b63e10ad1ed08052dcdbed`（与当前 HEAD 一致） |
| 结论 | **success**，3/3 jobs：`offline checks`、`dotnet build + loader host tests`、`powershell gates` |
| 时间 | 2026-11-24T06:25:43Z → 06:51:43Z |

**本轮工作区结果（与远端分开列示）**

- `git status` 显示本轮只修改 7 个已跟踪文件（原型 4 + 测试 3），另新增本报告；**远端 CI = NOT_RUN**（未 commit / 未 push / 未触发 Actions）。
- 因此不得用 `45340372641` 冒充本轮通过：它跑的是 `d76f20e` 的**改动前**代码。

### 1.2 工作流对 PrototypeTests 的实际覆盖：**0**

这是本轮复核出的一个明确事实，不是推断：

```text
.github/workflows/ci.yml
  grep -n 'PrototypeTests|Prototype|WorldReady'  ->  无匹配
```

`dotnet-tools` job 只构建/运行 `ModInjector`、`ModFramework.Runtime`、fixture mods、`test_loader.py`、`InjectorTests`；
`offline-checks` 只跑 `test_ci_offline.py`、`test_modded_build.py`、`test_apk_admission.py`、`test_p114_probe.py`、隐私扫描。

**结论**：Phase 2.1-P.1 的 65 项原型检查是**本机专属**的，GitHub Actions 不会执行它们；报告的计数不能写成“CI 通过”。
本轮**没有**修改 `.github/workflows/`（CI 改动不在本任务授权范围）。

### 1.3 历史资料的使用边界

上一轮 `.tmp/phase2.1-prototype/target/StardewValley.prototype.patched.dll`（9,261,056 B / `6FB679EB…05046` / MVID `1d13f2a8…`）
是**历史产物**，本轮**没有**复用它作为输入或对照基线。本轮从原始字节重新拷贝一份临时副本
（`.tmp/phase2.1-p1/target/StardewValley.p1.dll`，哈希与原始一致）再改写。
上一轮报告里的命令、退出码与权限语义也**不自动沿用**：本轮的计数全部来自本轮实跑（§10）。

### 1.4 被保护内容

本轮**没有**修改：`Mod/`、`Tools/ModInjector/`、`Tools/build_modded_apk.py`、`verify_modded_apk.py`、
Gradle、部署脚本、CI、`final/`、`README.md`、`AGENTS.md`；没有生成 APK；没有触碰设备。

---

## 2. 输入身份复核

| 维度 | 值 | 类别 |
| --- | --- | --- |
| APK 显示版本 | `1.6.15.0`（`app/apktool.yml` → `versionInfo.versionName`） | CURRENT_STATIC |
| versionCode | `239` | CURRENT_STATIC |
| 托管程序集 | `StardewValley 1.6.15.24354` | CURRENT_STATIC |
| 字节数 / SHA-256 | 9,280,000 B / `587FF5FF9C258679004977BBEEC359ABA86649BD3E4860DDCAA428DF8F07E047` | CURRENT_STATIC |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` | CURRENT_STATIC |
| 注入状态 | 原始：`ModFramework.Bootstrap` 不存在、`Phase21.Prototype.WorldReadyBridge` 不存在 | CURRENT_STATIC |
| 同字节副本 | `.tmp/phase0.5/refs/StardewValley.dll` 与本轮临时副本哈希一致 | CURRENT_STATIC |

本轮输入是 `.tmp/phase2.1-p1/target/StardewValley.p1.dll`（原始字节的拷贝）；工具在写入前后都核对了它的 SHA-256，
**输入文件未被改写**。

---

## 3. 目标静态分析：`Game1.ResetGameStateOnTitleScreen()`

### 3.1 身份、调用方、形状（CURRENT_STATIC）

```text
signature   = public virtual instance void ResetGameStateOnTitleScreen()
attributes  = Public, Virtual, HideBySig, VtableLayoutMask（newslot=True, final=False）
ins         = 543    maxstack = 4    locals = 3    initlocals = True
EH          = 1 个 Finally：try=[IL_0299..IL_0310) handler=[IL_0310..IL_0314)
正常返回    = 1 个 ret，位于 IL_0542（不在任何 try/handler 内）
入口        = IL_0000（不在任何 try/handler 内）
```

**直接调用方（3 处，全部 `callvirt`）**：

| 调用方 | 场景 |
| --- | --- |
| `Game1.CleanupReturningToTitle()` | 游戏内退标题：`_update` 的 `exitToTitle` 分支 |
| `Menus.LoadGameMenu.saveFileScanComplete()` | 载入菜单扫描完成后的重置 |
| `Menus.TitleMenu.ReturnToMainTitleScreen()`（静态） | 标题菜单返回主标题 |

**重写扫描**：全模块按名字扫描 `ResetGameStateOnTitleScreen` 得到 `total = 1`，即**没有派生类型重写它**。
因此入口钩子不存在“虚分派到另一实现而绕过”的问题（这与“方法本身是 virtual”是两件事）。

**分支**：所有分支都是 try 内的小循环（`IL_0061→0064`、`IL_0063→0065`、`IL_0273→0276`、`IL_0275→0277`、
`IL_0299→0306`、`IL_0308→0300`、`IL_0309→0314`、`IL_0513→0520`、`IL_0531→0538`）。
**没有任何边直接指向 `IL_0542` 的 ret**，所以“在唯一正常 ret 前插钩子”不需要重定向分支；
工具仍然复用了同一套“重定向边 + 移动 EH 半开边界”的通用规则。

### 3.2 为什么选这个失效点

1. 它是**唯一**在“回标题”路径上清空 `currentLoader`、`hasLoadedGame` 的方法；可行性文档的场景 E/F/G
   都把世界生命周期的结束点放在这里。
2. 入口（`IL_0000`）在**一切状态写入之前**：epoch 递增的提交点因此严格先于游戏字段变化，
   这一点让“旧代次拒绝”和“窗口拒绝”有明确的先后语义（§4.4）。
3. 结构上可注入且可验证：1 个正常 ret、0 个 handler 内 ret、ret 无入边、入口不在保护区内、
   单个 finally 不覆盖入口与 ret。
4. 无重写，3 个调用方覆盖了退标题、扫描完成、标题返回三条已知路径。

### 3.3 首个状态写入与清理顺序（这是本轮的关键新证据）

| 顺序 | IL | 写入 | 意义 |
| --- | --- | --- | --- |
| 1 | `IL_0000–0005` | `LocalizedContentManager.localizedAssetNames.Clear()`、`Event.invalidFestivals.Clear()`、`NPC.invalidDialogueFiles.Clear()` | 先清缓存 |
| 2 | `IL_0006–0007` | `SaveGame.CancelToTitle = false` | **第一条直接静态字段写入** |
| 3 | `IL_0008–0009` | `overlayMenu = null` | |
| 4 | **`IL_0025–0026`** | `set_gameMode(0)` | **gameMode 清零**（早） |
| 5 | **`IL_0087–0088`** | `currentLoader = null` | **loader 清零**（中） |
| 6 | **`IL_0192–0193`** | `hasLoadedGame = false` | **世界已加载标志清零**（晚） |
| 7 | `IL_0194–0195` | `hasStartedDay = false` | |
| 8 | `IL_0541–0542` | `ItemRegistry.ResetCache()`、`Rumble.stopRumbling()`，然后 `ret` | 正常退出 |

**由此存在两个“epoch 已递增、游戏字段仍是旧值”的窗口**：

- **窗口 W1：`[入口 .. IL_0026)`** —— `gameMode` 仍可能是 3 或 6，`currentLoader` 仍是 `L_old`，
  `hasLoadedGame` 仍为 true。
- **窗口 W2：`[IL_0026 .. IL_0193)`** —— `gameMode` 已是 0，但 `currentLoader` 直到 IL_0088 才清空，
  `hasLoadedGame` 直到 IL_0193 才为 false。

在 W1 内到达的**开始**信号可以读到“当前 epoch + 上一世界的 mode/loader/hasLoadedGame”；
在 W2 内到达的**完成**信号可以读到 mode 0（被 `TitleMode` 拒绝）——这两者必须分开处理，
不能只靠 epoch。

### 3.4 哪些取消、异常与返回路径可能绕开这个失效点

| 路径 | 结果 | 本轮处理 |
| --- | --- | --- |
| 正常退标题（`CleanupReturningToTitle` → 本方法） | 入口钩子与正常 ret 钩子都触发 | 已建模并合成执行 |
| 读档被取消（`SaveGame.CancelToTitle` → `ExitToTitle` → mode 0 → 之后 `_update` 走 exitToTitle） | 只要 `CleanupReturningToTitle` 真的执行，reset 就到达 | 静态可达；**运行时是否每一帧都到达 = NOT_RUN** |
| 本方法内部抛异常（543 条 IL 里多处 `Clear()`/`destroy()`/`Reset()`/`resetPlayer()`，外层只保护 netcode foreach） | **`ResetCompleted` 不会执行 → latch 保持** | 已定义并测试（§4.3、§9） |
| 完全绕开本方法、只把 `gameMode` 置 0 的取消路径 | 没有 epoch 递增，也没有 latch；只能靠 mode 0 完成（`TitleMode`）或下一次 `Begin`（`Superseded`）清掉 pending | 模型覆盖，但**“是否存在这样的路径”未证明为不存在 → UNKNOWN** |
| handler 内 return | 本方法 0 个 handler 内 ret | 验证断言 `ResetHandlerReturns = 0` |
| 子类重写 | 全模块 0 个重写 | 已扫描并断言 |

> 说明：本轮没有重新做“`currentLoader`/`hasLoadedGame` 是否只在该方法里被清空”的全局写点普查，
> 因此“只有这里会清空”仍沿用研究阶段的 CURRENT_STATIC 结论，不升级为证明。

---

## 4. 原型增量实现

### 4.1 协调器：状态与提交顺序

新增 `RefusalKind.ResetInProgress`、`RefusalKind.NoResetInProgress`，并用 `TryBeginReset` /
`TryCompleteReset` 表示 reset 边界（不再用“模型里随手 `ResetWorld`”冒充真实边界）：

```text
TryBeginReset(thread, reason)  —— reset 入口
  同一把锁内依次提交：
    (1) 丢弃 pending（cancelled++）
    (2) resetEpoch++（单调）
    (3) 置 resetInProgress = true
  线程权威已确定且 thread 不等于它  -> WrongThread，拒绝且不改任何状态
  若已处于 reset 中（上一次未正常返回）-> 接受并重新武装（唯一的自愈入口）

TryCompleteReset(thread)       —— reset 正常 ret
  同线程；resetInProgress=true -> 清除 latch，completedReset++；
  resetInProgress=false        -> NoResetInProgress

TryBeginAttempt(..., observedEpoch)
  observedEpoch != resetEpoch -> ResetEpochChanged（**不动 pending**）
  resetInProgress             -> ResetInProgress（**不动 pending**）
  其余守卫不变（mode==6、loader!=null、线程不检查开始路径）

Complete(...)
  resetInProgress -> ResetInProgress（不动任何状态）
  其余守卫与顺序不变；仅 StaleLoaderEvidence 保留 pending
```

**提交顺序的三条断言**（`completion_then_reset_keeps_one`、`reset_then_completion_order`、
`reset_correct_thread_signal_order`）：

- 完成先提交 → 观察 1 次；随后 reset 只递增 epoch，不撤销已提交的观察
  （`StartAccepted>CompletionAccepted>ResetBegan>ResetCompleted`）。
- reset 先提交 → 期间完成被 `ResetInProgress` 拒绝，结束后完成为 `NoPendingAttempt`，观察 0。
- 旧 epoch 的开始被拒绝时，**更新的 B pending 保持**（`stale_epoch_begin_preserves_newer_pending`）。

新增 `AttributionBasis`（`None` / `LoaderReference`）与 `AttemptOwnershipProven`（恒为 false），
把“比较引用成功”和“证明这次完成属于这次尝试”分开记录（§7）。

协调器还保留一份**有界有序信号轨迹**（`Signals`，上限 1024，溢出计数），只给测试断言顺序用，
不派发、不回调、不写日志。

### 4.2 注入后的实际 IL（CURRENT_STATIC，写入后重新打开验证）

**开始边（`loadForNewGame(bool)` 入口，epoch 最先读）**：

```text
IL_0000  call   Phase21.Prototype.WorldReadyBridge::GetResetEpoch()          -> int64
IL_0001  ldarg  loadedGame                                                   -> bool
IL_0002  call   StardewValley.Game1::get_gameMode()                          -> byte
IL_0003  ldsfld StardewValley.Game1::currentLoader                           -> object
IL_0004  call   WorldReadyBridge::WorldLoading(int64, bool, int32, object)
IL_0005  ldsflda Game1::startingGameSeed      ; 原第一条指令，位置未移动
```

参数压栈顺序 = 方法签名顺序 = **epoch → loadedGame → gameMode → loader**，因此“先读 epoch、再读 gameMode/loader”
不是靠注释，而是由指令顺序和签名共同固定；`PrototypeInjector.Verify` 会逐条比对这 5 条指令
（`EpochReadFirst`）。

**reset 边（入口 + 唯一正常 ret）**：

```text
IL_0000  call   WorldReadyBridge::ResetBegan()         ; 原 IL_0000 后移
…
IL_0543  call   WorldReadyBridge::ResetCompleted()
IL_0544  ret
```

**完成边（`OnDayStarted()` 唯一正常 ret 前）——签名与顺序保持上一轮形状**：

```text
IL_0113  call   Game1::get_gameMode()
IL_0114  ldsfld Game1::hasLoadedGame
IL_0115  ldsfld Game1::currentLoader
IL_0116  call   WorldReadyBridge::DayCompleted(int32, bool, object)
IL_0117  ret
```

完成边**不读 epoch**：epoch 只用于开始与 reset 的先后，不用于给迟到的完成重新贴标签
（沿用上一轮的规则，避免“按当前代次重新归因”这种更糟的设计）。

### 4.3 两个 bridge 的能力差异（分别说明，不混用语义）

| 能力 | 测试桥 `Phase21.WorldReady.PrototypeBridge`（合成夹具调用） | 目标诊断桥 `Phase21.Prototype.WorldReadyBridge`（注入到临时副本） |
| --- | --- | --- |
| 方法面 | `GetResetEpoch/ResetBegan/ResetCompleted/WorldLoading/DayCompleted` | 同名同签名的 5 个方法 |
| epoch 来源 | 转发到活的 `WorldReadyCoordinator`（真实 epoch） | 模块内 `EpochCounter` 自增，**只是替身** |
| 语义 | 真实的锁定、pending、latch、拒绝原因、观察 | **只计数并记住最后一次参数**，没有任何拒绝语义 |
| 依赖 | 引用 `Phase21.WorldReady` 程序集 | 完全在模块内，**不新增 AssemblyRef** |
| 校验方式 | 写出后 `Assembly.LoadFrom` **真实执行**，断言协调器状态与信号顺序 | 写出后**重新打开模块**做结构校验（调用点、签名、指令顺序、EH、方法集合） |
| 不能证明 | 真实游戏字段/线程 | 任何运行时行为；不能把它的计数器当成协调器结果 |

两条链各自有自己的“写出后校验”：合成侧是执行断言，目标侧是 `--verify-target` 重开模块的结构断言。
报告中的“拒绝原因”“pending”“观察计数”**只适用于测试桥**。

### 4.4 为什么需要 reset 进行中判定（窗口 W1）

只靠 epoch 不够：如果一个开始信号在 reset 入口**之后**才读 epoch，它读到的是**新** epoch，
但此时游戏字段还是旧世界的（W1/W2）。若没有 latch，这个开始会被当成“新代次”接受，
带着 `L_old` 进入 pending。夹具 `reset_guard_absent_accepts_window_start` 就是这条反证的负向对照：
把 `honorResetInProgress` 关掉，同样的开始会被接受并留下 pending。

`SyntheticGate.MidReset()` 提供确定性的同步点：把重置体挂起在**入口钩子之后、字段清理之前**，
从另一个线程发起开始，观察它被 `ResetInProgress` 拒绝；恢复后 reset 正常结束，B 再正常完成一次。

**正常退出与异常退出的行为**（明确，不靠推断）：

- 正常退出：注入的 `ResetCompleted()` 在唯一正常 ret 前执行，latch 清除。
- 异常退出：`ResetCompleted()` 不执行，latch **保持**；此后所有开始/完成都被 `ResetInProgress` 拒绝
  （fail-closed）。恢复规则只有一个：**下一次走到正常 ret 的 reset** 会重新武装并最终清除 latch。
- 这条规则被 `reset_abnormal_exit_then_next_reset_heals`（模型）与
  `synthetic_reset_exception_leaves_latch`（合成执行：throwing 夹具 + 正常夹具共用同一协调器）同时覆盖。

---

## 5. 反例 A–E：各自的验证层级

| 反例 | 本轮验证层级 | 具体结果 | 剩余疑点 |
| --- | --- | --- | --- |
| **A** 取消后经 mode 0 间接完成、之后恢复非 0 模式 | HOST_MODEL + SYNTHETIC_EXEC | mode 0 完成被 `TitleMode` 拒绝并清 pending；随后 mode 3 完成 0；reset 后无 mode 0 完成时，pending 也已由 reset 取消（`cancel_then_late_completion_zero`、`synthetic_branch_to_ret_runs_completion`、`synthetic_title_mode_completion_refused`） | “取消必产生 mode 0 完成或必到达 reset”仍是两条**未证明必居其一**的假设；两者都不发生的取消路径 → UNKNOWN |
| **B** 旧 A 的迟到完成与 B 交错 | HOST_MODEL + SYNTHETIC_EXEC（**反证**） | 带 loader 身份：A 的 `L_A` 完成 → `StaleLoaderEvidence`，B 保留并 +1（`stale_a_completion_does_not_eat_b`、`synthetic_stale_completion_preserves_b`）；**但** A 的迟到完成若读到的是全局 `L_B`，会被接受为 B（`late_completion_reads_b_loader`、`synthetic_late_completion_reads_b_loader`） | **未关闭**：引用相等 ≠ 尝试拥有；无令牌 → B 保持 HOLD（§7） |
| **C** 开始与 reset 竞态 | HOST_MODEL + SYNTHETIC_EXEC | 旧 epoch 开始 → `ResetEpochChanged`，B 正常完成一次（`synthetic_stale_epoch_start_refused_e2e`）；窗口内开始 → `ResetInProgress`，pending 不产生（`synthetic_mid_reset_window_start_refused_e2e`） | 真实 reset 是否覆盖每条取消路径、真实工作线程是否与 reset 并发、异常退出的实际概率 = NOT_RUN（§6） |
| **D** 错线程/未知线程完成 | HOST_MODEL | 未设权威 → `UnknownGameLoopThread`；错线程 → `WrongThread`；reset 的错线程开始/完成也各自拒绝（`unknown_loop_thread_zero`、`wrong_thread_completion_zero`、`reset_wrong_thread_refused_preserves_pending`、`reset_completion_wrong_thread_refused`） | 真实 managed thread id 归属未测；权威未接线（§8） |
| **E** 消费后跨日/菜单/错误模式恢复的重复完成 | HOST_MODEL | 重复完成、菜单、跨日三次均为 `AlreadyConsumed`，观察保持 1（`post_consumption_completions_zero`） | 仍需真实世界的重复次数证据；模型只证明“不会多观察” |

---

## 6. 反例 C：关闭了哪一种交错，还差什么

**已关闭（合成链路，确定性）**

1. **旧 epoch 读取 → reset → 旧开始提交**：
   注入 IL 先调 `GetResetEpoch()`（读到 0），测试侧 sink 在**提交前**阻塞；此时在游戏循环线程执行
   `ResetGameStateOnTitleScreen`（epoch→1、取消 pending）；放行后开始携带 0 提交 → `ResetEpochChanged`；
   随后 B 用 epoch 1 开始并完成**恰好一次**。信号序列被断言为
   `ResetBegan>ResetCompleted>StartRefused:ResetEpochChanged>StartAccepted>CompletionAccepted`，
   pending 与观察计数同时断言。
2. **epoch 已递增、标题字段尚未清理的窗口**：
   夹具把重置体挂在入口钩子之后、`gameMode/currentLoader/hasLoadedGame` 清理之前；
   从另一线程发起开始 → `ResetInProgress`、pending 保持 0；恢复后 reset 正常完成，B 再完成一次。
   信号序列：`ResetBegan>StartRefused:ResetInProgress>ResetCompleted>StartAccepted>CompletionAccepted`。

这两条覆盖的正是“开始证据与 reset 不原子”的具体形态：**读 epoch 之后 reset 插进来**，以及
**读 epoch 时 reset 已经开始、但字段还没清**。负向对照证明这不是“测试写死”的结论：

- `negative_epoch_read_after_mode_rejected`：把 `get_gameMode` 移到 epoch 读取之前，`Verify` 直接判失败。
- `negative_epochless_begin_accepts_start`：用不带 epoch 的 `Begin` 重载，同一个开始被接受 —— 旧接线会回归。
- `reset_guard_absent_accepts_window_start`：关掉 latch，窗口内的开始被接受并留下 pending。

**仍未关闭（真实游戏）**

| 缺口 | 为什么没有关闭 | 需要什么证据 |
| --- | --- | --- |
| reset 覆盖 | 合成夹具的 reset 是最小形状，不是 543 条 IL；真实游戏是否在**每条**取消/失败路径都执行到 `ResetGameStateOnTitleScreen` 的**正常 ret**，没有运行时记录 | 一次隔离读档 + 取消 + 退标题，记录 reset 入口/正常 ret 的调用次数与 thread |
| 并发真实性 | “`loadForNewGame(true)` 在工作线程、reset 在主线程”是静态推断（`SaveGame/<getLoadEnumerator>d__123` 的 Task 闭包 vs `_update`） | 在两个钩子里记录 thread id，验证二者确实不同且与 Tick 线程一致 |
| 异常退出 | latch 的自愈只有模型与合成证据；真实异常会先破坏游戏主循环 | 故障注入（不在本任务范围）或静态证明该方法不抛 |
| 未知取消路径 | 是否存在既不调 reset、也不产生 mode 0 完成的取消路径 | 全路径调用图 + 运行时 mode/完成序列记录 |

---

## 7. loader 归因：比较两个引用成功 ≠ 已证明完成属于 B

这是本轮**主动保留为 HOLD** 的结论，而不是遗漏。

**机制**：完成钩子读的是全局 `Game1.currentLoader`。如果 A 的完成逻辑迟到，而 B 已经开始并把它自己的
loader 装进 `currentLoader`，那么 A 的迟到完成读到的是 `L_B`，与 B 的 pending 匹配 → 被接受为 B 的观察，
B 的真实完成变成 `AlreadyConsumed`。两条用例把这件事分别证明在模型层和合成执行层：

```text
late_completion_reads_b_loader                 (HOST_MODEL)
  acceptedAsB=True, basis=LoaderReference, bRealCompletion=AlreadyConsumed
synthetic_late_completion_reads_b_loader       (SYNTHETIC_EXEC，经真实注入 IL)
  acceptedAsB=True, basis=LoaderReference, bRealCompletion=AlreadyConsumed
```

**因此**：`WorldReadyObservation.AttributionProven` 现在只表示“loader 引用相等”，
并新增 `Basis = LoaderReference` 与恒为 false 的 `AttemptOwnershipProven`。
真实路径**没有**可靠的尝试身份（两个/三个钩子都不携带令牌），所以：

- **B 维持 HOLD**，不得把它写成“已证明归因”。
- 若将来仍要按 loader 身份上线，必须先拿到“完成边所属尝试”的独立证据；现有注入点无法提供。

---

## 8. 线程：静态可证明的约束与将来的测量方法

**静态链（CURRENT_STATIC，本轮重新 dump 调用者）**

```text
Game1.OnDayStarted()             ← 2 个直接调用者：Game1.set_activeClickableMenu(IClickableMenu)（静态）、Game1._update(GameTime)
Game1._update(GameTime)          ← 1 个直接调用者：Game1.Update(GameTime)
Game1.Update(GameTime)           ← 模块内 0 个直接调用者（virtual override，由 MonoGame.Framework 的 Game.Tick 虚分派）
GameRunner.Update(GameTime)      ← 模块内 0 个直接调用者（同上；Phase 2.0 的 Tick 钩子就挂在这个方法头）
Game1.ResetGameStateOnTitleScreen ← CleanupReturningToTitle / LoadGameMenu.saveFileScanComplete / TitleMenu.ReturnToMainTitleScreen
```

**能证明**：`_update` 与两个 `OnDayStarted` 来源在同一条同步调用链上；也就是说，
**谁执行 `Game1.Update`/`GameRunner.Update`，谁就执行完成边**。它们是 MonoGame 的 `Game` 主循环。

**不能证明**：这个主循环的 **managed thread id**。模块内没有任何地方记录它；
`Environment.CurrentManagedThreadId` 只能由钩子在运行时读取。因此：

- 模型对 `TrySetGameLoopThread` 采取**一次设定、不同值拒绝**；
- 完成边在权威未设定时是 `UnknownGameLoopThread`，设定后不等即 `WrongThread`；
- reset 边界同样做线程检查（错线程拒绝且不改状态），未设权威时接受并记录。

**将来如何测得可信的游戏循环线程**（明确的下一步，不在本轮执行）：

1. 复用 Phase 2.0 已注入的 `GameRunner.Update` 方法头的 `Bootstrap.Tick()`；
2. 在 `Tick` 里记录一次 `Environment.CurrentManagedThreadId` 作为候选权威（进程级一次）；
3. 与完成边钩子记录的 thread id 比较：相等 → 该权威可用；不等 → 该候选不可作为完成边的权威，
   改为记录两者并保持 HOLD；
4. 同一记录里带上 `gameMode`、`hasLoadedGame`、`currentLoader` 引用相等性，作为 §6 的补证。

---

## 9. 验收矩阵（本轮实跑）

层级：**M** = HOST_MODEL，**S** = SYNTHETIC_EXEC（写盘/重载/执行），**T** = 目标静态改写。
下表只列本轮新增或语义变更的 31 项；上一轮 34 项保持通过（0 删除）。

| 测试 | 层级 | 断言 | 结果 |
| --- | --- | --- | --- |
| `reset_cancels_pending_and_bumps_epoch` | M | pending 被取消、epoch=1、cancelled=1、latch 置位 | PASS |
| `reset_completion_clears_latch` | M | 正常 ret 清 latch、completedReset=1 | PASS |
| `duplicate_reset_completion_refused` | M | 无 reset 时的完成信号 → `NoResetInProgress` | PASS |
| `reset_in_progress_refuses_start` | M | 窗口内开始 → `ResetInProgress`，pending 0 | PASS |
| `reset_guard_absent_accepts_window_start` | M（负向对照） | 关 latch 后同一开始被接受 → 证明守卫必要 | PASS |
| `reset_in_progress_refuses_completion` | M | 窗口内完成 → `ResetInProgress`，观察 0 | PASS |
| `repeated_reset_counts_and_epoch` | M | 连续两次 reset：epoch=2、cancelled 不增加 | PASS |
| `reset_then_retry_plus_one` | M | reset 后正常重试 +1 | PASS |
| `new_game_after_reset_unproven` | M | 新游戏 null loader：接受但 `Basis=None` | PASS |
| `reset_abnormal_exit_then_next_reset_heals` | M | 异常退出 latch 卡住 → 拒绝 → 下次正常 reset 自愈 | PASS |
| `stale_epoch_begin_preserves_newer_pending` | M | 旧 epoch 开始拒绝且 B pending 保留、B 完成 +1 | PASS |
| `completion_then_reset_keeps_one` | M | 完成先提交：观察 1，信号顺序固定 | PASS |
| `reset_then_completion_order` | M | reset 先提交：期间 `ResetInProgress`，之后 `NoPendingAttempt` | PASS |
| `late_completion_reads_b_loader` | M | A 迟到完成读 `L_B` → 被接受为 B；`Basis=LoaderReference`、`Ownership=false` | PASS（反证） |
| `reset_wrong_thread_refused_preserves_pending` | M | 错线程 reset → `WrongThread`，epoch/pending 不变 | PASS |
| `reset_completion_wrong_thread_refused` | M | 错线程 reset 完成 → 拒绝且 latch 保持 | PASS |
| `reset_on_unknown_thread_accepted` | M | 未设权威：接受 reset 并记录，不声称线程 | PASS |
| `reset_correct_thread_signal_order` | M | 信号轨迹 `StartAccepted>ResetBegan>ResetCompleted` | PASS |
| `synthetic_reset_hooks_execute_once` | S | 注入 IL 触发 1 次入口 + 1 次正常退出，协调器同步 | PASS |
| `synthetic_reset_clears_game_fields` | S | mode=0、loader=null、hasLoadedGame=false | PASS |
| `synthetic_epoch_read_before_mode` | S | 注入序列读到的 epoch 在 reset 后为 1；观察 epoch=1 | PASS |
| `synthetic_stale_epoch_start_refused_e2e` | S（**竞态 1**） | 旧 epoch 开始拒绝、B 完成 1 次、信号序列逐项相等 | PASS |
| `synthetic_mid_reset_window_start_refused_e2e` | S（**竞态 2**） | 窗口内开始 `ResetInProgress`、pending 0、B 完成 1 次、信号序列相等 | PASS |
| `synthetic_late_completion_reads_b_loader` | S | 经真实 IL 的 A→B 误归因复现 | PASS（反证） |
| `synthetic_reset_exception_leaves_latch` | S | reset 抛异常 → 不触发退出钩子 → latch 卡住 → 后续 reset 自愈 | PASS |
| `synthetic_reset_ret_in_try_rejected` | S | reset 的 ret 在 try 内 → 拒绝写入 | PASS |
| `synthetic_reset_handler_only_ret_rejected` | S | reset 只有 handler 内 ret → 拒绝 | PASS |
| `synthetic_reset_with_finally_accepted` | S | 带 finally 的 reset：1 个退出点、0 个 handler ret | PASS |
| `negative_epoch_read_after_mode_rejected` | S（负向对照） | 读 mode 早于 epoch → `Verify` 失败 | PASS |
| `negative_epochless_begin_accepts_start` | M（负向对照） | 不带 epoch 的 Begin 接受同一开始 → 旧接线回归 | PASS |
| `synthetic_reset_handler_end_moves_with_hook` | S | EH 半开边界随钩子前移 | PASS |

**计数**：**31 项新增 + 34 项原有 = 65 checks，65 passed，0 failed**（退出码 0）。

---

## 10. 实际运行的命令与结果（本轮实测）

| 命令（仓库根） | 工具 | 退出码 | 结果 | 日志（`.tmp/`，gitignore） |
| --- | --- | ---: | --- | --- |
| `dotnet build Tools/tests/PrototypeTests/... -c Release` | .NET SDK 8.0.404 / Win10.0.26100 | 0 | 0 warning / 0 error | `logs/build-baseline.log` |
| `dotnet .../Phase21.PrototypeTests.dll`（**改动前基线**） | 同上 | 0 | **34/34** | `logs/host-tests-baseline.log` |
| `dotnet .../Phase21.PrototypeTests.dll`（改动后） | 同上 | 0 | **65/65**（新增 31，删除 0） | `logs/host-tests.log` |
| `dotnet .../Phase21.PrototypeTests.dll --target <temp copy> <out> <refs>` | 同上 | 0 | 结构 PASS；reset 1+1、开始 1、完成 1；3 方法变化、5 方法新增 | `logs/target-check.log` |
| `dotnet .../Phase21.PrototypeTests.dll --verify-target <patched> <refs>` | 同上 | 0 | 结构 PASS（重开模块） | `logs/target-verify.log` |
| `dotnet build Tools/tests/InjectorTests/... -c Release` + 运行 | 同上 | 0 / 0 | **29/29** | `logs/injector-tests.log` |
| `python Tools/tests/test_loader.py --dotnet ... --refs .tmp/phase0.5/refs` | Python 3.12.10 | 0 | **53/53**，host summary **552/552**，Phase 2.0 0 失败 | `logs/loader-host.log` |
| `python Tools/tests/test_ci_offline.py` | 同上 | 0 | **13/13**（含隐私 OK、workflow 静态检查） | `logs/ci-offline.log` |
| `python Tools/tests/test_modded_build.py` | 同上 | 0 | **55/55** | `logs/modded-build.log` |
| `python Tools/tests/test_apk_admission.py` | 同上 | 0 | **16/16** | `logs/apk-admission.log` |
| `python Tools/tests/test_mod_pipeline.py --dotnet ... --refs .tmp/phase0.5/refs` | 同上 | 0 | **39/39** | `logs/mod-pipeline.log` |
| `python Tools/tests/test_p114_probe.py` | 同上 | 0 | **46/46** | `logs/p114.log` |
| `git diff --check` | git | 0 | 无输出 | — |

**改动前基线（同一 HEAD、未改动代码）**：34/34。上一轮报告写的 34 也是这个数，本轮**实测复现**，
不是照抄；改动后为 65/65。

**NOT_RUN（明确未执行）**：ADB / 设备 / 模拟器；APK 构建（`assembleDebug`/`assembleLoaderOnlyDebug`/`assembleModdedDebug`）；
`verify_modded_apk.py`；安装/部署；读写 Saves；进入或创建存档；
PowerShell 部署门禁（`test_deploy_safe.ps1`、`test_deploy_mods_safe.ps1`、`test_device_precheck.ps1`，本轮未改这些路径且耗时约 20+ 分钟）；
GitHub Actions；commit / push。

---

## 11. 三项判定

### 11.1 原型增量：**PASS**

- 协调器新增 reset 生命周期（入口/正常退出/窗口/latch/重复 reset/异常退出自愈），
  epoch 与带 epoch 的 `Begin` 被真实接线；提交顺序有三条断言。
- 三个信号点（reset、开始、完成）在合成程序集上**写出、重载、真实执行**；
  两条竞态用例的**信号顺序、拒绝原因、pending、观察计数**全部断言。
- 负向对照齐全：关掉 latch、去掉 epoch 参数、把 mode 读在 epoch 前，三种“旧接线”都会触发回归。
- **PASS 边界**：仅主机模型 + 合成程序集；不代表真实游戏。

### 11.2 目标静态检查：**PASS（仅静态）**

- 临时副本：`ResetGameStateOnTitleScreen` 入口 + 唯一正常 ret、`loadForNewGame` 入口、`OnDayStarted` 唯一正常 ret，
  共 4 个调用点/3 个信号边；`EpochReadFirst=True`。
- 3 个方法变化（恰为预期）、5 个 bridge 方法新增、0 删除；**AssemblyRef 28→28 完全一致**；
  MVID `573bf1e5…` → `8fa5564c-c0ba-4295-8def-df9f3dd507fb`；输入 SHA-256 前后一致。
- **maxstack 归一化范围单独列出**（Cecil 重写模块的副产物，非语义改动）：

  | 方法 | maxstack |
  | --- | --- |
  | `Child::canTalk()` | 3 → 1 |
  | `Program::GetAppDataFolder(string,bool)` | 3 → 1 |
  | `DyeMenu::performHoverAction(int,int)` | 4 → 0 |
  | `InventoryMenu::receiveGamePadButton(Buttons)` | 4 → 0 |
  | `InviteCodeDialog::.ctor(string,behavior)` | 10 → 4 |
  | `ItemGrabMenu::receiveGamePadButton(Buttons)` | 5 → 2 |

  三个被钩方法**没有**被抬高：reset 4→4、load 5→5、completion 6→6。
- **PASS 边界**：未执行任何游戏方法、未构建/安装 APK、未从最终 APK 反验。

### 11.3 设备准备度：**HOLD**

按顺序的三个阻塞项：

1. **反例 B：loader 归因不等于拥有。** 已有反证用例，说明旧完成可以污染 B；
   要解除必须拿到“完成边所属尝试”的独立证据，现有注入点不提供。
2. **反例 C 的真实覆盖：reset 边界是否覆盖所有取消路径、工作线程是否真与 reset 并发。**
   合成链路已关闭两种交错，但真实调用序列未测。
3. **反例 D：线程权威。** 模型完备，但真实 managed thread id 未测、权威未接线。

**解除它的最小证据**（需单独批准，本轮不执行）：

- 一份只读运行时记录：一次隔离读档 + 一次正常跨日 + 一次回标题 + 一次同档重载，
  记录 reset 入口/正常 ret 次数、开始/完成/reset 三处的 `gameMode`、`hasLoadedGame`、
  `currentLoader` 引用相等性、managed thread id，以及 Tick 线程；
- 若记录显示“A 的迟到完成发生在 B 开始之后且读到 `L_B`”真实可达 → B 降级为 NO-GO，不进入设备写入；
- 若 reset 的完成信号在真实取消路径上缺失 → 保持 HOLD，并重新评估“取消必有 mode 0 完成”。

---

## 12. 若仍有旧开始/旧完成能污染 B：最早的错误状态转换与最小复现

| 项 | 内容 |
| --- | --- |
| 最早的错误状态转换 | `WorldReadyCoordinator.Complete()` 在 `pending.Kind == Load` 且 `ReferenceEquals(loader, pending.Loader)` 时**仅凭引用相等**就提交观察（`Accepted=true`、`Basis=LoaderReference`） |
| 为什么这是“最早” | 在它之前没有任何信号携带尝试身份：开始边只记 `L`，完成边只读全局 `L`，两者之间没有令牌 |
| 最小复现（模型） | `late_completion_reads_b_loader`：A 开始 → reset → B 开始 → 用 `L_B` 调 `Complete`（代表 A 的迟到完成）→ 被接受为 B，B 的真实完成 `AlreadyConsumed` |
| 最小复现（合成执行） | `synthetic_late_completion_reads_b_loader`：完全经注入 IL、bridge、协调器，结论相同 |
| 为什么现在不能修 | 修复需要“完成边知道自己是哪次尝试”的信号；`OnDayStarted()` 没有参数、没有调用者上下文、也没有可安全读取的尝试字段。任何基于全局字段的推断都会重蹈同一问题 |
| 下一步所需证据 | ① 运行时确认“A 的迟到完成是否真实可达”（若不可达，B 的风险降级为理论）；② 若可达，寻找新的注入点（例如 `loadForNewGame` 返回后的续体、或 `UpdateTitleScreenDuringLoadingMode` 的完成分支）并证明它能给出尝试身份；③ 在此之前维持设备 HOLD |
| 设备准入 | 维持 **HOLD**；不得以“65/65 全绿”为依据放行 |

---

## 13. 限制与未知

- 只覆盖 `StardewValley` 1.6.15.24354 / versionCode 239 的**原始**程序集；未从设备安装包重新取字节。
- 目标程序集只做静态改写与结构校验；未执行任何游戏方法，未构建/安装 APK，最终 APK 验证 NOT_RUN。
- 合成夹具不是 `Game1`：重置体的字段清理顺序按真实 IL 复刻（mode → loader → hasLoadedGame），
  但只有 4 条清理指令 + 1 个测试同步点，不复刻 543 条 IL 的其他语义。
- 反例 C 的合成链路只关闭“旧 epoch 提交”和“窗口内提交”两种交错；真实 reset 覆盖、线程、异常概率未测。
- 反例 B 明确**未关闭**：loader 引用相等不是尝试拥有证明；新游戏无令牌，归因保持 UNKNOWN。
- 线程权威未接线；报告只给出静态约束与测量方案。
- 多人/本地联机、非空真实存档、断电恢复、Android 11+、其它 OEM 均未覆盖。
- 没有 commit / push / 分支 / Actions；没有修改原始游戏输入、`AGENTS.md`、`README.md`、`final/` 或 CI；
  没有把原始程序集、存档、密钥、设备日志或大型转储写入仓库（临时副本与日志都在 `.tmp/`，被 gitignore）。

---

## 14. 收尾检查（本轮实际结果）

```text
git status --short
  M Tools/prototype/WorldReady/PrototypeBridge.cs
  M Tools/prototype/WorldReady/PrototypeInjector.cs
  M Tools/prototype/WorldReady/TargetBridgeEmitter.cs
  M Tools/prototype/WorldReady/WorldReadyCoordinator.cs
  M Tools/tests/PrototypeTests/Program.cs
  M Tools/tests/PrototypeTests/SyntheticFixture.cs
  M Tools/tests/PrototypeTests/TargetCheck.cs
  ?? docs/audit/phase2.1-p1-reset-epoch-report.md

git diff --check   -> exit 0，无输出
git diff --stat    -> 7 files changed, 2106 insertions(+), 229 deletions(-)
```

- 变更只落在 Phase 2.1-P 原型与 PrototypeTests（授权范围内）与本报告；`Mod/`、`Tools/ModInjector/`、
  Gradle、部署脚本、CI、`final/` 均未改动。
- `.tmp/phase2.1-p1/` 保存本轮日志、临时程序集副本、IL dump 与被 gitignore 的构建产物；
  仓库内没有新增 APK、程序集、存档、密钥或设备日志。
- 未暂存任何文件。
