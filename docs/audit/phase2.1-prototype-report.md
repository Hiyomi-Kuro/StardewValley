# Phase 2.1-P：SaveLoaded 内部原型、合成验证与设备准入评审

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-24（+08:00） |
| 阶段 | Phase 2.1-P（内部原型 + 合成验证 + 目标程序集静态检查；未接触设备） |
| 分支 / HEAD | `main` / `8712cf09a42e8336f97a0c6a3f5b63c69b1bb101` |
| 本地 `origin/main` / 远端 `main` | 同上（`git ls-remote` 复核） |
| 工作区（开工） | 仅有两份未跟踪研究文档；无已跟踪改动 |
| 游戏程序集 | `StardewValley.dll` 1.6.15.24354；SHA-256 `587FF5FF9C258679004977BBEEC359ABA86649BD3E4860DDCAA428DF8F07E047`；MVID `573bf1e5-3245-46d6-925a-1c3713afb677`；原始、未注入 |
| APK 身份 | `com.chucklefish.stardewvalley` / versionCode **239** / versionName **1.6.15.0** |
| 结论 | **主机原型 PASS**；**设备试验准备 HOLD**；**公开发布 NOT_IN_SCOPE** |
| `DayStarted` | **HOLD**（本轮未实现、未派发、未处理每日去重） |

> 证据分级沿用研究阶段：**CURRENT_STATIC**（本轮对目标程序集的静态观察）、**HISTORICAL**（历史文档/历史设备）、
> **INFERENCE**（静态推导）、**NOT_RUN**、**UNKNOWN**。本轮没有任何“真机已验证”或“最终 APK 已验证”的结论。
> 历史 CI Run `44004070683` 属于已提交的 `8712cf0`；**本轮改动的远端 CI = NOT_RUN**。

---

## 0. 结论摘要

| 项 | 判定 | 一条最强证据 | 一条最关键限制 |
| --- | --- | --- | --- |
| 主机原型（状态协调器） | **PASS** | 34 项主机/合成检查全过：一次尝试最多一次观察；取消、退标题、重复完成、旧 A 交错、错线程、reset 竞态都按状态表收敛 | 只证明模型行为，不证明真实游戏提供令牌、线程或就绪证据 |
| 合成 Cecil 注入 | **PASS** | 正常返回、分支直指 `ret`、异常退出、错误重载、重复插桩、不支持形状、handler 边界均按预期执行或拒绝 | 合成程序集不是 `Game1`；不能外推真实库存行为 |
| 目标程序集静态改写 | **PASS（仅静态）** | 临时副本上 1 个开始钩子 + 1 个完成钩子；`IL_0159` 分支被重定向；仅 2 个方法变化；无新增 AssemblyRef；input 字节未变 | 未执行游戏方法、未构建 APK、未安装；最终 APK 验证 **NOT_RUN** |
| 设备试验准备 | **HOLD** | 首个具体阻塞项：开始信号在调用点读取的证据（mode/loader）无法与 reset 原子绑定（反例 C）；线程权威与 `currentLoader` 生命周期也未有运行时证据 | 不是“合成测试全绿所以给 GO”；也不是 NO-GO，仍有明确最小补证路径 |
| 公开发布 | **NOT_IN_SCOPE** | 未加 `IModEvents` 成员、未加公开 EventArgs、未改 AssemblyVersion 1.0.0.0 或 Framework API 1.1.0 | 本轮不决定公开命名（`WorldReady` 等）、不承诺 API |

---

## 1. 开工基线、范围与被保护的既有交付物

### 1.1 Git / 远端 / CI

| 查询 | 实际结果 | 类别 |
| --- | --- | --- |
| `git rev-parse --abbrev-ref HEAD` | `main` | CURRENT_STATIC |
| `git rev-parse HEAD` | `8712cf09a42e8336f97a0c6a3f5b63c69b1bb101` | CURRENT_STATIC |
| `git rev-parse refs/remotes/origin/main` | 同上 | CURRENT_STATIC |
| `git ls-remote origin refs/heads/main` | 同上 | CURRENT_STATIC |
| `git status --short`（开工） | 仅两份未跟踪文档 | CURRENT_STATIC |
| 历史 CI | Run `44004070683`，head_sha `8712cf0`，3/3 jobs success | HISTORICAL |
| 本轮 CI | **NOT_RUN**（未 commit、未 push、未触发 Actions） | NOT_RUN |

历史 CI 只覆盖已提交的 `8712cf0`；本轮新增的原型、夹具和报告都不在它里面。不得用该 Run 冒充本轮通过。

### 1.2 被保护的既有研究文档

两份文档在开工时已存在且未被 Git 跟踪。本轮只读，未暂存、未覆盖、未改写：

| 文件 | 大小 | SHA-256 | 最后写入 |
| --- | ---: | --- | --- |
| `docs/architecture/phase2.1-save-lifecycle-feasibility.md` | 55,260 B | `32EF29E003A0BF658C5FA4C1DAD25F69807EC3AE1EED39306BE235774D4FD4AF` | 2026-11-23 23:25:26 |
| `docs/audit/phase2.1-research-report.md` | 34,576 B | `EAF5C211FFACBAEB89AE013CF83ABB67AE3E5CABC31A636A9694BF930390CAAA` | 2026-11-23 23:25:00 |

### 1.3 本轮改动范围

只新增以下内部文件；**没有修改**任何已跟踪文件、`README.md` 四节、`Mod/`、`Tools/ModInjector/`、
`Tools/build_modded_apk.py`、`verify_modded_apk.py`、Gradle、部署脚本、CI 或 `final/`：

```text
Tools/prototype/WorldReady/Phase21.WorldReady.csproj
Tools/prototype/WorldReady/WorldReadyCoordinator.cs
Tools/prototype/WorldReady/PrototypeBridge.cs
Tools/prototype/WorldReady/PrototypeInjector.cs
Tools/prototype/WorldReady/TargetBridgeEmitter.cs
Tools/tests/PrototypeTests/Phase21.PrototypeTests.csproj
Tools/tests/PrototypeTests/Program.cs
Tools/tests/PrototypeTests/SyntheticFixture.cs
Tools/tests/PrototypeTests/TargetCheck.cs
docs/audit/phase2.1-prototype-report.md
```

原型程序集与测试项目不被默认构建、Gradle、Loader、Bootstrap、CI 或任何部署脚本引用；
生产源码中对 `Phase21` / `WorldReady` / `PrototypeInjector` 的引用数为 **0**（开工后复核）。
因此 baseline、Loader-only、默认事件版的默认产物行为保持不变；本轮**没有**生成新的 APK 或修改 `final/`。

