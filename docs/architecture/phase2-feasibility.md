# Phase 2 可行性：正式游戏事件 API

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-14（+08:00） |
| 状态 | **可行性研究完成；Phase 2.0 已按本文件路线 B 实现并完成真机验收**（见 §12 与 [Phase 2.0 报告](../audit/phase2.0-report.md)、[事件 API](phase2-events.md)） |
| 基线 | 2026-11-14 推进为 `53f8171b9eb8faaee430ec8530d1eb942e5ade09`（Phase 2.0 开工基线）；本文件原始研究基线为 `4221283ee73e7a1bc26408fc7709431a9c004d40` |
| 游戏程序集 | `StardewValley.dll` 1.6.15.24354，SHA-256 `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047`，MVID `573bf1e5-3245-46d6-925a-1c3713afb677` |
| 关联 | [Phase 1.2.4 报告](../audit/phase1.2.4-report.md) · [Loader 设计](../architecture/phase1-loader-design.md) · [部署设计](../architecture/phase1.2.1-deployment-design.md) · [Phase 0 运行时可行性](runtime-feasibility.md) |

> **阅读顺序提示（2026-11-14 修订）**：本文件是**可行性研究**，不是实现说明。
> §0–§11 保留提出时的判断与推测；**§12 逐条列出 Phase 2.0 实测对它们的修订**（哪些被证实、哪些被推翻、
> 哪些仍然未知）。当前实现契约以 [phase2-events.md](phase2-events.md) 为准。
>

> 证据分级沿用本项目惯例：**【观测】** 直接来自程序集元数据/IL 或真机输出；**【推断】** 由观测按已知运行时语义推导；
> **【未知】** 本阶段无证据。桌面与 ARM64 真机分开标注。
>
> 本文件**不**把 `runtime-feasibility.md` 中的历史语句当作最终结论。该文档 §7.1/§7.2 的“Harmony 可选 / 动态 IL 可用”
> 等早期判断已被同文档 §6.5、§7.2 与 `phase0-report.md` 修订；本文件只采用修订后的结论，并重新用 1.6.15.24354
> 程序集核对了“游戏是否有事件扩展点”这一项。

---

## 0. 结论（先看这一段）

**推荐路线：B（构建期 Mono.Cecil IL 注入）+ 运行时静态事件调度器（EventDispatcher）。**

- 游戏**没有**正式的生命周期事件总线；`StardewValley.ModHooks` 在这个版本**不存在**（【观测】，REA
  `unmatched_type_names`）。全程序集只有 **34 个 `event`**，全部属于网络同步、UI/文本输入、本地化、地点请求等局部对象，
  没有 `DayStarted` / `Tick` / `SaveLoaded` 这类游戏生命周期事件。
- 因此“A 游戏已有扩展点”只能覆盖极少数局部事件（如 `TitleMenu.OnCreatedNewCharacter`、
  `LocalizedContentManager.OnLanguageChange`），**不足以作为正式事件 API 的主体**。
- Harmony/运行时 detour（C）继续**不可行**：`Reflection.Emit` 在裁剪后的运行时缺失（Phase 0 T09/T10），
  Harmony 的 Prefix/Postfix/Transpiler 依赖它；原生 detour 的指令缓存刷新问题未解决（T12 崩溃），
  本阶段不重新启用 T11/T12，也不引入 NDK。
- 首批事件选择 **2 个**，都可以在**标题界面**验证、不需要进入或创建存档：
  1. `GameLaunched`：注入到 `StardewValley.GameRunner.Initialize()` 末尾；每个进程一次。
  2. `Tick`：注入到 `StardewValley.GameRunner.Update(Microsoft.Xna.Framework.GameTime)` 开头；每帧一次。
- 二者都位于 `GameRunner`（`Microsoft.Xna.Framework.Game` 的子类）上，而 **不是** `Game1`：
  `Game1` 实例只有在读档/新建存档后才存在，`Game1.Update` / `Game1.OnDayStarted` 不适合作为首帧可用事件。

**结论边界**：以上为静态证据与既有运行时事实的组合；`GameLaunched`/`Tick` 的线程、顺序、异常隔离与 AOT 行为
仍需 Phase 2 的真机实现验收（见 §9）。本阶段没有实现正式事件框架。

---

## 1. 证据与方法

