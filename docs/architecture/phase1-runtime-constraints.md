# Phase 1 运行时约束（Mono / BCL / 程序集加载 / Android 存储）

- 阶段：Phase 1
- 适用代码：`Mod/ModFramework.Runtime/`（在游戏进程内执行的那一份程序集）
- 证据等级：见 `docs/audit/phase0.5.1-evidence-index.md` §0
- 本文所有"不可用"结论都是**编译期**证据（A/B 级）：用游戏自带的 65 个程序集当参考程序集编译，
  编译器直接报错。不是猜的，也不是从桌面 .NET 文档推的。

---

## 1. 一句话结论

游戏进程里的 BCL 是 **ILLink 裁剪过的 .NET 8**（.NET for Android / MonoVM 8.0.1124）。
写 `ModFramework.Runtime` 时必须假设：**只有被游戏自己用到过的 BCL 成员存在**。
新增一个 BCL 调用前，先跑 §2 的编译检查，否则只会在设备上以 `MissingMethodException`
或"整个方法 JIT 失败"的形式爆炸（方法级失败，方法内 `try/catch` 拦不住）。

---

## 2. 验证方法（可重复）

```powershell
# 65 个程序集（若 .tmp/phase0.5/refs 不存在）
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.blob .tmp/phase0.5/refs --manifest app/src/main/apkroot/assemblies/assemblies.manifest
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.arm64_v8a.blob .tmp/phase0.5/refs --manifest app/src/main/apkroot/assemblies/assemblies.manifest

# 用"运行时真实形态"编译：这一条就是 CI/本地门禁里的 runtime_compiles_against_android_refs
.tmp/phase0.5/dotnet/dotnet.exe build Mod/ModFramework.Runtime -c Release -p:AndroidRefsDir=<abs path to refs>
```

`Mod/Directory.Build.props` 在给了 `AndroidRefsDir` 时会关掉隐式框架引用、改为直接引用那 65 个 dll。
于是"运行时缺这个成员"会在**编译期**变成 `CS0117 / CS0234 / CS1061 / CS7036`。

> 这个检查只证明**元数据里存在**该成员，不证明它在设备上行为正确。行为验证仍需设备（E1/E2，当前 BLOCKED）。

---

## 3. 能力矩阵（实测，2026-11-05）

### 3.1 可用

| 能力 | 用法 | 备注 |
| --- | --- | --- |
| 目录枚举（仅子目录） | `Directory.GetDirectories(path)` | **这是唯一的目录枚举手段** |
| 目录/文件存在与创建 | `Directory.Exists` / `Directory.CreateDirectory` / `File.Exists` | |
| 文件读写 | `File.ReadAllText` / `ReadAllBytes` / `WriteAllText` / `AppendAllText` / `Delete` / `Copy` / `OpenRead` | `AppendAllText` 是日志的基础 |
| 流 | `FileStream(path, FileMode, FileAccess)` / `StreamReader` / `Stream.Read(byte[],int,int)` / `Stream.Length` | 用来给 manifest 做有上限读取 |
| 路径 | `Path.Combine` / `GetFileName` / `GetDirectoryName` / `GetFileNameWithoutExtension` / `GetExtension` / `DirectorySeparatorChar` | **没有** `Path.GetTempPath` |
| 程序集加载 | `Assembly.LoadFrom(string)` | 唯一可用的外部程序集入口 |
| 反射 | `Assembly.GetType(string,bool,bool)`、`Type.GetType(string,bool)`、`GetInterfaces`、`GetConstructors`、`GetFields`、`GetMethods`、`IsPublic`/`IsAbstract`/`IsClass`/`IsGenericType`、`Activator.CreateInstance`、`MethodBase.Invoke` | `GetProperties()` 无参重载**不存在**，要用 `GetProperties(BindingFlags)` |
| 版本/时间 | `Version.Parse`、`DateTimeOffset.Now`、`DateTime.UtcNow`、`ToString("O")`、`TimeSpan` | `Version.TryParse` **不存在** → 自己写 SemVer |
| 集合 | `List<T>` / `Dictionary<K,V>` / `HashSet<T>` / `SortedDictionary<K,V>` / `Stack<T>` / `Queue<T>` / `KeyValuePair<K,V>` | |
| 文本 | `StringBuilder`（含 `AppendLine`）、`Encoding.UTF8`、`string.Split(char[], StringSplitOptions)`、`string.Join`、`CompareOrdinal`、`Trim`、`PadLeft`、`ToLowerInvariant` | |
| 环境 | `Environment.NewLine`、`Environment.GetEnvironmentVariable` | **没有** `Environment.CurrentDirectory` |
| 进程内程序集清单 | `Assembly.GetEntryAssembly()`、`AppDomain.CurrentDomain.GetAssemblies()` | 用来找 `StardewValley` 判断游戏版本 |
| 程序集身份 | `Assembly.GetName()`、`AssemblyName.GetPublicKeyToken()`、`Assembly.Location`、`Assembly.IsDynamic` | **没有** `AssemblyName.GetPublicKey()`（见 3.2）；Token 是宿主能给出的全部强名称身份 |
| 密码学摘要 | `SHA256.HashData(byte[])` | 只用于内容摘要的快速否定；相等判定仍走逐字节比较 |
| 控制台 | `Console.WriteLine` / `Console.Error.WriteLine` | 输出进 logcat |

