# Phase 2.1-P.3：加载尝试的端到端归属与设备诊断准备

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-26（+08:00） |
| 阶段 | Phase 2.1-P.3（源码 + 目标程序集静态证据；内部原型增量；合成执行；目标临时副本静态改写；设备诊断方案设计；未接触设备） |
| 分支 / HEAD | `main` / `4534fc99d1390e26ccea74d630c5fd50736cdd10` |
| 本地 `origin/main` / 远端 `main` | 同上（开工时 `git status --porcelain` 为空） |
| 游戏程序集 | `StardewValley` 1.6.15.24354；SHA-256 `587FF5FF…E047`；MVID `573bf1e5-3245-46d6-925a-1c3713afb677`；原始、未注入 |
| APK 身份 | `com.chucklefish.stardewvalley` / versionCode **239** / versionName **1.6.15.0** |
| 三项判定 | 端到端归属 **PASS（仅 CURRENT_STATIC + HOST_MODEL + SYNTHETIC_EXEC）**；原型验证 **PASS**；设备准备度 **HOLD** |

> 证据分级：**CURRENT_STATIC**（本轮对目标程序集字节的静态观察）、**SYNTHETIC_EXEC**（写出合成程序集、重新加载并真实执行）、
> **HOST_MODEL**（协调器/测试宿主行为）、**CURRENT_RUNTIME**（本轮不存在）、**HISTORICAL**（历史文档/历史 CI）、**NOT_RUN**、**UNKNOWN**。
>
> 本轮**没有**任何“真机已验证”“最终 APK 已验证”“真实存档已加载”的结论。目标程序集只做静态改写与重开校验，
> 未执行任何游戏方法、未构建/安装 APK、未连接设备/模拟器、未创建或读取存档、未 commit/push、未触发 Actions。

---

## 0. 结论摘要

| 问题 | 本轮结论 | 最强证据 | 关键限制 |
| --- | --- | --- | --- |
| 目标程序集身份 | 与预期一致 | SHA-256 `587FF5FF…E047`、9,280,000 B、MVID `573bf1e5…`、无 `ModFramework.Bootstrap`/`Phase21.Prototype.WorldReadyBridge` | 未从设备安装包重新取字节 |
| 开始端能否携带“这次尝试自己的”身份 | **能，但不在 `loadForNewGame`**（CURRENT_STATIC） | 身份对象在 `SaveGame.getLoadEnumerator` 的 `newobj` 产生，在 `LoadAfterPermissionCheck` IL_0154→0155 写入 `currentLoader`；该点同时可读 epoch 与 mode，且不在任何 try/handler 内 | 创建点所在线程（权限回调线程）未记录（NOT_RUN） |
| `loadForNewGame(true)` 能否作为开始边 | **不能**（CURRENT_STATIC，比 P.2 §11 的“仍需运行时确认”更强） | 其唯一调用者是 `SaveGame/<>c::<getLoadEnumerator>b__123_2`：`<>c` **缓存单例**上的实例方法，7 条 IL，唯一状态读取是全局 `Game1.game1`，**不持有 `d__123`**；两个 Task 闭包（`<>8__2`/`<>c`）都不回指枚举器 | — |
| A 的延迟任务是否会借 L_B 与新 epoch 为 B 建立/覆盖 pending | **P.2 接线会，P.3 接线不会** | 最小复现 `legacy_start_edge_would_steal_attempt`（HOST_MODEL）；闭合用例 `stale_load_entry_cannot_claim_new_attempt`、`synthetic_stale_entry_does_not_steal`（SYNTHETIC_EXEC） | 真实帧序列未测（NOT_RUN） |
| 该交错能否被“返回标题 / 任务取消 / 异常”挡住 | **不能**（CURRENT_STATIC） | `SaveGame.CancelToTitle` 全模块只有一个写点（reset 里置 **false**）；reset 只清字段不撤销已派发的 Task；`<>c__DisplayClass123_1` 无 `CancellationToken` 字段，`StartTask` 结果被 `pop` | 外部（Mod/反射）改公开静态字段的可能未评估 |
| 成功端哪一处能证明“装载成功” | `MoveNext` 尾部 `set_game_mode(3)`（IL_1848–1849）之后、枚举器返回前（CURRENT_STATIC） | 该翻转在**枚举器自己的 `MoveNext` 内**、同一调用帧内完成；之后到钩子采样只有 `stloc/ldloc` | 真实帧序列未测（NOT_RUN） |
| 哪一处才足以判断“世界就绪” | 同一推进点 + `hasLoadedGame`（唯一写入者是 `loadForNewGame` 与 reset） | `hasLoadedGame` 全模块 2 个写点；`gameMode==3` 在同一帧被钩子读到即说明本对象已完成翻转 | 其它 mode 写入点（属性 setter 17 处 + 方法 12 处）都在别的调用帧 |
| `OnDayStarted` 能否作为读档完成边 | **不能**（CURRENT_STATIC；P.2 的“tick 归零 → 下一帧触发”本轮**核实为真**） | 成功翻转 IL_1849 用的是 `set_game_mode`（**属性 setter，会归零 `gameModeTicks`**）→ 下一帧 tick=1 → `OnDayStarted` 确实触发；但它无参数、无调用者上下文，且同一事件还有第二个调用者 | 触发时机晚一帧、身份已不可达；真实帧序列 NOT_RUN |
| mode 7 分支 | **已知漏失**（CURRENT_STATIC） | `UpdateTitleScreen` IL_0010–0013：mode 7 时 `currentLoader.MoveNext()` 结果被 `pop` 丢弃，且不经装载驱动 | 读档终结是否可能落在 mode 7 = UNKNOWN |
| 新游戏 | **UNKNOWN（本轮改为“不注册”而不是“未证注册”）** | P.3 起 `loadForNewGame(false)` 只记录，不建立 pending；`generateNewFarm` 占用同一 `currentLoader` 槽位 | 新游戏归属需独立一轮 |
| 原型链路 | **PASS** | 本机 **97/97**（P.2 基线 83/83 → +14，0 删除，0 跳过）；含 9 项模型 + 5 项合成（写盘→重载→真实执行） | 不代表真实游戏 |
| 目标程序集静态改写 | **PASS（仅静态）** | 5 个方法变化（恰为预期）、7 个 bridge 方法新增、0 删除、AssemblyRef 不变、输入哈希不变、重开校验 PASS、重复注入被拒 | 未执行、未构建 APK、未从最终 APK 反验 |
| 设备准备度 | **HOLD** | 创建/推进/完成三点的真实 managed thread、真实帧序列、reset 覆盖全部 NOT_RUN；诊断方案本轮只设计 | 不是“全绿所以 GO”，也不是 NO-GO |

**本轮修复的核心缺陷（P.2 遗留）**：P.2 把“开始边”留在 `loadForNewGame(bool)` 入口，并在那里读取全局 `currentLoader`。
该入口运行在**没有因果链**的工作线程上，因此延迟执行的 A 会读到 reset 之后的新 epoch 与 B 的 `currentLoader`，
从而为 B 建立或覆盖 pending。P.3 把开始边前移到**枚举器创建点**（`getLoadEnumerator` 返回值即将写入 `currentLoader` 的那条边），
身份与代次在同一线程、同一时刻绑定；`loadForNewGame` 降级为**非拥有**观察，只能记录，永远不能建立/覆盖/消费/取消 pending。

---

## 1. 开工基线

### 1.1 Git / 远端（本轮实测，CURRENT_STATIC）

| 查询 | 实际结果 |
| --- | --- |
| `git rev-parse HEAD` | `4534fc99d1390e26ccea74d630c5fd50736cdd10` |
| `git rev-parse origin/main` | 同上（与预期一致） |
| `git for-each-ref refs/remotes refs/heads` | `refs/heads/main`、`refs/remotes/origin/main`、`refs/remotes/origin/HEAD` 三点同 SHA |
| `git status --porcelain`（开工） | 空（无已跟踪/未跟踪改动） |
| 历史 | `ce4c432`（P.2 基线）、`d76f20e`（P.1 基线） |

远端 main 与本地一致仅说明**查询时**没有比 `4534fc9` 更新的提交；本轮未 `fetch`（未联网写操作依赖）。

### 1.2 HEAD 对应的 Actions run（`gh` 未安装，用 GitHub REST 核验）

| 项 | 值 |
| --- | --- |
| Run | **`45672512203`**（`run_number` 20，`event=push`，`display_title=2026-11-26 02:10:00`） |
| `head_sha` | `4534fc99d1390e26ccea74d630c5fd50736cdd10`（与本地 HEAD 一致） |
| **查询时状态** | **`status=in_progress`、`conclusion=null`**（`created_at`/`run_started_at` = 2026-11-25T18:11:09Z） |
| Jobs（`total_count=3`） | `offline checks …` = **completed / success**（18:11:12Z→18:11:28Z）<br>`dotnet build + loader host tests` = **completed / success**（18:11:12Z→18:12:40Z）<br>`powershell gates …` = **in_progress**（18:11:12Z 起，无 `completed_at`） |
| 历史 run（仅作历史） | `45447375735`（`head_sha=ce4c432…`）= `completed / success` |

> 查询时 run 仍在运行，照实记录为 **in_progress**，不作“CI 通过”的依据。
> 本轮改动**未 commit / 未 push / 未触发 Actions**，因此**本轮改动的远端 CI = NOT_RUN**。

### 1.3 CI 对 PrototypeTests 的实际覆盖：**0**（独立缺口，本轮按授权不改工作流）

