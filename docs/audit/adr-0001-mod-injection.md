# ADR-0001：模组注入点与运行时路线

- 状态：**Proposed**（Phase 0 结论 + Phase 0.5 复核；转 Accepted 的门槛见 §6，尚未满足）
- 日期：2026-11-04（Phase 0.5 复核：2026-11-05）
- 关联：`docs/audit/runtime-feasibility.md`、`docs/audit/decompile-report.md`、`docs/audit/phase0.5-report.md`
- 决策者：Phase 0 审计

---

## 1. 背景

被测 APK 是 .NET for Android（MonoVM 8.0.1124，AOT 模式 `normal`）打包的星露谷物语 1.6.15.0。工程通过 apktool 重打包 + 自签名，**不使用 smali、不引入 AGP**（`app/build.gradle.kts`，AGENTS.md §4）。

需要在不破坏"无模组启动"的前提下，让用户在 `Mods/` 放入 C# DLL 即可生效。因此必须解决两个问题：

1. **入口**：托管代码在什么位置、以什么方式被我们的 Loader 接管；
2. **执行**：外部 DLL 的 IL 能不能在这个运行时里跑。

---

## 2. 关键事实（来自 Phase 0 审计）

| 事实 | 证据 |
| --- | --- |
| 运行时是 MonoVM，JIT 后端存在 | `libmonosgen-2.0.so` 符号 `mono_jit_init_version`、`mono_compile_method` |
| AOT 模式 `normal`（AOT + JIT 混合，非 full） | `libxamarin-app.so` 重定位解析出 `mono_aot_mode_name = "normal"` |
| 65 个程序集全部有 AOT 镜像（含桩） | `jniLibs/arm64-v8a/libaot-*.so` × 65 |
| 程序集从两个 `XABA` 存储按需加载 | `assemblies.blob`(58) + `assemblies.arm64_v8a.blob`(7) |
| Java `MainActivity` 只是托管类型的 peer | dex `__md_methods` → `GetOnCreate_Landroid_os_Bundle_Handler` |
| 游戏真正启动在 `MainActivity.OnCreatePartTwo` | IL：`newobj InstanceGame::.ctor` → `SetContentView` → `Run()` |
| 修改程序集会使对应 AOT 镜像被判定过期 | `libmonosgen` 字符串 `AOT: module %s is unusable (GUID … doesn't match)`、`Image out of date` |
| 应用非 debuggable，`run-as` 不可用 | `adb shell run-as` → `package not debuggable` |
| 存档位于应用专属外部目录 | `/sdcard/Android/data/com.chucklefish.stardewvalley/files/Saves` |

---

## 3. 决策

### D1. 采用 Route A（动态 IL）为主路线，Harmony 为可选增强（Route D 形态）

- Loader 通过 **`Assembly.LoadFrom(string)`** 加载用户 DLL，依赖 MonoVM 的 JIT 执行。
  - **已完成决策 (2026-11-04 装机实测，Phase 0.5 复核)**：`Assembly.LoadFrom(string)` 是唯一可用入口；
    同一路径下 `Assembly.Load(byte[])` 在本 APK 的 ILLink 裁剪后**不存在**（编译期 CS1503，
    设备上 `MissingMethodException`，见 `runtime-feasibility.md` §6.5、`phase0-status.md` §3.15）。
  - **被否决方案**：`Assembly.Load(byte[])` 只保留为历史记录与复现开关
    （`ModInjector --loader-api load`），不得再作为实现方案。
  - 架构后果：模组 DLL 必须是**磁盘上可读的文件**（`Mods/`），因此 `Bootstrap.Start()` 先 `File.Exists` 再 `LoadFrom`；
    "从内存字节加载"在这个运行时不可用。
- Harmony **默认关闭**；开启时按 H1（选择性 JIT detour）→ H2（构建期去 AOT）递进，任一步失败必须能整体回退，且不得影响 Events/Content/Data/Config/Reflection API。
  - Phase 0 实测已把 H1 判死（缺 `Reflection.Emit`），详见 §6.1 与 `runtime-feasibility.md` §7.2。

### D2. 注入点在托管侧：`StardewValley.MainActivity.OnCreatePartTwo` 首行

具体做法（构建期，由本工程完成）：

