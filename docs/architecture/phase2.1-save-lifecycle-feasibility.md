# Phase 2.1-R：SaveLoaded 与 DayStarted 存档生命周期事件可行性

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-23（+08:00） |
| 状态 | **研究完成；未实现、未构建 APK、未接触设备** |
| 基线 | `HEAD == origin/main ==` 远端 `main` = `8712cf09a42e8336f97a0c6a3f5b63c69b1bb101`；Actions Run `44004070683`，3/3 jobs success |
| 游戏程序集 | `StardewValley.dll` 1.6.15.24354，SHA-256 `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047`，MVID `573bf1e5-3245-46d6-925a-1c3713afb677`；**原始、未注入** |
| APK 身份 | `com.chucklefish.stardewvalley` / versionCode **239** / versionName **1.6.15.0** |
| 判定 | **`SaveLoaded`：GO**；**`DayStarted`：HOLD**（只表示下一轮验证建议，不表示已实现或真机通过） |
| 关联 | [研究报告](../audit/phase2.1-research-report.md) · [Phase 2.0.1 报告](../audit/phase2.0.1-report.md) · [事件契约](phase2-events.md) · [构建/安装验证](phase2.0.1-build-install.md) |

> 证据分级：**CURRENT_STATIC**（本轮对目标程序集的静态观察）、**HISTORICAL**（历史文档/历史设备证据）、
> **INFERENCE**（由静态观察按运行时语义推导）、**NOT_RUN**、**UNKNOWN**。本轮没有“真机已验证”的结论。
> 未单独标注的“方法/字段/IL 偏移/调用者”陈述默认是 **CURRENT_STATIC**；历史设备陈述是 **HISTORICAL**；
> 运行时次数/线程/状态推断是 **INFERENCE**；设计建议不是观测；未执行项为 **NOT_RUN/UNKNOWN**。
>
> 本文只做设计与判定，不写生产实现，不改公开 Mod API、事件调度器、Bootstrap、Loader、注入器、构建脚本、
> CI 或 APK。

---

## 0. 结论摘要

- **`SaveLoaded` 推荐 GO**：以 `Game1.loadForNewGame(bool)` 作为“世界初始化开始”的标记，以
  `Game1.OnDayStarted()` 的唯一正常 `ret` 作为“世界已可供 Mod 读取”的完成边；两者合起来可以给出
  “一次成功进入世界 = 一次 SaveLoaded”，并区分 `IsNewGame`。主要正向/失败路径都能解释；剩余风险集中在
  `loadForNewGame(true)` 在工作线程执行、以及失败/取消后 pending 标志的可见性，主机夹具和下一轮设备线程记录可验证。
- **`DayStarted` 推荐 HOLD**：`Game1.OnDayStarted()` 的 `ret` 确实是“日初始化完成后”的唯一边界，日期字段也在此时可用；
  但原始调用并不是天然每日一次：`_update` 尾部、SaveGameMenu/ShippingMenu 关闭、错误日志静态分支、`beginGame`（新游戏）、
  以及 `setGameMode(0)` 间接进入 `set_activeClickableMenu` 的退标题路径都可能调用它。去重设计可以覆盖这些路径，
  但在主机夹具证明“标题模式抑制 + 同日去重 + 线程归属”之前，不能把“恰好一次、主线程”写成已证明契约。
- 两个事件都**不**使用 `GameRunner.CreateGameInstance()` 作为边界：该调用只证明 `Game1` 实例存在，而且在本版本中
  它发生在启动/本地联机路径，不是单机读档路径；实例在标题界面就已存在，读档复用它。

---

## 1. 版本、范围与方法

### 1.1 三个版本号不要混用

