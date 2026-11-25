# Phase 2.1-P.2：完成信号的尝试归属（attempt ownership）

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-26（+08:00） |
| 阶段 | Phase 2.1-P.2（源码 + 目标程序集证据核对；内部原型增量；合成执行；目标程序集静态改写；未接触设备） |
| 分支 / HEAD | `main` / `ce4c432c350d34d492062a14b198d138826f7bce` |
| 本地 `origin/main` / 远端 `main` | 同上（开工时 `git status` 干净，已跟踪/未跟踪均无改动） |
| 游戏程序集 | `StardewValley` 1.6.15.24354；SHA-256 `587FF5FF…E047`；MVID `573bf1e5-3245-46d6-925a-1c3713afb677`；原始、未注入 |
| APK 身份 | `com.chucklefish.stardewvalley` / versionCode **239** / versionName **1.6.15.0** |
| 三项判定 | 归属可行性 **PASS（仅静态 + 合成）**；原型验证 **PASS**；设备准备度 **HOLD** |

> 证据分级：**CURRENT_STATIC**（本轮对目标程序集字节的静态观察）、**SYNTHETIC_EXEC**（写出到磁盘、重新加载并真实执行合成程序集）、
> **HOST_MODEL**（协调器/测试宿主行为）、**HISTORICAL**（历史文档与历史 CI）、**NOT_RUN**、**UNKNOWN**。
>
> 本轮**没有任何**“真机已验证”“最终 APK 已验证”“真实存档已加载”的结论。目标程序集只做静态改写与重开校验，
> 未执行任何游戏方法、未构建/安装 APK、未连接设备、未创建或读取存档。

---

## 0. 结论摘要

| 问题 | 本轮结论 | 最强证据 | 关键限制 |
| --- | --- | --- | --- |
| 目标程序集身份 | 与预期一致（CURRENT_STATIC） | SHA-256 `587FF5FF…E047`、9,280,000 B、MVID `573bf1e5…`、`ModFramework.Bootstrap`/`Phase21.Prototype.WorldReadyBridge` 均不存在 | 未从设备安装包重新取字节 |
| P.1 的完成边（`OnDayStarted`）能否携带尝试身份 | **不能**（CURRENT_STATIC） | `OnDayStarted()` 无参数、无调用者上下文；两个调用点的唯一“身份近似”是全局 `Game1.currentLoader`，而在装载完成路径上该字段被其他写入者覆盖/保留的时序不由完成边决定 | 运行时取值未测（NOT_RUN） |
| `MoveNext()==false` 外层分支是否等于成功 | **不等于 —— 它是失败/退出边**（CURRENT_STATIC） | 成功时 `set_game_mode(3)` 在 `MoveNext` 尾部执行并 `return true`（`<>1__state=25`），`UpdateTitleScreen` 的 `switch(gameMode-6)` 在调用前已派发，故 false 分支不被走到 | 仅静态；无运行时帧记录 |
| `loadForNewGame(true)` 是否等于装载完成 | **不是，返回过早**（CURRENT_STATIC） | 它只是 `Task` 闭包 `<>c::<getLoadEnumerator>b__123_2` 的方法体；返回后枚举器还有 mode 翻转、NPC/成就修正、日志与最后一次 yield | — |
| 是否存在能携带身份的完成边 | **存在**（CURRENT_STATIC + SYNTHETIC_EXEC） | `Game1.UpdateTitleScreenDuringLoadingMode` 是唯一“推进 `currentLoader` 并观察其结果”的位置；身份 = 被推进的那个枚举器对象（局部变量，非全局重读） | 只覆盖 save load；新游戏仍 UNKNOWN |
| A 的迟到完成能否污染 B | **在 P.2 新边上被排除**（HOST_MODEL + SYNTHETIC_EXEC） | 完成要求“身份 == pending 身份 **且** `gameMode == 3`”；stale advance 读到 B 的 loader 时 mode 仍是 6 → `InProgress`，B 保留 | 仅模型与合成；真实帧序列未测 |
| 原型链路 | **PASS** | 本机 **83/83**（基线 65/65 → 新增 18，0 删除）；合成侧写出→重载→真实执行 | 不代表真实游戏 |
| 目标程序集静态改写 | **PASS（仅静态）** | 4 个方法变化（恰为预期）、6 个 bridge 方法新增、0 删除、AssemblyRef 不变、输入哈希不变；重开校验 PASS；重复注入被拒 | 未执行、未构建 APK、未从最终 APK 反验 |
| 设备准备度 | **HOLD** | 线程归属、真实帧序列、reset 覆盖全部为 NOT_RUN | 不是“全绿所以 GO”，也不是 NO-GO |

---

## 1. 开工基线

### 1.1 Git / 远端 / CI（本轮实测）

| 查询 | 实际结果 | 类别 |
| --- | --- | --- |
| `git rev-parse HEAD` | `ce4c432c350d34d492062a14b198d138826f7bce` | CURRENT_STATIC |
| `git rev-parse origin/main` | 同上 | CURRENT_STATIC |
| `git status --porcelain`（开工） | 空 | CURRENT_STATIC |
| 已跟踪原型/测试文件 | `Tools/prototype/WorldReady/*`、`Tools/tests/PrototypeTests/*` 均在 `ce4c432` 中已提交 | CURRENT_STATIC |

**HEAD 对应的 Actions run（`gh` 未安装，用 GitHub REST 核验）**

| 项 | 值 |
| --- | --- |
| Run | **`45447375735`**（`run_number` 19，`run_attempt` 1） |
| `head_sha` | `ce4c432c350d34d492062a14b198d138826f7bce`（与本地 HEAD 一致） |
| 结论 | **`status=completed` / `conclusion=success`** |
| Jobs | **3/3 success**：`dotnet build + loader host tests`、`offline checks (python, powershell, manifests, privacy)`、`powershell gates (adb resolution, apk deploy safety, no-root mods deploy, no device)` |
| 时间 | 2026-11-24T17:19:57Z → 17:44:48Z（`powershell gates` 到 17:44:47Z） |

> 该 run 属于**改动前**的代码。本轮工作区未 commit / 未 push / 未触发 Actions，因此**本轮改动的远端 CI = NOT_RUN**，
> 不得用 `45447375735` 冒充本轮通过。

### 1.2 CI 对 PrototypeTests 的实际覆盖：**0**（独立缺口，本轮不改工作流）

```text
grep -n 'PrototypeTests|Prototype|WorldReady' .github/workflows/ci.yml   ->  无匹配
```

工作流只有三个 job：`offline-checks`、`dotnet-tools`、`powershell-gates`。
`dotnet-tools` 含一步 `local-only suites (not configured on this runner)`，在本 runner 上是**空操作**（步骤 0 秒完成），
并不执行 PrototypeTests。因此本轮 83 项原型检查是**本机专属**的，报告不得写成“CI 通过”。

