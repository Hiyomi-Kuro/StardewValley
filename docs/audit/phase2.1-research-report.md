# Phase 2.1-R 研究报告：存档生命周期事件证据、缺口与下一轮验收

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-23（+08:00） |
| 阶段 | Phase 2.1-R（可行性研究；未实现、未构建 APK、未接触设备） |
| 基线 | `HEAD == origin/main ==` 远端 `main` = `8712cf09a42e8336f97a0c6a3f5b63c69b1bb101`；Actions Run `44004070683`，3/3 success |
| 游戏程序集 | `StardewValley.dll` 1.6.15.24354；SHA-256 `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047`；MVID `573bf1e5-3245-46d6-925a-1c3713afb677`；原始 |
| 判定 | **`SaveLoaded`：GO；`DayStarted`：HOLD** |
| 关联 | [可行性文档](../architecture/phase2.1-save-lifecycle-feasibility.md) · [事件契约](../architecture/phase2-events.md) · [Phase 2.0.1 报告](phase2.0.1-report.md) |

> 本报告不把历史设备结论当作本轮结果；没有“本轮真机已验证”。所有证据只覆盖
> `StardewValley` 1.6.15.24354 / versionCode 239 的原始程序集。
> 标注约定：未单独标注的“方法/字段/IL 偏移/调用者”陈述默认是 **CURRENT_STATIC**；历史设备/历史报告是
> **HISTORICAL**；运行时次数/线程/状态推断是 **INFERENCE**；设计建议不是观测；未执行项是 **NOT_RUN/UNKNOWN**。

---

## 0. 结论摘要

| 事件 | 判定 | 一条最强证据 | 一条最关键风险 |
| --- | --- | --- | --- |
| `SaveLoaded` | **GO** | `Game1.loadForNewGame(bool)` 是读档/新游戏的共同世界初始化入口，且 `Game1.OnDayStarted()` 只有一个正常 `ret`（IL_0160）；用它消费 pending 可以得到“一次成功世界进入 = 一次事件” | 普通读档的 `loadForNewGame(true)` 由 `Task` 在工作线程执行；pending 的跨线程可见性、以及失败/取消后的残留，尚未有运行时证据 |
| `DayStarted` | **HOLD** | `OnDayStarted()` 的 `ret` 是日初始化完成后的唯一边界，日期字段此时可用；直接调用点恰好 2 个 | 原始调用并非天然每日一次：`setGameMode(0)` 经 `set_activeClickableMenu` 的退标题间接路径（保存失败/退出标题时旧菜单为 save/shipping）会在 mode 0 下调用 `OnDayStarted`；此外还有错误日志静态分支、`beginGame`（新游戏）和多人加入的 mode 3 路径；`_newDayTask` 还是工作线程。去重和 `gameMode != 0` 守卫只是设计，未运行时证明 |

`GO` 只表示推荐进入下一轮受控验证；`HOLD` 表示需要先补主机夹具/线程证据，不代表整个事件永远不可行。
两者都**没有**实现、构建、安装或真机验证。

---

## 1. Git / CI 预检

### 1.1 本地与远端

| 查询 | 实际结果 | 类别 |
| --- | --- | --- |
| 研究日期 | 2026-11-23（+08:00） | CURRENT_STATIC |
| 仓库根 | `D:/Download/DSH/StardewValley` | CURRENT_STATIC |
| 当前分支 | `main` | CURRENT_STATIC |
| `HEAD` | `8712cf09a42e8336f97a0c6a3f5b63c69b1bb101` | CURRENT_STATIC |
| 本地 `origin/main` | `8712cf09a42e8336f97a0c6a3f5b63c69b1bb101` | CURRENT_STATIC |
| GitHub REST `commits/main` | `8712cf09a42e8336f97a0c6a3f5b63c69b1bb101`；提交时间 `2026-11-17T02:35:01Z` | CURRENT_STATIC |
| 工作区（开工） | 干净；无未提交改动 | CURRENT_STATIC |
| 远端 | `https://github.com/Hiyomi-Kuro/StardewValley.git` | CURRENT_STATIC |

**历史交接对照**：交接给出的 SHA 与 Run 编号均正确。但本地 `AGENTS.md` 的“当前状态”仍写
`HEAD == origin/main == 97fdfa74…`、Phase 2.0.1 未提交；这与实际 git 不符。`8712cf0` 的提交内容正是
Phase 2.0.1 的代码/测试/文档（见 §3.3 的 `git show --stat`），说明 `AGENTS.md` 只是未更新，不是工作区另有改动。
本轮不修改 `AGENTS.md`，也不把 `97fdfa7` 的 Run 当作本轮基线。

### 1.2 CI

| 项 | 值 |
| --- | --- |
| Run | `44004070683`，workflow `ci`，event `push` |
| head_sha | `8712cf09a42e8336f97a0c6a3f5b63c69b1bb101`（与本地/远端一致） |
| 结果 | `completed / success`；3/3 jobs success；`2026-11-17T02:35:07Z` → `03:00:19Z` |
| Job 1 | `offline checks (python, powershell, manifests, privacy)`：success；步骤含 `offline suite`、`mod-enabled build static checks`、**`APK install-admission synthetic tests`**、`P114 static safety audit`、`privacy scan`，均 success |
| Job 2 | `dotnet build + loader host tests`：success；含 `build ModInjector`、`build runtime and fixture mods`、`loader host tests`、`injector tests` |
| Job 3 | `powershell gates (adb resolution, apk deploy safety, no-root mods deploy, no device)`：success；含 `adb resolution self-test`、`deploy gate fake-adb suite`、`no-root mods deploy fake-adb suite` |