`grep -n 'PrototypeTests|prototype' .github/workflows/ci.yml` → 无匹配。
工作流只有 `offline-checks`、`dotnet-tools`、`powershell-gates` 三个 job；`dotnet-tools` 里
`local-only suites (not configured on this runner)` 在本 runner 上是空操作，并不执行 PrototypeTests。
因此本轮 97 项原型检查是**本机专属**结果，报告不得写成“CI 通过”。

### 1.4 基线测试（改动前，同一 HEAD）

```text
dotnet run --project Tools/tests/PrototypeTests/Phase21.PrototypeTests.csproj -c Release
  -> 83 checks, 83 passed, 0 failed      exit 0
```

与预期 83/83 一致；其中 P.1/P.2 的负向对照（`late_completion_reads_b_loader`、
`pending_only_misattributes_b`、`synthetic_late_completion_reads_b_loader` 等）全部保留并通过。
系统只有 .NET 8 运行时，全部命令使用 `.tmp/dotnet-sdk/dotnet/dotnet.exe`（SDK 8.0.404）。

### 1.5 历史资料的使用边界

`docs/audit/phase2.1-p2-attempt-ownership-report.md`、`docs/audit/phase2.1-p1-reset-epoch-report.md`、
`docs/architecture/phase2.1-save-lifecycle-feasibility.md` 作为 **HISTORICAL** 读取。
P.1 的 65 项计数、P.2 的 83 项计数、以及 `.tmp/phase2.1-p2/` 的产物都**不作为本轮通过依据**；
本轮从原始资料（`.tmp/phase2.1/blob-extract/StardewValley.dll`，APK blob 解出）重新拷贝独立副本再改写。
历史会话附件仅用于定位线索，所有结论以本轮对字节的重新观察为准。

### 1.6 被保护内容（未改动）

`Mod/`、`Tools/ModInjector/`、`Tools/build_modded_apk.py`、`verify_modded_apk.py`、Gradle、部署脚本、
`.github/workflows/`、`final/`、`README.md`、`AGENTS.md`、生产 Loader 与公开 API（仍 1.1.0；`AssemblyVersion` 仍 1.0.0.0）。
`SaveLoaded`/`DayStarted` **未公开**。没有生成 APK、没有 ADB、没有设备、没有存档。

---

## 2. 目标程序集身份（CURRENT_STATIC）

| 维度 | 值 |
| --- | --- |
| APK 显示版本 / versionCode | `1.6.15.0` / `239` |
| 托管程序集 | `StardewValley 1.6.15.24354` |
| 字节数 / SHA-256 | 9,280,000 B / `587FF5FF9C258679004977BBEEC359ABA86649BD3E4860DDCAA428DF8F07E047` |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` |
| 注入状态 | 原始：`ModFramework.Bootstrap` 不存在、`Phase21.Prototype.WorldReadyBridge` 不存在 |
| 本轮输入 | `.tmp/phase2.1-p3/target/StardewValley.p3.dll`（从 blob 解出字节的独立副本；改写前后哈希均核对） |

身份用两条独立路径核对：`Get-FileHash`（9,280,000 B / 哈希一致）与 Cecil 读取时的
`ARTIFACT StardewValley 1.6.15.24354 mvid=573bf1e5-3245-46d6-925a-1c3713afb677` 行。
身份与预期完全一致，因此本轮**允许**进行目标程序集静态改写；全部读写只发生在 `.tmp/phase2.1-p3/`（gitignore）。

---

## 3. 身份链 A：开始端（CURRENT_STATIC）

### 3.1 逐点标注：方法签名 / IL 位置 / 闭包字段 / 赋值 / 线程边界

```text
(1) SaveGame.Load(string file, bool emergency, bool backup)            [22 IL, 0 EH]
      写 SaveGame.tempFilename / tempLoadEmergencySave / tempLoadBackupSave
      ldftn SaveGame::LoadAfterPermissionCheck
      → MainActivity.PromptForPermissionsIfNecessary(Action)
      线程：调用者线程（标题菜单交互线程）→ 权限回调线程（UNKNOWN，见 §3.6）

(2) SaveGame.LoadAfterPermissionCheck()                                [157 IL, 2 EH]
      IL_0037–0038  ldc.i4.6 ; call Game1::set_gameMode(byte)   ; 属性 setter：mode=6，gameModeTicks=0
      IL_0046       brfalse IL_0151                             ; 普通读档（非紧急/非备份）直达创建点
      IL_0050/0065/0144  Ret                                    ; 三条早退：设置关闭 / 索引缺失 / 异常
      IL_0151–0155  ldloc_0 ; ldloc_1 ; ldloc_2 ; call SaveGame::getLoadEnumerator(string,bool,bool)
      IL_0155       stsfld Game1::currentLoader                 ; ★ 身份对象写入全局字段
      IL_0156       Ret
      EH：Catch try=[IL_0073..IL_0129) handler=[IL_0129..IL_0146)；
          Finally try=[IL_0073..IL_0146) handler=[IL_0146..IL_0151)
      → IL_0151 起**不在任何 try/handler 内**，创建点是未受保护区域（注入安全前提）

(3) SaveGame.getLoadEnumerator(string,bool,bool)                        [12 IL, 0 EH]
      IL_0000–0001  ldc.i4.0 ; newobj SaveGame/<getLoadEnumerator>d__123::.ctor(int)
      IL_0002–0010  dup ; 写 file / loadEmergencySave / loadBackupSave
      IL_0011       Ret
      → **每次尝试一个新的状态机对象**（唯一“每次尝试一个”的运行时载体）

(4) SaveGame/<getLoadEnumerator>d__123                                  [MoveNext 1994 IL, 17 EH, 71 locals]
      字段：<>1__state, <>2__current, file, loadEmergencySave, loadBackupSave,
            <>8__1(DisplayClass123_0), <>8__2(DisplayClass123_1),
            <stopwatch>5__2, <readSaveTask>5__3
      state 2（IL_0065–0105）：
            IL_0068–0083  newobj DisplayClass123_1 → 字段 <>8__2；CS$<>8__locals1 = <>8__1
            IL_0084–0090  Task(new Action(<>8__2, DisplayClass123_1::<getLoadEnumerator>b__1))
                          → 存入**枚举器自己的字段** <readSaveTask>5__3
            IL_0095       Game1.hooks.StartTask(task, "Load_ReadSave")（结果 pop）
            IL_0098–0105  <>2__current = 15 ; state = 2 ; return true      ; yield 15
      IL_0109–0120  while (!IsCanceled && !IsCompleted && !IsFaulted) → yield 15
      IL_0121–0131  if (SaveGame.loaded == null) { set_game_mode(9); debugOutput(parseText(error)); return false; }
                    → **失败边：mode 9（selectGameScreen）**
      IL_0150–0157  yield 19 ; state 3
      IL_0230–0231  本地联机分支：直接 Game1.game1.loadForNewGame(true)（游戏循环线程）
      IL_0233–0250  否则：Task(new Action(<>c.<>9, <>c::<getLoadEnumerator>b__123_2))
                    → 存入 <readSaveTask>5__3；StartTask(task, "Load_LoadForNewGame")（结果 pop）
      IL_0252–0274  yield 24 直到任务完成
      IL_0275–0288  if (task.IsFaulted) { LogError("loadNewGameTask failed with an exception."); throw; }
      IL_1848–1849  ldc.i4.3 ; call Game1::set_game_mode(byte)         ; ★ 成功交接（mode 6→3，ticks 归零）
      IL_1850–1980  AddNPCs / AddModNPCs / RefreshQuestOfTheDay / fixProblems(自有 catch) /
                    ForEachBuilding / UpdateHorseOwnership / UpdateFarmPerfection / doMorningStuff /
                    achievements / netWorldState.UpdateFromGame1 / 日志 /
                    if (SaveGame.CancelToTitle) ExitToTitle(null)（IL_1910–1913）/
                    SaveGame.IsProcessing = false（IL_1914–1915）/ 两处 catch / 玩家与菜单修正
      IL_1981–1988  <>2__current = 100 ; state = 25 ; return true       ; yield 100
      IL_1989–1993  state = -1 ; return false                            ; state 25 的续跑（走不到）

(5) SaveGame/<>c::<getLoadEnumerator>b__123_2()                         [7 IL, 0 locals, 0 EH]
      IL_0000–0002  Thread.CurrentThread.CurrentCulture = InvariantCulture
      IL_0003–0005  ldsfld Game1::game1 ; callvirt Game1::loadForNewGame(true)
      IL_0006       Ret
      **<>c 是缓存单例**（static 字段 <>9；<getLoadEnumerator>b__123_2 以 <>9__123_2 缓存 Action）
      **该实例方法不读任何闭包字段、不持有 d__123** → 任务与尝试之间**没有因果链**

(6) Game1.loadForNewGame(bool)                                          [325 IL, 3 finally, 1 ret IL_0324]
      IL_0296–0297  ldc.i4.1 ; stsfld Game1::hasLoadedGame
      **不写 gameMode，不调用 OnDayStarted**
      8 个调用者：LoadNewGameFromCharacterCustomization、<>c::<getLoadEnumerator>b__123_2、MoveNext、
                  Utility/<generateNewFarm>d__340::MoveNext、Intro::tick、FarmhandSlot::Activate、
                  TitleMenu::receiveKeyPress、TitleMenu::createdNewCharacter
                  → 读档与新游戏混用，**不能**当读档专属边

(7) 线程边界
      创建 + 写 currentLoader：LoadAfterPermissionCheck 的线程（权限回调，UNKNOWN）
      loadForNewGame(true)：ThreadPool 工作线程（Task）
      MoveNext（含 mode 6→3）：游戏循环线程（_update → UpdateTitleScreen → 驱动）
