# MonoRuntimeProbe

Phase 0 的运行时探针：在**真实游戏进程**里验证 .NET for Android / MonoVM 的动态程序集加载、反射与代码生成能力。

> **它不属于常规 Mod Loader 流程。** 正常 `Loader.Start()/Run()` 不会加载、不会调用、也不会因为
> Mods 根目录里存在 `MonoRuntimeProbe.dll` 而做任何事（Phase 1.1 移除了那条隐式路径，
> 报告里固定写 `probe=not-run-by-loader`）。调用方式见 §4，必须显式。

> 状态（2026-11-06 Phase 1.1.4 复核）：
> - **安全入口 `Probe.RunSafe` 完成**，桌面基线通过；默认编译**不含**任何 P/Invoke / mprotect /
>   RWX / detour / unsafe 代码；
> - **T11 / T12 已从默认编译中移除**，只在 `-p:LegacyNativeExperiments=true` 时才存在，
>   而那个配置在 Phase 1.1.4 中**不构建、不运行**（见 §2.2）；
> - **Android 侧只在 MuMu 12 模拟器（x86_64 + `libnb.so` 翻译）跑过**：T00–T07、T11 记录为 PASS，
>   T12 原生崩溃。这些结果全部是 **RECORDED (Evidence D)**：原始日志已随 `.tmp/phase0/` 丢失，
>   不可独立复现；
> - **ARM64 真机未验证**（`BLOCKED: no physical device`，见 `docs/audit/phase0.5-device-checklist.md`）。

## 1. 结构

```
Mod/
├── MonoRuntimeProbe/                探针本体（net8.0，仅依赖 BCL）
│   ├── MonoRuntimeProbe.csproj      默认 AllowUnsafeBlocks=false；历史实验需显式开启
│   ├── ProbeEntry.cs                安全入口 Probe.RunSafe + T00–T07
│   ├── NativePageProbe.cs           T11（#if LEGACY_NATIVE_EXPERIMENTS，默认不编译）
│   └── DetourProbe.cs               T12（#if LEGACY_NATIVE_EXPERIMENTS，默认不编译）
├── MonoRuntimeProbe.Dependency/     "依赖外部程序集"用例
│   └── DependencyMarker.cs
└── MonoRuntimeProbe.Host/           桌面基线宿主（开发机自测用）
    └── Program.cs
```

设计约束：

1. 目标框架 `net8.0`，与游戏运行时 `.NETCoreApp,Version=v8.0` 一致；
2. **不引用** `StardewValley.dll` / `Mono.Android`：对游戏类型的访问全部走反射（按名字解析），
   这样探针不依赖参考程序集，也能在游戏程序集尚未加载时运行；
3. 只依赖 BCL，保证在任何 MonoVM 上都能编译；
4. **每个可疑 BCL 成员单独成方法**：ILLink 裁剪的失败是方法级的，方法内 `try/catch` 拦不住；
5. **Phase 1.1.4 起：默认路径不碰原生内存**。没有 `DllImport`、没有 `mprotect`、没有 RWX、
   没有机器码写入、没有 `Reflection.Emit`/`DynamicMethod`/Harmony、没有 `unsafe`；
6. **报告不写绝对路径**：`basedir` 只写末段目录名。

## 2. 用例（与当前源码一一对应）

| ID | 实现 | 验证项 | 期望 / 实测（模拟器记录，Evidence D） |
| --- | --- | --- | --- |
| T00 | `Probe.DynCode` | `RuntimeFeature.IsDynamicCodeSupported/Compiled` | 记录 `supported=True compiled=True` |
| T01 | `Probe.StaticGreeting` | 普通静态方法 | RECORDED PASS (D) |
| T02 | `ProbeSubject.InstanceGreeting` | 实例方法 | RECORDED PASS (D) |
| T03 | `Probe.GenericEcho` / `GenericPair` | 泛型方法（含嵌套实例化） | RECORDED PASS (D) |
| T04 | `Probe.EventRoundtrip` | 事件订阅/触发/退订 | RECORDED PASS (D)（hits=1） |
| T05 | `Probe.ReflectionRoundtrip` | 反射 字段 get/set + 方法 Invoke | RECORDED PASS (D) |
| T06 | `DependencyMarker.Describe` | 编译期引用另一个程序集 + 运行时加载 | RECORDED PASS (D) |
| T07 | `Probe.GameTypeSurface` | 反射**只读枚举** `StardewValley.Game1` 成员数量 | `Game1 576 methods/532 fields`（不可复核） |

