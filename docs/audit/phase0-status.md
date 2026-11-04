# Phase 0 状态报告

- 日期：2026-11-04
- 阶段：**Phase 0 — 技术可行性审计**
- 结论：**部分完成**。静态审计完成；运行时实验因**无真机**未完成。
- 是否可进入 Phase 1：**否**（见 §4 门控）。

---

## 1. 已完成

| 项 | 交付物 | 状态 |
| --- | --- | --- |
| 工程结构与关键源码审计 | 本报告 §2；`docs/audit/decompile-report.md` | ✅ |
| 程序集容器逆向（XABA/XALZ/LZ4）与 65 个程序集提取 | `.tmp/phase0/work/extract_assemblies.py`、`.tmp/phase0/extract/blob000/` | ✅ |
| 程序集身份表（版本/MVID/TFM） | `_identity2.tsv` + `runtime-feasibility.md` §4.2 | ✅ |
| MonoVM 版本 / JIT / AOT 模式判定 | `runtime-feasibility.md` §3 | ✅ |
| AOT 模式 `normal` 的重定位级证明 | `xa_aotmode.py` / `xa_reloc.py` | ✅ |
| Java 启动入口（dex）审计 | `decompile-report.md` §5.1 | ✅ |
| 托管 `MainActivity` IL 级启动链 | `decompile-report.md` §5.2–5.4 | ✅ |
| Android 存储/权限审计 | `runtime-feasibility.md` §8 | ✅ |
| 技术路线决策 + ADR | `docs/audit/adr-0001-mod-injection.md` | ✅（Proposed） |
| MonoRuntimeProbe 工程（T00–T11） | `Mod/MonoRuntimeProbe/` | ✅ 代码 + 桌面基线通过 |
| 外部参考研究（SMAPI/Harmony/MonoMod/许可/先例） | `adr-0001` §7、`runtime-feasibility.md` §7.1.1/§7.1.2 | ✅（外部参考，未独立验证） |
| 私有 .NET SDK 8.0.427（不改系统 PATH） | `.tmp/phase0/dotnet/` | ✅（可随时删除） |
| 注入器 `Tools/ModInjector`（Cecil，IL 注入 + 自检） | `Tools/ModInjector/` | ✅ 离线验证通过（见 §3.6） |
| 容器替换工具 `Tools/AssemblyStore`（XABA/XALZ 读写 + LZ4 编码器） | `Tools/AssemblyStore/store.py` | ✅ 离线验证通过（见 §3.7） |
| 程序集容器布局完整解析（含 65×40 hash 表） | `.tmp/phase0/work/gap.py` + ADR §D3 | ✅ |

## 3.6 注入器验证（离线，未装机）

| 检查 | 工具 | 结果 |
| --- | --- | --- |
| 注入成功 | ModInjector 自检 | `bootstrap-type=present`、`hook-call=present at IL_0000`、`hook-is-first=yes` |
| 结构合法 | dnfile | 解析无异常；`TypeDef 2867→2868`、`MethodDef 30067→30068`、`AssemblyRef 28→29` |
| 身份不变 | REA managed | `StardewValley 1.6.15.24354`、`.NETCoreApp v8.0`、IL-only/AnyCPU 保持；MVID `573bf1e5…` → `e8870106…` |
| 产物 | — | `9,280,000` → `9,261,056` 字节；SHA-256 `b46b8f33…` |

已知差异：PE `Characteristics` 由 `0x2022` 变 `0x2122`（Cecil 写入 `32BIT_MACHINE`）。对 IL-only AnyCPU 程序集应无影响，但需在 E1 中确认加载器不检查该位。

## 3.7 容器替换验证（离线，未装机）

`Tools/AssemblyStore/store.py`（纯 Python，自研 LZ4 编解码）：

| 检查 | 结果 |
| --- | --- |
| LZ4 往返（58 条目） | 58/58 一致；总 14,485,064 → 6,529,772 字节（45.08%） |
| 替换后逐条目比对 | **57/58 逐字节相同**，仅 `StardewValley` 变化 |
| 替换内容 | 与 `StardewValley.patched.dll` 的 SHA-256 `b46b8f33…` 一致 |
| 结构不变 | `entry_count=58`、`total_count=65`、`gap=2600`、`version=1` 全部保持 |
| 体积 | blob 5,588,326 → 6,183,574 字节（+0.59 MB，因自研 LZ4 压缩率弱于原厂 LZ4 HC） |

产物：`.tmp/phase0/out/assemblies.patched.blob`。

**尚未做**：把 blob 放回 `app/src/main/apkroot/assemblies/`、重打包、装机。这一步会改动仓库文件
（可用 `git checkout -- app/src/main/apkroot/assemblies/assemblies.blob` 还原），放在下一轮执行。

## 3.8 首次装机实验：**失败**（阻塞性，但信息量很大）

已执行完整链路：替换 blob → `gradlew assembleDebug`（371.51 MiB / `6994a32c…`）→ `adb install` → 启动。

结果：进程在托管代码执行前死亡。

```
F monodroid-assembly: Compressed assembly '<assembly_store>' is larger than when the application was built
                      (expected at most 13824, got 9261056). Assemblies don't grow just like that!
I ActivityManager: Process com.chucklefish.stardewvalley (pid 9377) has died: fg TOP
```

**结论**：`libmonodroid` 校验"存储内程序集大小 == 构建期记录值"，改字节而不改元数据会被直接拒绝。
详见 `runtime-feasibility.md` §4.1.1。

**同时得到的正面结论**：APK 结构、签名、安装、进程启动、`JNI_OnLoad`、ARM 翻译层初始化全部正常，
失败点精确落在程序集存储校验 —— 说明 apktool 流水线本身没被破坏。

**环境已恢复**：用原 blob 重建并重装，冷启动 `Status: ok / COLD`，进程存活（pid 14865），
`FATAL` / `monodroid-assembly` 均无输出；仓库 blob 已还原为 `c8ee504e…`，`git status` 干净。