```

### 3.2 三个状态必须分开：创建 / 首次有效推进 / 确实进入读档

| 状态 | 观察点 | 能证明什么 | 不能证明什么 |
| --- | --- | --- | --- |
| **枚举器创建** | `getLoadEnumerator` 返回 → `stsfld currentLoader` | 这次读档请求产生了**这个**状态机对象；当时 mode=6、epoch 可读 | 未推进过；文件还没读；可能马上被替换或 reset |
| **首次有效推进** | `UpdateTitleScreenDuringLoadingMode` 的 `MoveNext()` 返回 `true` 且 mode 仍为 6 | 该对象**开始执行**（读档 Task 已派出或仍在等） | 尚未成功；`moved==true` 只说明还有工作 |
| **确实进入读档** | `loadForNewGame(true)` 入口 | 世界构建代码开始跑（在**工作线程**） | **不能**归属到某次尝试（无身份，见 §3.1(5)） |

P.3 的接线选择：**在“创建”建立 pending**，用“首次有效推进”驱动，用“推进到 mode 3”完成；
“确实进入读档”只做非拥有观察。理由：创建点同时满足“原始枚举器在手”“同线程”“可读 epoch/mode”三个条件，
而 `loadForNewGame` 入口三个条件一个都不满足。

### 3.3 反例（本轮重点）：A 的延迟任务读取 L_B 与新 epoch

时间线（P.2 接线，HOST_MODEL 已复现；CURRENT_STATIC 说明为何可达）：

```text
t0  A: LoadAfterPermissionCheck → mode=6 → getLoadEnumerator → currentLoader = L_A
       A 的 Task 派出（闭包 <>c 无身份），工作线程被调度延迟
t1  reset（ResetGameStateOnTitleScreen）：epoch++（0→1）、currentLoader=null、hasLoadedGame=false、mode=0
t2  B: LoadAfterPermissionCheck → mode=6 → getLoadEnumerator → currentLoader = L_B
       B 的 Task 派出
t3  A 的工作线程终于进入 loadForNewGame(true)
       P.2 开始钩子（在 A 的线程上）：
         GetResetEpoch() → **1（新 epoch，看起来完全“当前”）**
         get_gameMode()  → 6
         ldsfld currentLoader → **L_B**
         WorldLoading(1, true, 6, L_B)
       → CoordinatorSink 走 BeginLoad(L_B, 6, 1) → **StartAccepted**
       → 若 B 已有 pending：**SupersededAttempts++，pending 变成 A 的登记**
t4  B 自己的驱动推进到 mode 3：身份 L_B 与 pending.Identity 相等 → **CompletionAccepted**
       → 观察 1 次，但 AttemptId / StartThreadId 属于 **A 的登记**
```

**最早错误转换**：`t3` 的 `WorldReadyCoordinator.BeginLoad(L_B, …)` 被接受。
在它之前没有任何信号把“哪次尝试”与“哪个线程/哪次创建”绑定；`t3` 之后错误已经写入协调器状态，
`t4` 只是把它兑现成一条归属错误的观察。

**为什么 epoch 守卫挡不住**：epoch 只在“读取之后、提交之前”发生 reset 时才过期。
`t3` 的 epoch 是在 reset **之后**读的，因此它拿到的是新值，`ResetEpochChanged` 不触发。
这正是 P.1 的“开始边读 epoch”规则在**延迟线程**上失效的原因：规则假设“读 epoch 的时刻约等于请求发起的时刻”，
而对 `loadForNewGame` 入口，这两个时刻可以相隔整个 reset。

**最小复现（HOST_MODEL，本机实跑）**：

```text
PASS legacy_start_edge_would_steal_attempt      stolen=True, attemptWasA=True, superseded=1, observations=1
```

该用例直接把 P.2 的两次 `BeginLoad(L_B)` 重放出来（第二次代表 A 的延迟入口），断言其**必然**抢走 B 的 pending。
它是负向对照：证明 P.2 接线的缺陷不是理论风险，而是设计使然。

**闭合用例（本机实跑）**：

```text
PASS stale_load_entry_cannot_claim_new_attempt   entry=MatchesPending, pendingStillB=True, superseded=0, observations=1
PASS synthetic_stale_entry_does_not_steal        stillB=True, superseded=0, observations=1, attempt=1/1
```

第二条是**经注入 IL → bridge → 协调器**的真实执行路径（SYNTHETIC_EXEC），不是纯模型。

### 3.4 返回标题 / 任务取消 / 异常能否阻止该交错：都不能（CURRENT_STATIC）

| 机制 | 观察 | 结论 |
| --- | --- | --- |
| 返回标题 | `ResetGameStateOnTitleScreen` 只清 `currentLoader`/`gameMode`/`hasLoadedGame` 并 epoch++；**不会**撤销已派发的 Task | A 的任务照样会跑到 `loadForNewGame` 入口 |
| 任务取消 | 两个 `Task` 都是 `new Task(Action)`（无 `CancellationToken`）；`DisplayClass123_1` 字段只有 `error`/`autoRecovered`/`CS$<>8__locals1`；`StartTask` 返回值被 `pop` | 没有可取消句柄；`IsCanceled` 检查只作用于**已派出**的任务状态，取消源不在模块内 |
| 异常 | `loadForNewGame` 的 fault 在 IL_0275–0288 被 `throw` 抛出，发生在 IL_1849 之前 | 异常本身会跳出 `UpdateTitleScreenDuringLoadingMode`，**我们的推进钩子根本不会被调用**（fail-closed）；但它也不能阻止 A 的入口信号 |
| `SaveGame.CancelToTitle` | 全模块**只有一个写点**：reset IL_0006–0007 `ldc.i4.0; stsfld` → 永远只置 **false** | “绕过 reset 的取消”在本模块内**不可达**；MoveNext 里的 5 处 `CancelToTitle` 读点只能被模块外（Mod/反射）置 true |

### 3.5 修复：把开始边前移到创建点，并把入口降级为非拥有

注入后（写入并重开验证过的实际 IL，见 §8.2）：

```text
IL_0154  call  SaveGame::getLoadEnumerator(string, bool, bool)   ; 原始创建调用
IL_0155  stloc V7                                                 ; ★ 身份落进局部
IL_0156  call  Phase21.Prototype.WorldReadyBridge::GetResetEpoch() ; ★ epoch 第一
IL_0157  call  Game1::get_gameMode()                              ; 当时的 mode（应为 6）
IL_0158  ldloc V7
IL_0159  call  …::LoadEnumeratorCreated(long, int, object)        ; ★ 身份 + epoch + mode 一起提交
IL_0160  ldloc V7
IL_0161  stsfld Game1::currentLoader                              ; 原始写入，位置不变
IL_0162  Ret
```

- 钩子参数来自**局部 V7**（`getLoadEnumerator` 的返回值），不是重读字段；校验器把这一点做成硬断言
  （`LoaderCreationIdentityFromFactory`），并显式拒绝“窗口内出现 `ldsfld currentLoader`”。
- 该点在同一线程、同一时刻读了 epoch 与 mode → **身份与代次一同绑定**。
- `loadForNewGame` 的钩子仍保留（`WorldLoading`，含 epoch/mode/loader 参数），但协调器只做
  `NoteLoadEntry`：**不建立、不覆盖、不消费、不取消** pending，只记录
  `MatchesPending / NoPendingAttempt / UnprovenPending / MismatchedLoader / NewGame` 并计数与入 trace。

### 3.6 未推进 / 替换 / 取消 的收尾规则（本轮实现并有断言）

| 情形 | 规则 | 断言用例 |
| --- | --- | --- |
| 创建后从未推进 | 永远不产生观察；pending 保留直到被替换或 reset 取消 | `creation_without_advance_is_never_observed`（0 观察，reset 后 `CancelledAttempts=1`） |
| 被新创建替换 | 后者获胜（`SupersededAttempts++`），旧身份的任何推进都是外来身份、保留新 pending | `second_creation_supersedes_first` |
| reset 取消 | 入口处丢弃 pending（`CancelledAttempts++`）、epoch++、latch 置位 | `creation_without_advance_is_never_observed`、P.1 的 reset 用例 |
| reset 窗口内创建 | `ResetInProgress` 拒绝且不改任何状态 | `synthetic_mid_reset_window_start_refused_e2e`（SYNTHETIC_EXEC） |
| 创建在 reset 之前、提交在 reset 之后（epoch 过期） | `ResetEpochChanged` 拒绝 | `synthetic_stale_epoch_start_refused_e2e`（SYNTHETIC_EXEC，屏障可控） |
| 创建点自身异常 / 未注册 | 没有 pending，后续推进一律 `NoPendingAttempt` | `load_entry_after_completion_creates_nothing` |
| 新游戏入口 | 只记 `NewGame`，**不动** load pending（新游戏保持 UNKNOWN） | `new_game_entry_does_not_clear_load_pending` |

---

## 4. 身份链 B：成功端（CURRENT_STATIC）

### 4.1 从成功分支到 `OnDayStarted`

```text
SaveGame/<getLoadEnumerator>d__123::MoveNext()
  IL_1848–1849  ldc.i4.3 ; call Game1::set_game_mode(byte)        ; ★ 装载成功：mode 6→3，**并把 gameModeTicks 归零**
  IL_1850–1980  世界修正（NPC/成就/建造物/日志/IsProcessing=false …）
  IL_1981–1988  <>2__current = 100 ; state = 25 ; return true     ; yield 100，本轮 MoveNext 返回 true
Game1.UpdateTitleScreenDuringLoadingMode()
  IL_0014–0015  currentLoader.MoveNext()                          ; 返回 true → IL_0016 brtrue → IL_0031 ret
  （loader **不清空**；成功路径不会走 IL_0017–0018 的 ldnull/stsfld）
Game1.UpdateTitleScreen(GameTime)
  IL_0002–0005  switch (get_gameMode() - 6)                       ; 派发发生在**调用前**，本帧读到 6
  case 0 → UpdateTitleScreenDuringLoadingMode