### 2.1 已移除的用例（历史记录，不在当前源码里）

| ID | 曾验证 | 结论 | 移除原因 |
| --- | --- | --- | --- |
| T08 | `Assembly.Load(byte[])` | ❌ `MissingMethodException` | 该方法在本运行时被裁剪；保留它只会让整个 `Run` 失败，改成在 `Loader`/Bootstrap 里验证 `LoadFrom` |
| T09 | `DynamicMethod` | ❌ `TypeLoadException`（缺 `System.Reflection.Module`） | 同上；结论已由编译期 `CS7036` 与设备日志共同确认 |
| T10 | `AssemblyBuilder` | ❌ `FileNotFoundException: System.Reflection.Emit` | 同上 |

> 这些失败**是重要结论**（Harmony 路线不可行的依据），只是不再作为探针的运行时用例。
> 历史输出见 `docs/audit/phase0-status.md` §3.22；原始证据文件已不在本机。
> 旧版 README 曾把 T11 写作 "Task/线程"、把 T08–T10 写作"期望 pass"，均与当前代码不符，已更正。

### 2.2 T11 / T12：历史危险实验，Phase 1.1.4 起默认不编译

| ID | 曾验证 | 结论 | 现在的状态 |
| --- | --- | --- | --- |
| T11 | 取方法地址 + `mprotect` 把代码页改 RWX 再还原（**不写字节**） | RECORDED PASS (D)（rwx_rc=0 restore_rc=0） | `NativePageProbe.cs`，`#if LEGACY_NATIVE_EXPERIMENTS` 包裹，**默认不编译** |
| T12 | 真写 16 字节 ARM64 跳转桩 | **RECORDED FAIL (D)**：写后进程原生崩溃（缺指令缓存刷新） | `DetourProbe.cs`，同上，**默认不编译** |

为什么必须隔离：`Probe.Run(...)` 以前无条件调用 T11 和 T12，而 T12 在 MuMu 12 上让进程**原生崩溃**
（报告来不及落盘）。把"能崩溃的实验"和"只读诊断"放在同一个聚合入口里，等于让任何一次常规诊断
都带上崩溃风险。

现在：

- `Probe.RunSafe(string outputDirectory)` 是**唯一**常规入口，只做内存内验证 + 只读元数据枚举；
- `Probe.Run(string, bool)` 保留旧签名，但**只委托到 `RunSafe`**，第二个参数被忽略
  （不再存在"允许调用 Game1"的分支）；
- 想编译历史实验必须显式：

  ```powershell
  dotnet build Mod/MonoRuntimeProbe -c Release -p:LegacyNativeExperiments=true
  ```

  这个配置产出 `MonoRuntimeProbe.legacy.dll`，**Phase 1.1.4 的任何测试、脚本、Loader 路径或
  manifest mod 都不构建、不运行它**。它是留给源码审计的描述，不是可用实验入口。

## 3. 构建

```powershell
# 方式 A：桌面基线（只证明探针自身正确，不代表 Android）
dotnet run --project Mod/MonoRuntimeProbe.Host -- <输出目录>
# 产物：<输出目录>/probe-safe.txt

# 方式 B：用游戏自带程序集当参考（推荐；缺的成员会在编译期报错）
$refs = (Resolve-Path .tmp/p113/refs).Path     # 65 个程序集，见 bcl-surface.md §3
dotnet build Mod/MonoRuntimeProbe "-p:AndroidRefsDir=$refs" -c Release -o <输出目录>
```

`AndroidRefsDir` 必须是**绝对路径**（相对路径会被 MSBuild 按项目目录解析，导致 `CS0518`）。

`dotnet` 必须是**带 SDK** 的主机。本仓库不假设任何用户目录：

```powershell
pwsh -NoProfile -File Tools/find-dotnet.ps1     # 打印可用路径；找不到时退出码 1
```

发现顺序：`-Dotnet` → `$env:STARDEW_DOTNET` → `.tmp/*/dotnet/dotnet[.exe]` → `PATH`，
并要求 `dotnet --list-sdks` 非空（只有运行时不算可用）。

产物：`MonoRuntimeProbe.dll` + `MonoRuntimeProbe.Dependency.dll`（+ `.pdb`）。
`Probe.RunSafe(string outputDirectory)` 返回**报告文件路径**（`probe-safe.txt`）。