**下一步两个候选实验**（均未验证）：

- **S1**：解出 `libxamarin-app.so` 的 `bundled_assemblies` 数组结构，把 `StardewValley` 的 size 改成新值；
- **S4**：改为"新增条目"（不动已有条目大小），只同步 gap hash 表。

先做 S1（改动面小、失败仍可回滚），失败再试 S4。

## 3.9 S1 前置探测 + 等长实验（都未打通）

| 实验 | 做法 | 结果 |
| --- | --- | --- |
| 定位尺寸表 | 在 `libxamarin-app.so` 里搜 9,280,000 / 13,824 | 命中：`9,280,000` 在文件偏移 `0xd178`，`13,824` 在 `0xd108`；相邻记录步长 16 字节、夹 12 个零字节 |
| 全表匹配 | 用 58 个已知尺寸按 manifest 顺序搜 65 条表 | **无匹配**（表里还含架构 blob 的 7 个条目，且顺序不是 manifest 序） |
| 等长假设 | 把补丁 DLL 零填充到原始长度 9,280,000 后重替换 | **仍然失败**，且 `expected` 恒为 13,824（`got` 随我们变化） |

结论：**校验不是"该条目大小 == 构建期该条目大小"**；`13,824` 不随我们改动变化，
说明它来自一张按其它顺序排列的表，或一条尚未理解的索引路径。

→ 盲试到此为止。下一轮必须**反编译这条校验**：定位字符串
`"is larger than when the application was built"` 的 xref，读出它实际索引的数组与下标。
本机未装 Ghidra，可用 `rizin`（REA 已提供 `execute_rizin_command`）或 `objdump` 完成。

设备已恢复：原 blob 重建重装，`COLD` 启动成功，pid 20437，无 `FATAL`/`monodroid-assembly`。

## 3.10 打通校验：注入代码**已在游戏进程内执行**

| 步骤 | 结果 |
| --- | --- |
| capstone 手写 ADRP/ADD xref | 定位 `libmonodroid.so` 校验点 `0x17618` |
| 反汇编读出语义 | `expected = sizeTable[XALZ.descriptor_index]`，16 字节/条 |
| 全表验证 | 4 个抽样条目（含 entry 10→desc 7→9,280,000）全部吻合 |
| 修 `Tools/AssemblyStore` | 重建时保留原 `descriptor_index` |
| 重新装机 | **尺寸校验通过**（不再有 `monodroid-assembly` fatal） |
| 新失败点 | 托管异常，但**发生在我们注入的调用里** |

崩溃栈（关键部分）：

```
android.runtime.JavaProxyThrowable: [System.MissingMethodException]:
    Method not found: System.Reflection.Assembly System.Reflection.Assembly.Load(byte[])
  at StardewValley.MainActivity.OnCreatePartTwo
  at StardewValley.MainActivity.CheckAppPermissions
  at StardewValley.MainActivity.OnCreate
```

【观测】三件事同时成立：

1. 我们改过的 `StardewValley.dll` **被加载并执行**了（栈帧就是 `OnCreatePartTwo`）；
2. 注入的 `Bootstrap.Start()` 进入了执行（否则不会有 `Assembly.Load(byte[])` 的解析失败）；
3. 失败原因是 **MemberRef 的作用域写错**：Cecil 用桌面 `System.Private.CoreLib`
   （带 `PublicKeyToken=7cec85d7bea7798e`）做 scope，而 Android 的 CoreLib 没有该强名称
   → 运行时按该标识找不到方法 → `MissingMethodException`。
   且该异常在 **JIT 解析阶段**抛出，落在方法自身的 `try/catch` 之外，所以没被吞掉。

**修复方向（下一轮）**：把 BCL 引用改成模块自身的 corelib 引用
（`module.TypeSystem.CoreLibrary`，即 `System.Runtime` 门面）并手工构造 `TypeReference`/`MethodReference`，
避免跨平台强名称不匹配。改完后重跑同一实验即可验证 E1 的第一半。

> 这一轮把"注入不可行"变成了"注入已执行，只差一个引用作用域 bug"。这是 Phase 0 至今最大的进展。

## 3.11 引用作用域两次尝试：仍未解析成功

| 版本 | 做法 | 结果 |
| --- | --- | --- |
| v2 | `RetargetToModuleCore` 把 CoreLib/mscorlib/System.Runtime 作用域改成 `module.TypeSystem.CoreLibrary` | 仍 `MissingMethodException: Assembly.Load(byte[])`；且 AssemblyRef 里仍新增了 CoreLib |
| v3 | 显式选取模块已有的 `System.Runtime, 8.0.0.0, b03f5f7f11d50a3a` 作 scope | **同样失败**，异常一字不差 |

本轮还查清了两件事：

1. Android 的 `System.Private.CoreLib` **确实是强名称**（160 字节公钥，`Version=8.0.0.0`），
   所以"Cecil 用了错误的 token"这个假设**不成立**——失败另有原因；
2. `Mods/` 路径本身没问题：`ModFramework.Runtime.dll`、`MonoRuntimeProbe.dll`、
   `MonoRuntimeProbe.Dependency.dll` 已成功推到
   `/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods/`，可读可列。

**下一轮的第一个实验（最小化定位）**：把 `Bootstrap.Start()` 临时换成只调
`File.AppendAllText(固定路径, "bootstrap ran")`。

- 若文件出现 → 跨程序集 BCL 调用**可用**，问题只出在 `Assembly.Load` 这个重载上
  → 改用 `AppDomain.CurrentDomain.Load(byte[])` 或 `Assembly.LoadFrom(string)`；
- 若文件不出现 → 说明我们追加的 TypeDef 里的 MemberRef 整体无法解析
  （可能是新类型没有对应 AOT/元数据注册），此时改走"P/Invoke 到 libmonodroid"或
  "把 Loader 直接编进游戏程序集"的路线。