**边界**：该 Run 属于已提交的 `8712cf0`，包含 Phase 2.0.1 的测试；本轮只新增两份未跟踪文档，
**这两份文档的远端 CI = NOT_RUN**。不得把 `8712cf0` 的绿灯写成“本文档/新事件已过 CI”。

### 1.3 历史设备证据及其边界

| 项 | 历史事实 | 本轮用法 |
| --- | --- | --- |
| 设备 | vivo V1963A / Android 10 / API 29 / arm64-v8a，非 debuggable、无 Root | 只作为“历史标题路径”的背景；不是本轮设备授权 |
| Phase 2.0 / 2.0.1 运行 | 标题界面；`GameLaunched`/`Tick` 在 managed thread 1；Saves 前后 0 文件；未进入存档 | 只支持“游戏循环线程曾是 1”的背景，不外推到存档路径 |
| Phase 2.0.1 安装 | candidate `c571a25b…`、installed 前后 `d4f60d57… → c571a25b…`、signer `858ef62d…`；准入 `INSTALL_CANDIDATE` | 只作为历史流程证据；本轮没有安装新候选 |
| 证据根 | `.tmp/phase2.0.1/20261114-2053/`（gitignore） | 未复制私有材料入库；本轮不重跑 |

**结论**：历史设备证据不能证明任何 `SaveLoaded`/`DayStarted` 行为；本轮所有事件结论均为静态或待验证。

---

## 2. 证据清单

### 2.1 输入指纹与来源

| 项 | 值 |
| --- | --- |
| 原始 blob | `app/src/main/apkroot/assemblies/assemblies.blob`，5,588,326 B，SHA-256 `c8ee504eea163d2c82d89d0004c8924314b3e6b41adb649ea5f136151eecabd9` |
| assemblies.manifest | 4,937 B，SHA-256 `0423d19caf72cda38d1acefd8fe73356b643ec05786b6d9a15b7134bed6e3393` |
| 解出的程序集 | `StardewValley.dll`，9,280,000 B，SHA-256 `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047`，MVID `573bf1e5-3245-46d6-925a-1c3713afb677` |
| 程序集版本 | `StardewValley` 1.6.15.24354；TFM `.NETCoreApp,Version=v8.0`；类型 2867（Cecil `GetTypes`，含嵌套）/ 方法 30,067 |
| APK 身份 | `com.chucklefish.stardewvalley`；versionCode 239；versionName 1.6.15.0；`app/apktool.yml` |
| 同哈希副本 | `.tmp/phase0.5/refs/StardewValley.dll`、`.tmp/build-modded/refs/StardewValley.dll`、`.tmp/build-loader-only/refs/StardewValley.dll`（均为原始） |
| 已注入副本 | `.tmp/build-modded/patched/StardewValley.dll`（9,262,592 B，SHA-256 `e536bbcdad4e01372563c7223eae91b06b77af6d2a41c3266185a4bbb2097882`）、`.tmp/build-loader-only/patched/StardewValley.dll`（9,261,568 B，SHA-256 `47448940678c99498bbefc7f738fbfcefa2ea3a5ef64a7a4fa001e3c0e6e6dbd`） |

说明：

- 原始程序集是**从仓库原始 blob 解出**的，不是从设备安装包重新取的；设备本轮未连接。
- 已注入副本只用于“当前构建产物不是原始输入”的对照，不参与游戏逻辑判断；其 MVID 因注入而变化。
- 没有在全盘搜索其它程序集；只检查了项目内与证据索引直接指向的路径。

### 2.2 证据编号