### 1.3 基线测试（改动前，同一 HEAD）

```text
dotnet run --project Tools/tests/PrototypeTests/Phase21.PrototypeTests.csproj -c Release
  -> 65 checks, 65 passed, 0 failed      exit 0
```

与预期一致；其中 `late_completion_reads_b_loader` 与 `synthetic_late_completion_reads_b_loader` 均**确认误归属**：

```text
PASS late_completion_reads_b_loader
     acceptedAsB=True, basis=LoaderReference, bRealCompletion=AlreadyConsumed
PASS synthetic_late_completion_reads_b_loader
     acceptedAsB=True, basis=LoaderReference, bRealCompletion=AlreadyConsumed
```

这两条作为**负向对照保留**（见 §5、§7）。系统只有 .NET 8 运行时，全部命令使用 `.tmp/dotnet-sdk/dotnet/dotnet.exe`（SDK 8.0.404）。

### 1.4 历史资料的使用边界

`docs/audit/phase2.1-p1-reset-epoch-report.md` 与 `docs/architecture/phase2.1-save-lifecycle-feasibility.md`
作为 **HISTORICAL** 读取。P.1 的 65 项计数、`d76f20e` 的 CI、以及 `.tmp/phase2.1-p1/` 的临时程序集**都不作为本轮通过依据**；
本轮从原始字节重新拷贝临时副本（`.tmp/phase2.1-p2/target/StardewValley.orig.dll`，哈希与原始一致）再改写。

### 1.5 被保护内容（未改动）

`Mod/`、`Tools/ModInjector/`、`Tools/build_modded_apk.py`、`verify_modded_apk.py`、Gradle、部署脚本、`.github/workflows/`、
`final/`、`README.md`、`AGENTS.md`。公开 API 仍为 1.1.0，`AssemblyVersion` 仍为 1.0.0.0，生产 Loader 与默认三种 APK 产物路径未动。
没有生成 APK、没有 ADB、没有设备、没有存档。

---

## 2. 目标程序集身份（CURRENT_STATIC）

| 维度 | 值 |
| --- | --- |
| APK 显示版本 / versionCode | `1.6.15.0` / `239` |
| 托管程序集 | `StardewValley 1.6.15.24354` |
| 字节数 / SHA-256 | 9,280,000 B / `587FF5FF9C258679004977BBEEC359ABA86649BD3E4860DDCAA428DF8F07E047` |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` |
| 注入状态 | 原始：`ModFramework.Bootstrap` 不存在、`Phase21.Prototype.WorldReadyBridge` 不存在 |
| 本轮输入 | `.tmp/phase2.1-p2/target/StardewValley.p2.dll`（原始字节副本；改写前后哈希均核对） |

身份与预期完全一致，因此本轮**允许**进行目标程序集静态改写；全部读写只发生在 `.tmp/phase2.1-p2/`（gitignore）。

---

## 3. 装载路径：从 `SaveGame.Load` 到 `Game1.OnDayStarted()`（CURRENT_STATIC）

### 3.1 先修正一个模式常量错误（P.1 沿用的命名是错的）

`Game1` 的字面量常量（本轮直接读 `FieldDefinition.Constant`）：

| 字段 | 值 |
| --- | --- |
| `titleScreenGameMode` | 0 |
| `loadScreenGameMode` | **1** |
| `newGameMode` | 2 |
| `playingGameMode` | 3 |
| `logoScreenGameMode` | 4 |

`GameModeToString` 的 switch 把 **6 命名为 `loadingMode`**（7=`saveMode`，8=`saveCompleteMode`）。

P.1 的 `WorldReadyCoordinator.LoadScreenGameMode = 6` **数值对、名字错**：mode 6 是 `loadingMode`，
真正的 `loadScreenGameMode` 是 1。协调器文档注释已订正，旧名作为源码兼容别名保留，并新增 `LoadingGameMode = 6`。

### 3.2 完整因果链

```text
(1) SaveGame.Load(string file, bool emergency, bool backup)            [22 IL, 0 EH]
      写入 SaveGame.tempFilename / tempLoadEmergencySave / tempLoadBackupSave
      → MainActivity.PromptForPermissionsIfNecessary(SaveGame.LoadAfterPermissionCheck)

(2) SaveGame.LoadAfterPermissionCheck()                                [157 IL, 2 EH]
      IL_0037  ldc.i4.6
      IL_0038  call Game1::set_gameMode(6)         ; 进入 loadingMode（属性 setter，tick 归零）
      ...
      IL_0154  call SaveGame::getLoadEnumerator(string,bool,bool)   → E
      IL_0155  stsfld Game1::currentLoader = E     ; <== 尝试身份在这里产生并保存

(3) SaveGame.getLoadEnumerator(...)                                    [12 IL]
      newobj <getLoadEnumerator>d__123(0)
      设 file / loadEmergencySave / loadBackupSave
      状态机字段：<>1__state, <>2__current, file, loadEmergencySave, loadBackupSave,
                  <>8__1(DisplayClass123_0), <>8__2(DisplayClass123_1),
                  <stopwatch>5__2, <readSaveTask>5__3

(4) Game1.UpdateTitleScreen(GameTime)                                  [164 IL]
      V0 = get_gameMode(); switch (V0 - 6)
        case 0 (mode 6 = loadingMode) -> UpdateTitleScreenDuringLoadingMode(); ret
        case 1 (mode 7 = saveMode)    -> currentLoader.MoveNext(); pop; ret      ; 结果被丢弃
        case 2 (mode 8 = saveComplete)-> pauseAccumulator 逻辑；setGameMode(3) …
        默认                          -> 普通标题更新路径
      （另有 IL_0143–0162：新游戏 currentLoader = Utility.generateNewFarm(IsClient); set_game_mode(6)）

(5) UpdateTitleScreenDuringLoadingMode()                               [32 IL, 0 EH, 2 ret]
      IL_0012 ldsfld currentLoader
      IL_0013 brfalse IL_0031                       ; 无 loader → ret
      IL_0014 ldsfld currentLoader
      IL_0015 callvirt IEnumerator::MoveNext()
      IL_0016 brtrue  IL_0031                       ; 仍有工作 → ret（loader 不清）
      IL_0017 ldnull
      IL_0018 stsfld currentLoader = null           ; 只有这条边会清 loader
      IL_0019 get_gameMode(); ldc.i4.3; bne.un IL_0029
      IL_0022 ldc.i4.3; IL_0023 call setGameMode(3)
      IL_0024–0027 fadeIn(true); fadeToBlackAlpha(0.99); IL_0028 ret
      IL_0029 ldnull; IL_0030 ExitToTitle(null); IL_0031 ret