这个实验只写一行 IL，成本低但能一刀切开两种完全不同的原因。

## 3.12 最小化实验：结果不干净，但排掉了两个变量

| 变体 | 内容 | 观察 |
| --- | --- | --- |
| A | 只调 `File.AppendAllText(<Mods>/bootstrap-marker.txt)` | 无崩溃、无 marker |
| A' | 修 `Mods` 目录权限（root 建的 `drwxrws---` → `0777`）后重跑 | 仍无 marker |
| B | 改写 `/data/data/<pkg>/files/bootstrap-marker.txt` + **无条件 `throw new Exception("bootstrap-ran")`** | 仍无 marker；**crash buffer 为空**，但启动瞬间 `houdini` 连续打印 9 帧 `libmonosgen` 回溯 |

判断：

1. 变体 B 里我故意在 try/catch **之外**抛异常——若 `Start()` 被调用，进程必然带着
   `bootstrap-ran` 死掉。crash buffer 为空、进程却活着 → **这一版 `Start()` 很可能没被调用**；
2. 但同一启动瞬间 `houdini`（ARM 翻译层）连续打印 `libmonosgen-2.0.so` 的 9 帧回溯，
   说明运行时确实在那一刻有异常/信号活动，只是**没有落到 Android 的 crash buffer 里**；
3. 与 v1–v3 矛盾：那几版同样是 IL_0000 插入调用，却**确实执行到了** `Bootstrap.Start()`
   （`MissingMethodException` 的栈帧就在 `OnCreatePartTwo`）。

所以现在有两种互相冲突的观察，**不能二选一**。差别的来源还没定位，可能是：
AOT 镜像是否被判失效（v1–v3 的 MVID 与这两版不同）、或 `OnCreatePartTwo` 是否真的被走到
（`am start` 一直返回 `Status: timeout`，游戏可能停在别处）。

**下一轮不要继续盲改 IL**，先补两件观测能力：

1. `logcat -b all`（不只 crash buffer）+ `-s houdini:V MonoDroid:V monodroid:V DOTNET:V` 全量抓一次启动；
2. 把探针插到**更早、必定执行**的位置 —— `StardewValley.MainActivity.OnCreate` 的第一条指令
   （而不是 `OnCreatePartTwo`），并同样用"无条件抛异常"确认调用是否发生；
3. 若确认调用发生但 marker 不出现，则把 `File.AppendAllText` 换成
   `Assembly.GetExecutingAssembly().Location` 之类的**只读**调用 + 抛异常携带返回值，
   用异常消息把结果带出来（不依赖文件写入）。

## 3.13 第 6 轮矛盾的根因：MVID 赋值被注释掉了

追 `new mvid = 旧值` 这个现象时发现：`module.Mvid = Guid.NewGuid();` 那一行
在之前某次 PowerShell 往返中被并进了中文注释里，**实际上从未执行**。

后果链：

```
MVID 未变 → Mono 认为程序集没变 → 使用旧的 libaot-StardewValley.dll.so
         → OnCreatePartTwo 的原生码照常运行 → 我们插入的 IL 根本不执行
         → 没有 marker、没有崩溃（第 6 轮看到的"异常安静"）
```

而 v1–v3 那次是有新 MVID 的（`e8870106…`/`f298991f…`/`d8c82eed…`），所以走了 JIT、
IL 生效、才崩在 `Assembly.Load`。**第 6 轮的两份冲突证据因此统一**：差别不在注入点，
而在 MVID 是否真的变了。

已修复并把注释改成 ASCII；同时修掉 `verify` 里写死 `OnCreatePartTwo` 的问题，
并新增 `--hook-method` 选项。

### 确认：注入确实在 `MainActivity.OnCreate` 的 IL_0000 执行

用 `--mode append --hook-method OnCreate`（方法体：写 marker → **无条件抛异常**）部署后：

```
E AndroidRuntime: android.runtime.JavaProxyThrowable: [System.Exception]: bootstrap-ran
I am_crash: [23245,0,com.chucklefish.stardewvalley,…,bootstrap-ran,…]
```

在 `logcat -b all` 里抓到 `bootstrap-ran`。**这是 Phase 0 第一次拿到"注入代码在真机运行时执行"
的正向证据**（此前只有"崩在我们代码里"这种间接证据）。

### 仍未完成

- 全量 Loader（`--mode full`）那一版没有拿到 `loader.log`，且设备上抓到的崩溃仍是上一版的
  `bootstrap-ran` —— **无法确认那次安装是否真的换了 APK**。所以 E1 仍不成立。
- 下一轮先解决"如何确认设备上跑的是哪个 APK"（比对 APK 内 `assemblies.blob` 的 SHA-256、
  或读 `dumpsys package` 的 lastUpdateTime / versionCode），再重跑 full 模式。

## 3.14 部署判据 + 又一个"注释吃掉代码"

### 新增部署判据（解决"装没装上"的自我欺骗）

`Tools/deploy-apk.ps1`：

1. 换 blob → `gradlew assembleDebug` → **立刻把仓库 blob 还原**（`finally`）；
2. `adb uninstall` + `install --no-incremental`；
3. 用 `su -c sha256sum $(pm path …)` 算**设备上已安装 APK** 的哈希，与本地产物比对；
4. 不一致直接抛错退出。

实测通过：`local apk = device apk = 595f2e61…`，证据在输出里。以后所有装机实验都走这个脚本。

### 又一次被 PowerShell 破坏：`if (minimal)` 被并进注释

`// 最小化模式：…` 这行中文注释在往返中把下一行 `if (minimal)` 吞了，导致 **`--mode full`
实际上一直在跑诊断分支**（只 AppendAllText + 抛 `bootstrap-ran`）。

这解释了第 7–8 轮反复出现的怪现象：
- "full 模式没有 loader.log" —— 因为根本没跑 full 体；
- "全量 Loader 也崩在 `bootstrap-ran`" —— 那个字符串只存在于诊断分支。