### 3.2 不可用（编译期证据）

| 成员 | 报错 | 影响 / 我们的替代方案 |
| --- | --- | --- |
| `Directory.GetFiles` / `EnumerateFiles` / `GetFileSystemEntries` | `CS0117` | 不能枚举文件 → mod 包里的文件名必须由 manifest 声明 |
| `Directory.GetDirectories(path, pattern, SearchOption)` | `CS0103`（无 `SearchOption`） | 只用单参重载，然后自己过滤 |
| `Directory.GetLastWriteTimeUtc` | `CS0117` | 不做时间戳比较 |
| `new DirectoryInfo(path)` / `new FileInfo(path)` | `CS1729` | 完全不用 `*Info` 类型 |
| `Path.GetTempPath` | `CS0117` | 临时文件只能放应用可写目录（本阶段不需要） |
| `Environment.CurrentDirectory` | `CS0117` | **不要**用 `Path.GetFullPath` 处理相对路径；所有路径都从程序集位置推导 |
| `Version.TryParse` | `CS0117` | 自写 `SemVersion` |
| `System.Text.Json`（整个命名空间） | `CS0234` | 自写 JSON 解析器（`Json.cs`） |
| `System.Linq`（`Enumerable` 扩展方法：`Where`/`Select`/`ToArray`/`Any`/`FirstOrDefault`/`Count`） | `CS1061` | **运行时程序集里一行 LINQ 都不能有**，全部手写 `for` 循环 |
| `File.AppendAllLines` | `CS0117` | 用 `AppendAllText` 自己拼 |
| `Type.GetProperties()`（无参） | `CS7036` | 用 `GetProperties(BindingFlags)` |
| `Assembly.GetType(string, bool)`（2 参） | `CS7036` | 用 3 参重载 `(name, throwOnError, ignoreCase)` |
| `Assembly.GetCallingAssembly` | `CS0117` | 不用 |
| `Assembly.Load(byte[])` | `CS1503`（Phase 0 记录 + 编译期） | 唯一入口是 `Assembly.LoadFrom(string)` |
| `Reflection.Emit` / `DynamicMethod` / `AssemblyBuilder` | `CS0234` / `CS7036` | Harmony 类方案在本运行时不可行（Phase 0 结论，ADR §6） |
| `AppContext` / `RuntimeInformation` / `RuntimeFeature` | `CS0117` / `CS0234` | 不做运行时特性探测 |
| `AssemblyName.GetPublicKey()` | `CS1061`（Phase 1.1.1 实测，AndroidRefsDir 编译） | 拿不到**完整公钥**，只有 `GetPublicKeyToken()`；因此预检从文件读到的完整公钥要用 ECMA-335 规则（SHA-1 末 8 字节倒序）派生成 Token 才能与宿主比较，见 `PublicKeyToken.cs` |