---

## 2. 输入身份复核

### 2.1 三身份

| 维度 | 值 | 来源 | 类别 |
| --- | --- | --- | --- |
| APK 显示版本 | `1.6.15.0` | `app/apktool.yml` | CURRENT_STATIC |
| Android versionCode | `239` | `app/apktool.yml`；`AndroidManifest.xml` 包名 `com.chucklefish.stardewvalley` | CURRENT_STATIC |
| 托管程序集版本 | `StardewValley 1.6.15.24354` | Cecil `info` | CURRENT_STATIC |
| 程序集字节 | 9,280,000 B；SHA-256 `587F…E047`；MVID `573bf1e5-3245-46d6-925a-1c3713afb677` | `.tmp/phase2.1/blob-extract/StardewValley.dll`（由仓库原始 blob 解出） | CURRENT_STATIC |
| 原始 / 注入 | **原始、未注入** | 字节与 MVID 同研究阶段；无 `ModFramework.Bootstrap`、无原型 bridge | CURRENT_STATIC |

同一原始字节在本机的四份副本哈希一致：`.tmp/phase2.1/blob-extract`、`.tmp/phase0.5/refs`、
`.tmp/build-modded/refs`、`.tmp/build-loader-only/refs`。两份 `patched/` 副本大小/哈希不同
（9,262,592 B `E536…7882`、9,261,568 B `4744…6DBD`），只作“不是原始输入”的对照，**未用作本轮输入**。

### 2.2 本轮验证输入