已修（注释改 ASCII）。修完后 `--mode full` 的方法体是 **26 指令 / 104 字节**，
且产物里不再含 `bootstrap-ran` 字面量。

### 现在的位置

用**带身份证明**的脚本部署真正的 full 版：

- 游戏**不再崩溃**（早期 `MissingMethodException: Assembly.Load(byte[])` 没有复现，
  可能是把作用域改成 `System.Runtime` 之后解析成功了）；
- 但 **`loader.log` 仍未出现** —— 说明 `Bootstrap.Start()` 在 `File.Exists` 之后、
  `Loader.Start()` 之前就失败了，且被自己的 `catch` 吞掉。

### 下一轮（一步到位）

已给 `ModInjector` 加 `--no-catch`。下一轮用它 + **deploy-apk.ps1** 部署一次：
不吞异常，失败会以 `JavaProxyThrowable` 的形式进 logcat，能直接看到是
`File.ReadAllBytes`、`Assembly.Load` 还是 `GetType` 出的问题。

> 注意：本轮最后一次 `--no-catch` 尝试用的是 `install -r`（不是脚本），身份未证明，
> 所以那次"没崩也没日志"的结果**不能采信**，下一轮必须走脚本重做。

## 3.15 `--no-catch` + 身份证明：拿到确定的失败原因

用 `deploy-apk.ps1 -NoCatch` 部署（`local apk == device apk == 19c69ea3…`），得到确定的异常：

```
[System.MissingMethodException]: Method not found: System.Reflection.Assembly System.Reflection.Assembly.Load(byte[])
```

三种作用域（桌面 CoreLib / `TypeSystem.CoreLibrary` / 显式 `System.Runtime`）结果完全一致，
且 Android 的 CoreLib 本身是强名称 → **"引用写错"这个方向可以关闭了**。

判断：这是 **ILLink 裁剪**导致的成员缺失（游戏从不调用 `Assembly.Load(byte[])`）。
→ 架构必须改为 **P/Invoke 调 Mono 的 C API** 加载程序集，详见
`runtime-feasibility.md` §6.5 的 L1/L2/L3 三方案。

下一轮：先用最简单的 `File.Exists` 探清"BCL 还剩多少可用面"，再实现 L1。

## 3.16 尝试 `Assembly.LoadFrom` 与第三个工具 bug

想用一条更短的路线验证"是否还有别的程序集加载 API 存活"：把 `Assembly.Load(byte[])` 换成
`Assembly.LoadFrom(string)`（`--loader-api loadfrom`）。为此加了 `--no-catch` 让异常不被吞。

结果收到：

```
[System.InvalidProgramException]: Invalid IL code in ModFramework.Bootstrap:Start (): IL_005c: ret
```

**这不是运行时的限制，而是我的 IL 生成 bug**：`--no-catch` 去掉了异常处理器，
却仍然发出 `leave`；`leave` 在保护区域外是非法 IL（应当用 `br`）。改成 `br` 后依然报错，
于是改用**保留 try/catch、但 catch 里 `rethrow`** 的写法——IL 合法，异常照常暴露。

修完重新部署时，`adb uninstall` 与 `install` 之间出现竞态，install 失败，设备一度处于未安装状态；
已重新装回基线（pid 27566，blob `c8ee504e…`）。

**所以 `LoadFrom` 是否可用仍无结论。** 下一轮干净地重跑这一步即可（脚本里加 install 重试）。

> 这是本轮第二次被自己的工具绊住（前一次是 `leave`/`br`）。`ModInjector` 至今出过三个 bug：
> MVID 赋值被注释、`if (minimal)` 被注释、`leave` 用在无保护区。共同点都是**生成的 IL/代码没有被校验**。
> 下一轮先给注入器加一个"打完后重新读回、校验指令数与分支/栈合法性"的自检，再继续实验。

## 3.17 自检落地，并且立刻抓到一个真 bug

新增 `Tools/ModInjector/IlChecks.cs`：打包后**重新读回**产物并校验

1. `ModFramework.Bootstrap` 存在、`Start()` 存在、hook 方法首条指令是对它的 `call`；
2. MVID 确实变了；
3. 所有分支目标都在方法内；
4. **没有 handler 时不允许出现 `leave`**；
5. **工作列表式栈深检查**：合并点深度必须一致，`ret` 时深度必须为 0。

第一次跑就报错：

```
- IL_0067 ret: inconsistent stack depth from IL_0030 brfalse (0 vs 1)
```

顺藤摸瓜发现根因：`getType` 用的是 `Type.GetType(string)`（**静态**），却按 `callvirt` 实例调用，
于是 `Assembly` 一直留在求值栈上，最后 `ret` 时栈深为 1。改成 `Assembly.GetType(string)`
（实例方法）后，三种模式全部 `RESULT: OK`。

**这个 bug 原来会在设备上以 `InvalidProgramException` 出现**——和上一轮那个 `leave` 一样。
自检把这类问题挡在构建阶段了。

## 3.18 `Assembly.LoadFrom` 的初步结果

用校验过的构建（`hash_match=True`）部署 `--loader-api loadfrom --no-catch`（catch 里 `rethrow`）：

- **没有任何异常**（对比 `Assembly.Load(byte[])` 的 `MissingMethodException`）
  → 说明 `Assembly.LoadFrom(string)` **很可能存在**；
- 但 `loader.log` 仍未生成 → 说明 `Start()` 是**静默返回**的：
  `File.Exists` 为假、或 `asm.GetType("ModFramework.Runtime.Loader")` 返回 null、
  或 `GetMethod("Start")` 返回 null，三者都会走 `brfalse ret`。

下一轮把这三处 `brfalse ret` 换成**带阶段号的 `throw`**（`stage-1:file` / `stage-2:type` /
`stage-3:method`），一次部署就能定位到底卡在哪一步。这是纯 IL 改动，`IlChecks` 会继续兜底。