```
；原始
.method … OnCreatePartTwo() {
    call MobileDisplay::SetupDisplaySettings()
    ...
    newobj InstanceGame::.ctor
    ...
    callvirt …Run()
}

；注入后
    call ModFramework.Bootstrap::Start()      ; ← 新增，1 条指令 + 1 个引用
    call MobileDisplay::SetupDisplaySettings()
    ...
```

备选注入点（按优先级）：
1. `OnCreatePartTwo` 首行（**选定**：离游戏对象创建最近，UI 线程上下文正确）；
2. `MainActivity.OnCreate` 末尾（在 `CheckAppPermissions()` 之后）；
3. `InstanceGame::.ctor` 首行（过深，随版本变化风险大）。

### D3. `ModFramework.Bootstrap` 以 **IL 注入形式合并进 `StardewValley.dll`**（修订于 2026-11-04）

原计划是"新增一个程序集条目到 `assemblies.blob`"。解析 blob 后发现**不可取**：

`assemblies.blob` 的布局是 `header(20) + 65×24 条目表 + 65×40 hash 映射表 + 负载`。
那张 2600 字节的映射表（实测 `2600 = 65 × 40`，每条含两组 `{hash32, hash64_lo, store, index}`）
是 `name hash → (store, entry)` 的查找表，与 `libxamarin-app.so` 里的 `assembly_image_cache` 同源。
**新增一个程序集要同时改这张表和 `.so` 内的缓存**，风险远大于收益。

因此改为：把 Bootstrap 作为一个新类型**写进 `StardewValley.dll` 自身**（Cecil），
容器字节只做"同条目替换"，条目数与 hash 表都不变，查找逻辑完全不受影响。

已实现并离线验证：`Tools/ModInjector`。

- 新增 `ModFramework.Bootstrap`（自包含，仅用 BCL）；
- 在 `MainActivity.OnCreatePartTwo` **首条指令前**插入 `call Bootstrap.Start()`；
- 强制更新 MVID（否则 AOT 镜像/缓存会认为程序集未变）；
- `Start()` 的逻辑（与 `Tools/ModInjector/Program.cs` 的 `EmitBootstrap` 一一对应）：
  `Mods/ModFramework.Runtime.dll` 存在 → `Assembly.LoadFrom(path)` → `Assembly.GetType("ModFramework.Runtime.Loader")`
  → `GetMethod("Start")` → `Invoke(null, null)`。每一步失败都抛**带阶段号**的异常
  （`stage-1:file-missing` / `stage-2:assembly-null` / `stage-3:type-null` / `stage-4:method-null`）；
  stage-5 在 `Loader.Start()` 返回**非 null** 时把该字符串当异常抛出。
  整段包在 `catch (Exception) { }` 里，**任何失败都不影响游戏启动**。
- 该 catch 同时意味着：正式构建里阶段异常**不会进 logcat**。诊断需要
  `--no-catch`（catch 里 `rethrow`，会以 `JavaProxyThrowable` 出现），见 §6.1 与 `phase0.5-report.md` §6。
- 默认 `--loader-api loadfrom`（2026-11-05 修正：此前默认是已不可用的 `load(byte[])`，
  会让默认构建在 `Start()` 的 JIT 阶段失败并被 catch 吞掉，表现为"模组装不上但游戏正常"）。

代价：`StardewValley.dll` 被改动 → 其 AOT 镜像失效 → 回退 JIT（§5.2 已列）。

#### D3 追加修订（2026-11-04，装机实测后）

上述"合并进 StardewValley.dll"在**装机验证中被运行时拒绝**：

```
F monodroid-assembly: Compressed assembly '<assembly_store>' is larger than when the application was built
                      (expected at most 13824, got 9261056)
```

`libmonodroid` 会拿构建期记录的程序集大小与运行期实际大小比对。因此"改字节、不改元数据"不成立。

两条候选出路（未验证，下一步做）：

| 编号 | 做法 | 说明 |
| --- | --- | --- |
| **S1** | 同步改 `libxamarin-app.so` 里 `BundledAssembly.size` | 把 `StardewValley` 的期望大小改成新值（9,261,056）。改动面仅一个 u32，可回滚 |
| **S4** | 改为**新增**条目（已有条目大小不变） | 触发不到"已有条目变大"的校验；但要同步 gap hash 表 |