| 项 | 值 |
| --- | --- |
| 临时副本 | `.tmp/phase2.1-prototype/target/StardewValley.prototype.dll` |
| 输入 SHA-256 | `587FF5FF9C258679004977BBEEC359ABA86649BD3E4860DDCAA428DF8F07E047` |
| 输入 MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` |
| 输入是否被改写 | **否**；工具运行前后哈希一致 |
| 解析依赖 | `.tmp/phase2.1/blob-extract`（65 个程序集）只读解析 |
| 输出 | `.tmp/phase2.1-prototype/target/StardewValley.prototype.patched.dll`，9,261,056 B；本次 MVID `1d13f2a8-76e5-4625-baae-41b129e4d0c1`；SHA-256 `6FB679EB…05046` |

工具在写入前强制拒绝以下输入：程序集名/版本不是 `StardewValley 1.6.15.24354`；已含
`ModFramework.Bootstrap`（默认注入产物）；已含 `Phase21.Prototype.WorldReadyBridge`。本轮没有把原型与默认工具链混合。

---

## 3. 原型契约与状态表

### 3.1 两个真实钩子能提供什么

| 钩子 | 调用点 | 能提供的信号 | 不能提供什么 |
| --- | --- | --- | --- |
| 开始 | `Game1.loadForNewGame(bool)` 第一条指令前 | `loadedGame`（新游戏 = `!loadedGame`）、`Game1.currentLoader` 引用、`Game1.get_gameMode()`、该调用线程（由 bridge 捕获） | 这次开始属于哪个“尝试代次”；调用点读取这些值之后到协调器记录之间是否发生了 reset |
| 完成 | `Game1.OnDayStarted()` 唯一正常 `ret` 前 | `gameMode`、`hasLoadedGame`、`currentLoader` 引用、该调用线程（由 bridge 捕获） | “这次完成属于哪次开始”的显式令牌；世界是否真正可读（玩家/地点/日期只能另读）；`OnDayStarted` 被调用的原因 |
| 失效（提议，未实现） | `Game1.ResetGameStateOnTitleScreen()` 入口 | 一次回标题/取消边界 + 可由框架维护的 epoch | 不是本轮已存在的游戏信号；未接入，也不算已证明 |

`currentLoader` 是真实静态字段（`IEnumerator<int>`），读档成功路径上由
`SaveGame.LoadAfterPermissionCheck` 赋值，成功完成后**不会归零**（研究阶段 CURRENT_STATIC；本轮未重新推翻）。
新游戏的 `loadForNewGame(false)` 路径没有 loader 对象，因此新游戏的尝试**没有令牌**，只能标记为 UNPROVEN。

### 3.2 状态表

状态：`Idle`、`PendingLoad(token, epoch)`、`PendingNew(epoch)`、`Consumed(attemptId)`、`Canceled(epoch+1)`。
所有转换在同一短锁内完成；观察在锁外不派发（本原型没有消费者）。

| 输入 | 线程 | 前置状态 | 关联依据 | 输出 | 新状态 |
| --- | --- | --- | --- | --- | --- |
| `Begin(read, mode, loader, thread, epoch)` | 读档工作线程 / 主线程 | `Idle`/`Pending*` | `mode == 6`；`loader != null`；`epoch == 当前 epoch` | 接受则返回 attemptId | `PendingLoad`（旧 pending 记 `Superseded`） |
| `Begin(new game, mode, null, thread)` | 主线程 | `Idle`/`Pending*` | `loadedGame == false` | 接受则返回 attemptId | `PendingNew`（无令牌） |
| `Complete(mode, hasLoadedGame, loader, thread)` | 必须是已确认的游戏循环线程 | `PendingLoad` | 1) 线程 == 权威；2) `mode == 3`；3) `hasLoadedGame`；4) `ReferenceEquals(loader, pending.loader)` | 1 次内部观察；`AttributionProven = true` | `Consumed` |
| `Complete(...)` | 同上 | `PendingNew` | 1) 线程；2) `mode == 3`；3) `hasLoadedGame` | 1 次内部观察；`AttributionProven = false`（明确 UNKNOWN） | `Consumed` |
| `Complete(...)` 无 pending | 任意 | `Idle`/`Consumed`/`Canceled` | 无 | 0；`NoPendingAttempt` 或 `AlreadyConsumed` | 不变 |
| `Complete(...)` 旧 loader | 任意 | `PendingLoad(B)` | `loader == L_A != L_B` | **0；`StaleLoaderEvidence`；保留 B** | `PendingLoad(B)` 不变 |
| `Complete(...)` 错线程 / 线程未知 | 其他 / 未设置 | `Pending*` | `thread != 权威` 或权威 == 0 | 0；`WrongThread` / `UnknownGameLoopThread`；清 pending | `Idle` |
| `Complete(mode 0)` | 游戏循环 | `Pending*` | 退标题 | 0；`TitleMode`；清 pending | `Idle` |
| `Complete(mode != 3)` | 游戏循环 | `Pending*` | 非 playing 模式（7/8 等不纳入） | 0；`NotPlayingMode`；清 pending | `Idle` |
| `Complete(hasLoadedGame == false)` | 游戏循环 | `Pending*` | 世界未就绪 | 0；`WorldNotLoaded`；清 pending | `Idle` |
| `ResetWorld()` | 回标题/取消路径 | `Pending*`/`Idle` | 显式失效输入 | 撤销 pending；`epoch++` | `Idle` |
| `Begin(..., 旧 epoch)` | 任意 | 任意 | reset 已发生 | 0；`ResetEpochChanged` | 不变 |

**关键规则**：一旦 `Consumed`，同一尝试不能因为跨日、菜单回调、错误模式恢复或重复完成而重新武装；
只有新的 `Begin` 才会产生新 attemptId。模型不按进程、Loader session、`Game1` 对象地址、存档名或真实日期去重。

### 3.3 只有两个钩子时明确不能表达的东西

1. **新游戏的尝试令牌**：没有 `currentLoader`，只能接受“pending-only”归因并标 `AttributionProven=false`。
2. **开始证据的原子性**：`mode`/`loader` 在游戏调用点读取，协调器随后加锁记录；reset 可以插在两者之间（反例 C）。
3. **世界可读性的完整证明**：模型只检查 `mode == 3` 与 `hasLoadedGame`；玩家、当前地点、日期、菜单/输入状态不在
   hook 载荷内，也不在本轮结论内。
4. **线程权威**：hook 只能报告 `Environment.CurrentManagedThreadId`；“哪一个是游戏循环线程”必须由外部权威提供
   （例如既有 Tick/GameLaunched）；本轮**没有接线**，也没有设备记录。
5. **“同一世界”与“同一尝试”**：loader 引用相等只证明枚举器对象相同，不证明世界数据没有被旧任务部分覆盖。

**只承诺能证明的原子性**：协调器的一个短锁只保证“记录/消费”互斥；它**不是**尝试身份的证明。
身份只来自 `loadedGame`、`currentLoader` 引用、`gameMode` 与 `hasLoadedGame` 的组合；`lock`/`Volatile`/`Interlocked`
本身不被当作“同一次尝试”的证据。完成边不读“当前全局代次”来给迟到通知重新贴标签；epoch 只用于
**开始**与 reset 的先后，不用于完成归因。

**没有“下一 Tick”交接**：本原型的完成消费发生在 `OnDayStarted` 正常 `ret` 的同步调用点，不排队到下一 Tick。
如果未来把消费转到 Tick，必须重新证明尝试身份、状态重读和取消规则；本轮不把“下一 Tick”当就绪证明。

### 3.4 对研究文档的一处更正（CURRENT_STATIC）

研究文档 §4.2 写 `Game1.OnDayStarted()` **无异常处理区**。本轮重新 dump 同一原始字节，实际有 **3 个 Finally**：

```text
maxstack=6 locals=6 ins=114
EH Finally try=IL_0045..IL_0060 handler=IL_0060..IL_006E
EH Finally try=IL_00C8..IL_00FE handler=IL_00FE..IL_0108
EH Finally try=IL_0120..IL_0142 handler=IL_0142..IL_014E
IL_0154 call  Game1::get_IsMasterGame()
IL_0159 brfalse.s IL_0160
IL_015B call  Woods::ResetLostItemsShop()
IL_0160 ret
```

结论不变：唯一正常 `ret` 在 `IL_0160`，不在任何 try/handler 内；`IL_0159` 的分支必须被重定向。
但“无 EH”这个表述是错的，完成边注入必须按有 EH 处理（本轮原型就是这样做的，`handler_returns_skipped = 0`）。
`OnDayStarted` 的直接调用者仍为 2 处：`_update IL_1480`、`set_activeClickableMenu IL_0080`。
`loadForNewGame(bool)` 的直接调用者为 8 处；其中传 `true` 的只有 SaveGame 读档闭包（工作线程）与 MoveNext 的本地联机分支，
两处都处在 loading mode 6；传 `false` 的 6 处都是新游戏/开场路径。这支持“read 开始必须 mode == 6”的模型守卫。

---

## 4. 反例 A–E 的正面回答

> 读法：**模型** = 协调器 + 合成夹具的结果；**真实** = 仅凭两个已证明可注入的游戏钩子能证明到哪一步。

### A. 开始 A → 取消 A → 退标题路径触发正常完成 → 游戏后来恢复非 0 模式

**交错**：A 在 mode 6 开始；取消后 `ExitToTitle` 把 mode 置 0；旧菜单是 SaveGameMenu/ShippingMenu 时，
`setGameMode(0)` → `set_activeClickableMenu` 会在 mode 0 调用 `OnDayStarted`；完成钩子看到 mode 0。
之后错误日志分支（`errorLogMode 11 → 3`）把 mode 以属性 setter 置 3，`_update` 尾部再次触发完成。

**模型**：mode 0 完成被 `TitleMode` 拒绝，并**清掉** pending；随后恢复 mode 3 的完成没有 pending → 0。
测试 `title_completion_then_restore_zero`。

**真实**：`ResetGameStateOnTitleScreen` 会把 `hasLoadedGame=false`、`gameMode=0`；若取消路径确实经过它，
即使没有 mode 0 完成，pending 也应在 reset 时失效。但两个钩子**不提供** reset 信号；如果取消没有产生 mode 0 完成，
pending 会残留到错误模式恢复。**结论：模型 PASS；真实依赖一个未实现的 reset 边界 → 开放。**

### B. 开始 A → 取消 A → 开始 B → A 的延迟通知到达 → B 完成

**模型（带 loader 身份）**：A 的完成带 `L_A`，B 的 pending 记录 `L_B` → `StaleLoaderEvidence`，
**保留 B**；B 的完成随后被接受，恰好 1 次。测试 `stale_a_completion_does_not_eat_b`。

**模型（不带身份，pending-only）**：A 的完成会消费 B 的 pending，产生 1 次**错误归因**的观察；B 的真实完成变成
`AlreadyConsumed`。测试 `pending_only_misattributes_b` **用反证证明：只有两个钩子、且不读 `currentLoader` 的设计不足。**

**真实**：完成边可以读 `currentLoader`，所以“读身份”的方案在真实信号上是可表达的；但“开始时的 loader 引用
在完成时仍是同一个对象”只有静态推断（成功读档后不归零），没有运行时证据。新游戏没有令牌，归因保持 UNKNOWN。
**结论：tokenless 两钩子 = 不足（已证）；带 loader 身份 = 模型 PASS，真实 HOLD 待生命周期证据。**

### C. 开始 A 与 reset 并发，后台任务在 reset 后才发出旧 A 的开始通知

**模型**：调用点在 reset 前读 `epoch=0`、`mode=6`；reset 后 epoch=1；旧开始的 `Begin(epoch=0)` →
`ResetEpochChanged`，被拒绝。测试 `stale_epoch_start_refused`；另有 100 次 `Complete` vs `Reset` 屏障竞态，
不变量（观察 ≤ 1、pending 不泄漏、接受与观察数一致）全部成立。

**真实**：两个钩子**没有** epoch。即使协调器在 `TryBeginAttempt` 内部再读自己的 epoch，游戏侧的 `mode`/loader
已经在调用点读完了；reset 可以插在“读 mode”与“协调器加锁”之间，旧开始仍会被记录。
**这是本轮真正的首个阻塞项。** 最小额外边界是：

1. 在 `Game1.ResetGameStateOnTitleScreen()` 入口增加一个失效钩子（实例方法、`void()`、543 条 IL、3 个静态调用者）；
2. 开始调用点在 `get_gameMode()` **之前**读取框架 epoch，并把 epoch 传给 bridge；
3. 协调器在同一锁内比较 epoch，不一致拒绝。

这三步都没有实现，也没有接入默认工具链。**结论：两个钩子无法关闭 C → 设计不足，HOLD。**

### D. 完成信号在线程不明或错误的线程到达 → 游戏循环后来遇到另一个无关完成

**模型**：线程权威未设置 → `UnknownGameLoopThread`；线程不等 → `WrongThread`；两者都清 pending，
之后无关完成是 `NoPendingAttempt` → 0。测试 `wrong_thread_completion_zero`、`unknown_loop_thread_zero`。

**真实**：hook 只报告 managed thread id；“游戏循环线程”必须由外部权威提供。静态调用链支持入口日完成与 Tick 同线程：
`Game1._update` 的唯一调用者是 `Game1.Update`；`Game1.Update` 经 `InstanceGame.Update` 从
`GameRunner.Update`（Tick）到达。但 managed thread id 的运行时归属 **NOT_RUN**；跨日菜单路径的线程也未测。
**结论：模型 PASS；真实线程权威未接线、未证明 → HOLD。**

### E. A 成功消费 → 跨日、开关菜单、错误模式恢复产生另一次完成信号

**模型**：消费后 pending 为空；任何后续完成都是 `AlreadyConsumed`，观察不增加。测试
`post_consumption_completions_zero`（重复完成、菜单回调、跨日完成三次均 0）。新世界必须重新 `Begin`。
**真实**：只要没有新的开始，跨日/菜单完成不会消费任何东西；但若错误模式恢复发生在取消后且 pending 未清，
则回到 A/C 的问题。**结论：模型 PASS；真实受 A/C 的 reset 边界约束。**

### 反例结论汇总

| 反例 | 模型 | 真实两钩子 | 判定 |
| --- | --- | --- | --- |
| A 取消后退标题间接完成 | 0（TitleMode 清 pending） | 需要 reset 或“取消必产生 mode 0 完成”的证明 | 模型 PASS / 真实开放 |
| B 旧 A 完成与 B 交错 | 带身份：A 拒绝、B +1；无身份：误归因 | `currentLoader` 提供身份，但生命周期未测；新游戏无令牌 | tokenless 不足；带身份 HOLD |
| C 开始与 reset 竞态 | 旧 epoch 拒绝 | 两钩子无 epoch，调用点证据不可原子绑定 | **HOLD（首个阻塞）** |
| D 错线程/未知线程 | 拒绝并清 pending；后续 0 | 线程权威未提供、未测 | 模型 PASS / 真实 HOLD |
| E 消费后重复完成 | 0 | 依赖无新开始；受 A/C 约束 | 模型 PASS |

---

## 5. 分层实施结果

### 5.1 第一层：独立内部状态协调器

- `WorldReadyCoordinator`：短锁、显式 attemptId、`PendingLoad/PendingNew`、`Consumed`、`Canceled`、epoch、
  逐原因拒绝计数；观察列表只用于测试/诊断，不派发、不回调、不写日志。
- `PrototypeBridge` / `CoordinatorSink`：把两个 hook 的原始信号转成协调器输入；`PrototypeBridge` 位于原型程序集，
  不在 `ModFramework.Runtime`，默认构建不引用。
- **没有**在每个 Tick 增加反射、扫描、字符串构造或日志；没有修改 Phase 2.0.1 的会话替换、回调隔离、重入拒绝规则。
- 本层 19 项检查全过（见 §6）。通过只表示**模型行为正确**，不代表实际游戏提供所需证据。

### 5.2 第二层：Cecil 合成注入验证

夹具 `Synthetic.Game1` 具有：实例 `loadForNewGame(bool)`、静态 `OnDayStarted()`（一个正常 ret、一个
可选的 finally、一个 `brfalse` 直指 ret 的分支）、`get_gameMode()`、`hasLoadedGame`、`currentLoader`。
钩子调用真实跨到 `Phase21.WorldReady.PrototypeBridge`，再进协调器；写出后在本进程 `Assembly.LoadFrom` **真实执行**。

| 用例 | 结果 |
| --- | --- |
| 普通返回 + 分支路径 | 开始钩子 1 次、完成钩子 1 次；分支跳过函数体时完成钩子仍执行（`BodyCount=0`） |
| 异常退出 | 方法抛异常，完成钩子 0 次；异常不冒充成功 |
| 错误重载（`loadForNewGame(int)` / `OnDayStarted(int)`） | `Resolve` 拒绝 |
| 缺失状态成员（无 `hasLoadedGame`） | `Resolve` 拒绝 |
| 重复插桩 | 第二次 `Inject` 拒绝，调用点仍各 1 次 |
| `ret` 在 try 内 | 拒绝，不写出 |
| 只有 handler 内 `ret` | 拒绝（无正常完成路径） |
| 分支直指 `ret`（手工构造） | `Verify` 拒绝（边绕过 hook） |
| handler 边界正好落在 `ret` | 边界随钩子前移到序列首指令；执行后 hook 1 次 |
| 短分支 | 注入后 `bne.un.s` 扩展为 `bne.un` |

**结构校验通过**与**写出后真实执行通过**在日志中分开记录；所有合成结果只属于合成程序集。表中标“结构”的用例
只检查写出的模块（钩子签名、调用点、边、EH、拒绝原因），不执行；标“执行”的用例会把写出的 DLL 重新
`Assembly.LoadFrom` 并调用真实方法。`Inject` 之后、`Write` 之后各读一次：写出后重新解析的模块再跑
`Verify`，不是只看内存中的指令表。

### 5.3 第三层：本地目标程序集适配验证（仅临时副本）

输入身份已在 §2 核实。工具对临时副本执行：

```text
loadForNewGame entry:
  IL_0000  ldarg  loadedGame
  IL_0004  ldsfld Game1::currentLoader
  IL_0009  call   Game1::get_gameMode()
  IL_000E  call   Phase21.Prototype.WorldReadyBridge::WorldLoading(bool, object, int)
  IL_0013  ldsflda Game1::startingGameSeed      ; 原第一条指令，未移动