## 3.19 **加载链路已打通**：stage-1..4 全过，Invoke 已到达

把 Guard 改成"带阶段号的 `brtrue`/`throw`"后（`EmitThrowUnlessTrue`），第一次部署就得到：

```
stage-1:file-missing
```

关键发现：`File.Exists("/sdcard/Android/data/<pkg>/files/Mods/ModFramework.Runtime.dll")`
在**应用进程内返回 false**——尽管文件确实存在、属主 uid 已 chown 给应用。
判断：`su` 创建的外部目录在应用进程视角下不可见（SELinux/FUSE 层对 `Android/data` 的限制）。

把 `--mods-dir` 改到应用私有目录 `/data/data/<pkg>/files/Mods` 后：

```
[System.Reflection.TargetInvocationException]: Arg_TargetInvocationException
  at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs
  at System.Reflection.RuntimeMethodInfo.Invoke
  at System.Reflection.MethodBase.Invoke
  at ModFramework.Bootstrap.Start
```

【观测】**这是 E1 的实质进展**：`Assembly.LoadFrom` → `Assembly.GetType("ModFramework.Runtime.Loader")`
→ `GetMethod("Start")` → `Invoke` 全部成功执行。异常来自 `Loader.Start()` 自身。

待办：`Loader.Start()` 的第一句仍写死外部路径常量（`ModsDirectory`），应改为从环境/参数获取；
以及确认它内部到底抛了什么（`TargetInvocationException` 的 inner 未出现在 crash buffer 里，
需要用 `--no-catch` + 打点或改成"把 inner.Message 拼进外层异常"的方式取出来）。

### 对存储设计的实证

`Android/data/<pkg>` 目录**即使 chown 给应用 uid，由 root/shell 创建后应用仍可能读不到**
（`File.Exists` 返回 false 而不是抛异常，极易误判）。这条必须写进 `android-storage` 设计：
模组目录应由**应用自己创建**，或改放应用私有目录。

## 3.20 BCL 存活面：实测清单（本轮核心产出）

把 `Loader` 拆成"每个 BCL 依赖一个独立方法"，失败由调用点捕获并拼进报告，
再由 Bootstrap 抛出（`return "loader-report: " + report`）。一次部署得到：

```
locate       = /data/data/<pkg>/files/Mods/ModFramework.Runtime.dll      OK
mods-exists  = yes                                                       OK (Directory.Exists)
list-dlls    != MissingMethodException: Directory.GetFiles(string,string)  ← 被裁剪
runtime-info != TypeLoadException: could not resolve 'System.Runtime.InteropServices.Architecture'
                                                                         ← facade 缺类型
write-log    = ok                                                        OK (File.WriteAllText!)
probe-run    != TargetInvocationException                                 ← Probe 内部抛错
```

【观测】由此确立两条**架构级约束**：

### 约束 A：运行时对 BCL 做了**选择性裁剪**

同一命名空间里 `Directory.Exists` 在、`Directory.GetFiles` 没了；`File.Exists` 在、
`File.WriteAllText` 也在。也就是**不能用"这个 API 属于 BCL"来假设它存在**。

→ SDK 必须提供"已证实可用"的 API 白名单，并在构建期用 ModValidator 检查模组引用了哪些
`System.*` 成员，对白名单外的成员发出警告。

### 约束 B：模组 DLL 必须针对 **net8.0-android** 参考程序集编译

> 更正（§3.23）：当时的解释"Android facade 缺类型"不准确——真正原因是**我们用桌面 net8.0
> 参考程序集编译**，生成的 TypeRef 指向桌面 facade。约束本身仍然成立，且已用
> `-p:AndroidRefsDir` 落实。详见 §3.23 与 `bcl-surface.md` §3。

`TypeLoadException` 说得很清楚：`System.Runtime`（facade）里没有
`System.Runtime.InteropServices.Architecture`，而我们是在**桌面 net8.0** 下编译的。
桌面参考程序集会生成指向桌面 facade 的 TypeRef，Android 的 facade 不认。

→ `ModSdk` 的 csproj 必须用 `net8.0-android`（或引用 Android 的参考程序集包），
不能用 `net8.0`。这条如果不做，**任何模组都会在类型解析阶段炸**。

### 附带确认

`loader.log` **成功写出**（130 字节，属主 `u0_a141`）——文件写入在私有目录是可用的。

### 下一步

1. `probe-run` 的 `TargetInvocationException`：`MonoRuntimeProbe` 用了 `System.Text.Json`，
   而 65 个程序集里**没有** System.Text.Json → 探针改为手写文本/JSON 输出，去掉该依赖；
2. `ModSdk` 与 `ModFramework.Runtime` 全部改成 `net8.0-android`；
3. 把这份 BCL 清单整理成 `docs/audit/bcl-surface.md` 交给 SDK 使用。

## 3.21 探针瘦身后仍被"整体 JIT"拖死

去掉了 `System.Text.Json`（APK 里根本没有这个程序集），把每个用例改成独立 lambda，
报告改成纯文本 `probe.txt`。重新部署后：

```
loader-report: locate=…; mods-exists=yes; list-dlls!=…GetFiles…;
               runtime-info!=…Architecture…; write-log=ok;
               probe-run!=TargetInvocationException; 
```

`loader.log` 写得出来，但 `Probe.Run` 仍然整体失败、`probe.txt` 不存在。

原因：**`Run` 方法自己的方法体里**还引用着可疑 API（`AppContext.BaseDirectory`、
`Directory.CreateDirectory`、`new UTF8Encoding(false)`、`DateTimeOffset.Now`）。
只要其中任何一个被裁剪，**整个方法的 JIT 就失败**，方法内的 try/catch 拦不住——
和上一轮 `Loader.Start` 是同一个坑。

### 结论（方法层面）

在这个运行时里，**"一个方法引用一个坏 API"会连坐整段逻辑**。所以：
- 每个 BCL 依赖必须落在**独立方法**里，且调用方负责 catch；
- 连"写报告"这种收尾逻辑也要单独成方法，否则前面任一用例的坏引用会把报告一起带走。