| 维度 | 值 | 本轮用途 |
| --- | --- | --- |
| APK 显示版本 | `1.6.15.0` | 构建/安装与文档标识 |
| Android versionCode | `239` | 安装准入与版本绑定 |
| 托管游戏程序集版本 | `1.6.15.24354` | 注入点与 IL 绑定 |
| 程序集文件 | `StardewValley.dll`，9,280,000 B | 静态分析输入 |
| 程序集 SHA-256 | `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047` | 输入指纹 |
| 程序集 MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` | 输入指纹 |
| 原始/注入 | **原始** | 结论只基于原始程序集 |

同一原始字节在本机有三份副本（`.tmp/phase0.5/refs`、`.tmp/build-modded/refs`、`.tmp/build-loader-only/refs`），
SHA-256 全部相同；本轮又从仓库原始 `app/src/main/apkroot/assemblies/assemblies.blob`
（SHA-256 `c8ee504eea163d2c82d89d0004c8924314b3e6b41adb649ea5f136151eecabd9`，5,588,326 B）解出同一文件。
`.tmp/build-modded/patched` 与 `.tmp/build-loader-only/patched` 是**已注入的构建产物**（大小和 SHA-256 均不同），
只用于说明“当前注入副本不是原始输入”，不参与游戏逻辑结论。

### 1.2 输入与工具

| 项 | 内容 |
| --- | --- |
| 静态工具 | 本机临时 `IlProbe`（Mono.Cecil 0.11.6，.NET 8.0.404，只读；位于 `.tmp/phase2.1/`，gitignore） |
| 程序集提取 | 项目 `Tools/AssemblyStore/store.py`（XABA v1 / XALZ 解包，只读） |
| 证据根 | `.tmp/phase2.1/`（临时产物，不入库；报告中只保留最小定位与指纹） |
| 未使用 | 未运行游戏、未加载程序集执行、未启动模拟器/真机、未运行 ADB、未读写 Saves、未构建 APK |

### 1.3 方法

1. 先用 git/GitHub REST 核对本轮基线与 CI，再核对程序集指纹与原始/注入状态。
2. 用 Cecil 只读枚举：类型/方法签名、方法体 IL、异常处理区、分支目标、`call`/`callvirt`/字段引用；
   对关键结论用“调用点 + IL 偏移 + 调用者”交叉核对，而不是只看反编译文本。
3. 对“没有直接引用”的结论，额外扫描全部方法体中的调用目标和 `ldstr` 方法名字符串，并注明搜索口径与局限。
4. 对候选落点按同一张表比较；对拟推荐的 Cecil 候选检查入口/完成边、异常区、分支到 `ret`、最大栈、幂等与失败关闭。
5. 结论只覆盖 `StardewValley` 1.6.15.24354 / versionCode 239；不外推到其它版本、Android 11+、其它 OEM 或本地联机。

---

## 2. 预检摘要（完整命令与输出见研究报告）

| 项 | 本轮事实 | 类别 |
| --- | --- | --- |
| 仓库根 | `D:/Download/DSH/StardewValley` | CURRENT_STATIC |
| 分支 | `main` | CURRENT_STATIC |
| `HEAD` | `8712cf09a42e8336f97a0c6a3f5b63c69b1bb101` | CURRENT_STATIC |
| 本地 `origin/main` | 同上 | CURRENT_STATIC |
| GitHub 远端 `main`（REST） | 同上；提交时间 2026-11-17T02:35:01Z | CURRENT_STATIC |
| 工作区 | 本轮开始时干净；本轮只新增两份未跟踪文档 | CURRENT_STATIC |
| CI | Run `44004070683`，3/3 success；含 `APK install-admission synthetic tests`、Loader host tests、InjectorTests、PowerShell gates | CURRENT_STATIC |
| 历史交接 | 交接所述 SHA 与 Run 编号正确；但 `AGENTS.md` 的“当前状态”仍写 `97fdfa7`，是**过时文本**，不是本轮基线 | CURRENT_STATIC / HISTORICAL |

> `AGENTS.md` 是本地文件；本轮不修改它，也不把它过时的“97fdfa7 / Phase 2.0.1 未提交”当成现状。
> 本轮工作区新增文档未提交，因此这两份文档本身没有远端 CI；不能拿 `8712cf0` 的绿灯冒充“文档已过 CI”。

---

## 3. 路径与状态图

### 3.0 先看对象关系（这一版与旧可行性文档不同）

- `GameRunner` 是 MonoGame `Game` 子类，进程内 `GameRunner.instance` 一个。
- `GameRunner.Initialize()` → `InitializeMainInstance()` → `AddGameInstance(PlayerIndex)` → `CreateGameInstance(...)`
  → `new Game1(player_index, index)`。因此 **`Game1` 实例在启动/标题阶段就存在**，
  `Game1` 的 `Update`/`_update` 在标题界面也会运行（mode 0）。
- `CleanupReturningToTitle()` 对**主实例**只移除其它实例，保留自己；`ResetGameStateOnTitleScreen()` 清空世界状态。
  所以“同一进程回标题再读档”复用的是**同一个 `Game1` 对象**；`instanceId` 标识的是对象，不是一次世界生命周期。
- 单机读档不调用 `CreateGameInstance`；它复用启动时创建的 `Game1`，通过 `Game1.loadForNewGame(true)` 重新初始化世界。
- 本地联机/调试命令会调用 `AddGameInstance`，不是本研究的单机主路径；本文只在限制中标注，不把它当契约。

### 3.1 场景 A：冷启动到标题，不选择存档

**入口**：Activity → `MainActivity.OnCreatePartTwo` → `new GameRunner` → `Game.Run()` → `GameRunner.Initialize`
→ `InitializeMainInstance` → `new Game1`（实例存在，但无玩家/地点/日期）。

**中间态**：`Game1.Update` → `_update` 以 mode 0 走标题界面；`Game1.hasLoadedGame == false`；
`Game1.currentLocation == null`；`Game1.currentLoader == null`；`SaveGame.Load` 未被调用。

**成功/退出**：停在标题，等待用户操作；没有“进入世界”的成功边。

**能观察到的状态**：`Game1` 实例存在；`GameRunner.gameInstances` 有主实例；世界不可用。

**断点**：不要把“`Game1` 实例存在”或“`CreateGameInstance` 返回”当成世界可用；
`CreateGameInstance` 甚至不在这条单机读档路径上。

### 3.2 场景 B：从标题加载已有存档

**入口**：`TitleMenu`/`LoadGameMenu` 选择存档 → `SaveGame.Load(filename, false, false)`。

**中间态**（CURRENT_STATIC）：

1. `SaveGame.Load` 只把文件名/紧急标志写入静态字段，然后
   `MainActivity.PromptForPermissionsIfNecessary(LoadAfterPermissionCheck)`；此时**没有开始读世界**。
2. `LoadAfterPermissionCheck` 设置 `gameMode = loadingMode (6)`，创建
   `Game1.currentLoader = SaveGame.getLoadEnumerator(file, ...)`；`currentLoader` 只是枚举器，**尚未推进**。
3. `_update` 在 mode 6 进入 `UpdateTitleScreen` → `UpdateTitleScreenDuringLoadingMode` →
   `currentLoader.MoveNext()`，每帧推进一次。
4. 枚举器内部：读取/解析存档数据；调用 `Game1.loadForNewGame(true)`（普通单机分支在工作线程 Task 中执行）；
   再 `loadDataToFarmer` / `loadDataToLocations`；再设置 `gameMode = 3`（属性 setter，重置 `gameModeTicks`）；
   再 `resetForPlayerEntry()` 等收尾；再 `yield return 100`。
5. 下一次 `_update` 命中尾部：`gameMode == 3 && gameModeTicks == 1` → `Game1.OnDayStarted()` → 唯一正常 `ret`。
6. `OnDayStarted` 内部完成 NPC/地点/触发器/位置重置等（`hasStartedDay` 从 false 置 true），然后返回。

**成功完成**：`OnDayStarted()` 正常 `ret`（IL_0160）。此时玩家、当前地点、日期、`hasLoadedGame` 都已就绪；
`gameMode` 可能是 3；`currentLoader` **仍可能非 null**（见 §4.3）。

**失败/取消**：`ExitToTitle(...)` 把 `gameMode` 置 0；枚举器可能在之后仍然走到 `resetForPlayerEntry()` 和
`yield`，但不再有真正的日启动；`currentLoader` 最终可能由标题重置清空。详见场景 E。

**能观察到的状态**：世界可读；不保证淡入结束或输入已启用。

**迭代器四要素（静态）**：

- **启动点**：`SaveGame.Load` → `PromptForPermissionsIfNecessary(LoadAfterPermissionCheck)` →
  `gameMode = 6`、`currentLoader = getLoadEnumerator(...)`。返回枚举器**不等于**开始/完成载入。
- **推进点**：`_update` 在 mode 6 调用 `UpdateTitleScreenDuringLoadingMode()` →
  `currentLoader.MoveNext()`，每帧一次。
- **成功终点**：枚举器在数据恢复后 `set_game_mode(3)`，再做 `resetForPlayerEntry` 等收尾并 `yield return 100`；
  世界可读的完成边由下一帧 `_update` 尾部的 `OnDayStarted()` `ret` 给出。
- **失败/取消点**：`CancelToTitle`/异常 → `ExitToTitle` → `gameMode = 0`；枚举器可能继续走到 reset/yield，
  但不再有正常日启动。`currentLoader` 的 `MoveNext() == false` 成功分支在成功路径上不再被驱动。

**断点**：`UpdateTitleScreenDuringLoadingMode` 里“`MoveNext()` 返回 false 且 `gameMode == 3`”的完成分支，
在成功路径上**不会被再次驱动**：因为枚举器自己在中途把 `gameMode` 设成 3，`_update` 从下一帧起走 playing 分支，
不再调用 `UpdateTitleScreen`。因此不能把“`currentLoader` 完成”当作成功边；本方案的完成边是 `OnDayStarted` 的 `ret`。

### 3.3 场景 C：新建存档并首次进入世界

**入口**：标题/角色定制 → `Game1.loadForNewGame(false)`（`loadedGame == false`）；
部分路径随后 `NewDay(0)`、播放开场事件，或由 `Utility.generateNewFarm(false, true)` 迭代器生成农场。

**中间态**：

- `loadForNewGame(false)` 建立新世界基础（年份、农场、地点、玩家基础状态等），设置 `hasLoadedGame = true`。
- 开场事件结束时，`Event.endBehaviors` 把 `gameMode` 设为 3（属性 setter，重置 ticks）；下一帧 `_update` 尾部调用
  `OnDayStarted`。
- 跳过开场的 `TitleMenu.createdNewCharacter(skipIntro: true)` 路径：`loadForNewGame(false)` → `NewDay(0)` →
  `exitActiveMenu()` → `setGameMode(3)`（方法，不重置 ticks）；随后 SaveGameMenu/ShippingMenu 关闭时经
  `set_activeClickableMenu` 调用 `OnDayStarted`。`LoadNewGameFromCharacterCustomization` 在程序集内**没有直接调用者**
  （全方法体扫描为 0），不作为活路径证据。

**成功完成**：`OnDayStarted()` 的 `ret`；与 B 的共同边界是“日初始化完成”，特有分支是 `IsNewGame = true`。

**能观察到的状态**：新世界可读；不一定已经产生存档文件（首日结束保存是另一件事）。

**断点**：新游戏存在不止一条子路径；静态上能证明“至少一条路径会到 `OnDayStarted`”，
但“两条子路径是否可能先后都到”未证明，需要主机夹具或动态记录；本方案用同日去重保证最多一次。

### 3.4 场景 D：正常从当前日切换到下一日

**入口**：睡觉/`GameLocation.doSleep`（或其它 `NewDay` 调用者）→ `Game1.NewDay(timeToPause)`。

**中间态**：

1. `NewDay` 设置 `newDay = true`，启动淡出，必要时关闭活动菜单；**此时日期还没变**。
2. 淡出完成 → `newDayAfterFade(after)` → `ModHooks.OnGame1_NewDayAfterFade`。
3. 单机路径把 `_newDayAfterFade()` 迭代器包装成 `Task`（`_newDayTask`），在**工作线程**推进；
   日期字段在 `_newDayAfterFade` 内递增，接着做地点/NPC/天气/邮件等日更新。
4. 日更新末尾经 `showEndOfNightStuff()` 打开 ShippingMenu / SaveGameMenu；保存由 `SaveGameMenu.update` 驱动。
5. 主线程菜单更新中，`SaveGameMenu`/`ShippingMenu` 关闭 → `exitActiveMenu()` → `set_activeClickableMenu(null)`；
   旧菜单属于 SaveGameMenu/ShippingMenu 且新菜单不是这两类 → `wasMenuOpen == true` → `OnDayStarted()`。
6. `OnDayStarted()` → `ret`。

**成功完成**：`OnDayStarted()` 的 `ret`；日期已经是下一日，地点已重置。

**失败/中断**：保存失败/退出会走 `ExitToTitle`；`_newDayTask` 故障时 `_update` 在主线程抛错或断开客户端。
这些路径不应产生“正常的 DayStarted”。

**断点**：`_newDayAfterFade` 的完成**不是** DayStarted；它还在“夜间收尾”，日期虽已递增，
但 `OnDayStarted` 尚未执行。`NewDay` 入口更不是。

### 3.5 场景 E：读档失败或取消

**入口**：同 B，但存档读取/解析/权限/取消路径失败。

**中间态**（CURRENT_STATIC）：

- `SaveGame.CancelToTitle` 在枚举器多个位置被检查；检查为真时调用 `ExitToTitle(null)`，把 `gameMode` 置 0。
- 读文件/解析异常由 `LoadAfterPermissionCheck` 的 `catch` 或 `HandleLoadError` 转为 `ExitToTitle`；
  `SaveGame` 保存失败路径也会 `ExitToTitle`。
- 枚举器在 `ExitToTitle` 之后仍可能继续执行到 `resetForPlayerEntry()`，然后 `yield`；但 `gameMode` 已是 0。
- `_update` 的 `exitToTitle` 分支随后调用 `CleanupReturningToTitle()` → `ResetGameStateOnTitleScreen()`：
  `gameMode = 0`、`currentLoader = null`、`currentLocation = null`、`hasLoadedGame = false`、`hasStartedDay = false`。

**成功/退出**：回到标题；没有 `OnDayStarted` 的正常完成边。

**能观察到的状态**：世界不可用；`loadForNewGame` 可能已被调用过（pending 可能残留），但没有日启动。

**断点**：不能把“`loadForNewGame` 已经调用”或“`resetForPlayerEntry` 已经执行”当作读档成功；
`ExitToTitle` 之后仍可能执行这些代码。失败/取消的隔离必须由**完成边**和 `gameMode` 条件共同保证。

### 3.6 场景 F：同一进程回标题后重读同一档

**入口**：游戏内退出到标题（`ExitToTitle`）→ `CleanupReturningToTitle` → `ResetGameStateOnTitleScreen`；
再从标题加载同一存档。

**状态复用**：主 `Game1` 对象保留；`ResetGameStateOnTitleScreen` 清空 `currentLoader`、`currentLocation`、
`hasLoadedGame`、`hasStartedDay`、`gameMode = 0`；下一次读档复用该对象，并再次调用 `loadForNewGame(true)`。
上一轮世界生命周期在 `CleanupReturningToTitle`/`ResetGameStateOnTitleScreen` 结束；本方案不在该处派发
“世界结束”事件，只在下次 `loadForNewGame` 时递增世界代次。

**成功完成**：第二次 `OnDayStarted()` 的 `ret`；同一逻辑日期允许再次触发，因为这是一次新的世界进入。

**断点**：不能用 `Game1` 对象地址、`instanceId`、Loader session 或存档名去重；它们都不能区分“同一次世界进入”和
“回标题后重新进入”。本方案用 `loadForNewGame` 递增的世界代次来区分。

### 3.7 场景 G：同一进程回标题后切换不同档

**入口/状态**：同 F，但选择不同存档。

**成功完成**：新的 `loadForNewGame(true)` → 新世界代次 → 第二次 `OnDayStarted()` 的 `ret`；
日期字段来自新存档，可能与旧档同日期；新代次允许触发。

**断点**：Loader 会话与存档选择无关；一次 Loader 会话可以在多个世界之间持续存在；
绝不能用 Loader session 计数去重存档载入。

### 3.8 场景 H：同一游戏日重复开关菜单、过渡中恢复或相关路径重入

**已知重复来源**（CURRENT_STATIC）：

- `_update` 尾部：只要 `gameMode` 以属性 setter 变为 3，`gameModeTicks` 被重置，下一次 `_update` 就可能再次调用
  `OnDayStarted`。静态可见的来源包括：多人加入（`Client.setUpGame`/`FarmhandMenu` 设 3）、新游戏开场
  `beginGame` 事件结束（`Event.endBehaviors` 设 3，属于新世界路径，不是同一日的二次日启动）、以及
  `UpdateControlInput` 中的 `errorLogMode 11 → 3` 分支（该分支的入口可达性需动态确认）。
- `set_activeClickableMenu`：SaveGameMenu/ShippingMenu 关闭时触发；若同一逻辑日重复出现这两类菜单的关闭，
  可能重复调用。
- `setGameMode(0)` 间接路径：`ExitToTitle` 先 `setGameMode(0)`，后者创建 TitleMenu 并调用
  `set_activeClickableMenu`；若旧菜单是 SaveGameMenu/ShippingMenu，`wasMenuOpen` 为真，会在 mode 0 下调用
  `OnDayStarted`。例如保存失败时退出标题，就可能走到这里（控制流可达，实际失败未触发）。

**能观察到的状态**：`OnDayStarted` 可能被多次调用；`hasStartedDay` 会把 NPC/任务块限制为“本世界首次”，
但 `TriggerActionManager.Raise("DayStarted")`、地点重置和 `ret` 仍会再次执行。

**断点**：不能写“两个直接来源 = 必然重复”，也不能写“天然恰好一次”。正确表述是：
**正常路径通常一次，但存在可证明的重复/间接路径；必须去重，且必须用 `gameMode != 0` 隔离退标题路径。**

### 3.9 最小状态转换图

```text
[进程启动]
  └─ GameRunner.Initialize -> new Game1（实例存在，世界不可用）
       └─ 标题(mode 0)
            ├─ 加载存档 -> SaveGame.Load -> mode 6 -> currentLoader
            │     └─ MoveNext* -> loadForNewGame(true) -> loadData* -> set_gameMode(3)
            │           -> resetForPlayerEntry -> yield 100
            │           -> 下一帧 _update 尾部 -> OnDayStarted() -> ret
            │                 └─ 成功：世界可读（本方案 SaveLoaded + DayStarted）
            │                 └─ 失败/取消：ExitToTitle -> mode 0（不得派发）
            ├─ 新建存档 -> loadForNewGame(false) -> NewDay/开场事件或 SaveGameMenu
            │     └─ OnDayStarted() -> ret（IsNewGame = true）
            └─ 读档失败/取消 -> ExitToTitle -> mode 0 -> 标题