| 编号 | 输入指纹 | 签名/IL 定位 | 支持的结论 | 局限 |
| --- | --- | --- | --- | --- |
| **E-01** | 仓库/CI | `HEAD`/`origin/main`/远端 `8712cf0`；Run `44004070683` | 本轮基线是 `8712cf0`，3/3 success；`AGENTS.md` 的 `97fdfa7` 过时 | 文档未提交，CI 不覆盖本文 |
| **E-02** | blob SHA `c8ee504e…`；程序集 SHA `587ff5ff…`；MVID `573bf1e5…` | `Tool AssemblyStore/store.py extract`；`IlProbe info` | 输入是 1.6.15.24354 原始程序集，身份可复核 | 未从设备 APK 重新取字节；未做签名校验 |
| **E-03** | 已注入 SHA `e536bbcd…` / `47448940…` | 文件大小/哈希 | 当前 patched 副本是注入产物，不是原始输入 | 未用于 IL 结论；MVID 未记录 |
| **E-04** | 原始程序集 | `GameRunner.Initialize` 15 ins，IL_0007 → `InitializeMainInstance`；`InitializeMainInstance` IL_000c → `AddGameInstance`；`CreateGameInstance` IL_0000-0007 仅 `newobj Game1`；`AddGameInstance` IL_0092 设 `Game1.game1`、IL_0098/00a6 实例初始化、IL_00b7 `LoadInstance` | `CreateGameInstance` 返回只证明实例存在；单机读档不调用它；`Game1` 在启动/标题就存在 | 本地联机/调试分支未展开 |
| **E-05** | 原始程序集 | `Game1..ctor()` 60 ins，IL_0088-0092 `GetNewInstanceID` → `instanceId`；`GetNewInstanceID` 10 ins 递增 `nextInstanceId`；`CleanupReturningToTitle` 65 ins 保留主实例 | `instanceId` 标识对象，不是世界生命周期；回标题复用同一 `Game1` | 多实例/本地联机未验证 |
| **E-06** | 原始程序集 | `public static void Game1::OnDayStarted()`；114 ins；无 EH；唯一 `ret` IL_0160；`IL_0159 brfalse.s IL_0160`；直接调用者 `_update` IL_1480、`set_activeClickableMenu` IL_0080；`hasStartedDay` IL_00a9/014f；`TriggerActionManager.Raise("DayStarted")` IL_000a | 完成边在日初始化之后；必须重定向分支到 ret；直接调用点恰好 2 处 | 间接路径见 E-14；线程见 E-17 |
| **E-07** | 原始程序集 | `_update` 1508 ins；IL_000f-0016 ticks+1；尾部 IL_1470-1485 `gameMode==3 && ticks==1` → `OnDayStarted`；`set_gameMode` 属性 IL_0006-0008 同值早退、IL_004c-0053 写 `_gameMode` 并重置 ticks；`setGameMode` 方法 IL_001f-0020 只写 `_gameMode` | 属性 mode 3 后的下一次 `_update` 命中一次；方法 set mode 3 不重置 ticks | 只证明调用条件，不证明实际路径次数 |
| **E-08** | 原始程序集 | `_update` IL_05f3-0601：mode 3/2 → playing，否则 IL_1396 `UpdateTitleScreen`；`UpdateTitleScreen` 164 ins，switch mode 6/7/8：case 6 → `UpdateTitleScreenDuringLoadingMode`，case 7 → `currentLoader.MoveNext`，case 8 → `setGameMode(3)`；`SaveGame.LoadAfterPermissionCheck` IL_0097 mode 6、IL_0221 赋 `currentLoader` | 读档启动点与推进点；成功后不再走标题屏分支 | 成功分支不可达是推断（E-10） |
| **E-09** | 原始程序集 | `SaveGame/<getLoadEnumerator>d__123::MoveNext` 1994 ins、25 states；`loadForNewGame` IL_038c；`loadDataToFarmer` IL_06e8/08c6；`loadDataToLocations` IL_0dbf；`set_gameMode(3)` IL_1a7f；`resetForPlayerEntry` IL_1c22；`yield 100` IL_1c81-1c92；`ret false` IL_1c9a；`CancelToTitle` IL_1b74 | 读档是迭代器状态机；成功晚于数据恢复；取消可在晚段仍执行 reset | 编译器生成类型，不作为注入点；无全局 try，但内部多 finally |
| **E-10** | 原始程序集 | `UpdateTitleScreenDuringLoadingMode` IL_002d-0066：`MoveNext` false + mode3 → `setGameMode(3)` + fade，否则 `ExitToTitle`；唯一调用者 `UpdateTitleScreen`；唯一调用者 `_update` IL_1398；`_update` 在 mode 3/2 不走标题屏 | 成功读档后 `UpdateTitleScreen` 不再被驱动，`currentLoader` 完成分支不可达；不能以 `currentLoader` 完成为完成边 | 静态调用图推导；需动态确认 |
| **E-11** | 原始程序集 | `set_activeClickableMenu` 74 ins；`wasMenuOpen` IL_0000-0032；`OnDayStarted` IL_007d-0080；`showEndOfNightStuff` 12 ins → ModHooks → 闭包压入 ShippingMenu/SaveGameMenu；`SaveGameMenu.update` IL_001c/0051 `exitActiveMenu`；`exitActiveMenu` IL_0007 `set_activeClickableMenu(null)` | 跨日路径：夜间菜单族关闭时触发；不是读档入口路径 | `_newDayAfterFade` 工作线程的菜单设置需动态线程复核 |
| **E-12** | 原始程序集 | `Utility.generateNewFarm(bool,bool)` 工厂 → `<generateNewFarm>d__340` 56 ins：`loadForNewGame(false)` IL_005d、mode 2 IL_0088、开场事件；`Event.endBehaviors` mode3 IL_08b8（`beginGame`）；`TitleMenu.createdNewCharacter(skipIntro:true)`：`loadForNewGame(false)` IL_003d、`NewDay(0)` IL_00a9、`setGameMode(3)` IL_00b4；`createdNewCharacter` 的调用者是 `MobileCustomizer`/`MobileFarmChooser`；`LoadNewGameFromCharacterCustomization` 全方法体扫描 0 直接调用者 | 新游戏有至少两条活子路径到 OnDayStarted；`loadForNewGame(false)` 是新游戏入口；`skipIntro` 选择路径 | 两条子路径是否相继未证明；`LoadNewGameFromCharacterCustomization` 不作为活路径 |
| **E-13** | 原始程序集 | `hasStartedDay`：`.cctor`、`OnDayStarted` IL_00a9 读/IL_014f 写、`ResetGameStateOnTitleScreen` IL_02c6 写 0、`FarmerTeam` 读 | 它是“本世界首次日启动/防御”标志，不是每日重置；不能当 DayStarted 去重键 | 读取语义来自 IL；无运行时验证 |
| **E-14** | 原始程序集 | `setGameMode` mode0 分支 IL_0035-0079 创建 TitleMenu 并 `set_activeClickableMenu`；`ExitToTitle` IL_002c `setGameMode(0)`；`SaveGame/<Save>d__114::MoveNext` IL_0344 `ExitToTitle`；`UpdateControlInput` IL_2453-245d errorLogMode 11 → `set_gameMode(3)`；`Event.endBehaviors` IL_08b8（`beginGame`）；`Client.setUpGame` IL_0015；`FarmhandMenu.update` IL_0051 | 存在退标题间接 OnDayStarted（旧菜单为 save/shipping 时，mode 0）；错误日志静态分支、`beginGame`（新游戏）、多人加入会命中尾部的 OnDayStarted | 保存失败路径是控制流可达，不是已实际触发；错误日志分支入口可达性未证明；`beginGame` 不是同日重复 |
| **E-15** | 原始程序集 | `CleanupReturningToTitle` 65 ins：主实例保留、其它移除，IL_0065 `ResetGameStateOnTitleScreen`；后者 mode0 IL_0063、`currentLoader=null` IL_012f、`currentLocation=null` IL_0135、`hasLoadedGame=false` IL_02c0、`hasStartedDay=false` IL_02c6 | 回标题复用主实例并清空世界；下一次读档需新世界代次 | 未测真实回标题 |
| **E-16** | 原始程序集 | `StardewValley.Mods.ModHooks` 公共实例类型；方法含 `OnGame1_NewDayAfterFade`、`OnGame1_ShowEndOfNightStuff`、`AfterNewDayBarrier` 等；无 `SaveLoaded`/`DayStarted` | 历史“ModHooks 不存在”的说法只对 `StardewValley.ModHooks` 命名空间成立；`StardewValley.Mods.ModHooks` 存在，但不是生命周期事件总线 | 不是注入候选；未分析全部调用者 |
| **E-17** | 原始程序集 + 历史 | `GameRunner.Update` 319 ins → `Game1.Instance_Update` IL_01d4 → `InstanceGame.Update`（虚）→ `Game1.Update` → `_update`；`SaveGameMenu.update` 主线程菜单更新；`_newDayTask` 由 `Task` 工作线程跑 `_newDayAfterFade`；历史 Phase 2.0 V1963A 的 Tick/GameLaunched 为 managed thread 1 | 静态主路径为游戏循环线程；跨日工作线程菜单路径需动态复核；历史线程证据只覆盖标题路径 | 本轮无设备/线程记录 |
| **E-18** | 原始程序集 | 扫描全部 30,067 方法体的 `call`/`callvirt`；扫描所有 `ldstr`：`OnDayStarted` 0 条，`DayStarted` 仅 `OnDayStarted` 与 `TriggerActionManager..cctor` | 没有发现字符串反射调用；直接调用 2 处；但存在 E-14 的间接路径 | 不能证明绝对无反射/间接；只覆盖方法体字符串与直接调用 |
| **E-19** | 项目源码（非游戏程序集） | `Tools/ModInjector/Injection.cs`、`IlChecks.cs`；Phase 2.0.1 的 `HasCompleteCompletionHook`、`bypass_edge_rejected`、`completion_wrong_overload_rejected` | 现有注入器支持完成边分支重定向与拒绝边直指 ret；`OnDayStarted` 的 IL_0159 属于可处理形状 | 源码能力不等于目标 IL 已验证；需新夹具 |
| **E-20** | — | 本轮未运行 APK 构建/设备/测试 | 所有事件次数/线程/状态都是静态设计或待验证；文档 CI = NOT_RUN | 不把历史设备结果计入本轮 |