### 下一轮

1. 把 `Probe.Run` 的收尾（建目录、写文件）抽成 `WriteReport(string)` 独立方法；
   `Run` 里只做字符串拼接和调用；
2. 逐个隔离 `AppContext.BaseDirectory` / `Directory.CreateDirectory` / `UTF8Encoding` /
   `DateTimeOffset.Now`，用同样的 `Probe("name", fn)` 模式报告存活情况；
3. 拿到 `probe.txt` 后，E1 才算真正通过。

## 3.22 **E1/E2 在设备上跑通**（本轮核心结果）

把 `Run` 方法体里每个 BCL 成员都挪进独立方法、由 `Try(Func<string>)` 包裹之后，
设备上终于产出完整报告（`.tmp/phase0/evidence/probe-device.txt`）：

```
MonoRuntimeProbe report
time    : 2026-11-04T22:35:42.7660767+08:00
basedir : !TypeLoadException: 找不到 System.AppContext（System.Runtime facade 缺失）
FAIL T00 dyn-code       : TypeLoadException: 找不到 System.Runtime.CompilerServices.RuntimeFeature
PASS T01 static         : static:probe
PASS T02 instance       : instance:probe#1
PASS T03 generic        : generic<Int32>:42 | generic<String>:text | pair<Int32,String>:1|x
PASS T04 event          : hits=1 handled=True
PASS T05 reflection     : field=41 invoke=static:reflection
PASS T06 ext-assembly   : dependency-assembly-alive: MonoRuntimeProbe.Dependency v1.0.0.0
PASS T07 game-types     : Game1 576 methods/532 fields
FAIL T08 load-bytes     : MissingMethodException: Assembly.Load(byte[])
FAIL T09 dynamic-method : TypeLoadException: 找不到 System.Reflection.Module
FAIL T10 dyn-assembly   : FileNotFoundException: System.Reflection.Emit, Version=8.0.0.0
```

### 结论

| 能力 | 结果 |
| --- | --- |
| 从磁盘加载外部程序集 | ✅ `Assembly.LoadFrom` 可用（Bootstrap 与 Probe 都靠它加载） |
| 执行外部程序集的 IL | ✅ 静态/实例/泛型/事件/反射全部 PASS |
| 跨程序集调用 | ✅ T06（探针 → 依赖 DLL） |
| 反射访问游戏类型 | ✅ **T07 读到 `Game1` 576 方法 / 532 字段** |
| 写文件 | ✅ `loader.log`、`probe.txt` 均落盘 |
| `Assembly.Load(byte[])` | ❌ 被裁剪 |
| **Reflection.Emit** | ❌ `DynamicMethod` 与 `AssemblyBuilder` 都不可用（缺 `System.Reflection.Emit` 程序集） |
| `AppContext` / `RuntimeFeature` / `Architecture` | ❌ facade 缺失 |

### 门控判定

按 ADR-0001 §6，E1（动态加载 + IL 执行）与 E2（反射访问游戏类型）**在设备上通过**。
但该设备是 **MuMu 模拟器（x86_64 + ARM 翻译）**，按任务书 §2.3 不得替代真机结论。

→ 记为 **E1/E2 = 通过（模拟器）/ Awaiting real-device confirmation**，**仍不进入 Phase 1**。

### 对 Harmony 的影响（重要）

`Reflection.Emit` 完全不可用（T09/T10 两个独立失败）。Harmony/MonoMod 的
`MethodReplacement`、Transpiler、以及 `DynamicMethod` 生成 trampoline 全部依赖它。

→ **Route D（Harmony）在这套运行时上基本不可行**，除非改用纯原生 detour（MonoMod 的
`NativeDetour` 不依赖 Reflection.Emit，但需要可写代码页与 AOT 兼容性验证）。
这会把 Phase 5 的验收标准从"Prefix/Postfix 可用"改为"原生 detour 可行性"，
需要单独一轮实验（E5）来确认。

## 3.23 参考程序集改造：把运行期风险前移到编译期（本轮核心）

上一轮那些 `TypeLoadException`（`AppContext`/`RuntimeInformation`/`RuntimeFeature`）
**不是运行时缺功能，而是我们用桌面 net8.0 参考程序集编译的产物**。证据是：换成
Android 自己的程序集当参考后，同样的引用在**编译期**就报错——

```
Loader.cs(67): error CS0117: "Directory" 未包含 "GetFiles" 的定义
Loader.cs(77): error CS0234: 命名空间 System.Runtime.InteropServices 中不存在 RuntimeInformation
ProbeEntry.cs(133): error CS1503: 参数 1: 无法从 "byte[]" 转换为 "string"   ← Assembly.Load(byte[]) 的编译期证据
ProbeEntry.cs(153): error CS7036: ModuleBuilder.DefineType 参数不匹配       ← Emit 面不完整
```

实现：`Mod/Directory.Build.props` 在传入 `-p:AndroidRefsDir=<dir>` 时
关闭隐式框架引用 + `NoStdLib`，改为引用该目录下全部 65 个游戏自带程序集：

```powershell
dotnet build Mod/ModFramework.Runtime -p:AndroidRefsDir=.tmp/phase0/refs
```

### 结果

用 Android 参考程序集重新编译 `ModFramework.Runtime` 与 `MonoRuntimeProbe`（去掉三个
已确认不可用的用例）后在设备上重跑，**全部通过**：

```
basedir : /data/user/0/com.chucklefish.stardewvalley/files
PASS T00 dyn-code       : supported=True compiled=True
PASS T01 static         : static:probe
PASS T02 instance       : instance:probe#1
PASS T03 generic        : generic<Int32>:42 | generic<String>:text | pair<Int32,String>:1|x
PASS T04 event          : hits=1 handled=True
PASS T05 reflection     : field=41 invoke=static:reflection
PASS T06 ext-assembly   : dependency-assembly-alive
PASS T07 game-types     : Game1 576 methods/532 fields
```

