# Phase 2.0 最小游戏事件 API

| 项 | 值 |
| --- | --- |
| 状态 | **已实现并真机验收**（`GameLaunched` + `Tick`，构建期注入，无 Harmony/NDK） |
| 基线 | `53f8171b9eb8faaee430ec8530d1eb942e5ade09` |
| Framework API 版本 | **1.1.0**（`Loader.FrameworkApiVersion`，manifest 门禁使用） |
| Framework AssemblyVersion | **1.0.0.0**（保持不变，见 §6） |
| 游戏程序集 | `StardewValley.dll` 1.6.15.24354，MVID `573bf1e5-3245-46d6-925a-1c3713afb677` |
| 关联 | [可行性](phase2-feasibility.md) · [Phase 2.0 报告](../audit/phase2.0-report.md) · [真机验收](../audit/phase2.0-device-acceptance-report.md) |

> 证据分级沿用项目惯例：**【观测】** 直接来自程序集/IL、主机测试或真机输出；**【决定】** 本阶段明确选择的契约；
> **【未知】** 本阶段没有证据。

---

## 1. 公共契约

```csharp
public interface IModEvents
{
    IDisposable OnGameLaunched(Action<GameLaunchedEventArgs> handler);
    IDisposable OnTick(Action<TickEventArgs> handler);
}

public readonly struct GameLaunchedEventArgs
{
    public string GameVersion { get; }
    public string FrameworkVersion { get; }
    public int ThreadId { get; }
}

public readonly struct TickEventArgs
{
    public long TickCount { get; }
    public int ThreadId { get; }
}

public interface IModContext
{
    // Phase 1.0 成员不变
    string ModId { get; } string ModName { get; } string ModVersion { get; }
    string GameVersion { get; } string FrameworkVersion { get; } string ModDirectory { get; }
    IModLogger Log { get; }

    // Phase 2.0 新增
    IModEvents Events { get; }
}
```

事件参数是**只读 struct**：每次派发零堆分配。公共契约不暴露任何 `StardewValley.*` 类型
（`GameTime`、`Game1`、`GameRunner` 都不出现）。

### 1.1 `GameLaunched`

| 项 | 契约 |
| --- | --- |
| 触发点 | `StardewValley.GameRunner.Initialize()` 的**每一个正常 `ret` 之前** |
| 次数 | 每进程**至多一次**（调度器状态机保证；重复钩子调用直接返回） |
| 含义 | “`GameRunner.Initialize` 正常返回”。**不**表示内容资源全部加载完成，**不**等于标题界面已显示 |
| 异常退出 | 方法抛出异常时没有任何 `ret` 被执行，因此不会派发；`catch` 处理器内的 `ret` 被视为错误路径，不插入钩子 |
| 顺序 | 严格早于第一次 `Tick`（`EventDiagnostics.GameLaunchedBeforeFirstTick` 记录该事实） |

### 1.2 `Tick`

| 项 | 契约 |
| --- | --- |
| 触发点 | `StardewValley.GameRunner.Update(Microsoft.Xna.Framework.GameTime)` 的**第一条指令之前** |
| 频率 | 每次方法**进入**一次。**不**承诺等于渲染帧数、固定 60 Hz 或真实时间 |
| 计数 | `TickCount` 从 **1** 开始，每次 +1，`long` 单调递增；溢出策略为**饱和**到 `long.MaxValue`（不回绕） |
| 参数 | 只有 `TickCount` 与 `ThreadId`，没有 `GameTime` |

---

## 2. 线程

**【决定】** 框架不做线程切换，处理器在“进入被注入游戏方法的那个线程”上运行。
**【观测】** 真机记录见 [真机验收报告](../audit/phase2.0-device-acceptance-report.md)：
Loader 初始化、`GameLaunched`、`Tick` 的实际 `ManagedThreadId` 已写入探针报告，并与 Android UI 线程区分。

调度器的互斥只保护极短的记账区段（订阅表、计数、快照引用），**不会**在处理器运行期间持锁，
因此处理器内部可以安全地注册/退订。处理器**不得阻塞**。

---

## 3. 顺序

**【决定】** 派发顺序 = **Ordinal mod id** 升序，同一 Mod 内按**注册顺序**。

- 使用 `string.CompareOrdinal`，不使用当前文化，也不依赖 `Dictionary` 枚举或目录枚举顺序。
- 快照使用**稳定插入排序**（订阅数量很小）：稳定排序保证同一 Mod 的多个订阅保持注册顺序。
- 跨 Mod 验证：`EventMod.A`/`EventMod.Z`、`Zeta`/`alpha`（Ordinal 与常见文化排序相反）以及真机上的
  `P20.Acceptance.Alpha` → `P20.Acceptance.Beta`（Alpha 依赖 Beta，因此 Beta **先**初始化，顺序仍为 Alpha 在前）。

---

## 4. 快照与订阅生命周期