[世界内]
  └─ 睡觉 -> NewDay -> 淡出 -> _newDayTask（工作线程）-> 日期递增/日更新/保存
        └─ 主线程 SaveGameMenu/ShippingMenu 关闭 -> set_activeClickableMenu(null)
              -> OnDayStarted() -> ret（DayStarted）
[回标题]
  └─ ExitToTitle -> CleanupReturningToTitle -> ResetGameStateOnTitleScreen
        -> 同一 Game1 对象保留；世界状态清空；下一次 loadForNewGame 新代次
```

---

## 4. 世界就绪、调用路径与线程

### 4.1 `GameRunner.CreateGameInstance()` 返回时能证明什么

**只能证明“`Game1` 实例存在”，不能证明“目标世界可用”。**

- `CreateGameInstance(PlayerIndex, int)` 的完整方法体只有 4 条指令：
  `ldarg.1; ldarg.2; newobj Game1::.ctor(PlayerIndex, int); ret`。
- 它被 `AddGameInstance` 调用；`AddGameInstance` 之后才做 `Instance_Initialize()`、`Instance_LoadContent()`、
  `SaveInstance()` 等实例级工作，且**不做存档数据恢复**。
- 单机读档不调用 `CreateGameInstance`；启动时 `InitializeMainInstance` 就已经创建了 `Game1`。
- `Game1..ctor()` 只设置少量字段、`instanceId`、`Program.gamePtr`、临时内容管理器；没有玩家、地点、日期。

因此，“实例存在”和“世界可用”之间还隔着：`loadForNewGame`（建立地点/玩家/年份等）、
`loadDataToFarmer`/`loadDataToLocations`（恢复存档数据）、最终 `set_gameMode(3)`、`resetForPlayerEntry`、
以及 `OnDayStarted` 的日初始化。

### 4.2 `Game1.OnDayStarted()` 的来源复核

**签名**：`public static void StardewValley.Game1::OnDayStarted()`；
**方法体**：114 条 IL、**无异常处理区**、**唯一 `ret` 在 IL_0160**；
**分支**：IL_0159 `brfalse.s IL_0160` 直接指向该 `ret`（因此完成边注入必须重定向该分支，不能简单 `InsertBefore(ret)`）。

**直接调用者（全程序集扫描，精确匹配 DeclaringType + Name）**：恰好 2 处。

1. `Game1._update(GameTime)` 的 IL_1480：
   ```text
   IL_1470 call  Game1::get_gameMode()
   IL_1475 ldc.i4.3
   IL_1476 bne.un.s IL_1485
   IL_1478 call  Game1::get_gameModeTicks()
   IL_147d ldc.i4.1
   IL_147e bne.un.s IL_1485
   IL_1480 call  Game1::OnDayStarted()
   IL_1485 ret
   ```
   前置条件：`gameMode == 3` 且 `gameModeTicks == 1`。`_update` 在方法头 IL_000f-0016 每帧把 `gameModeTicks` 加一；
   `set_gameMode`（属性 setter）在值变化时把 ticks 重置为 0，而 `setGameMode`（方法）不重置。因此“属性设为 3 后的下一次
   `_update`”会命中一次。

2. `Game1.set_activeClickableMenu(IClickableMenu)` 的 IL_0080：
   ```text
   IL_0000..IL_0032  wasMenuOpen = (旧菜单是 SaveGameMenu 或 ShippingMenu)
                                  && (新菜单不是 SaveGameMenu 且不是 ShippingMenu)
   IL_0077 stsfld Game1::_activeClickableMenu
   IL_007d ldloc.0
   IL_007e brfalse.s IL_0085
   IL_0080 call Game1::OnDayStarted()
   ```
   前置条件：离开 SaveGameMenu/ShippingMenu 这一“夜间菜单族”，且目标是普通菜单或 null。
   典型路径：`SaveGameMenu.update`/`ShippingMenu.update` → `exitActiveMenu()` → `set_activeClickableMenu(null)`。

**间接路径（重要）**：`setGameMode(0)` 在 mode 0 分支创建 `TitleMenu` 并调用 `set_activeClickableMenu(TitleMenu)`；
`ExitToTitle` 先调用 `setGameMode(0)`，再设置 `exitToTitle`。若此时旧菜单是 SaveGameMenu/ShippingMenu，
`wasMenuOpen` 为真，**会在 mode 0 下调用 `OnDayStarted`**。保存失败退出标题就是可静态追踪到的例子。
因此“两个直接来源”之外还有一条“退标题间接来源”；它不能产生合法日启动，必须由 `gameMode != 0` 条件隔离。

**反射/字符串搜索口径**：扫描全部 30,067 个方法体中的 `call`/`callvirt` 目标；扫描所有 `ldstr` 中是否出现
`OnDayStarted`；结果：2 个直接调用点，0 个 `ldstr "OnDayStarted"`。`ldstr "DayStarted"` 只出现在
`OnDayStarted` 自身的 `TriggerActionManager.Raise("DayStarted")` 和 `TriggerActionManager..cctor`。
这不能证明不存在其它间接派发；相反，上面已经找到一条经 `setGameMode(0)` 的间接路径。

### 4.3 “世界就绪”的最低可用条件

以下条件只用本版本已确认的字段/属性；它们是**消费者可读**的条件，不是“输入已启用/淡入已结束”的保证。

| 条件 | 证据 | 说明 |
| --- | --- | --- |
| `Game1.game1 != null` | 启动时 `AddGameInstance` 设置 | 实例存在是必要但远不充分 |
| `Game1.player != null` | `OnDayStarted` 内部直接使用 `Game1.player.team...`（IL_00b3） | 若为空，方法本身会抛异常，不会到 `ret` |
| `Game1.currentLocation != null` | 读档/新游戏设置；`OnDayStarted` 对 player.currentLocation 做 null 检查后 `resetForPlayerEntry` | 到 `ret` 时正常路径非空 |
| `Game1.hasLoadedGame == true` | `loadForNewGame` IL_0434 设为 true | 区分标题态；退标题时 `ResetGameStateOnTitleScreen` 设回 false |
| `Game1.year` / `season` / `dayOfMonth` | `loadForNewGame`/读档恢复/跨日递增 | 到 `ret` 时已由游戏设置；本方案把它们用作日期键，不单独当作完成证明（不在此处编造合法范围） |
| `Game1.hasStartedDay == true` | `OnDayStarted` IL_014e 设为 true | 到 `ret` 时正常为 true；但它不是每日重置标志 |
| `Game1.gameMode != 0` | `ExitToTitle`/`ResetGameStateOnTitleScreen` 设 0 | 用于隔离退标题的间接路径；正常日启动为 3/7/8 |
| `Game1.currentLoader` | 读档成功时由加载枚举器赋值 | **不是**就绪条件；见下 |
| `GameRunner.activeNewDayProcesses` | 单机日启动通常为空 | 不作为条件；本地联机未覆盖 |

**`currentLoader` 不是就绪屏障（关键）**：成功读档时，枚举器在 `resetForPlayerEntry` 之后 `yield return 100`，
但 `gameMode` 已经是 3；`_update` 从此走 playing 分支，不再调用 `UpdateTitleScreen`，因此
`UpdateTitleScreenDuringLoadingMode` 的“`MoveNext()` 返回 false”分支不会再被驱动。`currentLoader` 可以保持非 null，
直到标题重置。把“`currentLoader == null`”当成功条件会永远等不到，或错误地等到下一次标题重置。

**不要把“下一个 Tick”当就绪屏障**：本方案的完成边是 `OnDayStarted` 的 `ret`，不是“再等一帧”。`OnDayStarted` 由
`_update` 尾部或菜单关闭路径同步调用；它完成时世界状态已初始化。若未来采用 Tick 观察方案，必须另行证明其状态条件，
不能借用本结论。

### 4.4 线程证据

| 路径 | 静态调用关系 | 线程判断 | 类别 |
| --- | --- | --- | --- |
| 读档/新游戏的 `OnDayStarted` | `GameRunner.Update` → `Game1.Instance_Update` → `InstanceGame.Update`（虚调用）→ `Game1.Update` → `_update` → 尾部 | 游戏循环线程；历史 Phase 2.0 的 Tick/GameLaunched 在 V1963A 记录为 managed thread 1 | CURRENT_STATIC + HISTORICAL（标题路径） |
| 跨日的 `OnDayStarted` | `SaveGameMenu.update`/`ShippingMenu.update`（主线程菜单更新）→ `exitActiveMenu` → `set_activeClickableMenu` | 预期同一游戏循环线程；但 `_newDayAfterFade` 在工作线程调用 `showEndOfNightStuff`，后者也会设置菜单 | CURRENT_STATIC + INFERENCE；**仍需动态复核** |
| `loadForNewGame(true)` | 单机读档的普通分支由 `new Task(...)` + `ModHooks.StartTask` 执行 | **工作线程**；它只写 pending，不派发事件 | CURRENT_STATIC |
| `_newDayTask` | `Game1._update` 创建工作线程 Task，`_newDayAfterFade` 迭代器在 Task 内跑完 | 工作线程；其 `showEndOfNightStuff` 若在旧菜单为 save/shipping 时设置非 save/shipping 菜单，理论上可在工作线程触发 `OnDayStarted` | INFERENCE；需动态线程记录 |

**结论**：不能把“历史标题路径的 Tick 在 thread 1”写成“SaveLoaded/DayStarted 已经主线程验证”。
下一轮必须记录两个事件的 `ManagedThreadId` 并与首个 Tick 的线程比较；若跨日事件在工作线程派发，
要么增加线程约束/丢弃策略，要么把该候选降级。

---

## 5. 候选点比较

### 5.1 `SaveLoaded` 候选

| 编号 | 事件 | 完整签名与 IL 定位 | 所处语义阶段 | 哪些场景命中 | 漏发/误发 | 重复来源 | 失败/取消隔离 | 线程证据 | 版本绑定/控制流风险 | 结论 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| S-A | SaveLoaded | `Game1 GameRunner::CreateGameInstance(PlayerIndex, int)`，IL_0000-0007，仅 `newobj Game1::.ctor` | 实例创建 | 启动/本地联机 | 单机读档不调用；无数据恢复；标题也“命中” | 无 | 无关 | 游戏循环/启动 | 低；但语义过早 | **淘汰：过早** |
| S-B | SaveLoaded | `void GameRunner::AddGameInstance(PlayerIndex)`，80 ins；IL_0092 设置 `Game1.game1`，IL_0098 `Instance_Initialize`，IL_00a6 `Instance_LoadContent`，IL_00b7 `LoadInstance` | 实例加入 + 实例级初始化 | 启动/本地联机 | 单机读档不新增实例；无存档数据恢复 | 无 | 无关 | 游戏循环 | 中；本地联机分支多 | **淘汰：过早/不完整** |
| S-C | SaveLoaded | `void Game1::loadForNewGame(bool)`，325 ins；入口 IL_0000，`ret` IL_0499 | 世界基础初始化 | 读档（true）与新游戏（false） | 读档时在 `loadDataTo*` 之前；工作线程；失败后仍可能已执行 | 每次世界创建一次 | 有失败路径但不代表成功 | 读档普通分支为工作线程 | 中；有 3 个 finally，但入口在保护区外 | **淘汰：过早**；仅作为 `IsNewGame`/pending 辅助 |
| S-D | SaveLoaded | `SaveGame/<getLoadEnumerator>d__123::MoveNext()`，1994 ins；`loadForNewGame` IL_038c，`loadDataToLocations` IL_0dbf，`resetForPlayerEntry` IL_1c22，`yield` IL_1c81，`ret false` IL_1c9a | 读档状态机内部 | 读档成功 | 取消路径也走到 IL_1c22；编译器生成类型；外层完成分支不可达 | 每次读档一个枚举器 | **误发**：`CancelToTitle` 后仍可执行到 reset | 主线程推进（`UpdateTitleScreenDuringLoadingMode`） | 高：状态机/多 finally/取消路径 | **淘汰：误发风险高**（除非额外加 `gameMode` 守卫与状态机形状校验） |
| S-E | SaveLoaded | `Game1.loadForNewGame(bool)` 入口 + `Game1.OnDayStarted()` 唯一 `ret` IL_0160（分支 IL_0159 需重定向） | 世界初始化开始 + 日初始化完成 | 读档成功、新游戏成功、本地联机加入 | 失败/取消不派发；标题模式由 `gameMode == 0` 抑制 | pending 只消费一次；新代次才重新武装 | 较好：完成边 + 模式守卫 + pending | 读档完成边在 `_update` 尾部（游戏循环） | 中：两处注入；`OnDayStarted` 有分支到 `ret`；`loadForNewGame` 工作线程只写 pending | **推荐（GO）** |
| S-F | SaveLoaded | `Game1.set_gameMode(byte)`（属性 setter），36 ins；IL_004c 写 `_gameMode`，IL_0052-0053 重置 ticks | 模式切换 | 读档/新游戏/错误日志分支/加入 | 在 `resetForPlayerEntry` 之前；错误日志静态分支也会命中（可达性待验证） | 多次 | 差 | 主线程/工作线程均可能 | 低 IL 风险、语义过早 | **淘汰：过早** |
| S-G | SaveLoaded | `Game1.loadForNewGame(bool)` 入口，`ldarg.1` 可用 | 世界创建开始 | 读档/新游戏 | 不作为完成边 | 每世界一次 | 仅标记 | 工作线程/主线程 | 低 | **仅辅助：提供 `IsNewGame`** |

### 5.2 `DayStarted` 候选

| 编号 | 事件 | 完整签名与 IL 定位 | 所处语义阶段 | 哪些场景命中 | 漏发/误发 | 重复来源 | 失败/取消隔离 | 线程证据 | 版本绑定/控制流风险 | 结论 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| D-A | DayStarted | `Game1.NewDay(float)`，63 ins；IL_0034 `newDay=true`，IL_0061 起淡出 | 夜间过渡开始 | 跨日 | 日期未变；不覆盖读档入口日 | 多处 NewDay 调用 | 失败仍可能继续 | 游戏循环 | 低；语义过早 | **淘汰：过早** |
| D-B | DayStarted | `Game1._newDayAfterFade()` 迭代器；`MoveNext` 约 2.4k 行；日期递增、日更新、`showEndOfNightStuff`，结尾 `ret false` | 日更新/夜间收尾 | 跨日 | 工作线程；保存/菜单/OnDayStarted 还在后面 | 每次跨日一个 Task | 故障会使 `_newDayTask` 失败 | **工作线程** | 高：大状态机 | **淘汰：过早/线程不符** |
| D-C | DayStarted | `Game1.set_activeClickableMenu` IL_0080；`wasMenuOpen` IL_0000-0032 | 夜间菜单族关闭 | 跨日（SaveGameMenu/ShippingMenu 关闭） | 读档/新游戏入口日不命中；可能被标题路径误用 | 同类菜单重复关闭 | `gameMode==0` 时是误发 | 主线程菜单更新 | 中 | **淘汰：不完整**（只能作跨日补充） |
| D-D | DayStarted | `Game1._update` IL_1480 | 模式 3 的下一帧 | 读档/新游戏/错误日志分支/加入 | 普通跨日（方法 set mode 3，不重置 ticks）通常不命中 | 错误日志静态分支/加入可重复 | 退标题时 `gameMode 0` 不命中 | 游戏循环 | 低；但覆盖面不对 | **淘汰：不完整** |
| D-E | DayStarted | `Game1.OnDayStarted()` 入口，IL_0000 | 日初始化开始 | 所有日启动 | 消费者看到的是 reset 前状态；重复调用 | 与 D-F 相同 | 异常则无完成 | 同上 | 低 | **淘汰：过早** |
| D-F | DayStarted | `Game1.OnDayStarted()` 唯一正常 `ret` IL_0160；IL_0159 分支需重定向 | 日初始化完成 | 读档、新游戏、跨日 | 需要 `gameMode != 0` 抑制退标题；同日重复需去重 | 直接 2 处 + 间接 `setGameMode(0)` 路径 + 错误日志静态分支/`beginGame`（新游戏）/加入（多人） | 较好，但需守卫与去重 | 预期游戏循环；工作线程菜单路径待复核 | 中；单 ret、无 EH，分支到 ret 可重定向 | **推荐候选（HOLD）** |
| D-G | DayStarted | `Game1.hasStartedDay`：cctor 初始、OnDayStarted IL_00a9/014f、Reset IL_02c6 | 首日/防御标志 | 不是每日 | 只在世界首次/回标题后重置；不能表达每日 | 不适用 | 不适用 | 不适用 | 低 | **淘汰：语义错误** |
| D-H | DayStarted | `TriggerActionManager.Raise("DayStarted")`，`OnDayStarted` IL_000a | 日初始化最开始 | 所有 OnDayStarted 调用 | 在 NPC/地点 reset 之前；重复调用 | 与 OnDayStarted 相同 | 无完成语义 | 同上 | 低；但过早 | **淘汰：过早**（不是 C# 事件 API） |

### 5.3 被排除的落点（至少一个过早/不完整/不可去重）

- **`CreateGameInstance` / `AddGameInstance` 返回**：只证明实例存在；不恢复存档数据；单机读档根本不走
  `CreateGameInstance`。这是“过早”的典型。
- **`loadForNewGame` 的 `ret`**：读档时它在 `loadDataToFarmer`/`loadDataToLocations` 之前，且工作线程；
  新游戏时它在开场事件之前。消费者若在此读取世界，会看到未恢复/未初始化的状态。
- **`SaveGame.getLoadEnumerator` 内部 `resetForPlayerEntry` 之后**：取消路径 `ExitToTitle` 后仍可能执行到这里；
  单看这一行会误发 `SaveLoaded`。除非再加 `gameMode == 3`/`CancelToTitle` 守卫，否则不选。
- **`hasStartedDay` 转变**：它不是每日重置；把它当 DayStarted 会从第二天起漏发。
- **`currentLoader` 完成**：成功路径上不会被再次驱动；把它当完成边会永远等不到或误判。
- **`NewDay` 入口 / `_newDayAfterFade` 完成**：日期/日更新/保存/菜单都还没完成；后者还在工作线程。

### 5.4 推荐 Cecil 候选的 IL 形状与验证

推荐两个钩子（只设计，不实现）：

```text
[1] Game1.loadForNewGame(bool) 入口（第一条指令之前）
      ldarg.1
      call ModFramework.Bootstrap.WorldLoading(bool)