关键更正：`RuntimeFeature.IsDynamicCodeSupported = True`、`IsDynamicCodeCompiled = True`
——**运行时确实支持动态代码**。此前那条 `TypeLoadException` 是编译期引用方式造成的假象，
不是运行时的限制。这条如果没纠正，会误导整个 SDK 的设计。

证据：`.tmp/phase0/evidence/probe-device-androidrefs.txt`。

### 结论

- ✅ 动态加载 + IL 执行 + 反射访问游戏类型 + JIT 全部实测可用；
- ✅ "哪些 BCL 可用"现在由**编译器**回答，不再靠设备上试错；
- ❌ `Reflection.Emit` 确实不可用（Android 参考程序集里就没有完整 Emit 面）→ Harmony 仍不可行。

## 3.24 E5（第一半）：原生 detour 的**前提成立**

Harmony 唯一的替代路线是原生 detour。它的两个前提是：
① 能拿到托管方法的原生代码地址；② 能把这些页改成可写。

新增探针 T11 直接验证（**不写入任何字节**，只改权限再立刻还原）：

```
PASS T11 native-detour : fn=0x7db137ef2080 page=0x7db137ef2000 rwx_rc=0 restore_rc=0
```

| 前提 | 结果 | 说明 |
| --- | --- | --- |
| 取代码地址 | ✅ | `MethodHandle.Value` 可用；**`GetFunctionPointer()` 在 Android 参考面里不存在**（编译期 CS1061） |
| 页改 RWX | ✅ | `mprotect(page, 4096, PROT_READ\|WRITE\|EXEC)` 返回 0 |
| 还原 RX | ✅ | 再次 `mprotect(..., PROT_READ\|EXEC)` 返回 0 |

**结论：原生 detour 在内存权限层面可行**，Harmony 的替代路线没有被堵死。
但这只是"能改"，还不等于"改得对"——真正的 detour 还需要：

- 正确的跳转指令序列（ARM64 是 4 字节定长指令，跳转要用 `b`/`adrp+br` 或 16 字节的 `ldr x16; br x16` 桩）；
- trampoline（复制被覆盖的指令 + 跳回）；
- 跨 JIT/AOT 边界的异常处理。

这些属于 E5 的第二半，**未做**。

### 一个附带发现

`RuntimeMethodHandle.GetFunctionPointer()` 在桌面可用、在 Android 参考面不可用——
这是"桌面 net8.0 参考程序集不能用于本平台"的又一个例证（与 §3.23 同类）。

## 3.25 E5（第二半）：朴素 detour **不可行**（进程崩溃）

按"取地址 → mprotect RWX → 写 16 字节 ARM64 跳转桩（`ldr x16,#8; br x16; .quad 目标`）→ 还原"
对**本程序集内的私有方法**做了一次真实 detour（探针 T12）。

结果：`loader.log` 写出来了，但 **`probe-output/` 目录根本没创建**，
且没有 Java 层异常——典型的**原生层崩溃**（进程直接死，托管 catch 拦不住）。

### 判断

这次失败不能简单归因于"原生 detour 不行"，它至少暴露三个必须解决的问题：

| 问题 | 说明 |
| --- | --- |
| **指令缓存未刷新** | ARM64 要求 `dc cvau` / `ic ivau` + 屏障；托管侧没有可移植入口（`__clear_cache` 不在 libc 导出里）。裸写内存后 CPU 可能仍执行旧指令 |
| **翻译层缓存** | 本机是 x86_64 + houdini 翻译 ARM64；即使内存改了，翻译块缓存也可能不失效 —— **这条是模拟器特有干扰**，真机没有 |
| **方法可能被内联** | `Target` 若被内联到调用方，改它的入口不影响已内联的调用点，探测就得不出结论 |

### 下一步（E5 第三半，若要做）

1. 用 `[MethodImpl(MethodImplOptions.NoInlining)]` 排除内联干扰；
2. 实现 ARM64 缓存刷新：`syscall(__ARM_NR_cacheflush)` 或 P/Invoke 到 `libmonosgen` 内部的
   `mono_arch_flush_icache`（若导出）；
3. 先把 detour 做在**一个不会被再次调用的方法**上，确认桩本身正确，再验证"改完能正确执行"。

**结论（保守）**：如 §3.24 所述，内存权限层面可行；但"写跳转就能生效"**没有被证实**，
反而以崩溃告终。Phase 5 若依赖方法补丁，必须先解决缓存刷新问题，否则不能承诺。

## 3.26 E5 结论：**当前工具链下不可行**（缺少刷缓存手段）

T12 崩溃后，按计划找"指令缓存刷新"的可用入口，结果是没有：

| 查找项 | 结果 |
| --- | --- |
| `libmonosgen-2.0.so` 导出符号含 `icache` / `clear_cache` / `cacheflush` | **0 个** |
| `libmonodroid.so` 同上 | **0 个** |
| `libSystem.Native.so` 同上 | **0 个** |
| Android NDK（可自建 `__builtin___clear_cache` 小库） | **未安装**（`$ANDROID_HOME/ndk` 不存在，也没有 cmake） |
| ARM 私有 syscall `__ARM_NR_cacheflush` | ARM32 专有；**AArch64 内核不提供** |

【观测】AArch64 修改代码后必须由用户态执行 `dc cvau` / `ic ivau` + 屏障，
而托管代码无法发出这些指令，公开 API 也没有暴露。

### 判定

**E5（方法补丁）在当前工具链下不可行。** 依据：

1. `Reflection.Emit` 缺失 → Harmony/MonoMod 的托管侧 detour 无法实现（§3.22）；
2. 原生 detour 的内存前提成立（T11），但缺少缓存刷新 → 写入后崩溃（T12）；
3. 补齐缓存刷新需要自建原生库 → **需要引入 NDK 与新的构建步骤**（当前工程没有）。