Game1._update(GameTime)
  IL_1434       UpdateTitleScreen(GameTime)
  IL_1463–1465  if (gameMode == 6) goto IL_1500                    ; 本帧已是 3 → 不跳
  IL_1500–1506  if (gameMode == 3 && gameModeTicks == 1) OnDayStarted()
Game1.OnDayStarted()                                             ; [114 IL, 3 finally, 1 normal ret IL_0113]
```

### 4.2 哪个位置“能证明装载成功”，哪个位置“才足以判断世界就绪”

| 位置 | 证明强度 | 说明 |
| --- | --- | --- |
| `loadForNewGame(true)` 返回 | **不够** | 它只是 Task 闭包体；返回时枚举器还没做 mode 翻转、NPC/成就修正、yield 100；而且它是读档/新游戏混用边 |
| 外层 `MoveNext()==false` | **不等于成功** | 成功时本轮返回 true（state 25）；`false` 只在“mode 非 3 且没有更多工作”时出现，即失败/中止 |
| **`MoveNext` 尾部 `set_game_mode(3)` 之后、返回之前** | **可证明“装载成功交接”** | 翻转由**该枚举器自己**在**自己的调用帧**内完成；这是 `UpdateTitleScreenDuringLoadingMode` 推进点读取 mode 的依据 |
| 同一推进点 + `hasLoadedGame` | **足以判断“世界就绪”** | `hasLoadedGame` 只有两个写者（`loadForNewGame`=true、reset=false）；装载到 mode 3 时它已为 true |
| `OnDayStarted()` | **不能用于归属；但它在读档路径上确实会触发**（见 §4.3） | 触发时机晚一帧、身份已不可达；P.2 把它降级为辅助观察是正确的 |

### 4.3 核实 P.2 的 `gameModeTicks` 结论：`OnDayStarted` 在读档成功路径上**会**触发

P.2 的写法（`set_game_mode(3)`、tick 归零）是**对的**；由于类型里同时存在只差一个字母的
`setGameMode`（大写 `M`）且两者语义不同，本轮按**大小写敏感**逐处复核并把全部站点列出来，
以免后续轮次误引用：

| 方法 | 大小写 | `_gameMode` | `gameModeTicks` |
| --- | --- | --- | --- |
| `Game1.set_gameMode(byte)`（属性 setter，36 IL） | 小写 `g` | IL_0000–0003 早退；IL_0031–0032 写入 | **IL_0033–0034 归零** |
| `Game1.setGameMode(byte)`（普通方法，52 IL） | 大写 `M` | IL_0007–0008 无条件写入 | **不动** |

全模块各点的实际用法（CURRENT_STATIC，逐处核对）：

| 站点 | 调用 | ticks 归零？ |
| --- | --- | --- |
| `SaveGame.LoadAfterPermissionCheck` IL_0037–0038（进入装载 mode 6） | `set_game_mode` | 是 |
| **`SaveGame/<getLoadEnumerator>d__123::MoveNext` IL_1848–1849（成功 mode 3）** | **`set_game_mode`** | **是** |
| `MoveNext` IL_0123–0124（失败 mode 9） | `set_game_mode` | 是 |
| `Game1.ResetGameStateOnTitleScreen` IL_0025–0026（mode 0） | `set_game_mode` | 是 |
| `Game1.UpdateTitleScreen` IL_0028–0029（mode 8 → 3） | `setGameMode` | 否 |
| `Game1.UpdateTitleScreen` IL_0139 / IL_0148–0149（新游戏 mode 6） | `setGameMode` | 否 |
| `Game1.UpdateTitleScreenDuringLoadingMode` IL_0022–0023（失败清理后重申 3） | `setGameMode` | 否 |
| `Game1.ExitToTitle` IL_0013–0014（mode 0） | `setGameMode` | 否 |

因此 P.2 §3.2/§4.3 的描述**成立**：成功翻转把 ticks 归零，下一帧帧首 `_update` IL_0006–0009 加 1 得 1，
于是 `_update` IL_1500–1506 的 `OnDayStarted()` **在翻转后的下一帧触发**。

**这使 P.2 的降级理由更硬，而不是更软**：这条边确实会在读档成功后不久触发，看上去“就是完成事件”；
但它没有任何尝试身份（无参数、无调用者上下文），而且同一个 `OnDayStarted()` 还有第二个调用者
（`set_activeClickableMenu`，菜单关闭/跨日）。所以它能被误当成完成边，却无法回答“哪一次尝试”，
身份模式必须拒绝它并保留 pending（`day_started_edge_cannot_complete_load`）。

**顺带记录的差异（不扩大结论）**：其余把 mode 写成 3 的站点用的是**不归零**的 `setGameMode`（保存完成、标题路径），
所以它们**不会**制造 `gameModeTicks==1`；这也解释了为什么在实践中 `OnDayStarted` 与“读档刚成功”高度相关——
相关不等于归属，正是本轮要拆开的东西。新游戏路径（IL_0149 `setGameMode(6)`）同样不归零 ticks，
其首次可玩帧因此不满足 `ticks==1`；新游戏保持 UNKNOWN，本轮不引申。

### 4.4 `gameMode` / `hasLoadedGame` 的写入者，以及“MoveNext 返回到钩子采样”之间的窗口

| 成员 | 写入点（全模块扫描） | 备注 |
| --- | --- | --- |
| `Game1._gameMode` | 仅两处：`set_gameMode(byte)`（属性 setter）、`setGameMode(byte)`（方法） | 属性 setter 有 `if (_gameMode == value) return;` 早退；方法无早退 |
| `Game1.hasLoadedGame` | `loadForNewGame`（true，IL_0296–0297）、`ResetGameStateOnTitleScreen`（false，IL_0193） | 只有两个写者 |
| `Game1.currentLoader` | 11 处引用：`LoadAfterPermissionCheck`（写 E）、`UpdateTitleScreen`（写 `generateNewFarm`）、`UpdateTitleScreenDuringLoadingMode`（写 null）、`ResetGameStateOnTitleScreen`（写 null）、`emergencyBackup`（读/写） | `getLoadEnumerator` 的第二个调用点 `HandleLoadError` **不写**该字段，且本构建内 **0 个调用者**（死代码） |
| `setGameMode`（方法，不归零 ticks）的 12 个调用点 | `LoadNewGameFromCharacterCustomization`、`ExitToTitle`、`AfterLoadContent`、`UpdateTitleScreen`×3、`UpdateTitleScreenDuringLoadingMode`、`onFadeToBlackComplete`、`DebugCommands/DefaultHandlers::GameMode`、`TitleMenu::receiveKeyPress`、`TitleMenu::update`、`TitleMenu::createdNewCharacter` | 其中 `UpdateTitleScreen` 的 mode 8 分支会 `setGameMode(3)`；`DebugCommands` 可设**任意** mode |

**串行关系（能证明）**：
推进点插入的位置在 `MoveNext()` 返回之后、**同一方法、同一线程、同一调用帧**内，
注入序列只有 `stloc moved / ldloc loader / call get_gameMode / ldloc moved / ldsfld hasLoadedGame / call LoadAdvanced`
这两三条无副作用指令，中间没有 `yield`、没有调用、没有重入点。因此：

- 钩子读到的 mode 必然是**这次 `MoveNext` 返回时**的 mode；
- 该 mode 若为 3，只可能由**本次调用内的** `set_game_mode(3)`（IL_1849）产生——因为其它 mode 写入点
  （属性 setter 17 处 + 方法 12 处）都在别的调用帧（标题/保存/重置/调试），不可能插进这段指令序列；
- **仍需运行时验证的假设**：另一个线程是否会在这一两个指令的窗口内写 `_gameMode`。
  `Game1.set_game_mode` 的调用点里包含 **`Program::handleException`**（IL_0005–0006 `ldc.i4.s 11; call set_game_mode`
  → `errorLogMode`），它由未处理异常处理器调用，**可以在任意线程执行**。它写的是 11 而不是 3，
  所以**不能伪造成功**，但可以把被采样的 mode 从“进行中”改成“失败/标题”。这是 HOST_MODEL 之外的 NOT_RUN。

**为什么“前后两次模式读取”不够**：只观察 `gameMode` 从 6 变 3 只能说明“变化发生了”，
不能说明“是谁改的”。P.2/P.3 的接线之所以成立，是因为**身份（被推进的那个对象）与 mode 读数在同一次调用里一起取出**，
且该对象的 `MoveNext` 是同一线程上唯一被调用的代码；只读全局模式无法排除外部写入。

### 4.5 mode 7 / 异常退出 / 取消 / 普通跨日 / 同档重载

| 路径 | 观察（CURRENT_STATIC） | P.3 行为 |
| --- | --- | --- |
| **mode 7（saveMode）** | `UpdateTitleScreen` IL_0010–0013：`ldsfld currentLoader; callvirt MoveNext; pop; ret`，**结果丢弃**，且不经 `UpdateTitleScreenDuringLoadingMode` | **已知漏失**：若读档枚举器被 mode 7 驱动，成功翻转不可观察，pending 只能等 reset/替换。未证明读档终结会落在 mode 7 → UNKNOWN |
| **异常退出** | `loadForNewGame` fault → IL_0288 `throw`，发生在 mode 翻转前；异常跳出驱动方法，**推进钩子不执行** | 不产生观察、不消费 pending（fail-closed）。下一帧若 mode 仍 6，驱动会再次 `MoveNext`（枚举器仍在 state 5）并再次抛出——游戏自身行为，非本原型引入 |
| **`CancelToTitle`（绕过 reset 的取消）** | 唯一写点 reset 置 **false**；MoveNext 的 5 个读点因此在本模块内恒为 false | 本模块内**不可达**；若外部置 true，`ExitToTitle` 在 IL_1913 同步 `setGameMode(0)`，本轮新增分类把“moved=true 且 mode=0”判为 **Cancelled（消费不观察）**，而不是旧代码的“仍在装载” |
| **普通跨日** | `OnDayStarted` 的另一个调用者是 `set_activeClickableMenu`（模式/菜单关闭），与首次装载无关 | 明确不当作装载完成；身份模式下该边被拒并保留 pending（`day_started_edge_cannot_complete_load`） |
| **同档重载** | 每次 `LoadAfterPermissionCheck` 都新建枚举器 → 新身份 | 覆盖：`reload_plus_one_cross_day_zero`（观察只 +1） |
| **新游戏** | `UpdateTitleScreen` IL_0146–0149：`currentLoader = generateNewFarm(IsClient); setGameMode(6)`（注意用的是 `setGameMode`，不归零 ticks） | 不注册（只记 `NewGame`），保持 UNKNOWN |

### 4.6 本轮对 mode 判定的修正

`Advance` 的分类从“`!moved && mode != 3` 才失败，其余非 3 都算进行中”改为三段：

1. `!moved && mode != 3` → 终结且不观察（mode 0 → `TitleMode`；否则 `LoadFailed`，**实际失败边是 mode 9**，
   P.2 文档写的“仍是 mode 6”不准确）；
2. `moved && mode == 0` → **Cancelled**（`ExitToTitle` 在 MoveNext 内、mode 翻转之后），消费不观察；
3. `mode != 3` → `InProgress`（真装载中）。

第 2 段是新增的：旧代码会把“已取消”误判为“仍在装载”，pending 永久悬挂直到 reset/替换。

---

## 5. 可达性证据与反例汇总

| 项 | 结论 | 证据类别 | 用例/来源 |
| --- | --- | --- | --- |
| 创建点存在且唯一 | 是 | CURRENT_STATIC | `FindLoaderCreationCall`：`call getLoadEnumerator; stsfld currentLoader` 恰 1 处；全方法写该字段 1 次 |
| 创建点在未受保护区域 | 是 | CURRENT_STATIC | EH 区间 `[IL_0073..IL_0129)` / `[IL_0073..IL_0146)` / `[IL_0146..IL_0151)`；创建在 IL_0154 |
| `loadForNewGame` 任务无身份 | 是 | CURRENT_STATIC | `<>c::<getLoadEnumerator>b__123_2` 7 IL、0 局部、仅读 `Game1.game1`；`<>c` 无枚举器字段 |
| 读档 Task 闭包也无身份 | 是 | CURRENT_STATIC | `<>8__2` 字段 = `error/autoRecovered/CS$<>8__locals1`，后者 = `file/loadEmergencySave/loadBackupSave` |
| A 的入口信号能借 L_B（P.2） | 是，已复现 | HOST_MODEL | `legacy_start_edge_would_steal_attempt` |
| A 的入口信号不能借 L_B（P.3） | 是 | HOST_MODEL + SYNTHETIC_EXEC | `stale_load_entry_cannot_claim_new_attempt`、`synthetic_stale_entry_does_not_steal` |
| 外来身份的推进不能消费 B | 是 | HOST_MODEL + SYNTHETIC_EXEC | `second_creation_supersedes_first`、`synthetic_advance_foreign_identity_preserves_b` |
| 未观察的成功不会被吞 | 是 | HOST_MODEL | `load_entry_after_completion_creates_nothing`、`advance_moved_under_title_consumes` |
| reset 窗口/epoch 竞态 | 是 | SYNTHETIC_EXEC（屏障） | `synthetic_mid_reset_window_start_refused_e2e`、`synthetic_stale_epoch_start_refused_e2e` |
| 推进/完成线程差异 | 静态推断 | CURRENT_STATIC | 开始在工作线程（Task）、推进在主循环（`_update`）；**实测 NOT_RUN** |

---

## 6. 原型实现（授权范围内）

| 文件 | 变更 |
| --- | --- |
| `Tools/prototype/WorldReady/WorldReadyCoordinator.cs` | 新增 `LoadEntryObservation`、`SignalKind.LoadEntered`、`AdvanceOutcomeKind.Cancelled`、`TitleScreenGameMode`；新增 **`NoteLoadEntry`**（非拥有入口，永不改 pending）与 5 个计数器（`AttemptsCreated`/`LoadEntriesObserved`/`LoadEntryIdentityMatches`/`LoadEntryIdentityMismatches`/`LoadEntryWithoutPending`/`NewGameEntriesObserved`）；`BeginLoad` 文档改为**创建点**语义；`Advance` 新增“moved 且 mode 0 → Cancelled”分类；订正 `LoadFailed` 注释（失败边是 mode 9） |
| `Tools/prototype/WorldReady/PrototypeBridge.cs` | `IWorldReadySink.OnLoadEnumeratorCreated(long, int, object, int)`；`PrototypeBridge.LoadEnumeratorCreated(long, int, object)`；`CoordinatorSink.OnLoadEnumeratorCreated → BeginLoad`；`CoordinatorSink.OnWorldLoading → NoteLoadEntry`（不再 BeginLoad/TryBeginAttempt） |
| `Tools/prototype/WorldReady/PrototypeInjector.cs` | 新目标 `StardewValley.SaveGame::LoadAfterPermissionCheck`、新钩子 `LoadEnumeratorCreated(long,int,object)`；`FindLoaderCreationCall`（同时识别原始与已注入两种形状，保证重开可校验）；`InjectLoaderCreation`（局部化 + epoch/mode/identity 顺序）；`VerifyLoaderCreation`（4 个布尔证据 + 写入唯一性）；重复注入检测纳入新边 |
| `Tools/prototype/WorldReady/TargetBridgeEmitter.cs` | 目标桥新增 `LoadEnumeratorCreatedCalls`、`LastCreationResetEpoch`、`LastCreationGameMode`、`LastCreationLoaderWasNull` 与 `LoadEnumeratorCreated` 方法 |
| `Tools/tests/PrototypeTests/SyntheticFixture.cs` | 新增 `Synthetic.SaveGame` 形状（`getLoadEnumerator` 经测试装配的 `SyntheticGate.CreateLoader` 返回真实对象 + `LoadAfterPermissionCheck` 的 `set mode; call; stsfld` 尾部）；新增创建钩子引用；`NextLoaderScript` 可脚本化 |
| `Tools/tests/PrototypeTests/Program.cs` | 新增 9 项模型用例 + 5 项合成用例；既有 P.1/P.2 合成用例改为经**创建边**装配尝试（详见 §7.2）；`PausingSink` 支持在创建边暂停 |
| `Tools/tests/PrototypeTests/TargetCheck.cs` | 期望变化方法 4 → **5**（加 `SaveGame::LoadAfterPermissionCheck`）；bridge 方法 6 → **7**；新增 5 条创建边 EVIDENCE 与 4 个 bridge 字段 |

### 6.1 保留的 P.1/P.2 语义

- reset epoch：`GetResetEpoch()` 仍是开始边**第一条读取**（创建边同样断言 `LoaderCreationEpochFirst`）。
- reset 窗口：`TryBeginReset`/`TryCompleteReset` 的入口/正常 ret 钩子、latch、异常退出自愈语义未改。
- 负向对照全部保留：`pending_only_misattributes_b`、`late_completion_reads_b_loader`、
  `synthetic_late_completion_reads_b_loader`（后者仍证明**遗留完成边**按引用相等接受，故身份模式是必需的）。
- `CorrelationMode.LoaderIdentity` / `PendingOnly` 与旧 `Complete(...)` 路径不变。

### 6.2 有意的语义变更（必须明示）

1. `loadForNewGame(bool)` **不再注册尝试**。任何依赖“在该入口 BeginLoad/TryBeginAttempt”的测试都必须改用创建边；
   本轮已逐一改写（见 §7.2）。新游戏因此从“未证身份的 pending”变成“完全没有 pending”——UNKNOWN 更彻底。
2. P.2 的合成负向对照 `synthetic_late_completion_reads_b_loader` 仍可复现**遗留完成边**的误接受，
   但**无法**再通过开始边复现“A 抢走 B 的 pending”；该缺陷现在由模型负向对照
   `legacy_start_edge_would_steal_attempt` 固定，并由 `synthetic_stale_entry_does_not_steal` 闭合。

---

## 7. 验收矩阵（本轮实跑）

### 7.1 新增 14 项

层级：**M** = HOST_MODEL，**S** = SYNTHETIC_EXEC（写盘/重载/真实执行）。

| 测试 | 层 | 断言 | 结果 |
| --- | --- | --- | --- |
| `creation_edge_binds_identity` | M | 创建→入口→推进：1 次观察、`AttemptOwnershipProven`、无 Superseded | PASS |
| `stale_load_entry_cannot_claim_new_attempt` | M | A 的延迟入口只记录；B 的 pending 保持、观察 0→1、`LoadEntryIdentityMatches=1` | PASS |
| `legacy_start_edge_would_steal_attempt` | M（负向对照） | 重放 P.2 的 `BeginLoad(L_B)`：`stolen=True`、`attemptWasA=True`、观察 1 | PASS |
| `load_entry_after_completion_creates_nothing` | M | 完成后入口 → `NoPendingAttempt`，不建 pending，观察仍 1 | PASS |
| `creation_without_advance_is_never_observed` | M | 未推进 0 观察；reset 后 `CancelledAttempts=1` | PASS |
| `second_creation_supersedes_first` | M | `SupersededAttempts=1`；旧身份的推进 `ForeignAttemptIdentity` 且保留 | PASS |
| `advance_moved_under_title_consumes` | M | moved=true + mode 0 → `Cancelled`/`TitleMode`，消费不观察 | PASS |
| `advance_false_under_select_game_fails` | M | moved=false + mode 9 → `Failed`/`LoadFailed`，消费不观察 | PASS |
| `new_game_entry_does_not_clear_load_pending` | M | `NewGame` 只记录，load pending 保留并最终完成 1 次 | PASS |
| `synthetic_creation_shape` | S | 1 处调用、身份来自工厂返回值、写入未变、epoch 第一、未受保护、maxstack 2→3 | PASS |
| `synthetic_creation_identity_is_factory_result` | S | 钩子收到的对象 `ReferenceEquals` 工厂返回且等于写入字段的对象 | PASS |
| `synthetic_creation_completes_once` | S | 创建→入口→推进：进行中不完成、成功后 1 次、重复不再增 | PASS |
| `synthetic_stale_entry_does_not_steal` | S | A 的延迟入口不抢 B；B 完成 1 次、`AttemptId` 为 B | PASS |
| `synthetic_creation_edge_missing_rejected` | S | 缺创建边 → `Resolve` 失败关闭 | PASS |

### 7.2 既有用例的改写（因 §6.2 语义变更）

| 用例 | 改动 | 结果 |
| --- | --- | --- |
| `synthetic_end_to_end_one_observation` | 改为创建边装配 + 推进完成，断言 `AttemptOwnershipProven` | PASS |
| `synthetic_advance_completes_once` / `..._foreign_identity_preserves_b` / `..._wrong_thread_preserves` | 用创建边装配尝试（不再手工设 `currentLoader` + `loadForNewGame`） | PASS |
| `synthetic_stale_completion_preserves_b` | 用创建边；保留**遗留完成边**的 `StaleLoaderEvidence` 语义并断言 B 最终 1 次观察 | PASS |
| `synthetic_reset_hooks_execute_once` / `synthetic_epoch_read_before_mode` | 用创建边装配（`CancelledAttempts=1`、epoch=1 观察） | PASS |
| `synthetic_stale_epoch_start_refused_e2e` | 暂停点从入口移到**创建边**（`PausingSink.PauseOnCreation`），信号序列不变 | PASS |
| `synthetic_mid_reset_window_start_refused_e2e` | 窗口内改用创建边，序列不变 | PASS |
| `synthetic_reset_exception_leaves_latch` | 三处 start 改为创建边 | PASS |
| `synthetic_late_completion_reads_b_loader` | 保留为**遗留完成边**负向对照（`basis=LoaderReference`、`ownership=False`） | PASS |

### 7.3 命令、退出码与计数

| 命令（仓库根） | 工具 | 退出码 | 结果 | 日志 |
| --- | --- | ---: | --- | --- |
| `dotnet run --project Tools/tests/PrototypeTests/... -c Release`（**改动前**） | .NET SDK 8.0.404 / Win10.0.26100 | 0 | **83/83** | `.tmp/phase2.1-p3/logs/baseline-prototype-tests.log` |
| 同上（**改动后**） | 同上 | 0 | **97/97**（+14，删除 0，跳过 0） | `.tmp/phase2.1-p3/logs/host-tests-p3.log` |
| `dotnet ...Phase21.PrototypeTests.dll --target <副本> <out> <refs>` | 同上 | 0 | 结构 PASS；5 方法变化、7 方法新增 | `logs/target-check.log` |
| `dotnet ...Phase21.PrototypeTests.dll --verify-target <patched> <refs>` | 同上 | 0 | 重开结构 PASS | `logs/target-verify.log` |
| 同上 `--target <patched> …`（重复注入） | 同上 | **1** | 按预期拒绝（`input already carries the prototype bridge`），**未写出文件** | `logs/target-double.log` |
| `dotnet build Tools/tests/InjectorTests/... -c Release` + 运行 | 同上 | 0 / 0 | **29/29** | `logs/injector-tests.log` |
| `python Tools/tests/test_loader.py --dotnet ... --refs .tmp/phase0.5/refs` | Python 3.12.10 | 0 | **53/53**；host summary **552/552**；P114 0 失败；Phase 2.0 0 失败 | `logs/loader-host.log` |
| `python Tools/tests/test_ci_offline.py` | 同上 | 0 | **13/13** | `logs/ci-offline.log` |
| `python Tools/tests/test_modded_build.py` | 同上 | 0 | **55/55** | `logs/test_modded_build.log` |
| `python Tools/tests/test_apk_admission.py` | 同上 | 0 | **16/16** | `logs/test_apk_admission.log` |
| `python Tools/tests/test_p114_probe.py` | 同上 | 0 | **46/46** | `logs/test_p114_probe.log` |
| `git diff --check` | git | 0 | 无输出 | — |

**未跑（明确 NOT_RUN）**：ADB / 设备 / 模拟器；APK 构建（`assembleDebug`/`assembleLoaderOnlyDebug`/`assembleModdedDebug`）；
`verify_modded_apk.py`；安装/部署；读写 Saves；进入或创建存档；PowerShell 部署门禁（本轮未改这些路径且属部署轮次）；
GitHub Actions；commit / push。

---

## 8. 目标程序集静态改写（仅静态）

### 8.1 每个钩子：签名、数量、值来源、分支/异常边界

| 边 | 方法 | 签名 | 站点数 | 值来源 | 分支重定向 / EH |
| --- | --- | --- | ---: | --- | --- |
| reset 入口 | `ResetGameStateOnTitleScreen` | `instance void()` | 1 | 无参数 | 入口插入，原 IL_0000 后移；入口不在 try/handler |
| reset 正常退出 | 同上 | — | 1 | 无参数 | 唯一正常 ret（原 IL_0542）；handler ret 0；注入后**没有边**直指正常 ret（`edges_to_normal_returns=0`）；原 `try=[IL_0299..IL_0310)` / `handler=[IL_0310..IL_0314)` 按半开规则随钩子前移 |
| 开始（非拥有） | `loadForNewGame(bool)` | `instance void(bool)` | 1 | `GetResetEpoch` → `ldarg.0` → `get_gameMode` → `ldsfld currentLoader` → `WorldLoading`（epoch 第一） | 无 switch；3 个 finally 未触及；maxstack 5→5 |
| 完成（P.1 旧边，保留但降级） | `OnDayStarted()` | `static void()` | 1 | `get_gameMode` / `hasLoadedGame` / `currentLoader` | 唯一正常 ret（IL_0113）；handler ret 0；无入边；maxstack 6→6 |
| 完成（P.2 边） | `UpdateTitleScreenDuringLoadingMode()` | `instance void()` | 1 | 局部 V0（`MoveNext` 的对象）+ `get_gameMode` + 局部 moved + `hasLoadedGame` | 两个原守卫仍指向同一 ret（`guards=True`）；站点未受保护；2 个 ret；maxstack 2→4；0 个 EH |
| **创建（P.3 新边）** | `SaveGame.LoadAfterPermissionCheck()` | `static void()` | **1** | 局部 V7（`getLoadEnumerator` 返回值）+ `GetResetEpoch` + `get_gameMode` | 原 `call; stsfld` 保持一对一；`stsfld currentLoader` 仍**唯一**（`verify_creation_write_intact=True`）；站点未受保护；maxstack 4→4；EH 两个区间**未移动**（`ins` 157→163，`locals` 7→8） |

### 8.2 写入并重开后验证的实际 IL（关键片段）

```text
EVIDENCE verify_creation_calls          = 1
EVIDENCE verify_creation_from_factory   = True
EVIDENCE verify_creation_write_intact   = True
EVIDENCE verify_creation_epoch_first    = True
EVIDENCE verify_creation_unprotected    = True
EVIDENCE verify_advance_calls           = 1
EVIDENCE verify_advance_identity_local  = True
EVIDENCE verify_advance_guards_intact   = True
EVIDENCE verify_advance_site_unprotected = True
EVIDENCE verify_epoch_read_first        = True
```

重开后的 `LoadAfterPermissionCheck`：`ins=163 maxstack=4 locals=8`，`V7:IEnumerator<int>`；
`IL_0144 Leave IL_0162`（原 `Leave_S IL_0156` 随 `WidenShortBranches` 变长型），
`IL_0154 call getLoadEnumerator` … `IL_0161 stsfld currentLoader`，`IL_0162 Ret`，EH 两段与原始完全一致。

### 8.3 输入/输出身份与方法集合

| 项 | 值 |
| --- | --- |
| 输入 | `.tmp/phase2.1-p3/target/StardewValley.p3.dll`，9,280,000 B |
| 输入 SHA-256（前后一致） | `587FF5FF9C258679004977BBEEC359ABA86649BD3E4860DDCAA428DF8F07E047` |
| 输出 | `.tmp/phase2.1-p3/target/StardewValley.p3.patched.dll`，9,262,080 B |
| 输出 SHA-256 | `57A938CE2210660014AF330B5ABB76725842CE7BC4FBC3F4FEFC92B36148FFFF` |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` → `12450305-8e83-4ac2-9501-600d90730db2` |
| AssemblyRef | **未变化**（断言未触发） |
| 变化方法 | **5**：`loadForNewGame(bool)`、`OnDayStarted()`、`ResetGameStateOnTitleScreen()`、`UpdateTitleScreenDuringLoadingMode()`、`SaveGame::LoadAfterPermissionCheck()` |
| 新增方法 | **7**：`ResetBegan`、`ResetCompleted`、`GetResetEpoch`、`WorldLoading`、`DayCompleted`、`LoadAdvanced`、`LoadEnumeratorCreated` |
| 删除方法 | **0** |