(6) <getLoadEnumerator>d__123.MoveNext()                               [1994 IL, 17 EH]
      IL_0084–0095  readSaveTask = new Task(DisplayClass123_1::<getLoadEnumerator>b__1)
                    ModHooks.StartTask(readSaveTask, "Load_ReadSave")
      IL_0098–0105  yield 15 / state 2   （任务未完成）
      IL_0109–0120  while (!IsCanceled && !IsCompleted && !IsFaulted) yield 15
      IL_0121       if (SaveGame.loaded != null) …
      IL_0239–0247  第二个 Task：SaveGame/<>c::<getLoadEnumerator>b__123_2
                    → 闭包体 [7 IL]：CurrentThread.CurrentCulture = InvariantCulture;
                                      Game1.game1.loadForNewGame(true)
                    ★ loadForNewGame(true) 在**工作线程**执行，枚举器等待它
      IL_1848–1849  ldc.i4.3 ; call Game1::set_game_mode(3)   ; 6 → 3（tick 归零）
      IL_1850–1980  AddNPCs / AddModNPCs / doMorningStuff / checkForAchievements /
                    log "getLoadEnumerator() exited, elapsed = …" 等修正
      IL_1981–1988  <>2__current = 100 ; <>1__state = 25 ; ldc.i4.1 ; ret   ★ 返回 true
      IL_1989–1993  <>1__state = -1 ; ldc.i4.0 ; ret                        ★ 返回 false（state 25）
      状态派发：IL_0004 Switch …（26 项），state 25 → IL_1989

(7) Game1.loadForNewGame(bool)                                        [325 IL, 3 finally EH, 1 ret]
      IL_0296–0297  ldc.i4.1 ; stsfld Game1::hasLoadedGame
      **不设置 gameMode，也不调用 OnDayStarted**，在 IL_0324 正常返回。
      调用方 8 处：LoadNewGameFromCharacterCustomization、
                   SaveGame/<>c::<getLoadEnumerator>b__123_2、MoveNext、
                   Utility/<generateNewFarm>d__340::MoveNext、Intro::tick、
                   FarmhandSlot::Activate、TitleMenu::receiveKeyPress、TitleMenu::createdNewCharacter
                   → 混合了“读档”和“新游戏”，不能当作读档专属边。

(8) Game1._update(GameTime)                                            [1526 IL 行]
      IL_0006–0009  gameModeTicks = gameModeTicks + 1        ; 帧首
      IL_1434       UpdateTitleScreen(GameTime)              ; loadingMode 驱动器
      IL_1463–1465  if (gameMode == 6) goto IL_1500          ; 仍在装载 → 跳过
      IL_1500–1506  if (gameMode == 3 && gameModeTicks == 1) OnDayStarted()
      IL_1507       ret

(9) Game1.OnDayStarted()                                               [114 IL, 3 finally EH, 1 ret IL_0113]
      调用方 2 处：_update（IL_1506）、set_activeClickableMenu（IL_0046）
```

### 3.3 保存尝试身份的位置

| 位置 | 内容 | 说明 |
| --- | --- | --- |
| 产生 | `getLoadEnumerator` IL_0001 `newobj <getLoadEnumerator>d__123` | **每次尝试一个新对象**（唯一的“每次尝试一个”的运行时载体） |
| 保存 | `LoadAfterPermissionCheck` IL_0155 `stsfld Game1::currentLoader` | 字段类型 `System.Collections.Generic.IEnumerator\`1<System.Int32>`，即状态机自身 |
| 跨帧 | `currentLoader` 在枚举器 yield 15 期间一直保留 | 每帧由 `UpdateTitleScreen` case 0 推进一次 |
| 跨线程 | **枚举器本身不跨线程**：只在游戏循环线程（`UpdateTitleScreen`）被 `MoveNext`。跨线程的是闭包 `<>c::<getLoadEnumerator>b__123_2`，它把 `loadForNewGame(true)` 派到工作线程；枚举器只是 `yield` 等待 | 这是 P.1 “开始在工作线程、完成在主线程”的静态来源 |
| 丢失 | `UpdateTitleScreenDuringLoadingMode` IL_0017–0018（失败边）与 `ResetGameStateOnTitleScreen` | 成功边**不会**执行到 IL_0018（见 §4.1） |

**最后一个能可靠取得尝试身份的成功边**：`UpdateTitleScreenDuringLoadingMode` 中
`currentLoader.MoveNext()` 之后、`currentLoader = null` 之前的位置——此时“被推进的对象”就是身份，
且它仍在字段/局部变量里可达。越过这个点（`ldnull; stsfld` 之后，或等到 `OnDayStarted`），身份就不可达了。

### 3.4 `currentLoader` 的全部写入点（全模块扫描，共 11 处引用）

| 写入点 | 作用 |
| --- | --- |
| `SaveGame.LoadAfterPermissionCheck` IL_0155 | 读档尝试开始：`= getLoadEnumerator(...)` |
| `Game1.UpdateTitleScreen` IL_0147 | 新游戏：`= Utility.generateNewFarm(IsClient)`，随后 `set_game_mode(6)` |
| `Game1.UpdateTitleScreenDuringLoadingMode` IL_0018 | `= null`（失败/退出边） |
| `Game1.ResetGameStateOnTitleScreen` | `= null`（回标题） |
| `Game1.emergencyBackup` | 紧急备份路径 |

读取点：`UpdateTitleScreen` IL_0010（mode 7 分支，`MoveNext()` 结果被 `pop` 丢弃）、
`UpdateTitleScreenDuringLoadingMode` IL_0012/0014（装载驱动）、`emergencyBackup` 两处。

---

## 4. 候选边界对照

### 4.1 为什么 `MoveNext()==false` 外层分支**不是**成功边

关键在于 `UpdateTitleScreen` 的派发发生在调用**之前**：`switch (gameMode - 6)` 读的是进入本帧时的 mode。

```text
帧 N（mode == 6）：switch → case 0 → UpdateTitleScreenDuringLoadingMode
    currentLoader.MoveNext()
        内部：set_game_mode(3)      ; mode 6 → 3
              … 修正工作 …
              <>1__state = 25 ; return true     ★ 这一次返回 true
    brtrue IL_0031 → ret                          ; loader 保持非 null
帧 N+1：UpdateTitleScreen 读到 mode == 3 → switch(3-6) → 默认分支 → **不再驱动 loader**
        因此 IL_1989 的 return false（state 25）**没有被调用**
```

**结论（CURRENT_STATIC）**：`MoveNext()==false` 分支只有在“`MoveNext` 返回 false 且 mode 仍为 6”时才可达，
即**失败/中止**路径（随后 `currentLoader=null`，`get_gameMode()!=3` → `ExitToTitle(null)`）。
把“外层 `MoveNext()==false`”当作成功交接会**漏掉所有成功路径**。

### 4.2 为什么 `loadForNewGame(true)` 返回过早