| 规则 | 实现 |
| --- | --- |
| 本轮使用快照 | 派发开始时取一次订阅数组；回调中注册/退订**下一轮**生效 |
| 不每 Tick 排序 | 只有订阅集合变化时才重建快照并排序；稳定期一次派发零分配、零排序 |
| 重复注册 | 每次调用创建**独立订阅**，都触发；Dispose 只取消自己 |
| Dispose 幂等 | 令牌内部有 `Disposed` 标志，重复 Dispose 是 no-op |
| 初始化失败回滚 | 订阅先进入 per-mod staging 区；`Initialize` 抛异常则整体丢弃，失败 Mod 永远收不到事件 |
| 初始化成功才激活 | `Initialize` 正常返回后才并入 active 集合 |
| 会话隔离 | Loader 每次运行一个 session；运行失败/崩溃会移除本 session 的全部订阅（无幽灵订阅） |
| 重复启动 | 同一 Mods 根第二次 `Loader.Run` 直接返回 `already-initialized`，不会重复初始化或重复派发 |

---

## 5. 异常隔离与有界日志

- 每个 handler 单独 `try/catch`，其余 handler 继续执行；异常不会穿透到游戏循环。
- 故障计数：`EventDiagnostics.HandlerFaults`（全局）+ 每个订阅自己的计数。
- 日志**有界**：第 1、2 次故障写完整诊断，之后只在第 10/100/1000/... 次写一行；
  连续异常不会每 Tick 写一条日志。
- 日志写入本身失败 → `LogFaults++`，不影响游戏。
- **桥接层自身故障**（例如调度器内部异常）：`BridgeFaults++`，最多向 `stderr`（logcat）写一行，绝不抛出。

---

## 6. 版本与兼容性决定

| 维度 | 值 | 理由 |
| --- | --- | --- |
| `AssemblyVersion` | **1.0.0.0（不变）** | 这是所有既有 Mod 编译时绑定的程序集身份。旧 Mod 的 `AssemblyRef` 指向 `ModFramework.Runtime, Version=1.0.0.0`；保持该身份意味着旧二进制继续绑定，预检（host share/identity）也继续把“随包携带 Runtime 副本”识别为同一身份。 |
| Framework **API** 版本 | **1.1.0** | `IModContext` 增加了一个只读成员、新增两个事件。manifest 的 `frameworkVersion` 门禁使用该值。`^1.0.0` 仍然匹配；需要事件的 Mod 声明 `>=1.1.0`。 |
| `Version` / `FileVersion` | 1.1.0 | 元数据可读性，不参与绑定。 |

**为什么不“整体升级版本”**：把 `AssemblyVersion` 提到 2.0.0.0 会改变旧 Mod 绑定的身份与预检比较基准，
却不会带来任何兼容收益；而把 API 版本留在 1.0.0 又无法让新 Mod 表达“我需要事件 API”。

**二进制兼容证据**：`Tools/tests/FixtureMods/LegacyMod` 是**按冻结的 1.0.0 契约桩**
（`Tools/tests/LegacyContract`，程序集名 `ModFramework.Runtime`、版本 1.0.0.0、无 `Events`）编译的，
再由**新** Runtime 加载并初始化。不是“把旧 fixture 重新引用新接口编译”。

**不做**：热重载、卸载程序集、跨进程订阅、完整 SMAPI 兼容、事件优先级/取消、内容管线、存档 API。

---

## 7. 构建期注入

| 目标 | 位置 | 注入规则 |
| --- | --- | --- |
| `MainActivity.OnCreatePartTwo` | 方法头 | `call ModFramework.Bootstrap.Start()`（Phase 1 既有，不变） |
| `GameRunner.Initialize()` | 每个正常 `ret` 之前 | `call ModFramework.Bootstrap.GameLaunched()` |
| `GameRunner.Update(GameTime)` | 第一条指令之前 | `call ModFramework.Bootstrap.Tick()` |

### 7.1 目标识别（fail-closed）

- 按**类型全名 + 方法名 + 返回值 + 完整参数列表**精确匹配，不用“第一个同名重载”。
- 多个方法同时匹配 → 构建失败；找不到、无方法体、空方法体 → 构建失败。
- 版本/类型/签名/IL 形状不匹配即拒绝构建，不在设备上才失败。

### 7.2 控制流处理（不是“普通 InsertBefore”）

- **分支直跳 `ret`**：所有以该 `ret` 为目标的 `br`/`brtrue`/`brfalse`/`switch`/`leave` 边被改指向新插入的钩子调用。
- **异常边界**：`TryEnd`/`HandlerEnd` 若正好是该 `ret`，一并改指钩子调用，使新代码落在保护区**之外**。
- **`catch`/`finally` 内的 `ret`**：视为错误路径，跳过；若一个方法只有这种 `ret` → 构建失败。
- **`try` 内的 `ret`**：非法形状，直接拒绝。
- **短分支**：插入后统一把该方法内的短分支展开为长形式（`br.s`→`br`、`leave.s`→`leave` 等），
  避免位移超出有符号字节范围后在设备上变成 `InvalidProgramException`。