## 4. 在 Android 上运行（**必须显式启动**）

> **Phase 1.1 起，正常 Loader 不再执行这个探针。**
>
> 旧版 README 这里写的是"调用方是 `ModFramework.Runtime.Loader`"，并贴了一段 `RunProbe()`。
> 那是 Phase 0 的诊断捷径：Loader 扫到 `<Mods>/MonoRuntimeProbe.dll` 就直接
> `Assembly.LoadFrom` + 反射调用 `Probe.Run`。它绕过了 manifest、依赖解析和 `IModEntry`
> 契约，等于给 Mods 根目录开了一个不受 Manifest 管理的代码执行入口。
>
> 现在这段逻辑已删除。`loader-report.txt` 里固定输出 `probe=not-run-by-loader`，
> 以便旧报告读者看到明确状态而不是"字段消失"。

探针仍然保留（Phase 0 的历史证据与设备诊断都需要它），但只能**显式**启动：

**方式 A - 桌面宿主（开发机自测）**

```powershell
dotnet run --project Mod/MonoRuntimeProbe.Host -- <输出目录>
```

**方式 B - 设备上的显式诊断 Mod**

把探针包装成一个**普通的、符合 manifest 规范的 mod**，由它自己调用探针。
Phase 1.1.4 提供了现成的例子：[`Mod/P114.RuntimeProbe`](../P114.RuntimeProbe/README.md)，
它演示了"manifest + privateAssemblies + 自己 `Assembly.LoadFrom` + 写自己的报告"这条完整路径。

```csharp
// 你自己的诊断 Mod 的入口（manifest 里正常声明 entryAssembly/entryType）
public sealed class ProbeRunner : IModEntry
{
    public void Initialize(IModContext context)
    {
        var probe = Assembly.LoadFrom(Path.Combine(context.ModDirectory, "MonoRuntimeProbe.dll"));
        var run = probe.GetType("MonoRuntimeProbe.Probe")!.GetMethod("RunSafe")!;
        run.Invoke(null, new object[] { Path.Combine(context.ModDirectory, "probe-output") });
    }
}
```

这样触发是**明示的**：有 manifest、有 mod id、出现在 loader-report 里、失败会被隔离。
Loader 不会因为某个目录里存在 `MonoRuntimeProbe.dll` 就执行它。

报告落在 `<outputDirectory>/probe-safe.txt`（纯文本，**不是 json/log**）。
`outputDirectory` 为空时退化为探针自身所在目录。
写入失败时返回 `!write:<异常>`，不会抛。

## 5. 结果解读

- `T00 supported=False` → JIT 不可用，Route A 不成立，必须转 Route B/C。
- `T03` 失败但 T01/T02 通过 → 泛型实例化的 AOT 限制，需要 gsharedvt 白名单。
- `T07` 返回 `Game1 missing` → 游戏程序集未加载（或名字/命名空间变了），不是"反射不可用"。
- `T12` 崩溃 → 只能说明"直接写跳转桩不安全"；**不能**据此断言"原生 detour 永远不可行"
  （缺的是 AArch64 指令缓存刷新手段，见 `phase0-status.md` §3.26）。这条历史结论不构成本阶段
  的运行证据：T12 在本阶段**没有**被执行。

## 6. 已知限制

- 探针不捕获 native crash，所以**任何**会破坏进程的实验都会让报告来不及落盘。
  这正是 Phase 1.1.4 把 T11/T12 移出默认编译的直接原因。
- `T07` 只读枚举成员，**不调用**任何游戏成员（Phase 1.1.4 起不再提供 `allowGameInvocation`）。
- `T07` 的"576 方法 / 532 字段"是**反射计数**（`GetMethods/GetFields` 带 `NonPublic|Instance|Static`，
  会包含继承成员），与 `docs/audit/decompile-report.md` §4 的"558 方法 / 531 字段"
  （按 `Game1` 自身声明统计）**口径不同**，两者都对，但不可互相引用。差异归属未逐项复核。
- 桌面 .NET 的结果**不能**外推到 MonoVM/Android：`SHA256.HashData`、`Assembly.LoadFrom`
  的去重语义、`AppDomain.GetAssemblies()` 的顺序都要在设备上重新观察（见 Phase 1.1.4 报告 §R）。