> 输出哈希/MVID**不是跨次可复现**：每次注入 `Mvid = Guid.NewGuid()`（P.1 已记录）。报告引用的是本轮最后一次实跑产物；
> 输入哈希前后一致，说明原始副本未被改写。

### 8.4 maxstack：Cecil 归一化 vs 注入抬高（分开看）

**(a) Cecil 重写模块导致的归一化（6 项，非语义改动，P.1/P.2 已有）**：`Child::canTalk` 3→1、
`Program::GetAppDataFolder` 3→1、`DyeMenu::performHoverAction` 4→0、`InventoryMenu::receiveGamePadButton` 4→0、
`InviteCodeDialog::.ctor` 10→4、`ItemGrabMenu::receiveGamePadButton` 5→2。

**(b) 注入故意抬高（1 项，语义必需）**：`UpdateTitleScreenDuringLoadingMode` **2→4**（钩子同时压 4 个值）。

创建边**没有**抬高任何方法：`LoadAfterPermissionCheck` 4→4（最多压 3 个值）。
三个 P.1 钩子也未抬高：reset 4→4、load 5→5、completion 6→6。

### 8.5 拒绝的形状

| 形状 | 结果 |
| --- | --- |
| 重复注入（输入已含 bridge） | **拒绝**，退出码 1，且**未写出**输出文件（`Test-Path` = False） |
| 重复注入（同模块钩子已存在） | 拒绝：`prototype hooks already present … refusing to patch twice`（`synthetic_double_inject_rejected` 覆盖） |
| `LoadAfterPermissionCheck` 缺失 / 非 static / 有参数 | 拒绝（`Resolve` 抛 `expected exactly one static void …`；`synthetic_creation_edge_missing_rejected` 覆盖） |
| 创建边界不唯一（多次写 `currentLoader` 或两个 call+store） | 拒绝（`writes currentLoader N times` / `ambiguous creation boundary`） |
| 创建站点落入 try/handler | 拒绝 |
| `UpdateTitleScreenDuringLoadingMode` 缺失 / 带 EH / 形状不符 | 拒绝（P.2 用例保留） |
| 钩子参数形状不符 | 拒绝（`synthetic_advance_hook_shape_rejected` 等） |
| 输入不是 `StardewValley 1.6.15.24354` 或已含 `ModFramework.Bootstrap` | 拒绝 |