`loadForNewGame` 本身只是 `Task` 闭包的方法体，执行的是**同步世界构建**那一段（清集合、建 Farm、建地点、
`hasLoadedGame=true`），然后 `ret`。它返回时：

- 枚举器还没走到 `IL_1848` 的 mode 6→3 翻转；
- 还没做 NPC/成就/建造物修正（`IL_1850–1980`）；
- 还没有 `yield 100` 与最终的 `return false`。

而且它的 8 个调用方里既有读档也有新游戏，**它在读档流程内部被调用，不代表读档完成**。

### 4.3 为什么 `Game1.OnDayStarted()` 不能作为归属边

1. **无身份入口**：`static void OnDayStarted()` 无参数、无调用者上下文；两个调用点都不携带任何“哪次尝试”。
2. **是“跨日/进入可玩模式”的事件，不是“装载完成”的事件**：`set_activeClickableMenu` 那条是“离开存档/出货菜单”
   的普通跨日；`_update` 那条是“mode 3 且 `gameModeTicks==1`”的首次可玩帧。两者都不是读档专属。
3. **时间上晚于交接**：成功路径上 mode 在帧 N 由枚举器翻到 3、tick 归零；帧 N+1 帧首 tick 变 1，
   帧末才触发 `OnDayStarted`。身份对象在这之前就已不可达。
4. **唯一可读的“身份近似”是全局 `Game1.currentLoader`**，而它被多个写入者共享；用它的引用相等去“证明归属”
   正是 P.1 §7 保留 HOLD 的原因。

因此本轮**淘汰** P.1 的 `OnDayStarted` 完成边（作为归属来源），把它降级为**辅助信号**：
它不能消费、取消或清除 pending。协调器在身份模式下对这条边直接返回 `MissingAttemptIdentity` 并**保留** pending
（`day_started_edge_cannot_complete_load`）。

### 4.4 采用的新完成边：装载推进（`UpdateTitleScreenDuringLoadingMode`）

注入后（写入并重开验证过的实际 IL，见 §8.2）：

```text
IL_0012  ldsfld Game1::currentLoader
IL_0013  stloc  V0                        ; ★ 身份：只取一次
IL_0014  ldloc  V0
IL_0015  brfalse IL_0040
IL_0016  ldloc  V0                        ; ★ MoveNext 作用在同一个局部
IL_0017  callvirt System.Collections.IEnumerator::MoveNext()
IL_0018  stloc  V1                        ; ★ moved 结果
IL_0019  ldloc  V0                        ; 参数 1：身份（局部，非全局重读）
IL_0020  call   Game1::get_gameMode()     ; 参数 2：推进后的 mode
IL_0021  ldloc  V1                        ; 参数 3：moved
IL_0022  ldsfld Game1::hasLoadedGame      ; 参数 4：就绪标志
IL_0023  call   Phase21.Prototype.WorldReadyBridge::LoadAdvanced(object,int,bool,bool)
IL_0024  ldloc  V1
IL_0025  brtrue IL_0040
IL_0026  ldnull
IL_0027  stsfld Game1::currentLoader       ; 原始清空边，位置不变
…（原 mode==3 判定与两条 ret 不变）
```

**为什么这是归属而不是全局比较**

- 传给钩子的第一个参数是**局部变量 V0**，它就是 `MoveNext()` 被调用的那个对象；钩子**没有**重读 `currentLoader`。
  验证器把这一点做成硬断言（`AdvanceIdentityFromLocal`），并显式拒绝“参数位置出现 `ldsfld currentLoader`”的形状。
- 该对象由 `getLoadEnumerator` **每次尝试新建**，因此是“这次尝试自己的状态机”，不是合成令牌。
- 成功判据是 `gameMode == 3`，而 3 只可能由**该对象自己的** `MoveNext` 尾部翻转产生；
  一个 stale advance 读到 B 的 loader 时 mode 还是 6，只能得到 `InProgress`，无法完成 B（§7 反例 B）。

### 4.5 候选覆盖范围（明确列出）

| 路径 | 是否被新边覆盖 | 依据 |
| --- | --- | --- |
| 正常读档成功（mode 6 → 3） | **覆盖** | §4.4；合成用例 `synthetic_advance_completes_once` |
| 读档失败/中止（`MoveNext` 仍在 mode 6 返回 false） | **覆盖为失败** | 原 IL_0018 清 loader；`LoadFailed`/`TitleMode` 消费但不观察 |
| 回标题（`ResetGameStateOnTitleScreen`） | **覆盖**（P.1 reset 语义保留） | 入口 + 唯一正常 ret 钩子不变 |
| 同档重载 | **覆盖** | 每次新枚举器 = 新身份；`reload_plus_one_cross_day_zero` |
| 普通跨日（`set_activeClickableMenu` → `OnDayStarted`） | **明确不当作首次装载完成** | 身份模式下该边被拒并保留 pending |
| 新游戏（`Utility.generateNewFarm` 占用同一 `currentLoader` 槽位） | **不覆盖，保持 UNKNOWN** | 生成器是另一类型；`BeginLoad` 之外的 pending 无已证身份，`Advance` 一律 `MissingAttemptIdentity` |
| mode 7（`saveMode`）分支里 `currentLoader.MoveNext()` 且结果被丢弃 | **漏失（已知限制）** | 该分支不观察终结值；本轮未证明读档终结会落到 mode 7 |
| 多人/本地联机、非空真实存档、Android 11+ | **未覆盖** | 不在范围 |

---

## 5. A/B 反例的实际结果

### 5.1 反例 B：旧完成是否污染 B

| 层 | 用例 | 实际结果 |
| --- | --- | --- |
| HOST_MODEL（负向对照，tokenless） | `pending_only_misattributes_b` | 旧完成被接受为 B，B 真实完成 `AlreadyConsumed` —— **保留为负向对照** |
| HOST_MODEL（负向对照，loader 引用相等） | `late_completion_reads_b_loader` | `acceptedAsB=True, basis=LoaderReference` —— **保留为负向对照** |
| SYNTHETIC_EXEC（负向对照） | `synthetic_late_completion_reads_b_loader` | 经注入 IL 复现同一误归属 —— **保留** |
| HOST_MODEL（P.2 新边） | `foreign_identity_preserves_newer_pending` | A 的完成命名 A 自己的对象 → `ForeignAttemptIdentity`、pending 保留、观察 0；B 随后完成 **1 次** |
| HOST_MODEL（P.2 新边） | `stale_advance_cannot_complete_b` | A 的迟到推进**读到 B 的 loader** 但 mode 仍是 6 → `InProgress`、观察 0、pending 仍是 B；B 真实推进 → 完成 1 次 |
| SYNTHETIC_EXEC（P.2 新边） | `synthetic_advance_foreign_identity_preserves_b` | 同上结论，完全经注入 IL → bridge → 协调器；`foreignRefusals=1`、观察 1、观察者 = B |
| HOST_MODEL（重置后旧身份） | （`advance_after_reset` 形态，见 `reset_wins` 分支） | reset 已取消 pending → `NoPendingAttempt`/`ResetInProgress`，观察 0 |