[2] Game1.OnDayStarted() 唯一正常 ret IL_0160 之前
      call Game1::get_gameMode()
      ldsfld Game1::year
      ldsfld Game1::season
      conv.i4
      ldsfld Game1::dayOfMonth
      call ModFramework.Bootstrap.DayStarted(int, int, int, int)
      ret
```

- `loadForNewGame` 的入口在第一个 finally（IL_01e8）之外；插入 `ldarg.1` + 单参数 `call` 不改变原栈。
  该方法有 3 个 finally：`[IL_01e8, IL_0204) → handler [IL_0204, IL_0212)`、
  `[IL_022c, IL_0256) → handler [IL_0256, IL_0264)`、`[IL_02b6, IL_0392) → handler [IL_0392, IL_03a0)`；
  入口钩子不落在任何受保护区间内。
- `OnDayStarted` 无异常区，唯一 `ret` 在 IL_0160；IL_0159 的 `brfalse` 必须重定向到新钩子调用，
  否则该分支会直接 `ret`，漏掉事件。现有注入器已有“分支直跳 ret 重定向 + 拒绝边直指 ret”的机制
  （Phase 2.0.1 的 `HasCompleteCompletionHook` 与合成负向用例），可以复用；但不能用普通 `InsertBefore(ret)`。
- `OnDayStarted` 的 `maxstack` 是 6，钩子只需 4 个 int，不增加最大栈；`loadForNewGame` 的 `maxstack` 是 5，钩子只需 1。
- 守卫建议先取 `gameMode != 0`（隔离退标题的间接路径）；若主机/设备证明正常完成边只在 mode 3，可收紧为 `gameMode == 3`，
  以进一步缩小误发面。收紧前不要把 7/8 当成合法日启动证据。
- 不是状态机：`loadForNewGame` 是普通方法；`OnDayStarted` 是普通静态方法。未选 `SaveGame.getLoadEnumerator` 的编译器生成类型。
- 入口/完成边：`loadForNewGame` 是入口钩子（只记录“世界开始 + 是否新游戏”）；`OnDayStarted` 是**单一正常完成出口**。
  正常出口只有 IL_0160 的 `ret`；异常路径没有 `ret`（方法无 catch/finally，异常直接传播，不派发）；
  分支绕过是 IL_0159 的 `brfalse` 直跳 `ret`，必须重定向；没有 `leave`/`switch` 指向该 `ret`。
  不要在该方法上假设“任意 `ret` 都安全”，也不要使用普通 `InsertBefore(ret)`。
- 失败关闭：精确匹配 `StardewValley.Game1` + 方法名 + 返回 `void` + 参数 `(bool)` / `()`；
  方法体存在；`OnDayStarted` 只允许一个正常 `ret`，且所有指向它的分支必须可重定向；`ret` 不得在异常区内；
  找不到/多重载/方法体为空/形状不符 → 构建失败，不静默打错位置。
- 未来验证“应有/应无”：`ModInjector verify` 断言 `WorldLoading` 与 `DayStarted` 各 1 次、目标正确；
  baseline / loader-only 断言 0 次；最终 APK 解出的程序集再跑一次 verify，而不是只看构建日志。
- 每 Tick 开销：两个钩子都是每世界/每日一次；没有每 Tick 反射、字符串构造、快照分配或重复扫描。
  `Tick` 路径不变。Phase 2.0.1 的桌面 `0 B/tick` 只是既有 Tick 的桌面分配事实，不能外推为新事件或 Android 帧率结论。
- 幂等/混合状态：对已注入产物再次 patch，两个调用各仍为 1；loader-only 与 event 版混用要明确拒绝。

### 5.5 由游戏循环观察状态转换的方案（比较）

只有在“完成边不可注入”时才考虑此方案；本轮没有把它作为推荐：

| 方案 | 做法 | 优点 | 关键问题 | 结论 |
| --- | --- | --- | --- | --- |
| Tick 采样 `gameMode/currentLoader` | 在 `GameRunner.Update`/`Tick` 中比较前后值 | 不动 `Game1` 方法；可复用现有 Tick 钩子 | `currentLoader` 成功后**不归零**；`gameMode` 3 也会被错误日志/加入重复进入；需要每 Tick 读多个静态字段/分支；无法区分成功/失败/取消 | **不推荐** |
| Tick 采样日期字段 | 检测 `year/season/dayOfMonth` 变化 | 实现简单 | 日期变化不是日初始化完成证明；加载中日期可能已被写入；无法区分重载/同日重复；会误发 | **淘汰：不满足完成语义** |
| Tick 采样 `hasStartedDay` | 检测 false→true | 看似“首次日启动” | 它只在世界首次/回标题重置，不是每日；会从第二天起漏发 | **淘汰：语义错误** |
| 菜单/事件专用钩子 | `set_activeClickableMenu` 等 | 覆盖跨日 | 读档/新游戏入口日不覆盖；退标题间接路径会误发 | **只能作补充，不能单独作事件边界** |

如果未来仍要采用 Tick 观察：至少需要 `gameMode`、`currentLoader`、`hasLoadedGame`、日期、玩家/当前地点等状态；
每 Tick 至少是多次静态读取 + 分支（还要处理快照/去重），不是零成本；下一轮必须用
`Stopwatch.GetTimestamp()` 与 `GC.GetAllocatedBytesForCurrentThread()` 测量，不能把桌面上 Phase 2.0.1 的
`0 B/tick` 当作新方案或 Android 帧率结论。

---

## 6. 契约草案

> 本节是**产品语义草案**。`SaveLoaded` 判定 GO，`DayStarted` 判定 HOLD；两者都未实现，不能写成“已可用”。

### 6.1 `SaveLoaded`

**消费者语义（推荐）**：一次成功进入世界，且世界已经可供 Mod 读取（玩家、当前地点、日期、`hasLoadedGame` 已就绪）。
它**不**表示：读档请求已发出、文件已解析、屏幕已淡入结束、输入已启用、存档已重新写盘。

| 项 | 草案 |
| --- | --- |
| 触发点 | `Game1.loadForNewGame(bool)` 入口记录 pending；`Game1.OnDayStarted()` 唯一正常 `ret` 消费 pending |
| 次数 | 每次成功世界进入 **1 次**；失败/取消 **0 次**；标题 **0 次** |
| 新建存档 | **推荐纳入**，`IsNewGame = true`；`loadForNewGame(false)` 已提供精确区分，不需要猜日期/存档名 |
| 再次进入同一存档 | 每次重载 **+1**（新世界代次）；不按存档名/进程/Loader session 去重 |
| 切换不同存档 | 每次切换 **+1**；旧世界在回标题时结束 |
| 事件参数 | `bool IsNewGame`、`int ThreadId`（只读 struct；不暴露 `StardewValley.*` 类型） |
| 顺序 | 同一日启动中，`SaveLoaded` 先于 `DayStarted`；两者在同一钩子调用内按此顺序派发 |
| 失败隔离 | 没有 `OnDayStarted` 完成边就不派发；`gameMode == 0` 抑制退标题间接路径；pending 只被一次成功日启动消费 |
| 线程 | 静态证据为游戏循环线程；读档的 `loadForNewGame` 在工作线程只写 pending，不派发；设备线程仍需记录 |
| 版本/会话 | 世界事实，不随 Loader 会话替换重置；`GameLaunched` 仍是进程级至多一次 |

**新建存档纳入的代价/收益**：

- 纳入：消费者有一个统一的“世界可用”时刻；首日 Mod 不需要额外分支；实现代价小（`loadForNewGame` 已有 bool）。
  代价是 `SaveLoaded` 这个名字会让部分人以为一定有存档文件；文档必须写清“新游戏也会触发”。
- 不纳入：需要在 `loadForNewGame(false)` 时抑制事件，并让首日消费者改等 `DayStarted`；C 行会变成
  `SaveLoaded 0 / DayStarted 1`，设备场景也要多一条“新建测试档不应有 SaveLoaded”的断言。
- **推荐纳入**；事件命名是否改为 `WorldReady` 标为 `OPEN_DECISION`（不阻塞实现，但会影响公开 API 文案）。

### 6.2 `DayStarted`

**消费者语义（推荐）**：一个可玩的游戏日初始化完成，Mod 可以读取该日的玩家/地点/日期/世界状态。
它**不**保证淡入结束、输入启用或当天没有后续事件；也不把“日期字段变了”单独当作完成证明。

| 项 | 草案 |
| --- | --- |
| 触发点 | `Game1.OnDayStarted()` 唯一正常 `ret` IL_0160 |
| 入口当天 | **触发**；读档/新游戏的首日先 `SaveLoaded` 后 `DayStarted` |
| 正常跨日 | 每次从上一日推进到下一日 **+1**；日期在 `_newDayAfterFade` 内已递增，`OnDayStarted` 完成日初始化 |
| 同世界同逻辑日 | 最多 **1 次**；重复的 `OnDayStarted` 调用被同日键抑制 |
| 同进程回标题再进入同日 | 允许 **+1**；新 `loadForNewGame` 产生新世界代次，旧键不阻止新世界 |
| 去重键 | `(worldGeneration, year, season, dayOfMonth)`；`worldGeneration` 由 `loadForNewGame` 递增 |
| 重置条件 | 新 `loadForNewGame`（读档/新游戏/加入）；进程重启；**不**用对象地址、真实时间、Loader session、存档名 |
| 日期无效 | 不派发 `DayStarted`，记录 `InvalidDayKey`；`SaveLoaded` 仍可按 pending 派发 |
| 事件参数 | `int Year`、`int Season`、`int DayOfMonth`、`bool IsFirstDayOfWorld`、`int ThreadId`；建议含 `IsFirstDayOfWorld` 便于区分入口日 |
| 顺序 | `SaveLoaded`（若有）先于 `DayStarted`；两者状态相同（日初始化完成后） |
| 失败/取消 | 无完成边 → 0；`gameMode == 0` 抑制退标题间接路径；保存失败不得产生正常 DayStarted |
| 线程 | 静态路径为游戏循环；跨日菜单路径需动态线程记录；未证明前不得承诺主线程 |

**如果采用“入口日不触发 DayStarted”的另一种语义**：B/C 行的 `DayStarted` 变为 0，只有 `SaveLoaded`；
D 行仍为 1；设备场景“读已有测试档”的期望从 1 改为 0，“跨一天”仍为 1；`IsFirstDayOfWorld` 语义也要改。
本文件**推荐入口日触发**，因为消费者需要一个“首日也可用”的统一边界；另一种语义会迫使首日 Mod 单独处理
`SaveLoaded` 而没有 DayStarted。

### 6.3 保持不变

- `GameLaunched`：进程级至多一次；新 Loader 会话不重置。
- `Tick`：每次进入 `GameRunner.Update` 一次，不是渲染帧/60 Hz/真实时间。
- 回调隔离、重入拒绝、订阅快照、每 handler 独立 `try/catch`、有界日志、Loader 会话替换/失败回退语义均不变。
- 程序集绑定身份仍为 `1.0.0.0`；Framework API 仍为 `1.1.0`；本轮**不作版本升级**。
  下一轮若真的实现新事件，是否把 API 升到 `1.2.0` 是另一项决定，不在本文件承诺。
- 不扩大 Android 11+；不引入 Harmony/NDK/原生探针/SMAPI 兼容层/内容管线/存档 API。

---

## 7. 触发矩阵（A–H）

> 计数口径：`0`/`1`/`+1`；`+1` 表示“本次动作新增一次”，不是“总计 2”。
> “候选原始”是未去重的 `OnDayStarted`/`loadForNewGame` 可观察调用；“候选派发”是本方案去重后的事件。
> `DayStarted` 的派发列是**契约草案 + HOLD**，不是已可实施承诺。

| 场景 | 推荐契约 SaveLoaded | 推荐契约 DayStarted | 相对先后/状态要求 | 候选原始调用（静态） | 候选派发（去重后） | 静态可证明行为 | 动态待验证 | 证据编号 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A 冷启动到标题，不选档 | 0 | 0 | 无 | `loadForNewGame` 0；`OnDayStarted` 0 | 0 / 0 | `Game1` 实例存在但 `hasLoadedGame=false`、`currentLocation=null`；无日启动 | 标题停留期间事件计数保持 0 | E-04/E-06/E-15 |
| B 读已有档 | 1 | 1 | SaveLoaded → DayStarted；玩家/地点/日期/`hasLoadedGame` 就绪；不要求 `currentLoader` 为 null | `loadForNewGame` 1；`OnDayStarted` 1（mode3/ticks1） | 1 / 1 | 读档成功路径经 `loadForNewGame(true)`、数据恢复、mode3、`resetForPlayerEntry`，下一帧尾部命中 | 实际计数、线程、`currentLoader` 仍非 null、状态读取 | E-09/E-10/E-06 |
| C 新建档首次进入 | 1（`IsNewGame=true`） | 1 | SaveLoaded → DayStarted；首日状态 | `loadForNewGame` 1（false）；`OnDayStarted` ≥1（开场事件结束或菜单关闭） | 1 / 1 | 新游戏至少一条子路径到 `OnDayStarted`；两条子路径是否相继未证明；同日去重保证 ≤1 | 两条子路径的实际选择、计数、顺序 | E-12/E-06/E-13 |
| D 正常跨一天 | 0（本次跨日不新增） | 1 | DayStarted；日期已是下一日；地点已重置 | `OnDayStarted` 1（SaveGameMenu/ShippingMenu 关闭） | 0 / 1 | `_newDayAfterFade` 递增日期并做日更新；菜单关闭经 `set_activeClickableMenu` 触发 | 菜单关闭线程、计数、日期只加一天、无重复 | E-11/E-07 |
| E 读档失败/取消 | 0 | 0 | 无；回到标题 | `OnDayStarted` 0，或经 `setGameMode(0)` 间接 1 但 mode 0 被抑制 | 0 / 0 | `ExitToTitle` 设 mode 0；`CancelToTitle` 多处检查；失败不产生正常日启动 | 取消/失败路径的实际分支、无事件、回到标题稳定 | E-08/E-14/E-15 |
| F 回标题后重读同一档 | +1（本次重载） | +1（同日允许） | 每次进入各自 SaveLoaded → DayStarted；新世界代次 | 第二次 `loadForNewGame` 1；`OnDayStarted` 1 | 1 / 1（本次重载） | 主 `Game1` 对象复用；`ResetGameStateOnTitleScreen` 清空；`loadForNewGame` 新代次 | 同日重载不被吞、计数、状态不串档 | E-15/E-09 |
| G 回标题后切换不同档 | +1（本次换档） | +1 | 每次进入各自顺序；状态来自新档 | 新 `loadForNewGame` 1；`OnDayStarted` 1 | 1 / 1（本次换档） | 同 F；日期可相同，新代次允许 | 换档后状态、计数、无旧档残留 | E-15/E-09 |
| H 同一日重复菜单/过渡恢复 | 0 | 0（同日已派发后） | 同日键抑制；不得重复 | `OnDayStarted` 1+（错误日志静态分支/`beginGame`/间接标题路径/多人加入） | 0 / 0（同日已派发后） | 错误日志 mode11→3（入口可达性待验证）、`beginGame` mode3（新世界）、`setGameMode(0)` 间接路径均可调用；`gameMode 0` 守卫 + 同日键抑制 | 菜单负对照、错误日志（若可安全触发）、无重复、线程 | E-14/E-06/E-13 |

**矩阵读法**：B/C/F/G 的候选派发是“本次进入”的 1，不是累计总数；H 的 0 是“在已派发同一日之后的额外次数”。
若产品决定 C 不纳入 `SaveLoaded`，只改 C 行的 `SaveLoaded` 为 0，D–H 不变。

---

## 8. 未决产品语义 / OPEN_DECISION

1. **`SaveLoaded` 是否包含新建存档**：推荐包含（`IsNewGame` 区分）；若产品拒绝，C 行与设备“新建测试档”断言改变。
   标记为产品决策，但默认推荐已给出，不是 `OPEN_DECISION` 阻塞项。
2. **事件命名**：`SaveLoaded` 在新建档时也触发，是否改名 `WorldReady`/`SaveOrWorldLoaded`。
   `OPEN_DECISION`；不影响本轮技术判定，但会影响公开 API 文案与旧 Mod 预期。
3. **`DayStarted` 入口日是否触发**：推荐触发；另一种语义会改变 B/C 与设备期望。`OPEN_DECISION`，默认推荐触发。
4. **是否暴露 `IsFirstDayOfWorld`**：推荐暴露，便于消费者区分“入口日”和“跨日”；若不暴露，消费者需自己关联
   `SaveLoaded` 与 `DayStarted`。
5. **是否增加 `WorldReset` 钩子**：本方案用 `loadForNewGame` 新代次覆盖 pending；若下一轮发现失败/取消后 pending
   残留会造成误发，可在 `ResetGameStateOnTitleScreen` 入口增加 `Bootstrap.WorldReset()` 作为硬化项。
   目前标为可选，不阻塞 SaveLoaded GO。
6. **本地联机/多人加入**：`Client.setUpGame`/`FarmhandMenu` 也会把 mode 设 3；本方案按“世界代次 + 日期”去重，
   但未验证“同一世界多玩家”的语义。明确不在本轮范围；若要支持，需另行定义“每世界一次”还是“每玩家一次”。

---

## 9. 下一轮可验证的最小改动面（只设计，不实现）

> 以下是下一轮**候选**改动面，不代表本轮已改。任何实现都必须重新经过审查、主机测试和设备批准。

| 层 | 最小设计 |
| --- | --- |
| `Mod/ModFramework.Runtime/Events.cs` | 新增 `SaveLoadedEventArgs`、`DayStartedEventArgs`、`IModEvents.OnSaveLoaded/OnDayStarted`；只读 struct；只暴露框架 primitives |
| `EventDispatcher` | 新增 `NotifyWorldLoading(bool loadedGame)` 与 `NotifyDayStarted(int gameMode, int year, int season, int day)`；世界代次、pending、同日键、抑制计数；派发顺序 SaveLoaded → DayStarted；复用现有订阅快照/隔离/重入边界 |
| `Bootstrap` | 新增 `WorldLoading(bool)` 与 `DayStarted(int,int,int,int)` 两个桥接方法；缓存委托；异常不外抛；不增加 Runtime 的静态 `AssemblyRef` |
| `Tools/ModInjector` | 两个钩子：`loadForNewGame` 入口、`OnDayStarted` 完成边（分支重定向）；精确签名/方法体/单 ret/异常区检查；幂等；baseline/loader-only 拒绝混合 |
| 验证器 | `verify_modded_apk.py` / `ModInjector verify` 增加“应有/应无”断言；从最终 APK 解出程序集反验 |
| 测试 | 主机：调度器去重/顺序/失败/标题模式/新代次；InjectorTests：合成 `Game1` 夹具（单 ret、分支到 ret、catch 内 ret、错误签名、幂等）；pipeline/modded-build/offline/APK admission/PowerShell 回归 |
| 文档 | 事件契约、API 版本决定、诊断字段、设备验收计划 |

**不做的**：不改 Loader 会话语义、不改 `GameLaunched`/`Tick`、不改 Android 11+ 边界、不引入 Harmony/NDK/SMAPI、
不加内容管线/存档 API、不在本轮作版本升级。

---

## 10. 限制与未知

- 只覆盖 `StardewValley` 1.6.15.24354 / versionCode 239；未从设备安装包重新取字节（本轮用仓库原始 blob 与三份同哈希 refs）。
- 未构建 APK、未安装、未启动游戏、未运行 ADB、未读写 Saves、未进入/创建存档；所有“事件次数/线程/状态”都是静态设计或待验证。
- `UpdateTitleScreenDuringLoadingMode` 的“成功完成分支不可达”是调用图 + mode 分派的**静态推断**，仍需动态确认。
- 新游戏的两条子路径（`generateNewFarm` 开场事件结束 vs `createdNewCharacter(skipIntro:true)` 的 NewDay/菜单关闭）是否可能相继执行，未证明；同日去重只保证 ≤1，不保证一定 ≥1。
- `OnDayStarted` 的 `ret` 之前若抛异常，事件不派发；若异常发生在 `hasStartedDay` 置 true 之后，世界可能处于部分初始化状态，
  下一次调用可能跳过 NPC 块。此边界需要主机异常夹具/动态记录，不能靠猜。
- `OnDayStarted` 体内会执行地点 reset、NPC/触发器逻辑；若这些代码改变了 `gameMode`，`ret` 处读取的
  `gameMode` 可能与进入时不同。静态未见把它们改成 3 的路径，但这仍需夹具/动态验证；必要时应在入口捕获
  “进入时模式”而不是只在 `ret` 读取。
- `loadForNewGame(true)` 在工作线程执行；pending 的跨线程可见性需要锁/内存屏障设计；本轮没有运行时证据。
- 跨日 `OnDayStarted` 的线程归属未验证；`_newDayAfterFade` 是工作线程，`showEndOfNightStuff` 也由它调用，
  虽然静态上此时旧菜单通常为 null，仍必须动态记录线程。
- 本地联机、多人加入、拆分屏幕未覆盖；“每世界一次”还是“每玩家一次”未定义。
- Android 11+、其它 OEM、低端机帧率、非空真实存档、断电恢复均未覆盖。
- 本文不把历史 V1963A 的标题路径观测当作本轮存档路径证据；历史设备证据只用于“游戏循环线程曾记录为 1”的背景。