### 8.6 目标桥 vs 主机桥（不可混淆）

- **目标桥**（`Phase21.Prototype.WorldReadyBridge`，写进临时副本）：只做计数与“最后一次参数”记录，
  `EpochCounter` 是**替身**，**不实现**任何拒绝语义、pending、观察或线程权威。
  `verify_creation_from_factory` 等布尔证据来自**静态结构校验**，不是运行时行为。
- **主机桥**（`Phase21.WorldReady.PrototypeBridge` + `WorldReadyCoordinator`）：实现 epoch 比较、latch、
  pending、身份校验、观察与拒绝原因；97 项里的模型与合成结论只属于这一侧。
- 报告不得把目标桥的计数（`LoadEnumeratorCreatedCalls` 等）当作协调器结果；
  临时副本**从未执行**，其 MVID/哈希只证明“写出的字节形状正确”。

---

## 9. 三项判定

### 9.1 端到端归属：**PASS（仅 CURRENT_STATIC + HOST_MODEL + SYNTHETIC_EXEC）**

- 开始端：身份在**创建点**与 epoch、mode 一同绑定；`loadForNewGame` 入口被证明**无因果链**并降级为非拥有。
- 成功端：身份是**被推进的那个对象**（局部传递），成功判据是**该对象自己**完成的 `set_game_mode(3)` + `hasLoadedGame`。
- A/B 反例在模型与合成两层均被关闭；未推进/替换/取消/新游戏都有明确收尾规则与断言。
- **PASS 边界**：不含任何运行时线程或帧序列证据（NOT_RUN）。