以上规则由 `Tools/tests/InjectorTests` 用**合成 Cecil 夹具**（多返回、分支直跳 ret、`catch` 内 ret、
handler 边界、错误签名、歧义重载、部分补丁、短分支）逐一执行验证，不只依赖一份真实 DLL 的成功结果。

### 7.3 幂等与混合状态

- 对已注入产物再次 patch：`Start`/`GameLaunched`/`Tick` 各仍为 1 次调用，verify 通过。
- 对“loader-only”产物启用事件，或对“事件产物”禁用事件 → **明确拒绝**（`refusing to produce a mixed state`），
  不静默产出混合状态。
- 部分补丁（某个 `ret` 没有钩子调用）→ 拒绝补全。

### 7.4 桥接层

`ModFramework.Bootstrap` 不新增任何指向 `ModFramework.Runtime` 的静态 `AssemblyRef`：

1. `Start()` 用反射 `Assembly.LoadFrom` 加载 Runtime（Phase 1 既有）；
2. 之后按名称解析 `ModFramework.Runtime.EventDispatcher`，取 `DispatchGameLaunched` / `DispatchTick`；
3. 用 `Delegate.CreateDelegate(typeof(Action), MethodInfo)` 转成 `System.Action` 并缓存到两个静态字段；
4. `GameLaunched()` / `Tick()` 只做「字段空判断 + 一次缓存委托调用」，**不反射、不读文件**，
   并各自包在 `try/catch` 内，保证桥接故障不会打断游戏循环。

`Delegate.CreateDelegate(Type, MethodInfo)` 与 `Type.GetTypeFromHandle`、`Environment.CurrentManagedThreadId`
均被**游戏自带程序集**引用（Java.Interop / StardewValley 等），因此在本 ILLink 裁剪后的 BCL 中存在（【观测】静态扫描 65 个程序集）。

Runtime 缺失、旧 Runtime 没有 `EventDispatcher`、绑定失败时：`Start()` 的既有 catch 吞掉异常，或
绑定步骤走空检查分支，事件安全禁用，并向 `Mods/bootstrap-events.txt` 追加**一行**有界诊断
（路径不可写则静默跳过）；游戏照常启动。

---

## 8. 可观察性

`ModFramework.Runtime.EventDiagnostics.Capture()` 返回只读快照：

| 字段 | 含义 |
| --- | --- |
| `Ready` / `SessionId` | 事件会话是否已提交、会话号 |
| `GameLaunchedCount` / `TickCount` | 0/1 与累计 Tick |
| `SubscriptionCount` / `ModCount` | 活跃订阅数 / 拥有订阅的 Mod 数 |
| `HandlerFaults` / `LogFaults` / `BridgeFaults` | 三类故障计数 |
| `FirstGameLaunchedThreadId` / `FirstTickThreadId` / `LoaderThreadId` | 线程归属 |
| `GameLaunchedBeforeFirstTick` | 顺序事实 |
| `FirstGameLaunchedOrder` / `FirstTickOrder` | 首次派发的 mod id 顺序（每个 Mod 一条） |

Loader 报告只写“初始化时刻”的事实（`events=ready`、`event-subscriptions=N`、`event-mods=N`、
`event-loader-thread-id=`），**不**假装知道之后的事件计数；事件计数由探针在运行期按低频读取并写盘。

---

## 9. 构建开关与产物

| 产物 | 任务 | 工作目录 | 事件钩子 |
| --- | --- | --- | --- |
| A 原始基线 | `:app:assembleDebug` | `.tmp/build` | 无（原始 blob） |
| B Loader-only 回退 | `:app:assembleLoaderOnlyDebug` | `.tmp/build-loader-only` | **无**（`--event-hooks disabled`） |
| C Loader + events | `:app:assembleModdedDebug` | `.tmp/build-modded` | 有（`--event-hooks enabled`） |

开关贯通：Gradle 任务 → `Tools/build_modded_apk.py --event-hooks` → `ModInjector patch --no-event-hooks`
→ 构建 manifest（`event_hooks` 字段）→ `Tools/verify_modded_apk.py --expect-events`。
A/B/C 使用独立输出与工作目录；事件的“应有/应无”都从**最终 APK 解出的程序集**重新校验，不只相信构建 manifest。

---

## 10. 已知限制

- 只覆盖 `StardewValley` 1.6.15.24354（versionCode 239）与 V1963A / Android 10 / API 29 这一台设备。
- 事件只覆盖 `GameLaunched` 与 `Tick`；没有 `DayStarted` / `SaveLoaded` / 内容管线。
- 注入的 `GameRunner.Update` 每 Tick 增加一次静态字段读取 + 一次委托调用；没有量化低端机帧率影响。
- Android 11+ 的 Mods `apply` 继续 fail-closed。
- 事件线程由游戏循环决定；本阶段只在真机**记录**，不做保证。