> 注意：`System.Linq.dll` 这个**文件**在 65 个程序集里，但 `Enumerable` 的扩展方法被裁掉了。
> 引用存在 ≠ 成员存在，必须按成员判断。

---

## 4. 程序集加载的真实行为

### 4.1 只有 `Assembly.LoadFrom(string)`

- `Assembly.Load(byte[])` 被裁剪（编译期 `CS1503`；Phase 0 设备记录 `MissingMethodException`，D 级）。
- 路径必须是**应用进程可读**的目录。Phase 0 记录过一个坑：由 root 在
  `/sdcard/Android/data/<pkg>/files/...` 创建的文件，应用内 `File.Exists` 返回 `false`
  （不抛异常），因为属主是 root。写入时必须 `chown` 成应用 uid，或直接用应用私有目录。

### 4.2 `LoadFrom` **不会**自动解析同目录依赖（本阶段新发现，B 级）

`Tools/tests/ModFramework.Tests` 的宿主测试证明：在 .NET Core 上 `Assembly.LoadFrom(<mod>/X.dll)`
**不会**到 `<mod>/` 目录里找 `X.dll` 引用的 `Y.dll`，而是走宿主的正常探测
（deps.json / 应用基目录）。第一次跑该测试就得到：

```
id=FixtureMod.Good state=Failed code=MF0309
  message=FileNotFoundException: Could not load file or assembly 'FixtureMod.Dependency, Version=1.0.0.0'
```

而 `Directory.GetFiles` 又不存在，无法枚举"这个目录里有哪些 dll"。
**因此 Phase 1 的设计是显式声明**：manifest 的 `privateAssemblies` 列出随包携带的私有程序集，
Loader 在加载入口程序集之前**按声明逐个 `LoadFrom`**（两轮 fixpoint，与声明顺序无关）。

这样做的代价是 mod 作者要写清楚自己的私有依赖；收益是：

1. 不依赖某个运行时版本"碰巧会探测目录"的行为；
2. 不需要注册全局 `AppDomain.AssemblyResolve`（那会把 mod 目录变成全局搜索路径，
   有 DLL 劫持/同名冲突风险）；
3. 未声明的 dll 不会被加载，攻击面更小。

### 4.3 同名/重复加载

- 同一路径重复 `LoadFrom` 返回同一个 `Assembly`（.NET Core 默认 ALC 按标识去重），
  因此"同一个 dll 被两个 mod 引用"不会产生两份类型。
- 不同路径、相同标识：也返回**先加载的那一份**。这会让"两个 mod 各带一份同名不同版本的库"
  变成"先加载者胜"，属于已知限制（见 §6）。
- 因此契约程序集（`ModFramework.Runtime`）**绝不能**随 mod 打包：第二份副本会让
  `IModEntry` 变成另一个类型，Loader 会以 `MF0204` 拒绝（并给出"来自哪个程序集"的诊断）。

### 4.4 快照加载：Phase 1.1.2 的结论，Phase 1.1.3 的修正

`Assembly.LoadFrom(string)` 是唯一入口（§4.1），而"路径"本身是可变的：预检读完到加载之间，
文件可以被替换或同长度原位改写。Phase 1.1.2 因此**不按 mod 声明的路径加载**，而是：