### 1.1 程序集身份

| 项 | 值 | 来源 |
| --- | --- | --- |
| 文件 | `.tmp/build-modded/refs/StardewValley.dll`（== APK 内原始条目） | 本机 |
| SHA-256 | `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047` | `Get-FileHash` |
| 大小 | 9,280,000 B | 同上 |
| 程序集 / TFM | `StardewValley` 1.6.15.24354 / `.NETCoreApp,Version=v8.0` | REA `inspect_managed_artifact` `ev_5d1f1c35…` |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` | 同上 |

同一份字节此前已由 Phase 1.2 在 V1963A 上运行（探针 12/12），因此“程序集身份 → 真机行为”的链条不是本阶段新建的。

### 1.2 取证工具与边界

| 工具 | 用途 | 限制 |
| --- | --- | --- |
| REA `inspect_managed_members`（`detail: summary`） | 按类型选择成员，确认类型存在性、基类、方法/字段计数 | 只读取元数据，不加载、不执行；token 是构建内坐标 |
| 本阶段临时工具 `.tmp/phase1.2.4/meta-dump` | 枚举全部 `Event` 行、基类、方法/字段名 | 使用 `System.Reflection.Metadata` 只读；不是产品代码 |
| 本阶段临时工具 `.tmp/phase1.2.4/il-dump` | 反汇编选中方法的 IL（Cecil） | 只读；不修改程序集 |

两个临时工具都在 `.tmp/`（gitignore），不属于交付代码。它们的存在只为把结论锚定到具体 IL/元数据，
而不是把“反编译看起来像”当作事实。

### 1.3 与历史文档的关系

- `runtime-feasibility.md` §9.2 的“解析 manifest → 依赖拓扑 → 模组入口调用”已经由 Phase 1/1.1 实现；
  该文档 §9.2 中“事件总线尚未实现”仍然成立。
- `phase0-report.md` §5 D3 曾建议 `StardewValley.ModHooks` 作为扩展点。**该建议对本版本不成立**：
  `StardewValley.ModHooks` 在 1.6.15.24354 的 2867 个 TypeDef 中不存在（REA `unmatched_type_names=["StardewValley.ModHooks"]`）。
  `ModHooks` 是 SMAPI（桌面模组加载器）自己的类型，不在原版游戏程序集里；此处纠正历史表述，不追溯修改历史文档。
- `runtime-feasibility.md` 的“动态 IL 可用”是【观测】且成立；但“Harmony 可行”不是本轮结论，见 §3.3。

---

## 2. 当前接口与初始化时序

### 2.1 已有接口（源码）

`Mod/ModFramework.Runtime/Contracts.cs`：

```csharp
public interface IModEntry { void Initialize(IModContext context); }
public interface IModContext {
    string ModId; string ModName; string ModVersion; string GameVersion;
    string FrameworkVersion; string ModDirectory; IModLogger Log;
}
```

`Loader.LoadAndInitialize` 在预检、依赖解析、版本门禁之后调用 `entry.Initialize(context)`；
`context` 是只读视图（`ModContext.cs`），**没有**游戏对象、事件或内容管线。

### 2.2 注入点与真实 IL（【观测】）

`Tools/ModInjector/Program.cs` 把 `ModFramework.Bootstrap.Start()` 插入
`StardewValley.MainActivity.OnCreatePartTwo()` 的第一条指令之前。

原始（未注入）`OnCreatePartTwo` 的关键 IL：

```text
IL_0000 ldstr "MainActivity.OnCreatePartTwo"
IL_0005 call  Log::It
IL_000a call  MobileDisplay::SetupDisplaySettings
IL_000f ldarg.0 ; IL_0010 call MainActivity::SetPaddingForMenus
IL_0015 newobj StardewValley.GameRunner::.ctor      ← 不是 InstanceGame（纠正 Phase 0 报告）
IL_001a stloc.0
IL_001b…IL_0036  this.SetContentView(game.Services.GetService<View>())
IL_003b ldloc.0 ; IL_003c stsfld StardewValley.GameRunner::instance
IL_004e callvirt Microsoft.Xna.Framework.Game::Run
```

因此：

- `Bootstrap.Start()` 在 **`new GameRunner()` 之前**、在任何 MonoGame `Game` 实例存在之前运行；
- loader 初始化完成后，`OnCreatePartTwo` 才创建 `GameRunner`，然后 `Game.Run()` 进入
  `Initialize → LoadContent → Update/Draw` 循环；
- `Game1`（游戏状态）由 `GameRunner.CreateGameInstance(...)` 在**读档/新建存档**时创建；
  标题界面阶段 `Game1` 实例**不存在**。

### 2.3 运行时对象关系（【观测】）

| 类型 | 基类 | 作用 | 关键方法 |
| --- | --- | --- | --- |
| `StardewValley.MainActivity` | `Microsoft.Xna.Framework.AndroidGameActivity` | Android Activity；创建 `GameRunner` 并调用 `Game.Run()` | `OnCreate`, `OnCreatePartTwo` |
| `StardewValley.GameRunner` | `Microsoft.Xna.Framework.Game` | **真正的 MonoGame Game**；持有 `gameInstances`，驱动循环 | `.ctor`, `Initialize`, `LoadContent`, `Update(GameTime)`, `Draw`, `CreateGameInstance`, `ExecuteForInstances` |
| `StardewValley.InstanceGame` | `System.Object` | 薄包装；多数成员转发到 `GameRunner.instance` | `Update`/`Draw`/`Initialize` 均为 `ret`（空实现） |
| `StardewValley.Game1` | `StardewValley.InstanceGame` | 单个游戏存档/实例的状态与逻辑 | `Update(GameTime)`、`_update`、`OnDayStarted()`、`Initialize` |

`GameRunner..ctor` 的 IL 里有 `Microsoft.Xna.Framework.Game::add_Exiting`（【观测】），说明
MonoGame 的 `Exiting` 事件在 `GameRunner` 构造时就被自身订阅；`GameRunner.OnActivated` 也是
`Game.Activated` 的处理器。

---

## 3. 三条路线比较

### 3.1 A：游戏已有扩展点

**观测**：全程序集 `Event` 表 34 行，逐条枚举如下（`MetaDump`，`StardewValley.dll`）：

| 类别 | 事件 |
| --- | --- |
| 网络同步（Netcode/Network） | `NetArray<T>.OnFieldCreate`、`NetCollection<T>.OnValueAdded/OnValueRemoved`、`NetDictionary<K,V>.OnValueAdded/OnValueRemoved/OnValueTargetUpdated/OnConflictResolve`、`NetEvent0.onEvent`、`AbstractNetEvent1<T>.onEvent`、`NetFieldBase<T,..>.fieldChangeEvent/fieldChangeVisibleEvent`、`NetString.FilterStringEvent`、`NetHashSet<T>.OnValueAdded/OnValueRemoved`、`NetList<T>.OnElementChanged/OnArrayReplaced`、`NetRefBase<T,..>.OnConflictResolve`、`NetFarmerCollection.FarmerAdded/FarmerRemoved`、`NetPosition.fieldChangeEvent/fieldChangeVisibleEvent`、`OverlayDictionary<K,V>.onValueAdded/onValueRemoved` |
| 本地化/地点 | `LocalizedContentManager.OnLanguageChange`、`LocationRequest.OnLoad`、`LocationRequest.OnWarp` |
| UI/菜单 | `Menus.TextBox.OnEnterPressed/OnTabPressed/OnBackspacePressed`、`Menus.TitleMenu.OnCreatedNewCharacter` |
| 数据/装饰 | `SerializableDictionary<K,V>.CollectionChanged`、`Locations.DecorationFacade.OnChange`、`Inventories.Inventory.OnSlotChanged/OnInventoryReplaced` |

**判断**：

- 这些都是**实例事件**，且绑定在具体对象上；`Loader` 初始化时（`GameRunner` 尚未构造）拿不到实例。
- 没有一个能表达“游戏已启动/每帧/天开始/存档载入”。
- `ModHooks` 不存在；`Game1.events`（SMAPI 的 `Game1.events` 字段）也不存在。
- 结论：**A 不能作为正式事件 API 的主体**；只能作为后续“局部事件适配”的可选项（例如语言切换）。

### 3.2 B：构建期 Mono.Cecil IL 注入

**已具备的事实**：

- 同一套 Cecil 注入已经在 `MainActivity.OnCreatePartTwo` 上工作（Phase 0–1.2.3 真机验证）；
- 注入后 MVID 改变、`IlChecks` 校验分支/栈/leave、`verify` 子命令检查 Bootstrap 类型、首条调用、
  loader API 与 Mods 目录；
- 程序集被修改后 AOT 镜像判定过期、回退 JIT（这是已付出的代价，Phase 1 起一直如此）；
- 版本绑定明确：1.6.15.0 / versionCode 239。

**扩展方式**（本阶段只设计，不实现）：

1. 在 `GameRunner.Initialize()` 末尾插入 `call ModFramework.Bootstrap.GameLaunched()`；
2. 在 `GameRunner.Update(GameTime)` 开头插入 `call ModFramework.Bootstrap.Tick()`；
3. `Bootstrap` 在 `Start()` 里把 `ModFramework.Runtime.EventDispatcher` 的静态方法缓存成委托，
   `GameLaunched()/Tick()` 只做 `if (del != null) del();`（每帧一次静态委托调用，可忽略）；
4. 仍然用 Cecil 的 `InsertBefore`/`Append`，并扩展 `IlChecks` 校验新调用；
5. `verify` 增加“两个事件钩子存在且调用目标正确”的检查，`--no-event-hooks` 生成基线。

**风险**：`GameRunner.Update` 是大方法，注入点在方法头；版本升级后方法名/签名可能变化。因此必须有
**签名 + IL 形状**校验，并在 `verify_modded_apk.py` 与 CI 静态检查里 fail-closed。

### 3.3 C：Harmony / 运行时 detour

**继续不可行**，理由与 Phase 0 一致且未被本阶段推翻：

- `Reflection.Emit` 缺失（`DynamicMethod` / `AssemblyBuilder` 均失败，T09/T10）；
  Harmony 的 `MethodReplacement`/Transpiler 依赖动态方法生成；
- MonoMod `NativeDetour` 的原生路径需要可写代码页 + 指令缓存刷新；T11 只证明了 `mprotect` 返回成功，
  T12 直接写跳转桩导致原生崩溃，且没有用户态 `__builtin___clear_cache` 手段（本机无 NDK）；
- 按项目边界，本阶段**不重新启用 T11/T12**，也不引入 NDK。

**结论**：C 仅作为“未来若引入 NDK 且完成 icache 验证”的备选，不作为 Phase 2 路线。

---

## 4. 初始化时序：哪些事件能在此刻注册

| 时机 | 已存在的对象 | 可注册 | 不可注册 |
| --- | --- | --- | --- |
| `Bootstrap.Start()`（`OnCreatePartTwo` 头） | 无 `GameRunner`、无 `Game1`、无 `Game` 实例；`StardewValley` 程序集已加载 | **静态回调/订阅表**（本方案） | `Game.Exiting`、`Game.Activated`、`TitleMenu.OnCreatedNewCharacter`、`LocationRequest.*`、任何 `Game1` 实例事件 |
| `GameRunner..ctor` 之后、`Game.Run()` 之前 | `GameRunner` 实例存在，尚未 Initialize | 实例事件（需要在 `newobj` 之后加钩子） | `Game1` 实例事件 |
| `GameRunner.Initialize()` 末尾 | `GameRunner` 已初始化，`LoadContent` 尚未/刚结束 | `GameLaunched`（本方案） | `Game1` 实例事件 |
| `GameRunner.Update()` 每帧 | 循环运行中；标题界面 `Game1` 仍可能不存在 | `Tick`（本方案） | `DayStarted`（无 `Game1`） |
| `GameRunner.CreateGameInstance()` 之后 | `Game1` 实例存在 | `SaveLoaded`/`DayStarted` 等后续事件 | — |

**关键结论**：Loader 初始化时只能登记**静态订阅**，不能订阅任何实例事件；因此正式事件 API 必须是
“静态调度器 + 后续注入点驱动”，而不是“把游戏事件直接转发给 mod”。

---

## 5. 首批事件：精确触发点、签名、线程与生命周期

### 5.1 `GameLaunched`

| 项 | 值 |
| --- | --- |
| 触发方法 | `StardewValley.GameRunner::Initialize()`（无参数，返回 `void`） |
| 注入位置 | 方法末尾（所有 `ret` 之前），`call ModFramework.Bootstrap.GameLaunched()` |
| 触发次数 | 每个进程 1 次（MonoGame `Game.Initialize` 只调用一次） |
| 线程 | 【推断】Android 主线程：`MainActivity.OnCreatePartTwo` 同步调用 `Game.Run()`，MonoGame Android 在调用线程上跑循环 |
| 顺序 | `Bootstrap.Start()` → 所有 mod `Initialize` 完成 → `new GameRunner` → `Game.Run` → `Initialize` → `GameLaunched` |
| 生命周期 | 在 `Tick` 之前；不依赖存档 |
| 可验证性 | 标题界面即可（报告写入 `game-launched=1`） |

### 5.2 `Tick`

| 项 | 值 |
| --- | --- |
| 触发方法 | `StardewValley.GameRunner::Update(Microsoft.Xna.Framework.GameTime)`（`void`） |
| 注入位置 | 方法第一条指令之前，`call ModFramework.Bootstrap.Tick()` |
| 触发频率 | 每帧一次（MonoGame 固定/可变步长由游戏配置决定） |
| 线程 | 同上，主线程 |
| 顺序 | 每次 `Tick` 内按订阅顺序回调；`GameLaunched` 已先发生 |
| 生命周期 | 从第一帧到进程结束；退出时不额外发事件 |
| 可验证性 | 标题界面即可（报告写入 `tick-count=N`，N>0） |

### 5.3 为什么不是 `Game1.Update` / `Game1.OnDayStarted`

- `Game1` 实例只在读档/新建存档后由 `GameRunner.CreateGameInstance` 创建；标题界面没有。
- `Game1.OnDayStarted()` 的调用点是 `Game1._update` 与 `Game1.set_activeClickableMenu`（IL 观测，
  共 2 处），触发需要进入存档并跨天；本阶段不进入/创建存档。
- `Game1.Update` 由 `GameRunner` 在运行期按实例驱动（`GameRunner.Update` → `ExecuteForInstances` 一类的
  实例遍历；静态扫描只能看到对 `InstanceGame.Update` 的虚拟调用，对 `Game1.Update` 的直接 `call` 计数为 0）。
  因此事件钩子应放在 `GameRunner` 的循环方法上，而不是 `Game1`。

### 5.4 订阅契约（设计）

```csharp
public interface IModEvents {
    IDisposable OnGameLaunched(Action<GameLaunchedEventArgs> handler);
    IDisposable OnTick(Action<TickEventArgs> handler);
}
```

- `IModContext` **增加**只读属性 `IModEvents Events { get; }`；这是加法式接口变更，
  旧 mod 只调用已有成员，二进制兼容；回归用旧版编译的 fixture 验证。
- 订阅在 `Initialize` 内完成；`Loader` 在所有 mod 初始化完成后才允许第一次 dispatch。
- `IDisposable.Dispose()` 取消订阅；dispatch 前对订阅者做**快照**，回调中增删不影响本轮。
- 顺序：`(mod id 序, 订阅序)`，确定性；每次 dispatch 复现。
- 异常隔离：每个 handler 单独 `try/catch`，记录 mod id/事件/异常类型，继续下一个；
  不让异常穿透到游戏循环。是否在连续失败后自动退订，作为 Phase 2 的策略项（建议先不自动退订，保持确定性）。
- 重复注册：允许（与 .NET 事件一致），每次调用都触发；文档明确“重复注册=重复回调”。
- 线程：全部在主线程；handler 不得阻塞；框架不提供线程切换。
- 不在 `Initialize` 期间 dispatch：`GameLaunched` 最早发生在 `GameRunner.Initialize`，晚于所有 `Initialize`。

---

## 6. 兼容方案

| 对象 | 方案 |
| --- | --- |
| 现有 `IModEntry` / `IModContext` | 不改签名；`IModContext` 只增加 `Events` 属性 |
| 旧 mod（按 1.0.0 编译） | 不调用 `Events`，行为不变；用旧版 fixture 做二进制兼容回归 |
| 新 mod | 编译期引用新 Runtime；`context.Events.OnTick(...)` |
| 框架版本门禁 | `frameworkVersion` 已是 manifest 门禁字段；事件 API 需要提升 framework 版本并写清最低版本 |
| 无事件 mod | 不订阅即零开销（`Bootstrap.Tick()` 只判断委托非空） |

**不采用**“在 `IModEntry` 上新增方法”：那会破坏旧 mod 的类型实现（编译期/运行期都要改），
而 `IModContext` 是框架实现、mod 消费，增加属性风险最低。

---

## 7. 版本绑定、补丁幂等、IL 校验与回退

### 7.1 版本绑定

- 目标固定：1.6.15.0 / versionCode 239 / `StardewValley.dll` SHA-256 + MVID（见 §1.1）。
- 注入器必须校验：
  - `StardewValley.MainActivity::OnCreatePartTwo` 存在且签名匹配（已有）；
  - `StardewValley.GameRunner::Initialize()` 与 `Update(GameTime)` 存在、非静态、参数/返回类型精确匹配；
  - 方法体非空、`Update` 首指令可插入、`Initialize` 至少有一个 `ret`。
- 任一不满足 → 构建失败（不是运行时才炸）。

### 7.2 幂等

- 已存在 `ModFramework.Bootstrap`：不重复注入类型；
- 已存在对 `Bootstrap.Start/GameLaunched/Tick` 的 `call`：不重复插入；
- 对同一输入重复执行 `patch` 必须得到相同拓扑（除 MVID 外），`verify` 通过；
- 回归：对已注入的 DLL 再跑一次 `patch`，断言调用次数仍为 1。

### 7.3 IL 校验

`IlChecks` 现有检查（Bootstrap 类型、MVID 变化、Start 首指令、分支目标、leave、栈深）扩展：

- 新增钩子的 `call` 目标必须是 `ModFramework.Bootstrap::GameLaunched/Tick`；
- 插入点之后原指令偏移整体后移但控制流边界不变；
- `Tick` 插入后方法栈深不变（`call` 无参无返回）；
- `Initialize` 末尾插入不得落在 `try` 区域内（若存在异常处理器，插在最后一个 `ret` 前并校验 handler 边界）。

### 7.4 AOT / MVID / AndroidRefsDir

- 修改 `StardewValley.dll` → `module.Mvid` 必须更新，否则复用旧 AOT 镜像（已实现且真机验证）；
- 该程序集 AOT 镜像失效 → 回退 JIT；这是 Phase 0 起的既有代价，事件钩子不额外改变；
- 任何引用 `ModFramework.Runtime` 的程序集必须用 `-p:AndroidRefsDir=<65 refs>` 编译
  （AGENTS §11.2 第 6 条）；事件 API 新增成员不改变这条约束；
- AOT 方法 vs JIT 方法：`GameRunner.Update` 是否被 AOT 覆盖【未知】；注入后该程序集整体回退 JIT，
  因此不需要对单个方法做 AOT 特判。

### 7.5 回退

| 场景 | 回退 |
| --- | --- |
| 事件钩子导致启动/循环异常 | 用 `--no-event-hooks` 重新构建（保留 loader 钩子）并重装 APK |
| 整个 mod 框架异常 | 用 `-PmodFramework=false` 构建基线 APK（仅清单补丁），重装 |
| 单次设备部署失败 | 保留旧 APK/旧 Mods，不自动清理事务；按 `.mods-deploy` journal 恢复 |

回退验证必须包括：基线 APK 的 `verify` 不包含事件钩子调用、loader 仍正常、标题界面可进入。

---

## 8. 最小实施范围

| 文件/模块 | 变更 |
| --- | --- |
| `Mod/ModFramework.Runtime/Contracts.cs` | 新增 `IModEvents`、`GameLaunchedEventArgs`、`TickEventArgs`；`IModContext.Events` |
| `Mod/ModFramework.Runtime/EventDispatcher.cs`（新） | 订阅表、快照派发、异常隔离、计数、日志 |
| `Mod/ModFramework.Runtime/ModContext.cs` | 传入 dispatcher |
| `Mod/ModFramework.Runtime/Loader.cs` | 初始化完成后标记“可派发”；报告写 `events=` 计数 |
| `Tools/ModInjector/Program.cs` | 新增两个钩子 + `--no-event-hooks`；`Bootstrap.GameLaunched/Tick` 发射与委托缓存 |
| `Tools/ModInjector/IlChecks.cs` | 校验新钩子 |
| `Tools/tests/ModFramework.Tests/*` | 订阅/退订/顺序/异常/重复注册/旧 mod 兼容 |
| `Tools/tests/test_mod_pipeline.py` | 注入后 verify 两个钩子、幂等、`--no-event-hooks` 基线 |
| `Tools/verify_modded_apk.py` | 校验事件钩子与基线 |
| 文档 | `manifest-spec.md`（frameworkVersion 最低版本）、`phase2` 验收记录 |

**不做**：正式 Harmony/NDK、事件优先级/取消、跨线程调度、内容管线、存档 API、SMAPI 兼容层。

---

## 9. 可执行验收标准（Phase 2 最小实现）

前置：一台本项目已支持的设备（V1963A / API 29 / arm64-v8a），`-p:AndroidRefsDir` 编译，
`verify_modded_apk.py` 通过。

| # | 验收项 | 通过标准 |
| --- | --- | --- |
| E-1 | 构建期钩子 | `ModInjector verify` 报告 `Start`、`GameLaunched`、`Tick` 三个调用目标正确；MVID 已变 |
| E-2 | 幂等 | 对已注入 DLL 再 patch，三个调用各出现 1 次；输出字节稳定（MVID 除外） |
| E-3 | `GameLaunched` | 标题界面启动后，报告 `game-launched=1`，且发生在第一次 `tick` 之前 |
| E-4 | `Tick` | 标题界面停留 ≥5 s，`tick-count>0` 且持续增长；不进入存档 |
| E-5 | 线程 | 报告记录 `thread-id`，`GameLaunched` 与 `Tick` 相同且等于主线程（与 `OnCreatePartTwo` 记录一致） |
| E-6 | 顺序 | 两个订阅者按 (mod id, 订阅序) 收到；日志顺序可复现 |
| E-7 | 异常隔离 | 一个 handler 每次抛异常，另一个仍收到全部事件；报告 `handler-faults>0`，游戏不崩 |
| E-8 | 退订 | `Dispose()` 后不再收到；重复 `Dispose()` 幂等 |
| E-9 | 重复注册 | 同一 handler 注册两次 → 收到两次；退订一次 → 收到一次 |
| E-10 | 旧 mod 兼容 | 按 1.0.0 编译的 fixture（不调用 `Events`）仍 `initialized=1` |
| E-11 | 基线回退 | `--no-event-hooks` 构建：`verify` 无事件钩子；游戏正常到标题界面；报告无 `game-launched/tick` |
| E-12 | 无存档副作用 | `Saves` 文件数与 mtime 不变（与 1.2.4 同口径） |

**本阶段未执行 E-1…E-12**：它们属于 Phase 2 实现验收，不是本可行性研究的结论。

---

## 10. 风险与未知

| # | 项 | 类型 | 说明 |
| --- | --- | --- | --- |
| R-1 | `GameRunner.Update` 是否被 AOT 覆盖 | 未知 | 注入后整个程序集回退 JIT，影响主要是性能；需在 Phase 2 记录帧率/启动时间 |
| R-2 | 主线程假设 | 推断 | 由 `OnCreatePartTwo` 同步 `Game.Run()` 推导；Phase 2 必须用线程 id 实测 |
| R-3 | `GameRunner.Initialize` 的异常处理器边界 | 未知 | 若方法体含 `try`，末尾插入需要精确处理；注入器必须先审计 IL |
| R-4 | 每帧委托调用开销 | 推断可忽略 | 一次静态委托 + 一次 `if`；需在低端机测 |
| R-5 | 版本升级脆弱性 | 已知 | 方法与签名变化会使构建失败（fail-closed），需维护版本绑定表 |
| R-6 | 旧 mod 二进制兼容 | 推断 | 增加接口成员对消费者安全；用 fixture 验证 |
| R-7 | 只覆盖 ARM64/API 29 真机 | 已知 | 不外推到 Android 11+、其它 OEM |

---

## 11. 本阶段准入结论

- **可以进入 Phase 2 实现**：路线 B 有可复用的注入链、明确的注入点、可静态验证的签名，且首批两个事件在标题界面可验收。
- **准入缺项**：本阶段对 `Tools/` 与测试的修改**尚未 commit/push**，因此没有对应的远端 CI；
  用户提交后必须先确认 `powershell-gates` 三步真实通过（见 [Phase 1.2.4 报告](phase1.2.4-report.md) §4）。
- **不得**把本文件写成“事件框架已实现”或“Harmony 可行”。本阶段只做了可行性研究。

---

## 12. Phase 2.0 实测修订（2026-11-14）

> 本节由 Phase 2.0 实施/验收补写。它**不改写**上文的历史判断，只逐条标注实测结果。
> 证据来源：真实 `StardewValley.dll` 的 IL 审计、合成 Cecil 夹具执行、主机测试、A/B/C 构建校验、
> V1963A 真机运行（详见 [Phase 2.0 报告](../audit/phase2.0-report.md) 与
> [真机验收](../audit/phase2.0-device-acceptance-report.md)）。

| # | 上文表述 | 原状态 | Phase 2.0 证据 | 现在 |
| --- | --- | --- | --- | --- |
| R-1 | `GameRunner.Initialize()` 无参数、返回 `void`、末尾可插钩子 | 【观测】§5.1 | 真实程序集：`Family Virtual void Initialize()`，15 条指令、**0 个异常处理器**、**1 个 `ret`**、无分支 | **证实**；注入在唯一正常 `ret` 前 |
| R-2 | `GameRunner.Update(GameTime)` 可在方法头插钩子 | 【推断】§5.2 | 真实程序集：319 条指令、**3 个 finally**、2 个 `ret`、无 `catch`；方法头插入合法且不改栈 | **证实**；`Tick` 是第一条指令 |
| R-3 | `GameLaunched` 每进程一次 | 【推断】§5.1 | 调度器状态机 + 主机测试 `events_game_launched_exactly_once`；真机 `GameLaunched=1` | **证实**（框架保证，不依赖 MonoGame 只调一次） |
| R-4 | “主线程 / Android UI 线程” | 【推断】§5.1/§5.2 | 真机记录 Loader、`GameLaunched`、`Tick` 的 `ManagedThreadId` | **已按实测改写**：见真机报告；契约只承诺“进入游戏方法的线程”，不承诺 UI 线程 |
| R-5 | “每帧一次” | 【推断】§5.2 | `Update` 每次进入计数；真机计数随停留时间增长 | **改写**：每进入一次 `Update` 一次，**不**等于渲染帧/60 Hz |
| R-6 | `Initialize` 若含 `try` 需要精确处理 | 【未知】R-3 | 实测**不含**任何异常处理器；注入器已实现边界搬移与拒绝规则，并用合成夹具覆盖 `catch` 内 `ret` / handler 边界 | **本目标证实**；通用形状已实现并测试 |
| R-7 | 旧 Mod 二进制兼容 | 【推断】R-6 | 冻结 1.0.0 契约桩编译的 `LegacyFixtureMod`（不引用 `Events`）由新 Runtime 初始化成功 | **证实**；`AssemblyVersion` 保持 1.0.0.0 |
| R-8 | 注入后 MVID 变化、AOT 回退 JIT | 【观测】§7.4 | 每次 patch 新 MVID；`IlChecks` 强制 MVID 变化 | **证实**（沿用） |
| R-9 | `Bootstrap.Tick()` 只判断委托非空、每 Tick 一次静态调用 | 【推断】§3.2 | 生成 IL：`ldsfld; brtrue; ldsfld; callvirt Action::Invoke; leave; ret`（+catch） | **证实**；无反射、无文件 IO |
| R-10 | `--no-event-hooks` 生成基线 | 【设计】§7.5 | 已实现为 B 构建（`assembleLoaderOnlyDebug`）；verify 从最终 APK 反向确认“无钩子” | **证实** |
| R-11 | `Delegate.CreateDelegate` 在裁剪后 BCL 可用 | 【未知】 | 静态扫描 65 个游戏程序集：`System.Delegate::CreateDelegate(Type,MethodInfo)` 被 Java.Interop 等引用 | **证实**（静态）；真机运行进一步佐证 |
| R-12 | Harmony / 运行时 detour | 不可行 §3.3 | 本阶段未重新评估，未引入 NDK/Harmony | **维持不可行结论** |

**仍未验证**（与本阶段边界一致）：低端机帧率影响、Android 11+、其它 OEM、非空存档场景、
`Tick` 与渲染帧的精确关系、`GameLaunched` 之后内容资源何时全部就绪。