S1 优先：它修正的正是被校验的那个字段，且若不生效可以完全还原 .so。
若 S1 与 S4 都失败，则说明该运行时**只能在构建期决定程序集集合**，
届时架构必须退回"每个游戏版本重新编译带 Mod 支持的 APK"（Route C 形态），并把这一点写进面向用户的限制说明。

### D4. 模组目录位于应用专属外部目录

```
/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods/<UniqueID>/
```

理由：与 `Saves` 同级，游戏进程是属主、读写无需额外权限；且天然随应用卸载清理（与存档一致，符合"卸载只删除属于该模组的文件"的可实现性）。

### D5. 独立管理器不与游戏共享私有目录

由于 `run-as` 不可用 + Android 11+ 对 `Android/data` 的 SAF 限制，独立管理器采用：

- **主通道**：公共目录 `/sdcard/Documents/StardewMods/`（SAF 授权），游戏端在启动时把它同步/导入到 `files/Mods/`；
- **辅通道**：游戏端导出/导入（`ACTION_SEND` + `FileProvider`），用于单模组 ZIP。

两条通道**共用同一套 manifest/状态数据模型**（同一份 `mods.state.json`），避免两个管理器状态不一致。

### D6. 不改 dex、不改 `classes*.dex`、不引入 smali

与既有构建约定一致（AGENTS.md §2/§4）。所有注入都在 IL 层完成。

---

## 4. 被否决的方案

| 方案 | 否决理由 |
| --- | --- |
| 新增 Java `ContentProvider` / `Application` 子类来启动 Loader | 需要新 dex（d8 工具链 + 新类），且托管运行时在 provider 阶段尚不可从 Java 侧调用；破坏"不使用 smali/不动 dex"的约束 |
| 只把 Bootstrap 塞进程序集存储、不改任何 IL | 运行时不会主动加载它；Mono.Android 只在有托管 peer/被引用时才加载程序集 |
| 用解释器跑模组（Route B） | 解释器代码存在但无启用通道（应用非 debuggable，无 `wrap.sh`），且性能不可接受；保留为备选 |
| 每个模组单独 AOT + 独立原生库（Route C） | 需要完整 .NET Android 构建链（本机无 SDK），且"每装一个模组重编译游戏"直接违反需求 8 |
| 直接改写 AOT 原生代码 | 任务书 §7 明令禁止；且无版本鲁棒性 |
| 修改 dex 的 `MainActivity.onCreate` 调用 JNI 静态方法 | 需要托管方法注册（`__md_methods`）配合，复杂度高于 IL 注入，收益为零 |

---

## 5. 后果

### 5.1 正面

- 模组是**真正的 C# DLL**，用户可用 Visual Studio/Rider 开发，符合需求 1/2/8。
- 框架只需 1 个程序集进 APK；游戏版本更新时重打包成本低。
- 无模组时 `Start()` 立即返回 → 无模组启动路径与原版等价。

### 5.2 负面 / 风险

| 风险 | 影响 | 缓解 |
| --- | --- | --- |
| 修改 `StardewValley.dll` → 其 AOT 镜像失效 → 回退 JIT | 冷启动变慢、内存上升 | 实验 E4 量化；若不可接受，改为"只 patch 一个极小的中间方法"或改用 H2 整体去 AOT |
| 运行时可能校验程序集 hash（未证实） | 注入后启动失败 | 实验 E1 首要验证项；必要时同步更新 manifest/缓存 |
| Harmony 在 AOT/Android 上不可靠 | 方法补丁能力受限 | D1 的三档降级；Harmony 永远是可选 |
| 独立管理器无法直接读写游戏目录 | 体验割裂 | D5 双通道 + 共享状态文件 |
| 每次游戏更新都要重新定位注入点 | 维护成本 | 注入点选择"稳定且靠前"的方法；用 Game Compatibility Profile 校验类型/方法签名 |
| 重签名导致覆盖安装失败 | 用户需卸载（丢存档） | 文档写明；提供存档备份工具 |

### 5.3 明确不做的事