| 步骤 | 行为 | 依据 |
| --- | --- | --- |
| 读 | 一次读全文件进 buffer；身份/长度/摘要都来自这一个 buffer | A 级（源码 + 测试） |
| 写 | 把同一个 buffer 写入 `<Mods>/.staging/<run>/<mod id>/<声明文件名>`（同名已存在时追加 `~N`，永不复用路径） | A 级 |
| 验 | 立刻读回快照**逐字节比较**（`ContentIdentity.VerifyExactBytes`） | A 级 |
| 载 | `Assembly.LoadFrom(claim.StagedPath)`；加载前再校验长度 + **SHA-256 完整性摘要** | A 级 |
| 兜底 | 摘要不符 / 文件消失 → 该 mod 失败（`MF0202`），不加载 | A 级 |

Phase 1.1.3 补充的三条约束：

| 主题 | 规则 | 依据 |
| --- | --- | --- |
| 去重 | `dedup key`（可碰撞，允许 SHA-1 索引形式）只找候选；命中后必须对**当前 claim 的 buffer**逐字节比较，`Same` 才共享；`Unverifiable` 直接失败 | A 级 |
| 完整性 | 快照完整性**只用 SHA-256**；运行时算不出 SHA-256 → 该 claim `MF0209`（不落盘、不加载），不回退到 SHA-1 | A 级（桌面）／BLOCKED（真机 SHA-256 可用性） |
| 身份 | `LoadFrom`/缓存/宿主复用返回的 `Assembly` 必须与 claim 预检身份一致，否则 `MF0210` 且不写成功缓存 | A 级 |

`SHA256.HashData` 与 `File.ReadAllBytes` 在**目标引用程序集**（`.tmp/p113/refs` 的 65 个程序集，
从 APK 的 XABA 容器解出）里存在，`-p:AndroidRefsDir=…` 编译通过（B 级：编译期证据）。
真机 Mono 的**运行时**行为（算法实现是否被裁剪掉、异常类型、性能）未验证 → BLOCKED。

事实与边界：

- **不能用内存字节**：`Assembly.Load(byte[])` 在目标引用程序集里不存在。本阶段重新编译验证，
  得到 `CS1503: 无法从“byte[]”转换为“string”`（只有 `Load(string)` 重载）→ 只能落盘再从路径加载。
- **`Assembly.Location` 变成快照路径**：mod 若用 `Assembly.Location` 找自己的数据文件会失效，
  必须用 `IModContext.ModDirectory`（Phase 1 起就是这个契约）。
- **剩余窗口**：快照与 mod 同处应用私有存储，同 uid 的代码理论上可在"校验"与"加载"之间替换它。
  该替换会被下一次校验发现并拒绝，但不是原子操作。彻底关闭需要 `Assembly.Load(byte[])`，
  目标运行时没有 → **真机（ARM64 Mono）语义验证标记 BLOCKED**，不得把桌面结论当设备结论。
- **不确定就不加载**：快照目录建不出来、超预算（64 MiB/文件、256 MiB 总量、4096 文件）、
  文件写不进去，全部 fail-closed（整轮或该 mod 失败），绝不回退到"按路径加载"。

---

## 5. 启动时序与调用点

```
Java: mono.MonoRuntimeProvider (initOrder=1999999999)
  → Java peer MainActivity
    → 托管 StardewValley.MainActivity.OnCreate(Bundle)
      → CheckAppPermissions
        → OnCreatePartTwo()          ← 注入点（IL_0000 第一条真实指令）
            call ModFramework.Bootstrap::Start()
              → Assembly.LoadFrom(<Mods>/ModFramework.Runtime.dll)
                → ModFramework.Runtime.Loader::Start()   （反射 Invoke，返回值 = null 表示成功）
            newobj InstanceGame → SetContentView → Run()
```

事实与边界：

- `OnCreatePartTwo` 在**创建游戏对象之前**执行。此时进程、Mono 运行时、应用私有目录都已可用；
  游戏世界还没建立。
- 因此 Loader 只能做"与游戏状态无关"的事：读文件、解析 manifest、加载程序集、构造入口、
  调用 `Initialize`。**不允许**在 `Initialize` 里访问 `Game1` 实例或推进世界（本阶段也不提供这种 API）。
