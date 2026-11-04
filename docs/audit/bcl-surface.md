# BCL 可用面与模组编译规范（Phase 0 实测）

> 本文的每一条都来自设备实测或编译期验证，不是推测。
> 证据：`.tmp/phase0/evidence/probe-device-androidrefs.txt`、`phase0-status.md` §3.20–§3.23。
>
> **证据可得性（Phase 0.5 标注）**：上述 `.tmp/phase0/evidence/probe-device-androidrefs.txt`
> **已不在本机**。表中数字（576 方法 / 532 字段）是当时的探针输出，当前无法重放；
> 需要重新取证时走 `phase0.5-device-checklist.md` 的真机流程。
> Phase 0.5 已复核的部分：`Assembly.LoadFrom` / `Load(byte[])` 的编译期差异、Android 参考程序集编译、
> `Directory.GetFiles` 编译期缺失——均用本机私有 SDK 8.0.404 重新验证过（见 `phase0.5-report.md` §8）。

---

## 1. 结论先行

| 项 | 结论 |
| --- | --- |
| 动态加载程序集 | ✅ 只能用 `Assembly.LoadFrom(string)` |
| `Assembly.Load(byte[])` | ❌ 不存在（编译期 CS1503） |
| 执行 IL / JIT | ✅ `RuntimeFeature.IsDynamicCodeSupported = True`、`IsDynamicCodeCompiled = True` |
| 反射访问游戏类型 | ✅ `Game1` 576 方法 / 532 字段可枚举 |
| `Reflection.Emit` | ❌ `DynamicMethod` 不可用、`System.Reflection.Emit` 程序集缺失 |
| 模组静态引用游戏类型 | ✅ `using StardewValley;` + `Game1.IsMultiplayer` 编译通过 |
| 文件写入（应用私有目录） | ✅ `File.WriteAllText` |
| 目录枚举 | ❌ `Directory.GetFiles` 不存在（编译期 CS0117） |

---

## 2. 为什么会这样

该 APK 是 .NET for Android 的 **Release + ILLink 裁剪 + AOT(normal)** 构建：

- 被裁剪的成员对**方法级 JIT** 是致命的——只要方法体里引用了一个不存在的成员，
  **整个方法的 JIT 都会失败**，方法内的 `try/catch` 拦不住（实测两次）。
- Android 的 `System.Runtime` facade 与桌面 net8.0 的参考程序集**不一致**：
  桌面编译时能通过的引用，在设备上会 `TypeLoadException`。

因此有两条硬性规范：编译方式（§3）与编码方式（§4）。

---

## 3. 编译规范：用游戏的程序集当参考

不要用桌面 `net8.0` 参考包，改为引用 APK 里的 65 个程序集（两个存储：主存储 58 + 架构存储 7）：

```powershell
# 一次性：从两个 blob 解出参考程序集到同一个目录（65 个）。
# store.py 会按清单的 Blob ID 自动判定条目名；架构存储不能借用主存储的名字。
$refs = (Resolve-Path .tmp\phase0.5\refs).Path
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.blob $refs `
    --manifest app/src/main/apkroot/assemblies/assemblies.manifest
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.arm64_v8a.blob $refs `
    --manifest app/src/main/apkroot/assemblies/assemblies.manifest

# 每次构建：关闭隐式框架引用，改用这些程序集。必须传**绝对路径**。
dotnet build Mod/ModFramework.Runtime "-p:AndroidRefsDir=$refs"
```

> **Phase 0.5 修正（已实测）**：`-p:AndroidRefsDir=.tmp/phase0/refs` 这种**相对路径不生效**。
> `Directory.Build.props` 里的 `Reference Include="$(AndroidRefsDir)\*.dll"` 由 MSBuild 相对
> **项目目录**解析，于是匹配不到任何文件，编译直接报 `CS0518 预定义类型"System.Object"未定义`。
> 传绝对路径后 `0 warning / 0 error`。这是 Phase 0 文档里的命令错误，不是运行时问题。

**效果**：所有"运行时会缺的成员"变成**编译期错误**。已复现的例子：

```
CS0117  "Directory" 未包含 "GetFiles" 的定义
CS0234  命名空间 System.Runtime.InteropServices 中不存在 RuntimeInformation
CS1503  参数 1: 无法从 "byte[]" 转换为 "string"        (Assembly.Load(byte[]))
CS7036  ModuleBuilder.DefineType 参数不匹配            (Emit 面不完整)
```