**结论**：P.2 的新边把“引用相等”换成“**身份相等 + 该对象自己完成 mode 翻转**”，
反例 B 的两种形态（命名自己的对象 / 读到更新的对象）都**无法**观察或清除 B。

### 5.2 归属失败 vs 已证归属但守卫失败（分别定义并断言）

| 情形 | 行为 | 断言用例 |
| --- | --- | --- |
| 身份缺失（null / pending 无已证身份） | **拒绝 + 保留 pending**（`MissingAttemptIdentity`） | `missing_identity_preserves_pending`、`unproven_pending_identity_refused` |
| 身份是别人的（旧 A） | **拒绝 + 保留 pending**（`ForeignAttemptIdentity`） | `foreign_identity_preserves_newer_pending` |
| reset 进行中 | **拒绝 + 不改任何状态**（`ResetInProgress`） | `advance_during_reset_refused` |
| 已证归属，**线程守卫生效** | **拒绝 + 保留 pending**（`OwnedButWrongThread`）——离线程的推进不足以证明尝试终结 | `owned_wrong_thread_preserves`、`synthetic_advance_wrong_thread_preserves` |
| 已证归属，**就绪守卫失败**（`hasLoadedGame==false`） | **消费但不观察**（`OwnedButWorldNotLoaded`），`OwnedTerminalWithoutObservation++` | `owned_not_ready_consumes_without_observation` |
| 已证归属，装载失败（mode 仍 6 且 `moved==false`） | **消费但不观察**（`LoadFailed`） | `failed_load_consumes_without_observation` |
| 已证归属，成功 | **消费并观察一次**（`Basis=LoadStateMachine`、`AttemptOwnershipProven=true`） | `ownership_completion_once`、`synthetic_advance_completes_once` |

> “已证归属 + 线程守卫失败 → 保留”与“已证归属 + 就绪守卫失败 → 消费不观察”是**两种不同行为**，
> 分别有独立拒绝原因、独立计数与独立断言。前者保留是因为离线程推进不可信；后者消费是因为 mode 已到 3 说明尝试确实终结。

---

## 6. 实现（授权范围内）

### 6.1 变更文件

| 文件 | 变更 |
| --- | --- |
| `Tools/prototype/WorldReady/WorldReadyCoordinator.cs` | 新 `CorrelationMode.StateMachineIdentity`、`AttributionBasis.LoadStateMachine`、6 个新 `RefusalKind`、`AdvanceOutcomeKind`/`LoadAdvanceOutcome`、`PendingAttempt.Identity/IdentityProven`、`BeginLoad(...)`、`Advance(...)`、`AdvancesObserved`、`OwnedTerminalWithoutObservation`；`AttemptOwnershipProven` 现在只在 `LoadStateMachine` 时为真；订正 mode 6 的命名；身份模式下旧完成边被拒并保留 pending |
| `Tools/prototype/WorldReady/PrototypeInjector.cs` | 新目标 `UpdateTitleScreenDuringLoadingMode`；新钩子 `LoadAdvanced(object,int,bool,bool)`；`InjectAdvance`（局部变量化 + 钩子注入，maxstack 4）；`VerifyAdvance`（位置精确：身份来自局部、MoveNext 作用于同一局部、两处守卫仍指向同一 ret、站点不在 try/handler 内、ret 数为 2）；重复注入计数纳入新边 |
| `Tools/prototype/WorldReady/TargetBridgeEmitter.cs` | 目标桥新增 `LoadAdvancedCalls`、`LastAdvanceGameMode/Moved/HasLoadedGame/LoaderWasNull` 与 `LoadAdvanced` 方法 |
| `Tools/prototype/WorldReady/PrototypeBridge.cs` | `IWorldReadySink.OnLoadAdvanced`、`PrototypeBridge.LoadAdvanced`、`CoordinatorSink.OnLoadAdvanced`；`OnWorldLoading` 对“读档 + 非空 loader”改走 `BeginLoad`（新游戏仍走未证身份路径） |
| `Tools/tests/PrototypeTests/SyntheticFixture.cs` | 新 `UpdateTitleScreenDuringLoadingMode` 形状（两条守卫共用一个 ret、两条正常 ret）；`currentLoader` 改为 `IEnumerator` 类型以匹配真实 `callvirt MoveNext`；`TestLoader`（可脚本化 `MoveNext` 结果与计数） |
| `Tools/tests/PrototypeTests/Program.cs` | 新增 §5 全部用例；loader 替身统一为 `TestLoader`；两个 sink 实现新接口 |
| `Tools/tests/PrototypeTests/TargetCheck.cs` | 期望变化方法 3 → 4（加 `UpdateTitleScreenDuringLoadingMode`）；bridge 方法 5 → 6 |

### 6.2 保留的 P.1 语义

- reset epoch：`GetResetEpoch()` 仍是**开始边第一条读取**，`Verify` 仍断言 `EpochReadFirst`。
- reset 窗口：`TryBeginReset` / `TryCompleteReset` 的入口/正常 ret 钩子、latch、异常退出自愈语义**未改**。
- `BeginLoad` 的提交顺序与 `TryBeginAttempt` 一致（epoch → latch → mode → 身份），并额外要求身份非空。
- 旧 `Complete(...)` 路径与 `CorrelationMode.LoaderIdentity` / `PendingOnly` **保持不变**，负向对照仍可复现。

### 6.3 消费前先验归属

`Advance` 的顺序是：`resetInProgress` →（无 pending）→**身份校验**→ 失败/进行中/成功分类 →
线程/就绪守卫 → 消费。任何“身份不成立”的分支都在**尚未消费、取消或清除 pending** 之前返回，
并显式置 `PendingPreserved = true`。这与任务要求一致，也是 `foreign_*`/`missing_*` 用例断言的内容。

---

## 7. 验收矩阵（本轮实跑）

### 7.1 新增 18 项

层级：**M** = HOST_MODEL，**S** = SYNTHETIC_EXEC（写盘/重载/真实执行）。