---

## 3. 已运行命令与实际结果

### 3.1 Git / GitHub

```text
git rev-parse --show-toplevel        -> D:/Download/DSH/StardewValley
git rev-parse --abbrev-ref HEAD      -> main
git rev-parse HEAD                   -> 8712cf09a42e8336f97a0c6a3f5b63c69b1bb101
git rev-parse origin/main            -> 8712cf09a42e8336f97a0c6a3f5b63c69b1bb101
git status --short                   -> （空）
git remote -v                        -> origin https://github.com/Hiyomi-Kuro/StardewValley.git
```

```text
GET /repos/Hiyomi-Kuro/StardewValley/commits/main
  -> sha 8712cf09a42e8336f97a0c6a3f5b63c69b1bb101
     commit.committer.date 2026-11-17T02:35:01Z

GET /repos/Hiyomi-Kuro/StardewValley/actions/runs?head_sha=8712cf0…
  -> total_count 1
     id 44004070683, name ci, event push, status completed, conclusion success

GET /repos/Hiyomi-Kuro/StardewValley/actions/runs/44004070683/jobs
  -> 3 jobs, all success
     offline checks (python, powershell, manifests, privacy)
       - APK install-admission synthetic tests: success
     dotnet build + loader host tests
       - loader host tests (no game assemblies): success
       - injector tests (synthetic assemblies, no game assembly): success
     powershell gates (adb resolution, apk deploy safety, no-root mods deploy, no device)
       - adb resolution self-test / deploy gate fake-adb suite / no-root mods deploy fake-adb suite: success
```