- `Initialize` 在 UI 线程同步执行。Loader 是**同步、单线程**契约；重入用一个静态标志挡住（`MF0901`）。
- 同一个 Mods 根目录在**同一进程内只初始化一次**：第二次 `Run` 直接返回 `null`，
  报告写 `result=already-initialized`，不重复执行任何 mod 入口。
- Bootstrap 的 catch 会吞掉异常（不能让 mod 问题挡住游戏启动），所以**报告必须落盘**：
  `<Mods>/loader-report.txt` 与 `<Mods>/loader.log`。

### 5.1 安全性判断

在 `OnCreatePartTwo` 调用任意 mod 代码是**当前证据下可接受**的，因为：

1. 该点已在 Phase 0 被证明会执行（D 级记录）且注入在第一条指令（B 级，`verify` 可复核）；
2. Loader 不触碰游戏对象，只做文件/反射操作；
3. 任何异常都被 `try/catch` 限制在单个 mod 或整个 Loader 内，游戏继续启动。

**未验证的假设**（必须显式列出，见 §7）。

---

## 6. Android 存储与路径

| 项 | 事实 | 影响 |
| --- | --- | --- |
| 应用私有目录 | `/data/data/com.chucklefish.stardewvalley/files`，属主是应用 uid | 应用可读写；需要 root 才能从 adb 放文件进去 |
| 外部私有目录 | `/sdcard/Android/data/com.chucklefish.stardewvalley/files` | 应用**通常**可写；但由 root/shell 创建的文件应用读不到（属主错误） |
| 非 debuggable | 无 `run-as` | 调试只能靠 logcat + 落盘报告 |
| scoped storage | Android 11+ 在 DocumentsUI 里屏蔽 `Android/data` 子树 | 用户手动放 mod 需要 root 或应用自身提供导入 |

**Mods 根目录的确定方式**：`Loader.DefaultModsRoot()` = `ModFramework.Runtime.dll` 所在的目录。
不硬编码包名路径，因此：

- 由 Bootstrap 决定把运行时放在哪里，Loader 就扫哪里；
- 默认值（`/data/data/<pkg>/files/Mods`）只在程序集 `Location` 为空时兜底。

目录布局见 `docs/modding/manifest-spec.md` §2。

---

## 7. 未验证假设（设备缺失导致 BLOCKED）

| # | 假设 | 现状 | 验证方式 |
| --- | --- | --- | --- |
| 1 | `Directory.GetDirectories` 在游戏进程里**行为**正确（不只是元数据存在） | 仅编译期证据 | 设备 E1：扫到 mod 包并写报告 |
| 2 | `Assembly.LoadFrom` 在 Mono/Android 上加载 mod dll 并解析 `privateAssemblies` | 宿主已证明；设备未验证 | 设备 E1 |
| 3 | `Activator.CreateInstance` + 接口调用在混合 AOT+JIT 下工作 | 宿主已证明；设备未验证 | 设备 E1 |
| 4 | `AppDomain.CurrentDomain.GetAssemblies()` 在 Mono 上包含 `StardewValley` | 未验证 | 设备日志里的 `game-version=` |
| 5 | `File.AppendAllText` 在应用私有目录并发/异常场景下的行为 | 未验证 | 设备上反复启动 |
| 6 | UI 线程同步执行 `Initialize` 不阻塞到 ANR 阈值 | 未验证（当前 mod 只写日志） | 设备上加入耗时 mod 观察 |

这些都不影响离线实现的可测试性，但**不能让任何人把宿主测试当成设备证据**。

---

## 8. 变更记录

| 日期 | 变更 |
| --- | --- |
| 2026-11-05 | 初版：Phase 1 编译期能力矩阵、LoadFrom 依赖解析实测结论、启动时序与存储约束 |