OnDayStarted normal ret:
  IL_0193  call   Game1::get_gameMode()
  IL_0198  ldsfld Game1::hasLoadedGame
  IL_019D  ldsfld Game1::currentLoader
  IL_01A2  call   Phase21.Prototype.WorldReadyBridge::DayCompleted(int, bool, object)
  IL_01A7  ret
```

| 检查项 | 结果 |
| --- | --- |
| 开始钩子调用点 | 1（入口） |
| 完成钩子调用点 | 1（唯一正常 ret） |
| 被跳过的 handler 内 ret | 0 |
| `IL_0159` 分支目标 | 重定向到钩子序列首指令（`IL_0193`） |
| `ret` 是否在 try/handler 内 | 否；3 个 finally 区保持不变 |
| 直接指向 `ret` 的边 | 0 |
| maxstack（load / completion） | 5 → 5；6 → 6 |
| 变化的原方法 | 恰好 2：`Game1::loadForNewGame(bool)`、`Game1::OnDayStarted()` |
| 新增方法 | 恰好 2：bridge 的 `WorldLoading` / `DayCompleted` |
| 删除方法 | 0 |
| AssemblyRef 集合 | 未变（bridge 在本模块内，不新增依赖） |
| MVID | 变化（`573bf1e5…` → `1d13f2a8…`） |
| 输入文件字节 | 未变（前后 SHA-256 相同） |
| 输出字节 / SHA-256 | 9,261,056 B / `6FB679EB2F43F0ACAAA11D97BF7BE57DB6EAADEF0A250ABE3EC8ABB5D6F05046`（本次运行；MVID 随机，构建不可复现） |

**一个重要但非语义的写出效果**：Cecil 重写整个模块时会把**未改动方法**的 `maxstack` 重新计算成实际需要值
（例如 `Child::canTalk` 3 → 1）。因此“变化方法”指纹**排除了 maxstack**，否则会把无关方法误报为改动。
本轮仍验证了两个目标方法的栈：load 5（钩子深度 3）、completion 6（钩子深度 3），都没有增加最大栈。
除了 maxstack 归一化，改变的指令/操作数结构只有上述 2 个方法。

**候选位置复核**：`loadForNewGame` 为 `public virtual instance void(bool)`、325 条 IL、maxstack 5、入口不在任何
try/handler 内；`OnDayStarted` 为 `public static void()`、114 条 IL、3 个 finally、1 个正常 `ret`；
`currentLoader` 是静态 `IEnumerator<int>` 字段，`hasLoadedGame` 是静态 `bool` 字段，`get_gameMode()` 是静态
`byte` 方法。取消/失效边界方面，`ResetGameStateOnTitleScreen()` 是 **public virtual instance void()**、543 条 IL，
有 3 个直接调用者：`Game1.CleanupReturningToTitle`、`LoadGameMenu.saveFileScanComplete`、
`TitleMenu.ReturnToMainTitleScreen`；`ExitToTitle(Action)` 是静态 `void`、20 条 IL。
本轮**没有**给其中任何一个加钩子；它们只作为“最小额外边界”的候选（见 §4C、§9.2）。

**没有做**：没有执行 `StardewValley.dll` 的任何方法；没有组装 APK；没有安装；没有从最终 APK 反验。
最终 APK 验证 = **NOT_RUN**。baseline / Loader-only / 默认事件版也没有接入本原型（源码引用 0；工具拒绝已含
`ModFramework.Bootstrap` 的输入）。

### 5.4 最小世界就绪组合：每项能排除什么

本原型在完成边只读 `gameMode` 与 `hasLoadedGame`，并把 loader 引用用于**尝试身份**而不是就绪。
这不是完整的“世界可读”证明；逐项边界如下：

| 条件 | 能排除 | 不能证明 | 本轮是否使用 |
| --- | --- | --- | --- |
| `Game1.game1 != null` | 实例完全不存在 | 世界可用；标题界面也满足 | 否（启动时早已成立） |
| `Game1.player != null` | 无玩家的标题/早期状态 | 玩家数据来自本次存档；旧任务可能已写入 | 否（未加读取，避免扩大 hook 载荷） |
| `Game1.currentLocation != null` | 标题/无地点状态 | 地点属于本次进入且已 reset | 否 |
| `Game1.hasLoadedGame == true` | 明确回到标题后的状态 | 存档数据已完整恢复；它在 `loadForNewGame` 内较早即置 true | **是**（必要但非充分） |
| `Game1.gameMode` | `0` = 已知标题路径；`6` = 读档中 | `3` 本身不等于世界可读；`7/8` 未纳入 | **是**（只接受 `3`） |
| `Game1.year/season/dayOfMonth` | 日期字段尚未建立 | 日初始化完成；加载中日期也可能已写入 | 否（SaveLoaded 不要求日期；`DayStarted` 才需要） |
| `Game1.currentLoader` | 读档尝试有可关联的枚举器 | 枚举器返回 = 成功；成功后它可能仍非 null | **仅作身份**，不作就绪 |
| 完成线程 = 已确认的游戏循环线程 | 错线程/未知线程 | 世界状态本身正确 | **是**（权威由外部输入，未接线） |

因此本轮“就绪”严格说是**最小守卫**：`mode == 3 && hasLoadedGame && 线程合法`，再加读档尝试的 loader 身份。
玩家、地点、日期与菜单/输入状态都没有进入 hook 载荷；它们是否会作为后续硬化项，取决于下一轮的运行时证据。

---

## 6. 主机验收矩阵

> 所有测试名与日志在 `.tmp/phase2.1-prototype/logs/host-tests.log`（gitignore）。计数口径：`+1` 表示本次动作新增一次。

| 测试名 | 动作序列 | 预期观察增量 | 预期内部状态 | 实际 | 证据类型 |
| --- | --- | --- | --- | --- | --- |
| `cold_start_completion_zero` | 无开始，直接完成（mode 3） | 0 | `Idle`；`NoPendingAttempt=1` | PASS | 模型 |
| `load_entry_exactly_one` | `Begin(read,6,L)` → `Complete(3,true,L)` | +1 | `Consumed`；`AttributionProven=true` | PASS | 模型 |
| `new_game_entry_exactly_one` | `Begin(new,0,null)` → `Complete(3,true,null)` | +1 | `Consumed`；`AttributionProven=false`（UNKNOWN） | PASS | 模型 |
| `read_start_outside_load_mode_refused` | `Begin(read, mode 0)` | 0 | `Idle`；`StartOutsideLoadMode=1` | PASS | 模型 |
| `read_start_without_loader_refused` | `Begin(read,6,null)` | 0 | `Idle`；`StartWithoutLoaderEvidence=1` | PASS | 模型 |
| `post_consumption_completions_zero` | 成功后重复完成 ×3 | +0 | `Consumed`；`AlreadyConsumed=3` | PASS | 模型 |
| `title_completion_then_restore_zero` | 开始 → mode 0 完成 → mode 3 完成 | 0 | `Idle`；`TitleMode=1`、`NoPendingAttempt=1` | PASS | 模型（反例 A） |
| `not_playing_mode_zero` | 开始 → mode 7 完成 → mode 3 完成 | 0 | `Idle`；`NotPlayingMode=1` | PASS | 模型 |
| `world_not_loaded_zero` | 开始 → `hasLoadedGame=false` 完成 | 0 | `Idle`；`WorldNotLoaded=1` | PASS | 模型 |
| `wrong_thread_completion_zero` | 开始（主线程）→ 其他线程完成 → 主线程完成 | 0 | `Idle`；`WrongThread=1` | PASS | 模型 |
| `unknown_loop_thread_zero` | 未设线程权威 → 开始 → 完成 | 0 | `UnknownGameLoopThread=1` | PASS | 模型 |
| `same_day_reload_plus_one` | 进入 +1 → reset → 同日再进入 | 每次 +1（共 2） | 两个不同 attemptId；无泄漏 | PASS | 模型 |
| `switch_save_plus_one` | reset → 换 loader 再进入 | 每次 +1（共 3） | 无旧状态串档 | PASS | 模型 |
| `cancel_then_late_completion_zero` | 开始 → reset → mode 0 完成 → mode 3 完成 | 0 | `Idle`；无 pending | PASS | 模型（反例 A） |
| `stale_a_completion_does_not_eat_b` | A 开始 → reset → B 开始 → `L_A` 完成 → `L_B` 完成 | B 恰好 +1 | B pending 被保留；`StaleLoaderEvidence=1` | PASS | 模型（反例 B） |
| `pending_only_misattributes_b` | 同 B，但使用 pending-only 模型 | 1 次**错误归因** | 证明 tokenless 设计不足 | PASS（反证） | 模型 |
| `superseded_attempt_cannot_consume` | A 未完成 → B 开始 → A 完成 → B 完成 | B +1 | A 为 `StaleLoaderEvidence`；`Superseded=1` | PASS | 模型 |
| `stale_epoch_start_refused` | 调用点读 epoch 0 → reset → 旧开始 | 0 | `ResetEpochChanged=1` | PASS | 模型（反例 C） |
| `complete_versus_reset_race_invariants` | 100 次 `Complete` vs `Reset` 屏障竞态 | 接受 0–1 次，与观察数一致 | 无 pending 泄漏、无观察 >1 | PASS | 模型（并发） |
| `synthetic_injection_shape` | 合成夹具注入 + 结构校验 | hook 各 1 | 无 verify 问题 | PASS | 合成结构 |
| `synthetic_hooks_execute_once` | 工作线程开始 → 主线程完成 | 1 次观察链 | 参数/线程记录正确 | PASS | 合成执行 |
| `synthetic_start_thread_differs_from_completion` | 同上 | — | 开始线程 ≠ 完成线程（跨线程） | PASS | 合成执行 |
| `synthetic_end_to_end_one_observation` | 真实 hook IL → bridge → 协调器 | +1 | `AttributionProven=true` | PASS | 合成执行 |
| `synthetic_branch_to_ret_runs_completion` | mode 0，分支跳过函数体 | hook +1，协调器 0 | 分支重定向生效 | PASS | 合成执行 |
| `synthetic_title_mode_completion_refused` | 同上，协调器观察 | 0 | `NoPendingAttempt=1` | PASS | 合成执行 |
| `synthetic_exception_no_completion` | 方法抛异常 | 0 | 完成 hook 不被调用 | PASS | 合成执行 |
| `synthetic_missing_state_rejected` | 缺 `hasLoadedGame` | 拒绝 | `Resolve` 抛错 | PASS | 合成结构 |
| `synthetic_ambiguous_overload_rejected` | 两个同签名重载 | 拒绝 | `Resolve` 抛错 | PASS | 合成结构 |
| `synthetic_ret_in_try_rejected` | `ret` 在 try 内 | 拒绝 | `Inject` 抛错 | PASS | 合成结构 |
| `synthetic_handler_only_ret_rejected` | 只有 handler 内 ret | 拒绝 | `Inject` 抛错 | PASS | 合成结构 |
| `synthetic_double_inject_rejected` | 连续注入两次 | 拒绝 | 第二次抛错；调用点仍各 1 | PASS | 合成结构 |
| `synthetic_bypass_edge_rejected` | 手工构造“call 在 ret 前但分支直指 ret” | 拒绝 | `Verify` 报 bypass | PASS | 合成结构 |
| `synthetic_handler_end_moves_with_hook` | handler 结束于 ret | hook 1 次 | 边界移到钩子序列首指令 | PASS | 合成执行 |
| `synthetic_stale_completion_preserves_b` | 真实 hook 读取 `L_A`（B pending） | 0；B 保留 | `StaleLoaderEvidence=1`；B 随后 +1 | PASS | 合成执行 |
| 既有 `GameLaunched` / `Tick` | Loader host 套件（552 项） | 不变 | Phase 2.0 检查 0 失败 | PASS | HISTORICAL 回归 / 本轮实跑 |

**就绪条件不足**：`WorldNotLoaded` / `NotPlayingMode` / `TitleMode` / 错线程都会清 pending；
下一次观察必须由**新的 Begin** 重新武装，不能让无关完成补发。测试 `world_not_loaded_zero`、
`cancel_then_late_completion_zero` 覆盖这一点。

---

## 7. 实际运行的命令与结果

| 命令（仓库根） | 工具/运行时 | 退出码 | 结果 | 日志 |
| --- | --- | ---: | --- | --- |
| `dotnet build Tools/tests/PrototypeTests/... -c Release` | .NET SDK 8.0.404 / Windows 10.0.26100 | 0 | 0 warning / 0 error | — |
| `dotnet .../Phase21.PrototypeTests.dll` | 同上 | 0 | **34/34**（19 协调器 + 15 合成） | `logs/host-tests.log` |
| `dotnet ... --target <temp copy> <out> <refs>` | 同上 | 0 | 结构 PASS；1 开始 + 1 完成；2 方法变化 | `logs/target-check.log` |
| `dotnet ... --verify-target <patched> <refs>` | 同上 | 0 | 结构 PASS | `logs/target-verify.log` |
| `dotnet run --project Tools/tests/InjectorTests -c Release` | 同上 | 0 | **29/29** | `logs/injector-tests.log` |
| `python Tools/tests/test_ci_offline.py` | Python 3.12.10 / PowerShell 5.1.26100 | 0 | **13/13**（含 231 文件 UTF-8、隐私 OK） | `logs/ci-offline.log` |
| `python Tools/tests/test_mod_pipeline.py --dotnet ... --refs .tmp/phase0.5/refs` | 同上 | 0 | **39/39** | `logs/mod-pipeline.log` |
| `python Tools/tests/test_modded_build.py` | 同上 | 0 | **55/55** | `logs/modded-build.log` |
| `python Tools/tests/test_loader.py --dotnet ... --refs .tmp/phase0.5/refs` | 同上 | 0 | **53/53**，host summary **552/552**，Phase 2.0 0 失败 | `logs/loader-host.log` |
| `python Tools/tests/test_apk_admission.py` | 同上 | 0 | **16/16** | `logs/apk-admission.log` |
| `git diff --check` | Git | 0 | 无输出 | — |

**如实记录的失败与归因**：第一次 `test_mod_pipeline.py --refs .tmp/phase2.1/blob-extract` 得到 38/39，
失败项为 `runtime_compiles_against_android_refs`（`CS0518 System.Boolean 未定义`）。原因是该目录是
XABA blob 解出的**运行时**程序集（65 个），比文档使用的 `.tmp/phase0.5/refs` 少 7 个编译期 facade
（含 `System.Private.CoreLib.dll`），不是代码回归；改用 `.tmp/phase0.5/refs` 后 39/39。

**NOT_RUN（未执行）**：ADB / 设备 / 模拟器 / 安装 APK / 部署 Mods / 读写 Saves / 进入或创建存档；
`assembleModdedDebug`、`assembleLoaderOnlyDebug`、`assembleDebug`；`verify_modded_apk.py`；最终 APK 反验；
PowerShell 部署门禁（`test_deploy_safe.ps1`、`test_deploy_mods_safe.ps1`、`test_device_precheck.ps1`）；
GitHub Actions；commit / push / 分支。

---

## 8. 下一轮设备准入方案（只写，不执行）

> **本方案处于 HOLD 门禁之后。** 在 §9 的首个阻塞项解除前，不应执行任何一项。以下动作都需要用户逐项、单独批准。

### 8.1 需单独批准的动作

1. 指定设备的只读检查；停止目标游戏进程；项目外备份 Saves、Mods、`.mods-deploy`。
2. 安装候选 APK；部署测试 Mods / 内部诊断。
3. 创建或进入隔离测试档，正常跨日与同档重载。
4. 回退或恢复（Mods 事务、APK 版本/签名、测试档允许变化）。

### 8.2 预检与取证边界

- 显式指定设备；核对型号、Android/API、包名、versionCode 239、已安装 APK 的哈希与签名。
- **candidate 与 installed 独立取证**：`pm path` 必须恰好一条（split 明确拒绝）；普通 shell
  `sha256sum` 解析完整 64-hex 行并检查退出码；安装后哈希与候选逐字节一致才继续。
- baseline 只作内容比较，**绝不能冒充 installed**；没有 installed 证据就是 UNKNOWN。
- 如果无法确认兼容升级、发现 split、无法用普通 shell 证明安装后字节 → 停止；
  不用卸载、Root、`pm clear` 或换签名绕过。

### 8.3 备份与隔离

- 项目外备份；停止进程后记录 Saves、Mods、`.mods-deploy` 清单；按相对路径逐文件双端 SHA-256；
  检查缺失、额外文件、空目录状态。备份不可读、仍有写入或双端不一致 → 停止。
- 仅使用专用设备/环境与预先准备的隔离测试档；预先划定唯一可写范围与预期变化（跨日可能正常保存）。
- 不把用户真实存档作为夹具；不能证明隔离时停止。非测试存档必须与备份清单一致。

### 8.4 最小设备场景

| 步骤 | SaveLoaded 期望 | 说明 |
| --- | --- | --- |
| 0 标题负对照 | 0 | 停留 ≥10s；无事件；截图/logcat |
| 1 打开已准备好的隔离测试档一次 | 1 | 记录 attemptId、线程、`currentLoader` 证据、mode、`hasLoadedGame` |
| 2 正常跨一天 | 0（本次跨日不新增） | 记录完成次数与线程；同日不得重复 |
| 3 回到标题 | 0 | 记录 reset/取消边界 |
| 4 同档重读 | +1（本次重载） | 新 attemptId；不按存档名/对象去重 |
| 5 新建隔离测试档（独立场景） | 1（若纳入原型） | 若无法可靠区分新旧，记录 UNKNOWN，不猜 |

诊断应能关联：候选 APK 的 SHA-256、匿名运行 ID、尝试代次、事件计数、线程、`gameMode`、
`hasLoadedGame`、`currentLoader` 是否与开始相同、就绪检查结果。**不记录**存档名、角色名、内容或完整路径。

### 8.5 停止与回退

出现以下任一项立即停止后续写入、保留证据并报告：设备/包/签名/候选哈希不符；split/unreadable；
备份不一致、仍有写入、非测试数据变化；事件 0/2 次、顺序反了、同日重复；跨日完成在工作线程且未定义策略；
FATAL/AndroidRuntime/TypeLoad/InvalidProgram 或游戏崩溃。

回退：Mods 按 `.mods-deploy` 事务回滚，不自动清理事务；APK 只按签名准入尝试同签名旧包，
**不承诺旧 APK 一定能原地装回**；需要卸载/Root/清数据才能回退 → 列为阻塞并停止；测试档允许按预期保存，
非测试数据必须与备份一致。

---

## 9. 三项判定与下一步

### 9.1 主机原型：PASS

- 模型行为：34/34；状态表在 A–E 的反例上收敛；并发用屏障而非 Sleep；错线程、未知线程、取消、
  重复完成、旧 A 交错都有明确拒绝原因。
- 合成注入：正常/分支/异常/错误重载/重复/不支持形状/handler 边界均按预期执行或拒绝；结构校验与真实执行分开记录。
- 目标静态：临时副本上 1+1 钩子、分支重定向、仅 2 个方法变化、无新增 AssemblyRef、MVID 变化、输入未改写。
- **PASS 的边界**：只代表模型、合成程序集与静态改写通过；不代表真实游戏提供令牌/线程/就绪证据，也不代表可安装。

### 9.2 设备试验准备：HOLD

**首个具体阻塞项（按顺序）**：

1. **反例 C：开始证据与 reset 不原子。** 两个已证明可注入的钩子没有 epoch；`mode`/loader 在游戏调用点读取，
   reset 可以插在“读取”与“协调器记录”之间。模型用 epoch 关闭了它，但真实调用链没有这个输入。
2. **反例 D：线程权威未接线、未证明。** hook 只报告 managed thread id；Tick/GameLaunched 可作为权威，
   但本轮没有接线，也没有设备记录。
3. **反例 B 的运行时前提：`currentLoader` 生命周期。** 静态上成功读档后不归零；但“完成时仍是开始时的同一对象”
   没有运行时证据；新游戏没有令牌。

**能够解除它的最小证据**：

- 一份**不接触设备**的后续原型增量：`ResetGameStateOnTitleScreen` 入口失效钩子 + 开始调用点先读 epoch，
  在合成夹具上重放 C，并证明默认产物（baseline / loader-only / 事件版）仍无该钩子。
- 一份**只读**运行时记录（需单独批准）：一次隔离读档 + 一次跨日 + 一次同档重载，记录
  开始/完成/ reset 的 `gameMode`、`hasLoadedGame`、`currentLoader` 引用相等性、managed thread id 与 Tick 线程。
- 目标程序集静态检查已通过；不需要重复“钩子是否被调用”。

**不合格时的退出条件**：

- 若运行时记录显示成功读档的完成边 `currentLoader` 与开始不同，或完成线程与 Tick 线程不同，且无法用既有信号约束，
  则把该候选降级为 HOLD/NO-GO，不进入设备写入。
- 若 reset 边界必须改变公开 API、默认注入或部署脚本才能加入，则停止该路线，保持 HOLD。
- 若任何一次设备试运行出现 0/2 次、顺序颠倒、同日重复或错线程，立即停止写入并按 §8.5 回退。

### 9.3 公开发布：NOT_IN_SCOPE

- 未加 `IModEvents.OnSaveLoaded`、未加公开 EventArgs、未加 Mod 订阅点；`AssemblyVersion` 仍 1.0.0.0，
  Framework API 仍 1.1.0；事件命名（`WorldReady` 等）仍是 OPEN_DECISION。
- 未扩大 Android 11+、Harmony、NDK、原生探针、SMAPI、多人、内容管线或存档 API。
- `DayStarted` 维持 **HOLD**：本轮没有实现、没有派发、没有处理每日去重；本轮没有任何“真机已验证”。

### 9.4 下一个需要用户批准的动作

**不是设备动作。** 首个待批准项是下一轮的**内部原型增量**（仍不改默认产物、不改公开 API）：
给 `ResetGameStateOnTitleScreen` 增加失效边界，并在开始调用点先读 epoch，用合成夹具重放反例 C，
再决定是否进入设备方案评审。若用户更希望先拿运行时证据，则第二个可选批准项是 §8.1 的只读设备预检
（停止进程 + 项目外备份），但在本报告判定下不建议先做设备写入。

---

## 10. 限制与未知

- 只覆盖 `StardewValley` 1.6.15.24354 / versionCode 239 的**原始**程序集；未从设备安装包重新取字节。
- 目标程序集只做静态改写与结构校验；未执行任何游戏方法，未构建/安装 APK，最终 APK 验证 NOT_RUN。
- 合成夹具不是 `Game1`：它复刻了签名/状态成员/分支/EH 形状，但不复刻 114 条 IL 的全部语义；
  合成执行通过不等于真实游戏执行通过。
- 反例 C 的“reset 边界 + epoch”只是设计建议，未实现；反例 D 的线程权威未接线；反例 B 的 loader 生命周期未测。
- 新游戏的尝试归因明确为 UNKNOWN；没有按日期、存档名、进程、Loader session 或对象地址去重。
- 多人/本地联机、非空真实存档、断电恢复、菜单/错误的实际触发、Android 11+、其它 OEM 均未覆盖。
- 没有 commit / push / 分支 / Actions；没有修改原始游戏输入、`AGENTS.md`、`README.md` 或 `final/`；
  没有把原始程序集、存档、密钥、设备日志或大型转储写入仓库（原型只操作 `.tmp/` 中的临时副本）。

---

## 11. 收尾检查（本轮实际结果）

```text
git status --short
  ?? Tools/prototype/
  ?? Tools/tests/PrototypeTests/
  ?? docs/architecture/phase2.1-save-lifecycle-feasibility.md
  ?? docs/audit/phase2.1-prototype-report.md
  ?? docs/audit/phase2.1-research-report.md

git diff --check
  -> exit 0，无输出

git diff --stat
  -> 空（没有已跟踪文件被修改）
```

新增未跟踪文件另行检查（`git diff --check` 不覆盖未跟踪文件）：

- 10 个文件（5 个原型源文件、4 个测试入口/夹具、1 份本报告）；UTF-8 解码全部成功。
- 尾随空格 0；制表符 0；Git 冲突标记 0；敏感模式（用户目录绝对路径、`/home`、token、私钥、
  历史设备序列号）0 命中。
- 两份既有研究文档的 SHA-256 与开工时一致（§1.2）；`README.md` 四节未改；没有暂存任何文件。
- 未跟踪的构建输出（`bin/`、`obj/`）由 `.gitignore` 排除；仓库内没有新增 APK、程序集、存档或日志。