| 测试 | 层 | 断言 | 结果 |
| --- | --- | --- | --- |
| `ownership_completion_once` | M | 已证归属完成 1 次、`Basis=LoadStateMachine`、`AttemptOwnershipProven=True`、重复 → `AlreadyConsumed` | PASS |
| `foreign_identity_preserves_newer_pending` | M | 外来身份 `ForeignAttemptIdentity`、B pending 保留、B 完成 1 次 | PASS |
| `stale_advance_cannot_complete_b` | M | 读到 B 的 loader 但 mode 6 → `InProgress`、观察 0、B 保留、B 后完成 1 次 | PASS |
| `missing_identity_preserves_pending` | M | `MissingAttemptIdentity`、pending 保留、观察 0 | PASS |
| `unproven_pending_identity_refused` | M | 未证身份的 pending + advance → `MissingAttemptIdentity`、保留 | PASS |
| `owned_wrong_thread_preserves` | M | `OwnedButWrongThread`、`OwnershipVerified=True`、保留、观察 0 | PASS |
| `owned_not_ready_consumes_without_observation` | M | `OwnedButWorldNotLoaded`、清除、观察 0、`OwnedTerminalWithoutObservation=1` | PASS |
| `failed_load_consumes_without_observation` | M | `LoadFailed`、清除、观察 0 | PASS |
| `advance_during_reset_refused` | M | `ResetInProgress`、观察 0 | PASS |
| `day_started_edge_cannot_complete_load` | M | `OnDayStarted` 边 → `MissingAttemptIdentity`、pending 保留、观察 0 | PASS |
| `reload_plus_one_cross_day_zero` | M | 同档重载 +1、普通跨日不增加（观察 2） | PASS |
| `advance_versus_reset_race_invariants` | M | 100 轮：完成先 34 / reset 先 33 / 真并发 33，violations=0 | PASS |
| `synthetic_advance_shape` | S | `LoadAdvanced` 1 处、身份来自局部、守卫完整、站点不受保护、maxstack 2→4 | PASS |
| `synthetic_advance_completes_once` | S | 注入 IL → bridge → 协调器：进行中不完成、成功后 1 次、重复 1 次拒绝 | PASS |
| `synthetic_advance_foreign_identity_preserves_b` | S | A 的 stale 推进被拒、B 保留、B 完成 1 次、`foreignRefusals=1` | PASS |
| `synthetic_advance_wrong_thread_preserves` | S | 异线程推进 → `OwnedButWrongThread`、保留、观察 0 | PASS |
| `synthetic_advance_edge_missing_rejected` | S | 缺少 `UpdateTitleScreenDuringLoadingMode` → `Resolve` 失败关闭 | PASS |
| `synthetic_advance_hook_shape_rejected` | S | 钩子参数形状不符 → `Inject` 拒绝 | PASS |

**计数**：**18 项新增 + 65 项原有 = 83 checks，83 passed，0 failed**（退出码 0）。
P.1 的两条误归属用例仍存在并通过（作为负向对照），未删除任何用例。

### 7.2 命令、退出码与计数

| 命令（仓库根） | 工具 | 退出码 | 结果 | 日志 |
| --- | --- | ---: | --- | --- |
| `dotnet run --project Tools/tests/PrototypeTests/... -c Release`（**改动前**） | .NET SDK 8.0.404 / Win10.0.26100 | 0 | **65/65** | `.tmp/phase2.1-p2/baseline-prototype-tests.log` |
| 同上（**改动后**） | 同上 | 0 | **83/83**（+18，删除 0） | `logs/host-tests-p2.log` |
| `dotnet ...Phase21.PrototypeTests.dll --target <副本> <out> <refs>` | 同上 | 0 | 结构 PASS；4 方法变化、6 方法新增 | `logs/target-check.log` |
| `dotnet ...Phase21.PrototypeTests.dll --verify-target <patched> <refs>` | 同上 | 0 | 重开结构 PASS | `logs/target-verify.log` |
| 同上 `--target <patched> …`（重复注入） | 同上 | **1** | 按预期拒绝：`input already carries the prototype bridge` | `logs/target-double.log` |
| `dotnet build Tools/tests/InjectorTests/... -c Release` + 运行 | 同上 | 0 / 0 | **29/29** | `logs/injector-tests.log` |
| `python Tools/tests/test_loader.py --dotnet ... --refs .tmp/phase0.5/refs` | Python 3.12.10 | 0 | **53/53**；host summary **552/552**；P114 0 失败；Phase 2.0 0 失败 | `logs/loader-host.log` |
| `python Tools/tests/test_ci_offline.py` | 同上 | 0 | **13/13** | `logs/ci-offline.log` |
| `python Tools/tests/test_modded_build.py` | 同上 | 0 | **55/55** | `logs/test_modded_build.log` |
| `python Tools/tests/test_apk_admission.py` | 同上 | 0 | **16/16** | `logs/test_apk_admission.log` |
| `python Tools/tests/test_p114_probe.py` | 同上 | 0 | **46/46** | `logs/test_p114_probe.log` |
| `python Tools/tests/test_mod_pipeline.py --dotnet ... --refs ...` | 同上 | 0 | **39/39** | `logs/mod-pipeline.log` |
| `git diff --check` | git | 0 | 无输出 | — |

**未跑（明确 NOT_RUN）**：ADB / 设备 / 模拟器；APK 构建（`assembleDebug`/`assembleLoaderOnlyDebug`/`assembleModdedDebug`）；
`verify_modded_apk.py`；安装/部署；读写 Saves；进入或创建存档；PowerShell 部署门禁（`test_deploy_safe.ps1`、
`test_deploy_mods_safe.ps1`、`test_device_precheck.ps1`，本轮未改这些路径，且属部署轮次）；
GitHub Actions；commit / push。

---

## 8. 目标程序集静态改写（仅静态）

### 8.1 每个钩子：签名、数量、正常/异常边、分支与 switch 重定向、EH 半开区间

| 边 | 方法 | 签名 | 站点数 | 正常/异常边 | 分支重定向 | EH 区间 |
| --- | --- | --- | ---: | --- | --- | --- |
| reset 入口 | `ResetGameStateOnTitleScreen` | `instance void()` | 1 | 入口在一切写入之前；入口不在任何 try/handler 内 | 无（入口处插入，原 IL_0000 后移） | 原 `try=[IL_0299..IL_0310)` / `handler=[IL_0310..IL_0314)` 按半开规则随钩子前移 |
| reset 正常退出 | 同上 | — | 1 | 唯一正常 ret（原 IL_0542）；handler 内 ret 0 个 | 无入边指向该 ret | handler 末端随钩子前移 |
| 开始 | `loadForNewGame(bool)` | `instance void(bool)` | 1 | 入口；`EpochReadFirst=True`（epoch → loadedGame → gameMode → loader） | 无 | 3 个 finally 未触及 |
| 完成（P.1 旧边，保留但降级） | `OnDayStarted()` | `static void()` | 1 | 唯一正常 ret（IL_0113）；handler ret 0 个；入边 0 | 无 | 3 个 finally 未触及 |
| **完成（P.2 新边）** | `UpdateTitleScreenDuringLoadingMode()` | `instance void()` | **1** | 原 2 个 ret（`IL_0028` 成功、`IL_0031` 末）；**0 个 EH** | 两个原始守卫（`brfalse`/`brtrue`）**仍指向同一 ret**（`AdvanceGuardsIntact=True`）；无 switch 重定向（方法内无 switch） | 无 EH，站点不受保护（`AdvanceSiteUnprotected=True`） |