- 不宣称能隔离恶意模组（进程内 C# 代码拥有完整权限）。
- 不宣称模拟器结果等于真机结果。
- 不在未经真机验证前把 Harmony 列为"已支持"。

---

## 6. 验证计划（转为 Accepted 的条件）

1. **E1** 动态加载 + IL 执行通过（真机，ARM64）；
2. **E2** 反射访问 `Game1` 通过；
3. **E3** 事件钩子按序触发；
4. **E4** 去 AOT 回退可启动，且记录到可接受的性能数字；
5. **E5** Harmony 对 JIT 方法成功、对 AOT 方法失败且有明确诊断；
6. **E6** 至少一条管理器存储通道在 Android 12/14 真机可用。

任一条失败 → 回到本 ADR 重新决策（例如退回 H0-only 的"无补丁框架"）。

### 6.1 实际进展（2026-11-04）

| 实验 | 状态 | 证据 |
| --- | --- | --- |
| E1 加载外部 DLL 并执行 IL | **RECORDED PASS (Evidence D)**（模拟器） | 记录为：`ModFramework.Runtime.dll` 经 `Assembly.LoadFrom` 加载，`Loader.Start()` 执行并落盘 `loader.log`；探针 T01–T06 记录为 PASS。原始日志已丢失，本阶段未重跑 |
| E2 反射访问游戏类型 | **RECORDED PASS (Evidence D)**（模拟器） | 探针 T07 记录：`Game1` 576 方法 / 532 字段（Historical Record，未独立复核） |
| E5 Harmony | **不可行（当前工具链）** | ① `Reflection.Emit` 与 `System.Reflection.Emit` 均缺失 → Harmony/MonoMod 托管 detour 无法实现（编译期 A 级）；② 原生 detour 的内存前提记录为成立（`MethodHandle.Value` 取址、`mprotect` RWX 均成功，D 级），但 AArch64 需要用户态刷指令缓存，而三个运行时/系统库**都没有导出相关符号**，本机也**没有 NDK** 可自建；直接写跳转以原生崩溃告终（D 级记录） |

补充约束（实测）：

- 加载 API 只有 `Assembly.LoadFrom(string)` 可用；`Assembly.Load(byte[])` 被 ILLink 裁剪（**编译期 A 级**：CS1503）；
- 模组 DLL 必须针对游戏自带的程序集编译，否则 facade 类型解析失败（`Architecture`/`AppContext`/`RuntimeFeature` 均缺，**编译期 A 级**：CS0117/CS0234）；
- BCL 面被选择性裁剪（`Directory.GetFiles` 没了、`File.WriteAllText` 还在，**编译期 A 级**），SDK 需要 API 白名单；
- 一个方法里只要有一个被裁剪的引用，**整个方法的 JIT 就会失败**，方法内 try/catch 拦不住（D 级推断）。

→ E1/E2 尚未在 **ARM64 真机**复现，Phase 0 结束时 Phase 1 未启动。

### 6.2 Phase 0.5 复核结论（2026-11-05）

| 门槛 | 状态 | 说明 |
| --- | --- | --- |
| E1 动态加载 + IL 执行 | **RECORDED PASS (D)**（模拟器）/ 真机 **BLOCKED** | 目前只有 MuMu 12（x86_64 + `libnb.so` 翻译）；无物理 ARM64 设备（见 `phase0.5-report.md` §7） |
| E2 反射访问 `Game1` | **RECORDED PASS (D)**（模拟器）/ 真机 **BLOCKED** | 同上；统计数字（576 方法 / 532 字段）是模拟器反射结果，不可外推 |
| E3 事件钩子 | **RECORDED PASS (D)**（模拟器，间接） | 仅有"注入点执行 + 事件用例"的证据，未做 tick/day 驱动 |
| E4 去 AOT 回退 | NOT RUN | 已被 E5 结论取代（JIT 本就在，缺的是 `Reflection.Emit`） |
| E5 Harmony | FAIL（当前工具链） | 见 §6.1；缺 `Reflection.Emit` + 缺 AArch64 指令缓存刷新手段 |
| E6 独立管理器存储通道 | NOT RUN | 需第二台设备或 Android 14 |