### 9.2 原型验证：**PASS**

- 97/97（基线 83/83 → +14，0 删除，0 跳过，exit 0）。
- 负向对照齐全：tokenless 误归属、loader 引用相等误归属（模型 + 合成）、**P.2 开始边抢走 pending**（模型负向对照）。
- 合成侧写出 → 重新加载 → 真实执行；竞态用 `Barrier`/事件与可控暂停点。
- 目标副本：5 方法变化 / 7 方法新增 / 0 删除 / AssemblyRef 不变 / 输入哈希不变 / 重开 PASS / 重复注入被拒。
- **PASS 边界**：仅主机模型 + 合成程序集 + 静态改写；不代表真实游戏，不代表设备。

### 9.3 设备准备度：**HOLD**

按顺序的阻塞项：

1. **创建点线程未知**：`LoadAfterPermissionCheck` 由权限回调驱动，其 managed thread 未记录；
   若它与游戏循环线程不同，`BeginLoad` 的 `StartThreadId` 会记录一个非主线程值（当前语义允许，
   但“创建与推进同一线程”这个更强的假设仍是 UNKNOWN）。
2. **真实帧序列未测**：`set_game_mode(3)`（ticks 归零）→ `yield 100` → 下一帧 `_update`（tick=1）→
   `OnDayStarted()` 这条链是静态推导（§4.3），没有运行时帧记录；`OnDayStarted` 的真实线程也未测。
3. **reset 覆盖未测**：真实取消/失败路径是否都经过 `ResetGameStateOnTitleScreen` 的正常 ret，仍是 UNKNOWN。
4. **mode 7 漏失未评估**：`saveMode` 分支推进枚举器但丢弃结果，未证明读档终结不会落在那里。
5. **新游戏 UNKNOWN**：`generateNewFarm` 的创建/推进配对证据缺失。
6. **外部写入未排除**：`DebugCommands` 与 Mod 可在任意线程调用两个 setter；`Program::handleException`
   也会在任意线程 `set_game_mode(11)`。窗口内出现外部写入仍是 UNKNOWN。

**解除 HOLD 的最小证据**（需单独批准，本轮不执行，见 §10）：
一份只读运行时轨迹，记录创建点/入口点/推进点/reset/`OnDayStarted` 的 managed thread id、
推进对象匿名身份、mode、moved、`hasLoadedGame` 与拒绝原因，并覆盖“一次隔离读档 + 一次普通跨日 +
一次回标题 + 一次同档重载”。

---

## 10. 后续设备诊断方案（本轮只设计，不执行）

### 10.1 采集什么

每条记录（固定宽度，匿名）：

| 字段 | 类型 | 来源 | 用途 |
| --- | --- | --- | --- |
| `seq` | u32 | 递增 | 有序性 |
| `attempt` | u32 | 协调器分配（创建点） | 匿名尝试 ID（**不含**存档名/角色名/路径） |
| `epoch` | u32 | bridge `EpochCounter` | reset 代次 |
| `tid` | i32 | `Environment.CurrentManagedThreadId` | 实际执行线程 |
| `mainTid` | i32 | Tick 钩子首次记录的线程（0 = 未设定） | 游戏循环参照 |
| `identity` | u8 | 推进/创建对象的**匿名序号**（见 10.2） | 判断“同一对象”而不记录引用 |
| `mode` | u8 | `get_gameMode()` | 6/3/9/0… |
| `moved` | u8 | `MoveNext()` 返回值 | 终态判据 |
| `loaded` | u8 | `hasLoadedGame` | 就绪判据 |
| `kind` | u8 | 创建/入口/推进/完成边/reset/日切 | 信号类型 |
| `outcome` | u8 | Accepted / Refused | 结果 |
| `refusal` | u8 | `RefusalKind` | 拒绝原因（枚举，无自由文本） |

**明确不记录**：存档名、角色名、农场名、任何路径、存档内容、金钱/属性、设备序列号、账号、Mod 列表内容。

### 10.2 匿名身份与线程参照

- **匿名身份**：维护一个 16 项的固定数组 `(object, u8 ordinal)`（仅用 `ReferenceEquals` 比较，满则淘汰最旧），
  首次见到的对象分配下一个序号（1..255，回绕后从 1 重来并置 `identityWrap` 计数）。
  **不**用 `ConditionalWeakTable` 的稳定哈希以外的东西；不把对象引用、类型名或 `GetHashCode` 写入轨迹。
- **游戏循环参照**：复用 Phase 2.0 已注入的 `GameRunner.Update` 方法头 `Bootstrap.Tick()`，
  在 Tick 里**一次**调用 `TrySetGameLoopThread(Environment.CurrentManagedThreadId)`（set-once，
  第二个不同值被拒绝并计数）。在此之前 `mainTid = 0`，协调器对完成边返回 `UnknownGameLoopThread`（已有语义）。
  注意：生产接线仍属未来工作，本轮**未**把它接上。

### 10.3 缓冲区与溢出

- 固定环形缓冲 **512 条 × 32 B ≈ 16 KiB**，进程级、单写者（协调器锁内），无分配。
- **绝不每 Tick 写**：`InProgress` 推进**只**在每个尝试的**第一次**记录；失败/拒绝按 `refusal` 去重后
  每个原因最多记录前 8 次；`OnDayStarted`（辅助）每个日切最多 1 条。
- 溢出：覆盖最旧条目并 `dropped++`；`dropped` 也写进 dump 头，绝不静默丢。
- **输出时机**：每个进程**一次** dump（写到 Loader 自己的报告目录下的独立文件，走既有 loader 日志路径）。
  触发点取“以下最先发生者”：① 接受了一次观察；② 一个尝试有 pending 时 reset 到达正常 ret；
  ③ 缓冲溢出；④ 进程退出前的停止信号。**每个场景 = 一次 app 运行 = 一次 dump**，避免持续 I/O。
- dump 内容：头（版本/构建哈希/MVID/`dropped`/`mainTid`/`epoch`）+ 有序记录；无自由文本。