### 若要继续，只有一条路（成本已知）

| 步骤 | 内容 | 代价 |
| --- | --- | --- |
| 1 | 安装 Android NDK，写一个 ~20 行的 `libmodflush.so`（`__builtin___clear_cache`） | 新工具链依赖（约 1 GB） |
| 2 | 随 APK 打包该库（`jniLibs/arm64-v8a/`），由 Bootstrap 加载 | 每 ABI 一个 .so |
| 3 | 重做 T12：NoInlining + 刷缓存 + trampoline | 仍需在真机验证 |

这条路径**不在 Phase 0 的授权范围内**（要改工具链），应作为 Phase 5 的前置决策提交给项目方。

### 对 Phase 5 的含义

Phase 5 的目标应从"Harmony 式方法补丁"改为**游戏自带扩展点**
（`StardewValley.ModHooks`、`GameRunner`/`SGame` 覆写、`ContentCoordinator`）——
这也正是桌面 SMAPI 实际采用的主路径（见 ADR-0001 §7.1）。方法补丁应标为"需要 NDK，另行决策"。

## 2. 工程结构审计摘要

- 构建：**不是** Android Studio/AGP 工程，而是 apktool 组装工程。
  `app/build.gradle.kts` 只注册一个 `JavaExec` 任务（`com.kaori.svpatch.MainKt`），
  流程 = 组装 `app/src/main` → `.tmp/build/decoded` → 打补丁 → `apktool b` → `zipalign -p 4` → `apksigner`。
- 内容模块即 `app/src/main`：`assets/`（游戏资源，≥3000 文件）、`jniLibs/arm64-v8a/`（75 个 .so）、
  `prebuilt/classes{,.2}.dex`、`apkroot/assemblies/`（两个 blob + manifest + rc.bin）。
- 大文件分片：`music.xwb` 切成 `.part000..002`，构建时拼接并校验 SHA-256（`LargeAssets.kt`）。
- 补丁机制：`ApkPatch` 接口 + `Main.kt` 的 `patches` 列表；当前只有 `LaunchEntryPatch`（幂等校验 LAUNCHER）。
- 版本绑定：1.6.15.0 / 239，`apktool.yml` 与 `AppSource.verify()` 双重校验。

## 3. 未完成（阻塞项）

| 项 | 阻塞原因 | 解除条件 |
| --- | --- | --- |
| E1–E3 动态加载 / 反射 / 事件 | 需要把 Probe + Bootstrap 注入 APK 并装机 | ① 有真机；② 实现 Bootstrap 注入工具 |
| E4 去 AOT 回退 | 同上 + 需要重打包 | 同上 |
| E5 Harmony | 需要 `0Harmony.dll` + 注入 | 同上 |
| E6 存储通道 | 需要第二台设备/Android 14 真机 | 真机 |
| 注入工具（Cecil patch + blob 重写） | 尚未实现（Phase 1 的工作） | — |

**当前唯一设备**：MuMu 12 模拟器（Android 12 / x86_64 + ARM 翻译）。
按任务书 §2.3，**不得**用模拟器结果替代真机结论。因此 E1–E6 全部标记 `Awaiting Device Validation`。

## 4. 基线可用性验证（模拟器）

| 项 | 结果 |
| --- | --- |
| 冷启动 | `am start -W` → `Status: ok`, `LaunchState: COLD`, `TotalTime: 2440 ms` |
| 进程 | 存活（pid 5761） |
| 前台窗口 | `mCurrentFocus=Window{… com.chucklefish.stardewvalley/com.chucklefish.stardewvalley.MainActivity}`（**display 2**） |
| 截图 | `screencap` 得到空白帧（10608 字节）。原因是本机 MuMu 把游戏渲染在 **display 2**，而 `screencap` 默认抓 display 0；加 `-d 2` 仍为空白 → **该环境的 `screencap` 不可作为视觉证据**，后续验证以 `dumpsys window` 焦点 + 游戏日志 + 进程状态为准 |

## 5. 门控判定

Phase 1 的准入条件（来自 ADR-0001 §6）：E1、E2 至少通过。
**当前：E1/E2 已在模拟器上通过（§3.22/§3.23），真机未复现。**
→ **不进入 Phase 1**（不实现完整 Mod Loader）。允许继续做的是：
- 注入工具与 Bootstrap 的**离线**实现与单元测试（不宣称运行时通过）；
- 文档、SDK 骨架、manifest 校验器等不依赖运行时验证的部分。

## 6. 下一轮任务（建议顺序）

1. ~~实现 `Tools/ModInjector`~~ ✅ 已完成（合并式 IL 注入，离线验证通过）；
2. **下一步**：`Tools/AssemblyStore` —— 把改好的 `StardewValley.dll` **原位替换**回 `assemblies.blob`
   （条目数与 hash 表不变，只重算偏移/大小/LZ4 负载），产物过 apktool 重打包 + 签名；
3. 装机跑 E1（明确标注"模拟器结果"），把报告与日志入库；
4. 借/接一台 ARM64 真机，复跑 E1–E3，转 ADR 为 Accepted；
5. 视 E1 结果决定是否实现 E5（Harmony）。

## 7. 环境副作用清单

| 位置 | 内容 | 处理 |
| --- | --- | --- |
| `.tmp/phase0/dotnet/` | 私有 .NET SDK 8.0.427（约 285 MB 压缩包 + 解压） | 可整体删除，不影响系统 |
| `.tmp/phase0/artifacts/` | 探针构建产物 | 可删 |
| `.tmp/phase0/extract/` | 65 个程序集（从 blob 提取） | 可删，脚本可重建 |
| `.tmp/phase0/evidence/` | 证据文本（符号、字符串、哈希、探针报告） | 建议保留到 Phase 1 结束 |
| 系统 | **无改动**（未改 PATH、未装全局 SDK、未装 Ghidra） | — |