**因此 ADR 维持 Proposed**：§6 第 1、2 条要求"真机"，当前证据只到模拟器，且模拟器证据本身是 D 级。
不得因为模拟器记录而改 Accepted。

---

## 7. 外部参考、先例与许可证

### 7.1 参考对象（只取设计，不取代码）

| 项目 | 与本项目的关系 |
| --- | --- |
| SMAPI 4.5.2（LGPL-3.0） | 事件/内容/数据/配置/反射的 API 形态参考；其"自带 `Program.Main` + 覆写 `GameRunner`/`SGame` + 替换 `ModHooks`"的桌面引导模型**不适用于 Android** |
| Harmony 2.x（MIT）/ HarmonyX（MIT） | 方法补丁的可选实现 |
| MonoMod / MonoMod.RuntimeDetour（MIT） | detour 底层 |
| Mono.Cecil（MIT） | 构建期 IL 注入（D2）的实现工具 |
| `NRTnarathip/SMAPI-Android-1.6`（参考实现，许可证需单独确认） | 证明了"Android 进程内跑 SMAPI + Harmony"这条路线存在；本项目**不复用其代码** |

### 7.2 许可证义务

| 依赖 | 许可证 | 义务 |
| --- | --- | --- |
| SMAPI | **LGPL-3.0** | **不得**把 SMAPI 代码复制进本项目并伪装成原创；若链接/修改其代码，须提供对应源码并允许重新链接。本项目选择"不复制、只参考 API 命名与行为语义"（API 形状本身不受版权保护，但实现受保护） |
| Harmony / HarmonyX / MonoMod / Mono.Cecil | **MIT** | 保留版权声明与许可证文本 |
| 游戏程序集与资源 | 原厂版权 | 本项目只做"用户自有副本的本地修改"，不再分发游戏本体；`README` 与安装文档必须写明 |

### 7.3 已确认的工程结论

1. 不引入完整 SMAPI 源码树（LGPL 传染性与体积都不划算）；SDK 的 API 形状自行实现。
2. 若后续需要 Harmony，以 **NuGet 引用 MIT 包**的方式引入 `0Harmony.dll`，并随包附带 LICENSE。
3. 文档中凡引用外部结论（如 profiled AOT 默认值、先例可用性）必须标注"外部参考，未独立验证"。

---

## 8. 变更记录

| 日期 | 阶段 | 改了什么 | 为什么 / 证据 |
| --- | --- | --- | --- |
| 2026-11-04 | Phase 0 | 初版：D1 选 `Assembly.Load(byte[])`；D3 计划新增程序集条目 | 当时只有静态审计 |
| 2026-11-04 | Phase 0 | D3 追加修订：改为"Bootstrap 合并进 `StardewValley.dll`" | 装机实测被 `libmonodroid` 的大小校验拒绝（§D3 追加修订） |
| 2026-11-05 | Phase 0.5 | D1 改为 `Assembly.LoadFrom(string)`，`Load(byte[])` 降级为历史/否决方案 | 源码（`Loader.cs`、`ModInjector` 注入的 IL）与设备异常都只走 `LoadFrom`；`Load(byte[])` 在裁剪后的运行时不存在 |
| 2026-11-05 | Phase 0.5 | D3 的 `Start()` 描述改为与注入 IL 一致的 5 阶段 + 吞异常语义；补充"默认 `--loader-api loadfrom`" | `Tools/ModInjector/Program.cs` 默认值曾是 `load(byte[])`（缺陷，已修）；见 `phase0.5-report.md` §5 |
| 2026-11-05 | Phase 0.5 | 新增 §6.2 复核表：E1/E2 只在模拟器通过，ADR 维持 Proposed | 本机只有 x86_64 模拟器；真机 E1/E2 = BLOCKED |
| 2026-11-07 | Phase 1.1.7 | D3 的"构建期 IL 注入"从工具能力变成**正式构建入口**：新增 `assembleModdedDebug`（独立产物/work dir/keystore 策略），baseline `assembleDebug` 保持不注入；D4 的目录约定与 `Loader` fallback 对齐（外部 Mods 根，不再回退内部 `files/Mods`） | `docs/audit/phase1.1.7-report.md`；ADR 仍为 **Proposed**——本阶段只完成构建与静态验收，没有在设备上跑过注入后的 APK |
