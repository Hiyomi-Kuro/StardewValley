# MonoRuntimeProbe

Phase 0 的运行时探针：在**真实游戏进程**里验证 .NET for Android / MonoVM 的动态程序集加载、反射与代码生成能力。

> 状态（2026-11-05 复核）：
> - **代码完成**，桌面基线通过；
> - **Android 侧只在 MuMu 12 模拟器（x86_64 + `libnb.so` 翻译）跑过**：T00–T07、T11 PASS，T12 原生崩溃；
> - **ARM64 真机未验证**（`BLOCKED: no physical device`，见 `docs/audit/phase0.5-device-checklist.md`）。

## 1. 结构

```
Mod/
├── MonoRuntimeProbe/                探针本体（net8.0，仅依赖 BCL）
│   ├── MonoRuntimeProbe.csproj
│   ├── ProbeEntry.cs                T00–T07 + T11（入口 Probe.Run）
│   └── DetourProbe.cs               T12（原生 detour 实验，独立类型）
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
4. **每个可疑 BCL 成员单独成方法**：ILLink 裁剪的失败是方法级的，方法内 `try/catch` 拦不住。

## 2. 用例（与当前源码一一对应）

| ID | 实现 | 验证项 | 期望 / 实测 |
| --- | --- | --- | --- |
| T00 | `Probe.DynCode` | `RuntimeFeature.IsDynamicCodeSupported/Compiled` | 模拟器 `supported=True compiled=True` |
| T01 | `Probe.StaticGreeting` | 普通静态方法 | PASS |
| T02 | `ProbeSubject.InstanceGreeting` | 实例方法 | PASS |
| T03 | `Probe.GenericEcho` / `GenericPair` | 泛型方法（含嵌套实例化） | PASS |
| T04 | `Probe.EventRoundtrip` | 事件订阅/触发/退订 | PASS（hits=1） |
| T05 | `Probe.ReflectionRoundtrip` | 反射 字段 get/set + 方法 Invoke | PASS |
| T06 | `DependencyMarker.Describe` | 编译期引用另一个程序集 + 运行时加载 | PASS |
| T07 | `Probe.GameTypes` | 反射访问 `StardewValley.Game1`（只读枚举成员） | 模拟器 `Game1 576 methods/532 fields` |
| T11 | `Probe.NativeDetourProbe` | 取方法地址 + `mprotect` RWX 再还原（**不写字节**） | PASS（rwx_rc=0 restore_rc=0） |
| T12 | `DetourProbe.Run` | 真写 16 字节 ARM64 跳转桩 | **FAIL**：写后进程原生崩溃（缺指令缓存刷新） |

### 2.1 已移除的用例（历史记录，不在当前源码里）

| ID | 曾验证 | 结论 | 移除原因 |
| --- | --- | --- | --- |
| T08 | `Assembly.Load(byte[])` | ❌ `MissingMethodException` | 该方法在本运行时被裁剪；保留它只会让整个 `Run` 失败，改成在 `Loader`/Bootstrap 里验证 `LoadFrom` |
| T09 | `DynamicMethod` | ❌ `TypeLoadException`（缺 `System.Reflection.Module`） | 同上；结论已由编译期 `CS7036` 与设备日志共同确认 |
| T10 | `AssemblyBuilder` | ❌ `FileNotFoundException: System.Reflection.Emit` | 同上 |

> 这些失败**是重要结论**（Harmony 路线不可行的依据），只是不再作为探针的运行时用例。
> 历史输出见 `docs/audit/phase0-status.md` §3.22；原始证据文件已不在本机。
> 旧版 README 曾把 T11 写作 "Task/线程"、把 T08–T10 写作"期望 pass"，均与当前代码不符，已更正。

## 3. 构建

```powershell
# 方式 A：桌面基线（只证明探针自身正确，不代表 Android）
dotnet run --project Mod/MonoRuntimeProbe.Host -- .tmp/phase0.5/evidence/probe-desktop

# 方式 B：用游戏自带程序集当参考（推荐；缺的成员会在编译期报错）
$refs = (Resolve-Path .tmp/phase0.5/refs).Path     # 65 个程序集，见 bcl-surface.md §3
dotnet build Mod/MonoRuntimeProbe "-p:AndroidRefsDir=$refs" -c Release -o <输出目录>
```

> 本机没有全局 .NET SDK 时，用项目私有 SDK：`.tmp/phase0.5/dotnet/dotnet.exe`（Phase 0.5 重建）。
> `AndroidRefsDir` 必须是**绝对路径**（相对路径会被 MSBuild 按项目目录解析，导致 `CS0518`）。

产物：`MonoRuntimeProbe.dll` + `MonoRuntimeProbe.Dependency.dll`（+ `.pdb`）。
`Probe.Run(string outputDirectory, bool allowGameInvocation)` 返回**报告文件路径**。

## 4. 在 Android 上运行

调用方是 `ModFramework.Runtime.Loader`（不是 Bootstrap 直接调）：

```csharp
// ModFramework.Runtime/Loader.cs 的 RunProbe()，与源码一致
var probePath = Path.Combine(BaseDir(), "MonoRuntimeProbe.dll");
if (!File.Exists(probePath)) return "absent";
var asm = Assembly.LoadFrom(probePath);                      // 不是 Load(byte[])
var run = asm.GetType("MonoRuntimeProbe.Probe")?.GetMethod("Run");
run.Invoke(null, new object?[] { Path.Combine(BaseDir(), "probe-output"), false });
```

报告落在 `<outputDirectory>/probe.txt`（纯文本，**不是 json/log**）。
`outputDirectory` 为空时退化为 `Loader` 自身所在目录。
写入失败时 `Run` 返回 `!write:<异常>`，不会抛。

## 5. 结果解读

- `T00 supported=False` → JIT 不可用，Route A 不成立，必须转 Route B/C。
- `T03` 失败但 T01/T02 通过 → 泛型实例化的 AOT 限制，需要 gsharedvt 白名单。
- `T07` 返回 `Game1 missing` → 游戏程序集未加载（或名字/命名空间变了），不是"反射不可用"。
- `T12` 崩溃 → 只能说明"直接写跳转桩不安全"；**不能**据此断言"原生 detour 永远不可行"
  （缺的是 AArch64 指令缓存刷新手段，见 `phase0-status.md` §3.26）。

## 6. 已知限制

- 探针不捕获 native crash；T12 这类崩溃会让报告来不及落盘。
  建议同时开 `adb logcat -s monodroid:V DOTNET:V mono:V`。
- `T07` 默认不调用游戏成员（避免副作用）；需要读值时用 `allowGameInvocation=true`，
  且应只在测试用存档上做。
- `T07` 的"576 方法 / 532 字段"是**反射计数**（`GetMethods/GetFields` 带 `NonPublic|Instance|Static`，
  会包含继承成员），与 `docs/audit/decompile-report.md` §4 的"558 方法 / 531 字段"
  （按 `Game1` 自身声明统计）**口径不同**，两者都对，但不可互相引用。差异归属未逐项复核。