### 3.2 程序集提取与指纹

```text
python Tools/AssemblyStore/store.py extract \
  app/src/main/apkroot/assemblies/assemblies.blob .tmp/phase2.1/blob-extract

-> .tmp/phase2.1/blob-extract/StardewValley.dll
   9,280,000 B
   SHA-256 587FF5FF9C258679004977BBEEC359ABA86649BD3E4860DDCAA428DF8F07E047

IlProbe info <extracted>
-> assembly StardewValley 1.6.15.24354
   mvid 573bf1e5-3245-46d6-925a-1c3713afb677
   typedefs 2867, methods 30067
```

### 3.3 提交与工作区

```text
git show --stat --oneline 8712cf0
-> Phase 2.0.1 的代码/测试/文档（EventDispatcher、Events、Injection、apk_admission、
   Phase201Tests、test_apk_admission、phase2.0.1-* 文档等）

git diff --name-status 97fdfa7..8712cf0
-> 21 files changed；包含 Phase 2.0.1 的全部改动
```

这解释了 `AGENTS.md` 为什么仍写 `97fdfa7`：它没有随这次提交更新；不是工作区有未提交代码。

### 3.4 静态 IL 分析

本机临时工具（`.tmp/phase2.1/ilprobe/`，Mono.Cecil 0.11.6，.NET 8.0.404，只读）实际执行：

```text
IlProbe info        <original>
IlProbe type        <StardewValley.Game1>
IlProbe type        <StardewValley.GameRunner>
IlProbe type        <StardewValley.SaveGame>
IlProbe fields      <StardewValley.Game1>          # 读取 gameMode 常量
IlProbe dump        <Game1> OnDayStarted
IlProbe dump        <Game1> _update
IlProbe dump        <Game1> loadForNewGame
IlProbe dump        <Game1> set_activeClickableMenu
IlProbe dump        <Game1> setGameMode / set_gameMode
IlProbe dump        <Game1> NewDay / newDayAfterFade / _newDayAfterFade
IlProbe dump        <Game1> ExitToTitle / CleanupReturningToTitle / ResetGameStateOnTitleScreen
IlProbe dump        <Game1> showEndOfNightStuff / UpdateTitleScreen / UpdateTitleScreenDuringLoadingMode
IlProbe dump        <SaveGame> Load / LoadAfterPermissionCheck / getLoadEnumerator
IlProbe dump        <SaveGame/<getLoadEnumerator>d__123> MoveNext
IlProbe dump        <Utility/<generateNewFarm>d__340> MoveNext
IlProbe dump        <SaveGameMenu> update
IlProbe callers     <Game1> OnDayStarted / set_activeClickableMenu / CreateGameInstance / AddGameInstance / NewDay / newDayAfterFade / ExitToTitle / loadForNewGame
IlProbe fieldrefs   <Game1> currentLoader / hasStartedDay / _newDayTask / _afterNewDayAction
IlProbe strings     <original> OnDayStarted / DayStarted / SaveLoaded
IlProbe callcontext <Game1> UpdateControlInput / <Event> endBehaviors / <Client> setUpGame / <FarmhandMenu> update
```

关键数值：`OnDayStarted` 114 ins、唯一 `ret` IL_0160；`_update` 1508 ins、尾部 IL_1470-1485；
`loadForNewGame` 325 ins、入口 IL_0000；`getLoadEnumerator` MoveNext 1994 ins、25 states；
`set_activeClickableMenu` 74 ins、触发 IL_0080；`setGameMode` 52 ins、无 ticks 重置；
`set_game_mode` 36 ins、IL_0052-0053 重置 ticks。

### 3.5 未运行的命令

本轮**没有**运行：APK 构建、`verify_modded_apk.py`、`test_loader.py`、`InjectorTests`、`test_mod_pipeline.py`、
`test_modded_build.py`、`test_ci_offline.py`、`test_apk_admission.py`、PowerShell 套件、ADB、任何设备命令、
存档创建/读取、Mods 部署、`git commit/push`。原因是本轮只做静态研究与文档，不实现、不接触设备。

---

## 4. 静态证据缺口与补证方式