> 说明：`UpdateTitleScreenDuringLoadingMode` 内部没有 `switch`；`switch (gameMode-6)` 在**调用方**
> `UpdateTitleScreen` 中，注入不改变它，因此不存在 switch 表重定向。

### 8.2 写入并重开后验证的实际 IL（关键片段）

见 §4.4。校验器对每条指令做了位置精确比对，并把结果写成布尔证据：

```text
EVIDENCE verify_advance_calls            = 1
EVIDENCE verify_advance_identity_local   = True
EVIDENCE verify_advance_guards_intact    = True
EVIDENCE verify_advance_site_unprotected = True
EVIDENCE verify_epoch_read_first         = True
```

`VerifyAdvance` 另外断言该方法仍保持 **2 个正常 ret**（不等于 2 时写入 `problems`，本轮 `problems` 为空）。

重开后的方法形状：`ins=41 maxstack=4 locals=2`，`V0:IEnumerator<int>`、`V1:Boolean`，0 个 EH。

### 8.3 输入/输出身份与方法集合

| 项 | 值 |
| --- | --- |
| 输入 | `.tmp/phase2.1-p2/target/StardewValley.p2.dll`，9,280,000 B |
| 输入 SHA-256（前后一致） | `587FF5FF9C258679004977BBEEC359ABA86649BD3E4860DDCAA428DF8F07E047` |
| 输出 | `.tmp/phase2.1-p2/target/StardewValley.p2.patched.dll`，9,262,080 B |
| 输出 SHA-256 | `774B36EF4CFA7821AF2356F600A99D00F63204A8D47FA598405CEABB431D405D` |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` → `d24e81de-5ab8-463d-a7e3-bfb79e0e3740` |
| AssemblyRef | **未变化**（TargetCheck 的 `AssemblyRef set changed` 断言未触发） |
| 变化方法 | **4**（恰为预期）：`loadForNewGame(bool)`、`OnDayStarted()`、`ResetGameStateOnTitleScreen()`、`UpdateTitleScreenDuringLoadingMode()` |
| 新增方法 | **6**：`ResetBegan`、`ResetCompleted`、`GetResetEpoch`、`WorldLoading`、`DayCompleted`、`LoadAdvanced` |
| 删除方法 | **0** |

> 输出哈希/MVID **不是跨次可复现的**：每次注入都 `Mvid = Guid.NewGuid()`，字节随之变化（P.1 也已记录这一点）。
> 报告引用的是本轮最后一次实跑的产物。输入哈希前后一致，说明原始副本未被改写。

### 8.4 maxstack：Cecil 归一化与注入抬高要分开看

TargetCheck 的 `maxstack_normalised_methods = 7` 把两类混在一起，这里**单列区分**：

**(a) Cecil 重写模块导致的归一化（6 项，非语义改动，P.1 已有）**

| 方法 | maxstack |
| --- | --- |
| `Child::canTalk()` | 3 → 1 |
| `Program::GetAppDataFolder(string,bool)` | 3 → 1 |
| `DyeMenu::performHoverAction(int,int)` | 4 → 0 |
| `InventoryMenu::receiveGamePadButton(Buttons)` | 4 → 0 |
| `InviteCodeDialog::.ctor(string,behavior)` | 10 → 4 |
| `ItemGrabMenu::receiveGamePadButton(Buttons)` | 5 → 2 |

**(b) 本轮注入**故意**抬高（1 项，语义必需）**

| 方法 | maxstack | 原因 |
| --- | --- | --- |
| `Game1::UpdateTitleScreenDuringLoadingMode()` | 2 → 4 | 钩子最多同时压栈 4 个值（loader、gameMode、moved、hasLoadedGame） |

三个 P.1 钩子方法**没有被抬高**：reset 4→4、load 5→5、completion 6→6。

### 8.5 拒绝的形状

| 形状 | 结果 |
| --- | --- |
| 重复注入（输入已含 bridge） | **拒绝**，退出码 1（`logs/target-double.log`） |
| 重复注入（同一模块内钩子已存在） | 拒绝：`prototype hooks already present … refusing to patch twice`（`synthetic_double_inject_rejected` 覆盖） |
| `UpdateTitleScreenDuringLoadingMode` 缺失 / 重载 / 带 EH | 拒绝：`Resolve` 抛 `expected exactly one instance void …`；注入前检查 `body.ExceptionHandlers.Count != 0` |
| 方法形状不匹配（找不到 `ldsfld; brfalse; ldsfld; MoveNext; brtrue`） | 拒绝：`unsupported shape` |
| 钩子参数形状不符 | 拒绝（`synthetic_advance_hook_shape_rejected`） |
| reset：ret 在 try 内 / 只有 handler ret / 无正常返回 | 拒绝（P.1 用例保留） |
| 输入不是 `StardewValley 1.6.15.24354` 或已含 `ModFramework.Bootstrap` | 拒绝 |

---

## 9. 三项判定

### 9.1 归属可行性：**PASS（仅静态 + 合成）**

- 逐边核对了 `SaveGame.Load → getLoadEnumerator → loadForNewGame(true) → 成功交接 → OnDayStarted`，
  标出了身份的**产生点**（`newobj d__123`）、**保存点**（`currentLoader`）、**跨帧/跨线程**位置，
  以及**最后一个能可靠取得身份的成功边**（`UpdateTitleScreenDuringLoadingMode` 的推进点）。
- 淘汰了三条不可用候选并给出静态理由：`OnDayStarted`（无身份入口 + 事件语义不符 + 时间过晚）、
  外层 `MoveNext()==false`（只是失败边）、`loadForNewGame` 返回（过早且混合新游戏）。
- 采用的边把身份做成**数据传递链**（局部变量，非全局重读），并对“身份相等 + 该对象自己翻转 mode”
  双条件成立才观察；A/B 反例在模型与合成两层均被关闭。
- **PASS 边界**：只证明静态结构与合成执行；真实帧序列、真实线程归属、真实 `currentLoader` 取值 = NOT_RUN。

### 9.2 原型验证：**PASS**

- 83/83（基线 65/65 → +18，0 删除，0 跳过），含 12 项模型 + 6 项合成（写盘→重载→真实执行）。
- 负向对照齐全：tokenless 误归属、loader 引用相等误归属、合成 IL 误归属全部保留并通过；
  另外新增“缺少新边被拒”“钩子形状不符被拒”“重复注入被拒”。
- 竞态用 `Barrier` 与可控同步点；逐项断言拒绝原因、pending、观察计数与信号顺序。
- 目标副本：4 方法变化 / 6 方法新增 / 0 删除 / AssemblyRef 不变 / 输入哈希不变 / 重开 PASS / 重复注入被拒。
- **PASS 边界**：仅主机模型 + 合成程序集 + 静态改写；不代表真实游戏，不代表设备。

### 9.3 设备准备度：**HOLD**

按顺序的阻塞项：

1. **真实线程归属未测**：`Advance` 是否真的在游戏循环线程执行、`loadForNewGame` 是否真的在工作线程，
   都是静态推断（`_update → UpdateTitleScreen → Update → Game.Tick`；`Task` 闭包）。managed thread id 未记录。
   协调器的线程权威（`TrySetGameLoopThread`）在生产路径上**仍未接线**。
2. **真实帧序列未测**：`set_game_mode(3)`→`yield 100`→`UpdateTitleScreen` 默认分支→下一帧
   `gameModeTicks==1`→`OnDayStarted` 这条链是静态推导，没有运行时帧记录。
3. **reset 覆盖未测**：真实取消/失败路径是否都走到 `ResetGameStateOnTitleScreen` 的正常 ret，
   以及“既不 reset 也不产生 mode 6 失败”的取消路径是否存在，仍是 UNKNOWN。
4. **新游戏仍 UNKNOWN**：同一 `currentLoader` 槽位被 `Utility.generateNewFarm` 复用，
   本轮刻意不给它任何归属结论。
5. **mode 7 漏失未评估**：`saveMode` 分支推进 `currentLoader` 但丢弃结果，未证明读档终结不会落在那里。

**解除 HOLD 的最小证据**（需单独批准，本轮不执行）：

- 一份**只读**运行时记录：一次隔离读档 + 一次正常跨日 + 一次回标题 + 一次同档重载，记录
  ① `loadForNewGame` 入口线程 id；② `UpdateTitleScreenDuringLoadingMode` 推进点的线程 id 与
  `moved`/`gameMode`/`hasLoadedGame`；③ `currentLoader` 引用相等性（推进对象 vs 开始时捕获）；
  ④ `OnDayStarted` 调用点与线程 id；⑤ `ResetGameStateOnTitleScreen` 入口/正常 ret 次数与线程。
- 若记录显示“推进点不在同一线程”或“`currentLoader` 在推进前已被替换但 mode 已为 3” → 归属降级为 NO-GO。
- 若 reset 在真实取消路径上缺失 → 重新评估取消语义并保持 HOLD。

---

## 10. 仍缺失的最小证据 / 未知

| 缺口 | 类别 | 需要什么 |
| --- | --- | --- |
| 真实 managed thread id 归属（开始 vs 推进 vs `OnDayStarted`） | NOT_RUN | 只读运行时线程记录 |
| 真实帧序列证据（mode/tick/`currentLoader`） | NOT_RUN | 同上 |
| reset 在每条取消路径的覆盖 | UNKNOWN | 调用图 + 运行时 reset 次数记录 |
| 是否存在“既不 reset 也不失败”的取消路径 | UNKNOWN | 同上 |
| 新游戏的尝试身份 | UNKNOWN（故意保留） | 独立证据（`generateNewFarm` 的创建点与推进点配对） |
| mode 7 分支的漏失风险 | UNKNOWN | 运行时 `saveMode` 是否会推进装载枚举器 |
| 设备运行 / 最终 APK | NOT_RUN | 设备准备度另行评审 |
| 本轮改动的远端 CI | NOT_RUN | 用户 commit/push 后重新核验 |

---

## 11. 限制

- 只覆盖 `StardewValley` 1.6.15.24354 / versionCode 239 的**原始**程序集；未从设备安装包重新取字节。
- 目标程序集只做静态改写与结构校验；未执行任何游戏方法、未构建/安装 APK、最终 APK 验证 NOT_RUN。
- 合成夹具不是 `Game1`：推进方法复刻了“两条守卫共用一个 ret、两条正常 ret、0 个 EH”的形状与
  `callvirt IEnumerator::MoveNext`，但不复刻真实方法其余 30 条 IL 的语义。
- 身份链条的“开始边”读的是全局 `currentLoader`（P.1 已如此）。本轮只把**完成边**做成局部数据传递；
  开始边若被另一个尝试抢先替换该字段，会得到更晚尝试的身份——此时 pending 指向新尝试，属 fail-closed/后到者胜，
  但仍需运行时确认。
- 多人/本地联机、非空真实存档、断电恢复、Android 11+、其它 OEM 均未覆盖。
- 没有 commit / push / 分支 / Actions；没有修改原始游戏输入、`AGENTS.md`、`README.md`、`final/` 或 CI；
  `.tmp/phase2.1-p2/` 里的原始程序集副本、IL 转储与日志全部被 gitignore，未把存档信息、密钥或机器特定路径写入跟踪文件。

---

## 12. 收尾检查（本轮实际结果）

```text
git status --short
  M Tools/prototype/WorldReady/PrototypeBridge.cs
  M Tools/prototype/WorldReady/PrototypeInjector.cs
  M Tools/prototype/WorldReady/TargetBridgeEmitter.cs
  M Tools/prototype/WorldReady/WorldReadyCoordinator.cs
  M Tools/tests/PrototypeTests/Program.cs
  M Tools/tests/PrototypeTests/SyntheticFixture.cs
  M Tools/tests/PrototypeTests/TargetCheck.cs
  ?? docs/audit/phase2.1-p2-attempt-ownership-report.md