### 10.4 独立验收场景（各自独立运行、独立 dump）

| # | 场景 | 预期信号顺序 | 允许次数 | 失败条件 |
| --- | --- | --- | --- | --- |
| S1 | **标题空跑**（不读档，≥30 s） | `无 ResetBegan/LoadEntered/CompletionAccepted`（允许 0 条装载信号；`mainTid` 已设定） | 装载类信号 **0** | 出现任何 `StartAccepted`/`CompletionAccepted`；`mainTid=0` 超过 5 s |
| S2 | **隔离档读档成功** | `LoadEnumeratorCreated(mode=6) → StartAccepted → LoadEntered(MatchesPending) → Advance×N(InProgress, 只记首次) → Advance(mode=3,moved=1,loaded=1) → CompletionAccepted` | 观察 **1**；`CompletionAccepted` 1；`StartAccepted` 1；`LoadEntered` 1；`Superseded` 0 | 观察 ≠1；完成边身份序号 ≠ 创建序号；`refusal=ForeignAttemptIdentity/MissingAttemptIdentity`；`LoadEntered` 造成 pending 变化（应为 0）；同一尝试 2 次观察 |
| S3 | **普通跨日**（S2 之后睡一觉） | S2 的序列 + 至多 1 条 `Day edge`（辅助）；**不得**新增 `CompletionAccepted` | 观察总数仍 **1** | 跨日产生第二次观察；`DayCompleted` 被当成装载完成 |
| S4 | **回标题** | `ResetBegan → ResetCompleted`；若当时有 pending → `CancelledAttempts=1` 且无观察 | `ResetBegan` ≥1；观察增量 **0** | reset 后仍有 pending；reset 后新增观察 |
| S5 | **同档重载**（S2 完成后重新读同一档） | 第二组 `LoadEnumeratorCreated → StartAccepted → … → CompletionAccepted` | 观察总数 **2**（每个尝试各 1）；`Superseded` 0 | 只有 1 次（成功被吞）或 3 次（重复观察） |
| S6 | **取消**（仅当能安全重复触发时单列） | 预期 `StartAccepted → （取消）→ ResetBegan/ResetCompleted 或 Cancelled` | 观察 **0** | 取消后仍观察；pending 永久悬挂（S4 之后仍非 0） |
| S7 | **mode 7 观察（诊断性）** | 若读档终结落在 mode 7：推进点**不会**被调用 | 允许 0 条 Advance | 若出现“mode 7 下完成”且无 Advance 记录 → 确认漏失，需扩展边 |

**普通读档成功 vs mode 7 vs 新游戏 的证据范围**：
S2/S3/S5 只覆盖**普通单机读档**；mode 7 只做**观察**（S7，不宣称覆盖）；新游戏**不在**本方案内
（需要 `generateNewFarm` 的创建/推进配对，另开一轮），S1/S2 中出现 `NewGame` 记录属正常但**不构成**归属证据。

### 10.5 授权门槛（后两项会改变设备状态）

| 阶段 | 是否改变设备 | 需要什么 | 证据 |
| --- | --- | --- | --- |
| **A. 设备只读预检** | 否 | 独立授权（只读） | `Tools/device-precheck.ps1` / `test_device_precheck.ps1`（假 adb 门禁）；`adb devices -l`、`pm path`、`sha256sum`、`wm size/density`、`getprop` 白名单；**不** install/uninstall/`pm clear`/`am start`/push |
| **B. 候选 APK 安装** | **是** | 显式授权 + 备份 + 签名准入 | ① 项目外备份 Saves/Mods 并双端哈希；② `verify_modded_apk.py` 的 candidate vs installed 准入（多 signer/轮换则拒绝）；③ 说明“卸载会删存档”，不卸载；④ 安装后用**普通 shell** `pm path` + `sha256sum` 证明装上的就是这个包；⑤ 安装前后记录 installed 哈希 |
| **C. 进入/创建隔离存档** | **是**（生成存档文件） | 显式授权 + 隔离性证明 | ① 只使用**新建的隔离存档**，不打开既有存档；② 记录 Saves 目录文件集与哈希前后对比；③ 结束即停止进程；④ 回退：恢复 APK 到基线（记录哈希）、`deploy-mods-safe.ps1 rollback`、复核 Saves 文件集未被改动 |

**无授权时**：设备诊断与最终 APK 验证均为 **NOT_RUN**（本轮即如此）。

### 10.6 停止与回退证据清单

1. `am force-stop` 前后的 `pidof` 与进程列表。
2. APK：安装前 installed 哈希、安装后哈希、回退后哈希；signer 指纹。
3. Saves：文件清单 + 每个文件 SHA-256（前后各一次，集合相等）；目录 mtime。
4. Mods：`deploy-mods-safe.ps1 verify` 输出与事务 journal 状态。
5. 诊断 dump 的 SHA-256 与记录条数、`dropped` 计数。

---

## 11. 仍缺失的最小证据 / 未知

| 缺口 | 类别 | 需要什么 |
| --- | --- | --- |
| 创建点的真实 managed thread id | NOT_RUN | 只读轨迹的 `kind=创建` 记录 |
| 创建/推进/`OnDayStarted` 是否同线程、是否等于 Tick 线程 | NOT_RUN | `mainTid` 与三条记录的 `tid` 对比 |
| 真实帧序列（mode/tick/`currentLoader`） | NOT_RUN | 有序轨迹 |
| `OnDayStarted` 在读档后是否真的在下一帧触发、线程是否为游戏循环线程 | UNKNOWN（静态推断“会触发”） | 轨迹里 `Day edge` 的计数、`tid` 与 `mainTid` |
| reset 在每条取消路径的覆盖 | UNKNOWN | 轨迹里 reset 次数与 pending 状态 |
| 是否存在“既不 reset 也不失败”的取消路径 | UNKNOWN（本模块内 `CancelToTitle` 只被置 false） | 轨迹 + 调用图 |
| mode 7 分支的漏失风险 | UNKNOWN | 运行时 `saveMode` 是否推进装载枚举器 |
| 新游戏的尝试身份 | UNKNOWN（故意保留） | `generateNewFarm` 创建/推进配对 |
| 模块外写 `_gameMode` 的可能 | UNKNOWN | 运行时观测窗口内是否出现第三个写入者 |
| 最终 APK / 设备 | NOT_RUN | 另行授权 |
| 本轮改动的远端 CI | NOT_RUN | 用户 commit/push 后核验（且需先补 PrototypeTests 步骤） |

---

## 12. 限制

- 只覆盖 `StardewValley` 1.6.15.24354 / versionCode 239 的**原始**程序集；未从设备安装包重新取字节。
- 目标程序集只做静态改写与结构校验；**未执行**任何游戏方法、未构建/安装 APK，最终 APK 验证 NOT_RUN。
- 合成夹具不是 `Game1`/`SaveGame`：创建边复刻了 `set mode; call factory; stsfld` 与
  “守卫共用一个 ret、两条正常 ret”的形状，但不复刻真实方法其余 IL 的语义；`TestLoader` 只是可脚本化的 `IEnumerator`。
- 线程结论全部是静态推断（Task 闭包 vs `_update` 调用链），**没有** managed thread id 记录。
- 多人/本地联机、非空真实存档、断电恢复、Android 11+、其它 OEM 均未覆盖。
- 没有 commit / push / 分支 / Actions；没有修改原始游戏输入、`AGENTS.md`、`README.md`、`final/` 或 CI；
  `.tmp/phase2.1-p3/` 里的原始程序集副本、IL 转储与日志全部被 gitignore，未把存档信息、密钥或机器特定路径写入跟踪文件。

---

## 13. 收尾检查（本轮实际结果）

```text
git status --short
  M Tools/prototype/WorldReady/PrototypeBridge.cs
  M Tools/prototype/WorldReady/PrototypeInjector.cs
  M Tools/prototype/WorldReady/TargetBridgeEmitter.cs
  M Tools/prototype/WorldReady/WorldReadyCoordinator.cs
  M Tools/tests/PrototypeTests/Program.cs
  M Tools/tests/PrototypeTests/SyntheticFixture.cs
  M Tools/tests/PrototypeTests/TargetCheck.cs
  ?? docs/audit/phase2.1-p3-end-to-end-attribution-report.md

git diff --check   -> exit 0，无输出
git diff --stat    -> 7 files changed, 1310 insertions(+), 138 deletions(-)
```

- 变更只落在 Phase 2.1-P 原型与 PrototypeTests（授权范围内）与本报告；`Mod/`、`Tools/ModInjector/`、
  Gradle、部署脚本、CI、`final/`、`README.md`、`AGENTS.md` 均未改动。
- `.tmp/phase2.1-p3/` 保存本轮日志、独立程序集副本、IL 转储与静态分析工具（`tools/IlProbe`）；
  仓库内没有新增 APK、程序集、存档、密钥或设备日志。未暂存任何文件。

---

## 14. 下一轮的最小入口

1. **先补创建点线程与帧序列的只读记录**（§10），这是解除 HOLD 的唯一路径；在此之前不得把 97/97 当作设备放行依据。
2. 记录确认后，再决定是否把 `TrySetGameLoopThread` 接到 Phase 2.0 的 `GameRunner.Update` Tick 钩子。
3. `mode 7` 与 `HandleLoadError` 的漏失需要单独评估：前者可能让成功翻转不可观察，后者虽是死代码但仍是第二个 `getLoadEnumerator` 调用点。
4. 新游戏归属单独一轮：需要 `Utility.generateNewFarm` 的创建/推进配对证据，不得复用读档结论。
5. CI 漏跑 PrototypeTests 仍是独立缺口（本轮按授权未改工作流）；补步骤时应同时纳入 97 项与目标静态改写。