| # | 缺口 | 为什么重要 | 补证方式 | 验收判据 |
| --- | --- | --- | --- | --- |
| G-1 | 没有运行时存档路径证据 | 线程、计数、状态都未在存档路径验证 | 下一轮主机夹具 + 隔离设备读档/跨日 | 计数/顺序/线程/状态满足矩阵 |
| G-2 | `UpdateTitleScreenDuringLoadingMode` 成功完成分支不可达是静态推断 | 若推断错，`currentLoader` 可能仍是可用完成边 | 在设备/主机记录 `currentLoader`、`gameMode`、`gameModeTicks`、`OnDayStarted` 的时间线 | 成功读档后 `currentLoader` 不归零，且 `OnDayStarted` 仍触发 |
| G-3 | 新游戏两条子路径是否相继未证明 | 影响 C 行“至少一次/最多一次”的表述 | 主机夹具模拟两条路径；设备分别新建档（跳过/不跳过开场） | 每次新游戏 SaveLoaded 1 / DayStarted 1 |
| G-4 | `OnDayStarted` 在部分初始化后抛异常的行为 | 可能留下 `hasStartedDay=true` 但无事件 | 主机合成夹具（异常在 IL_014e 后） | 不误派发；下一次调用的行为被记录 |
| G-5 | `loadForNewGame(true)` 工作线程写 pending 的可见性 | 若不可见，SaveLoaded 会漏发 | 主机并发/屏障测试；设备记录 generation/pending | 每次成功读档都能消费 pending |
| G-6 | 跨日 `OnDayStarted` 的线程 | 可能在工作线程（`_newDayTask` 菜单设置） | 设备记录事件线程与首个 Tick 线程 | 同一游戏循环线程；否则降级 |
| G-7 | 退标题间接路径（`setGameMode(0)`）的实际可达性 | 保存失败/退出时可能误发 DayStarted | 主机夹具构造旧 SaveGameMenu + mode0；设备不制造真实保存失败 | mode0 下 0 派发；保存失败不产生正常 DayStarted |
| G-8 | 本地联机/多人语义 | 同一世界多实例/加入 | 本轮不做；下一轮若纳入需单独定义 | 未定义前不承诺 |
| G-9 | 设备安装字节/签名 | 需证明运行的是候选 APK | 设备准入 + 普通 shell `pm path`/`sha256sum` | candidate 与 installed 一致 |
| G-10 | 非空真实存档/存档内容 | 本轮未接触 | 只使用隔离测试档；不读用户真实档 | 测试档之外无变化 |

---

## 5. 主机验证计划（下一轮，预计覆盖，不表示已执行）

### 5.1 合成夹具（先做，无需设备）

1. **调度器单元测试**（`ModFramework.Tests`）：
   - `WorldLoading(true)` → `DayStarted(mode3, date)`：`SaveLoaded=1`、`DayStarted=1`、顺序 SaveLoaded → DayStarted。
   - `WorldLoading(false)` → 同上：`IsNewGame=true`。
   - 同日第二次 `DayStarted`：`DayStarted` 仍为 1，`SuppressedDuplicateDayStarted=1`。
   - 日期 +1：`DayStarted=2`；`SaveLoaded` 不变。
   - 回标题/新代次后同日：`WorldLoading(true)` 新代次 → `SaveLoaded=2`、`DayStarted` 允许 +1。
   - `mode 0`：`SaveLoaded=0`、`DayStarted=0`，`SuppressedTitleMode=1`。
   - 无 pending 的 `DayStarted`：`SaveLoaded=0`、`DayStarted=1`（若 date 有效）。
   - 无效日期：`DayStarted=0`、`InvalidDayKey=1`；`SaveLoaded` 仍可派发。
   - 0 订阅：计数仍递增；handler 抛异常：其余 handler 继续，故障计数增加。
   - 会话替换：新会话只影响订阅；不重置世界代次/日期键；`GameLaunched` 仍进程级一次。
2. **InjectorTests 合成程序集**：
   - 夹具 `Game1.loadForNewGame(bool)` + `Game1.OnDayStarted()`（单 ret、IL 分支到 ret、无 EH）；注入后加载执行，断言 `WorldLoading`/`DayStarted` 各 1 次。
   - 负向：多 ret、catch 内 ret、分支不可重定向、错误重载、缺方法体、错误类型/签名、`OnDayStarted` 在 try 内 → 拒绝构建。
   - 幂等：patch 两次仍各 1 次；loader-only 与 event 版混合 → 拒绝。
3. **pipeline / modded-build / offline / APK admission / PowerShell**：
   - `test_mod_pipeline.py`：两个新钩子存在、幂等、`--no-event-hooks` 无钩子、混合状态拒绝。
   - `test_modded_build.py`：构建 manifest 的 `event_hooks` 包含新事件；最终 APK 解出的程序集 verify。
   - `test_ci_offline.py`：静态检查钩子名/目标。
   - `test_apk_admission.py`、`test_deploy_safe.ps1`、`test_deploy_mods_safe.ps1`：保持现有门禁，不因新事件放宽。

### 5.2 基线 / Loader-only / 事件版“应有/应无”

| 产物 | 应有 | 应无 | 反向核验 |
| --- | --- | --- | --- |
| A baseline | 原始 `StardewValley.dll` 字节；Loader/事件钩子均无 | `Bootstrap.Start/GameLaunched/Tick/WorldLoading/DayStarted` 调用 | 从 A 解出程序集，`ModInjector verify` 断言 0 |
| B loader-only | `Bootstrap.Start` 1 次 | `GameLaunched`/`Tick`/`WorldLoading`/`DayStarted` 均 0 | 从 B 解出程序集反验 |
| C 事件版 | `Start` 1、`GameLaunched` 1、`Tick` 1、`WorldLoading` 1、`DayStarted` 1（每个入口/完成点） | 无重复、无错误目标、无 ret 绕过 | 从 C 最终 APK 解出程序集再 verify；不只看构建日志 |