> Phase 0.5 复核：以上四条**未逐条重跑**（探针源码已删掉 T08–T10，Emit 用例不存在了）。
> 可复核的是 `Directory.GetFiles`、`RuntimeInformation`、`Assembly.Load(byte[])` 这三类引用在
> Android 参考程序集下确实编译失败——`Mod/Directory.Build.props` 的机制本身已用
> `ModFramework.Runtime` / `MonoRuntimeProbe` / `ExampleMod.HelloWorld` 三个工程重新验证
> （0 error，见 `phase0.5-report.md` §8.2）。

`Mod/Directory.Build.props` 已实现：传入 `AndroidRefsDir` 时设 `NoStdLib=true` +
`DisableImplicitFrameworkReferences=true`，并引用该目录下全部 `*.dll`；不传时行为不变。

---

## 4. 编码规范：一个方法只碰一个"未证实"的 API

裁剪的连坐效应是**方法级**的，所以：

```csharp
// ✅ 正确：可疑 API 各自独立成方法，调用方负责 catch
private static string TimeNow() => DateTimeOffset.Now.ToString("O");     // 唯一依赖
private static string BaseDir() => AppContext.BaseDirectory;             // 唯一依赖
private static string Try(Func<string> f) { try { return f(); } catch (Exception e) { return "!" + e.GetType().Name; } }

public static string Run(...)   // 聚合方法只做拼接 + 调用自己的方法
{
    var report = "..." + Try(TimeNow) + Try(BaseDir);
    ...
}

// ❌ 错误：把可疑 API 直接写进聚合方法体
public static string Run(...)
{
    var t = DateTimeOffset.Now;     // 这一个引用就能让整个 Run 的 JIT 失败
    var d = AppContext.BaseDirectory;
    ...
}
```

已证实的"安全"成员（可以直接用在聚合方法里）：
`string` 拼接、`Path.Combine`、`Path.GetDirectoryName`、`File.Exists`、`File.WriteAllText`、
`Directory.Exists`、`Assembly.LoadFrom`、`Assembly.GetType`、`Type.GetMethod`、
`MethodBase.Invoke`、`Exception` 构造。

---

## 5. 模组作者视角

一个模组只需要：

```csharp
using StardewValley;

public static class ModEntry
{
    public static string Greet() => "hello; multiplayer=" + Game1.IsMultiplayer;
}
```

用 `-p:AndroidRefsDir=<refs>` 编译即可。实测产物只有 **2 个 AssemblyRef**
（`StardewValley` + `System.Runtime`）、16 个 TypeRef、15 个 MemberRef，
大小 4096 字节（`ExampleMod.HelloWorld.dll`，SHA-256 `d91efa07…`）。

---

## 6. 尚未验证

| 项 | 说明 |
| --- | --- |
| 模组 DLL 在设备上被 `LoadFrom` 后能否直接 `new Game1()` | 未测（构造游戏对象有副作用） |
| `System.Text.Json` | APK 里没有这个程序集，模组不能用它做配置序列化 |
| 泛型实例化的 AOT 边界 | 探针 T03 只覆盖了常见实例化 |
| 真机（ARM64） | 全部结论目前来自 x86_64 模拟器 + ARM 翻译层 |

---

## 7. 变更记录（Phase 0.5，2026-11-05）

| 改动 | 原因 / 证据 |
| --- | --- |
| 页首增加证据可得性说明 | `.tmp/phase0/evidence/*` 已删除 |
| §3 参考程序集命令改为"两个 blob + 绝对路径" | 相对 `AndroidRefsDir` 实测不生效（`CS0518`）；只解主 blob 会缺 7 个架构程序集，Cecil/编译器都可能解析失败 |
| §3 增加"四条编译错误未逐条重跑"的标注 | 探针里的 T08–T10 用例已删除，原证据无法整段复现 |
| §5 数字复核 | `ExampleMod.HelloWorld.dll` 本机重建为 **4096 字节**（与记录一致）；SHA-256 为 `1ec12bc0…`，与记录的 `d91efa07…` 不同——不同 SDK 版本/时间戳会改变字节，因此**哈希不应写进规范当身份**，只作历史记录 |