git diff --check   -> exit 0，无输出
git diff --stat    -> 7 files changed, 1563 insertions(+), 60 deletions(-)
```

- 变更只落在 Phase 2.1-P 原型与 PrototypeTests（授权范围内）与本报告；`Mod/`、`Tools/ModInjector/`、
  Gradle、部署脚本、CI、`final/`、`README.md`、`AGENTS.md` 均未改动。
- `.tmp/phase2.1-p2/` 保存本轮日志、临时程序集副本、IL 转储与静态分析工具（`tools/IlProbe`）；
  仓库内没有新增 APK、程序集、存档、密钥或设备日志。
- 未暂存任何文件。

---

## 13. 下一轮的最小入口

1. **先补线程与帧序列的只读记录**（§9.3 的五项），这是解除 HOLD 的唯一路径；
   在此之前不得把“83/83 全绿”当作设备放行依据。
2. 记录确认后，再决定是否把 `TrySetGameLoopThread` 接到生产 Tick 钩子（Phase 2.0 的 `GameRunner.Update`），
   并把 `CoordinatorSink` 的 `BeginLoad` 语义固化。
3. 新游戏归属单独一轮：需要 `Utility.generateNewFarm` 的创建/推进配对证据，不得复用读档结论。
4. CI 漏跑 PrototypeTests 仍是独立缺口；本轮按授权未改工作流。
