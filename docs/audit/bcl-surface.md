# BCL 可用面与模组编译规范（Phase 0 实测）

> 本文的每一条都来自设备实测或编译期验证，不是推测。
> 证据：`.tmp/phase0/evidence/probe-device-androidrefs.txt`、`phase0-status.md` §3.20–§3.23。

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

不要用桌面 `net8.0` 参考包，改为引用 APK 里的 65 个程序集：

```powershell
# 一次性：从 blob 解出参考程序集
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.blob .tmp/phase0/refs

# 每次构建：关闭隐式框架引用，改用这些程序集
dotnet build Mod/ModFramework.Runtime -p:AndroidRefsDir=.tmp/phase0/refs
```

`Mod/Directory.Build.props` 已实现：传入 `AndroidRefsDir` 时设 `NoStdLib=true` +
`DisableImplicitFrameworkReferences=true`，并引用该目录下全部 `*.dll`；不传时行为不变。

**效果**：所有"运行时会缺的成员"变成**编译期错误**。已复现的例子：

```
CS0117  "Directory" 未包含 "GetFiles" 的定义
CS0234  命名空间 System.Runtime.InteropServices 中不存在 RuntimeInformation
CS1503  参数 1: 无法从 "byte[]" 转换为 "string"        (Assembly.Load(byte[]))
CS7036  ModuleBuilder.DefineType 参数不匹配            (Emit 面不完整)
```

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