### 5.3 最小诊断字段

- 匿名 run id（不写存档名/角色名/完整路径）。
- 进程内世界代次、事件序号、事件类型。
- `year/season/dayOfMonth`（逻辑日键）；`gameMode`；`hasLoadedGame`；`hasStartedDay`；`player/currentLocation` 非空。
- `ThreadId`；是否与首个 Tick 相同。
- `pending` / `IsNewGame`；`SuppressedTitleMode` / `SuppressedDuplicateDayStarted` / `InvalidDayKey`。
- 失败原因（不记录存档内容/文件名/路径）。
- 诊断必须能区分：首次读档、回标题重载、切换存档、跨日、同日重复；不能靠人工猜日志。

### 5.4 预计覆盖标记

以上均为**预计覆盖**，不是已执行；下一轮实际运行后再写“实际计数”。

---

## 6. 设备验收计划（下一轮，未执行）

> 以下设备动作都需要下一轮**独立批准**；本轮没有设备写授权，也没有连接设备。历史 V1963A 验收不是本轮设备授权。

### 6.1 独立批准点

1. **停止目标进程 + 项目外备份**。
2. **安装候选 APK / 部署测试 Mods**。
3. **创建或进入隔离测试档并跨日**。
4. **回退/恢复**（Mods 事务、APK 版本/签名、测试档允许变化）。

### 6.2 预检（批准后、写入前）

- 显式指定设备；核对设备型号、Android/API、包名、versionCode 239、已安装 APK 哈希/签名。
- 候选 APK 与 installed 的准入必须是 `INSTALL_CANDIDATE`；`pm path` 必须恰好一条（split 明确拒绝）；
  普通 shell `sha256sum` 解析完整 64-hex 行；哈希不可读/不一致 → 停止。
- baseline 只作内容比较，**绝不能冒充 installed**。
- 无法确认兼容升级、发现 split、无法用普通 shell 证明安装后字节 → 停止；不用卸载/Root/清数据绕过。

### 6.3 备份与隔离

- 项目外备份；停止进程后记录 Saves、Mods、`.mods-deploy` 等清单；按相对路径逐文件双端 SHA-256；
  检查缺失、额外文件、空目录状态。备份不可读、仍有写入或双端不一致 → 停止。
- 只用专用设备/环境和隔离测试存档；预先划定唯一可写范围与预期变化（跨日可能正常产生保存写入）。
- 不把用户真实存档作为夹具；不能证明隔离时停止。

### 6.4 场景与期望

| 步骤 | SaveLoaded 期望 | DayStarted 期望 | 线程/状态/证据 |
| --- | --- | --- | --- |
| 0 标题负对照 | 0 | 0 | 停留 ≥10s；无事件；截图/logcat |
| 1 读已有隔离测试档 | 1 | 1 | SaveLoaded → DayStarted；玩家/地点/日期可读；线程与首个 Tick 比较 |
| 2 新建隔离测试档（若契约纳入） | 1（`IsNewGame=true`） | 1 | 同上；记录两条新游戏子路径实际走哪条 |
| 3 正常跨一天 | 0（本次跨日不新增） | 1 | 日期只加一天；无同日重复；记录线程 |
| 4 同一日菜单负对照 | 0 | 0 | 开关普通菜单；不重复；不崩 |
| 5 同档重载 | +1（本次重载） | +1（同日允许） | 新世界代次；状态不串档 |
| 6 切换不同档（若有第二个隔离测试档） | +1（本次换档） | +1 | 状态来自新档；旧档不残留 |

- 失败/取消优先由主机夹具覆盖；**不要为制造失败而破坏真实存档**。
- 保存失败/退标题的间接路径只做主机夹具；设备不故意制造保存失败。

### 6.5 停止条件

出现任一项立即停止后续写入、保留证据并报告：

- 设备/包/签名/候选哈希不符；split/unreadable。
- 备份不一致、仍有写入、非测试数据发生变化。
- 运行证据无法关联到候选 APK 哈希。
- 事件出现 0/2 次等契约偏差；SaveLoaded/DayStarted 顺序反了；同一日重复 DayStarted。
- 跨日事件在工作线程派发（与首个 Tick 线程不同）且未定义处理策略。
- 出现 FATAL/AndroidRuntime/TypeLoad/InvalidProgram 或游戏崩溃。

### 6.6 回退边界

- Mods：按 `.mods-deploy` 事务回滚；不自动清理事务。
- APK：只按签名准入尝试同签名旧 APK；**不承诺旧 APK 一定能原地装回**；需要卸载/Root/清数据才能回退 → 列为阻塞并停止。
- 测试档：允许测试档按预期产生保存写入；非测试数据必须与备份清单一致。
- 回退后重新核对 Mods/Runtime 哈希、测试档状态、非测试数据清单。

