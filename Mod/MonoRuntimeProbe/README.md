# MonoRuntimeProbe

Phase 0 的运行时探针：在**真实游戏进程**里验证 .NET for Android / MonoVM 的动态程序集加载、反射与代码生成能力。

> 状态：**代码完成、桌面基线通过、Android 真机未验证（Awaiting Device Validation）**。
> 桌面结果只能证明探针本身正确，**不能**代表 Android 运行时。

## 1. 结构

```
Mod/
├── MonoRuntimeProbe/                探针本体（net8.0，仅依赖 BCL）
│   ├── MonoRuntimeProbe.csproj
│   └── ProbeEntry.cs                T00–T11 用例
├── MonoRuntimeProbe.Dependency/     "依赖外部程序集"用例
│   └── DependencyMarker.cs
└── MonoRuntimeProbe.Host/           桌面基线宿主（开发机自测用）
    └── Program.cs
```

设计约束：

1. 目标框架 `net8.0`，与游戏运行时 `.NETCoreApp,Version=v8.0` 一致；
2. **不引用** `StardewValley.dll` / `Mono.Android`：对游戏类型的访问全部走反射（按名字解析），
   这样探针不依赖参考程序集，也能在游戏程序集尚未加载时运行；
3. 只依赖 BCL，保证在任何 MonoVM 上都能编译。

## 2. 用例

| ID | 验证项 | 期望 |
| --- | --- | --- |
| T00 | `RuntimeFeature.IsDynamicCodeSupported/Compiled`、框架描述、架构 | 在 Android 上给出 JIT 是否可用的第一手答案 |
| T01 | 普通静态方法 | pass |
| T02 | 实例方法 | pass |
| T03 | 泛型方法（含嵌套泛型实例化） | pass；失败则说明 AOT gsharedvt 限制 |
| T04 | 事件订阅/触发/退订 | 只触发 1 次 |
| T05 | 反射 字段 get/set + 方法 Invoke + 属性 | pass |
| T06 | 编译期引用另一个程序集 | pass |
| T07 | 反射访问游戏类型（`Game1`/`MainActivity`/`Program`/`InstanceGame`/`MobileDisplay`） | 类型可解析；默认**不调用**有副作用的成员 |
| T08 | `Assembly.Load(byte[])` | pass（这是 mod 加载的主路径） |
| T09 | `DynamicMethod` | pass（Harmony 的前置条件） |
| T10 | `AssemblyBuilder` 动态程序集 | pass（Transpiler / 动态代理前置条件） |
| T11 | Task/线程 | pass |

## 3. 构建

```powershell
# 需要 .NET SDK 8.x（本机若未安装，可用项目内私有 SDK：见 docs/audit/phase0-status.md）
dotnet build Mod/MonoRuntimeProbe/MonoRuntimeProbe.csproj -c Release -o <输出目录>

# 桌面基线（仅自测，不代表 Android）
dotnet run --project Mod/MonoRuntimeProbe.Host -- .tmp/phase0/evidence/probe-desktop
```

产物：`MonoRuntimeProbe.dll` + `MonoRuntimeProbe.Dependency.dll`（+ `.pdb`）。

## 4. 在 Android 上运行

需要 Bootstrap 在 `MainActivity.OnCreatePartTwo` 之前调用：

```csharp
// Bootstrap 侧（伪代码）
var dir  = <应用可写目录>/probe;
var asm  = Assembly.Load(File.ReadAllBytes(Path.Combine(<Mods 目录>, "MonoRuntimeProbe.Probe.dll")));
var run  = asm.GetType("MonoRuntimeProbe.Probe")!.GetMethod("Run")!;
var path = (string)run.Invoke(null, new object?[] { dir, false })!;   // 返回 json 报告路径
```

报告落在 `<dir>/probe-<时间戳>.json` 与 `.log`，同时把日志同时写一份到游戏日志目录，
方便 `adb logcat` / 文件回捞。

## 5. 结果解读

- `T00.IsDynamicCodeSupported=false` → JIT 不可用，Route A 不成立，必须转 Route B/C。
- `T03` 失败但 T01/T02 通过 → 泛型实例化的 AOT 限制，需要 gsharedvt 白名单。
- `T08` 失败 → 无法从字节加载程序集，mod 必须走"进 APK 的存储条目"路径。
- `T09/T10` 失败 → Harmony / Transpiler 不可用，Route D 降级为 H0-only。

## 6. 已知限制

- 探针不捕获 native crash；如果 JIT/动态代码触发 SIGSEGV，报告可能来不及落盘。
  建议同时开 `adb logcat -s monodroid:V DOTNET:V mono:V`。
- `T07` 默认不调用游戏成员（避免副作用）；需要读值时用 `allowGameTypeInvocation=true`，
  且应只在测试用存档上做。