---

## 7. 判定明细

### 7.1 `SaveLoaded`：GO

- **最强证据**：`Game1.loadForNewGame(bool)` 是读档（true）与新游戏（false）的共同世界初始化入口；
  `Game1.OnDayStarted()` 只有一个正常 `ret`（IL_0160），且读档成功路径在模式 3 后由 `_update` 尾部命中。
  用 `loadForNewGame` 设 pending、用 `OnDayStarted` `ret` 消费 pending，可以给出“一次成功进入世界 = 一次 SaveLoaded”，
  并用 `IsNewGame` 精确区分新游戏。
- **最关键风险**：普通读档的 `loadForNewGame(true)` 在工作线程 Task 中执行；pending 的跨线程可见性、
  失败/取消后的 pending 残留、以及“日初始化抛异常”的部分初始化，都没有运行时证据。
- **GO 的含义**：建议进入下一轮主机夹具 + 受控设备验证；不是已实现/已发布/真机通过。
- **最小验证**：主机调度器/注入器夹具 + 设备读档一次，记录 generation/pending/线程/计数。

### 7.2 `DayStarted`：HOLD

- **最强证据**：`OnDayStarted()` 的 `ret` 是日初始化完成后的唯一边界，日期字段可用；直接调用点恰好 2 个；
  现有注入器支持分支到 `ret` 的重定向。
- **最关键风险**：原始调用不是天然每日一次。`setGameMode(0)` 会经 `set_activeClickableMenu` 在 mode 0 下
  间接调用 `OnDayStarted`（例如 SaveGameMenu 仍活动时保存失败退出标题，控制流可达）；错误日志静态分支、
  `beginGame`（新游戏）和多人加入也会以属性 mode 3 重置 ticks 后命中 `_update` 尾部；`_newDayTask` 还是工作线程。
  `gameMode != 0` 守卫 + 同日键去重是设计，但没有运行时证明“标题模式 0 派发”和“跨日线程”。
- **最小补证项与验收判据**：
  1. 主机夹具：构造 SaveGameMenu/ShippingMenu 活动 + `setGameMode(0)` 等价路径 → `DayStarted=0`，抑制计数 +1。
  2. 主机夹具：同日第二次 `DayStarted` → 仍 1；日期 +1 → 2；回标题新代次同日 → 允许 +1。
  3. 主机夹具/设备：`_newDayTask` 菜单设置不产生工作线程 `DayStarted`；所有派发线程与首个 Tick 一致。
  4. 设备：读档首日 1 次、跨日 1 次、同日菜单/退标题 0 次；顺序 SaveLoaded → DayStarted。
  5. 新游戏：至少一次 `DayStarted`，且 SaveLoaded 顺序正确。
  以上全部通过 → 可把 DayStarted 提升为 GO 进入设备写入；任一项失败 → 保持 HOLD 或对该候选 NO-GO。
- **不是 NO-GO**：没有发现“必然过早/无法区分成功失败/与安全边界冲突”的反证；候选仍有明确验证路径。

---

## 8. 未做的事项与边界

- 未修改任何生产代码、公开 Mod API、事件调度器、Bootstrap、Loader、注入器、构建/部署脚本、CI、APK。
- 未构建 APK、未安装、未启动/连接设备、未运行 ADB、未读写 Saves、未创建/进入存档、未部署 Mods。
- 未 Root、未卸载、未 `pm clear`、未换签名、未刷新 `final/`、未下载来路不明的 APK/程序集。
- 未扩大 Android 11+，未引入 Harmony/NDK/原生探针/SMAPI 兼容层/内容管线/存档 API。
- 未 commit/push/建分支/改 GitHub 设置/触发 Actions；未清理用户文件。
- 未运行主机测试套件；本文所有“预计覆盖”都不是已执行。
- 未修改 `AGENTS.md`；其过时的基线文本保留原样，由本报告指出。

---

## 9. 交付与 git 检查

- 交付文件：
  - `docs/architecture/phase2.1-save-lifecycle-feasibility.md`
  - `docs/audit/phase2.1-research-report.md`
- 下一轮首个需要批准的动作：**在目标设备上停止游戏进程并做项目外备份**（设备写入前的独立批准点）。
  在该批准之前，先完成不需要设备的主机夹具；不要把主机夹具写成设备验收。

### 9.1 收尾检查（本轮实际结果）

```text
git diff --check
  -> exit 0，无输出

git status --short
  -> ?? docs/architecture/phase2.1-save-lifecycle-feasibility.md
     ?? docs/audit/phase2.1-research-report.md

git status --porcelain --untracked-files=all | Select-String 'phase2.1'
  -> 仅上述两个未跟踪文件
```

未跟踪文档另行检查（`git diff --check` 不覆盖未跟踪文件）：

- UTF-8 解码成功；两份文档分别为 643 行 / 427 行。
- 尾随空格 0；制表符 0；Git 冲突标记 0 命中。
- 敏感模式检查（用户目录绝对路径、`USERPROFILE`、设备序列号、用户名）0 命中。
- 没有暂存任何文件；没有 commit/push；没有修改 `AGENTS.md` 或其它用户文件。

