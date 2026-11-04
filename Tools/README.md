# Tools

构建期工具。全部离线可跑，不依赖设备。

| 工具 | 语言 | 作用 |
| --- | --- | --- |
| `ModInjector` | C# / net8.0 / Mono.Cecil | 把 Bootstrap 合并进 `StardewValley.dll`，并在指定的 `MainActivity` 方法首行插入调用 |
| `AssemblyStore` | Python 3（无第三方依赖） | 读写 `assemblies.blob`（XABA v1 + XALZ/LZ4），把改好的程序集**原位替换**回去 |
| `deploy-apk.ps1` | PowerShell（ASCII） | 一条命令完成"换 blob → 构建 → 还原 → 卸载重装 → **用设备端 sha256 证明装的是这个包**" |
| `device-precheck.ps1` | PowerShell（ASCII） | E0 只读设备预检（ABI/API/包信息/APK 哈希/残留文件），默认不改设备 |
| `tests/test_mod_pipeline.py` | Python 3 | 注入器 + 容器的离线回归（27 项，含负向用例） |
| `tests/LoaderContract/` | C# 桌面宿主 | 验证 `Loader.Start()` 返回契约（成功返回 null + 落盘报告） |

`deploy-apk.ps1` 的存在是为了杜绝一类自欺：**无法确认设备上跑的是哪个 APK**。
它会把已安装 APK 的哈希与本地产物比对，不一致直接失败。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk.ps1 -Mode full -HookMethod OnCreate
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk.ps1 -Restore   # 只还原仓库 blob
```

两者配合，把"改一行 IL"变成可装机 APK 的一半工作；另一半是 apktool 重打包 + 签名（已有流水线）。

> **Phase 0.5 布局变更**：`.tmp/phase0/` 已清空。脚本现在用 `.tmp/phase0.5/`：
> `dotnet/`（私有 SDK）、`refs/`（65 个程序集）、`out/assemblies.original.blob`（原始 blob 备份）、
> `artifacts/`（构建产物）。缺任何一个，脚本会直接报缺什么，不再静默失败。
> 默认 `-HookMethod OnCreate` 是 Phase 0 实际验证过的那一版；ADR 的首选注入点是
> `OnCreatePartTwo`，用 `-HookMethod OnCreatePartTwo` 切换。

---

## ModInjector

```powershell
# 构建（本机无全局 SDK 时用私有 SDK）
.tmp\phase0.5\dotnet\dotnet.exe build Tools\ModInjector\ModInjector.csproj -c Release `
    --artifacts-path .tmp\phase0.5\artifacts

# 注入
.tmp\phase0.5\dotnet\dotnet.exe <out>/ModInjector.dll patch <in.dll> <out.dll> `
    [--hook-method OnCreatePartTwo] [--loader-api loadfrom|load] [--refs-dir <dir>] [--mode append] [--no-catch]

# 校验（不看写入方，重新读文件；会打印实际注入的加载 API）
.tmp\phase0.5\dotnet\dotnet.exe <out>/ModInjector.dll verify <out.dll>
```

注入的 `Bootstrap.Start()` 实际 IL 语义（`Program.EmitBootstrap`，**不是**"失败就 return"）：

```csharp
// stage-1..4 各自抛带阶段号的异常；stage-5 把非 null 的 Loader 返回值当异常抛出。
try {
    if (!File.Exists(modsDir + "/ModFramework.Runtime.dll")) throw new Exception("stage-1:file-missing");
    var asm = Assembly.LoadFrom(path);                       // 默认；load(byte[]) 在本运行时不存在
    if (asm == null) throw new Exception("stage-2:assembly-null");
    var t = asm.GetType("ModFramework.Runtime.Loader");
    if (t == null) throw new Exception("stage-3:type-null");
    var m = t.GetMethod("Start");
    if (m == null) throw new Exception("stage-4:method-null");
    var report = (string)m.Invoke(null, null);
    if (report != null) throw new Exception(report);          // Loader 约定：成功返回 null
} catch (Exception) { /* 默认吞掉：模组问题不能阻止游戏启动 */ }
```

要点：

- **默认 `--loader-api loadfrom`**。`Assembly.Load(byte[])` 在本 APK 的裁剪运行时里不存在
  （JIT 阶段 `MissingMethodException`），旧默认值会让默认构建**静默不加载模组**；
  `--loader-api load` 只用于复现该历史失败。
- **失败语义**：默认吞异常（游戏照常启动，但阶段异常不进 logcat）；
  诊断用 `--no-catch`（catch 里 `rethrow`，以 `JavaProxyThrowable` 进 logcat）。
  `Loader.Start()` 自己会把报告写到 `loader-report.txt`，所以默认构建也有磁盘证据。
- 幂等：已存在 `ModFramework.Bootstrap` 时只检查/补插 hook call，不重复注入；
- 强制换新 MVID（否则 AOT 镜像与缓存会认为程序集没变）；
- 写完立即**重新读回**产物做结构校验（`IlChecks`：类型/调用/分支/leave/栈深）。
- `--refs-dir`：`<in.dll>` 不在其余 64 个程序集旁边时，Cecil 解析自定义特性会失败；
  把提取目录传进来即可。

## AssemblyStore

```powershell
python Tools/AssemblyStore/store.py list     <blob> [--manifest <m>] [--blob-id <id>]
python Tools/AssemblyStore/store.py extract  <blob> <outdir> [--manifest <m>] [--blob-id <id>]
python Tools/AssemblyStore/store.py selftest <blob>            # LZ4 编解码往返
python Tools/AssemblyStore/store.py replace  <blob> <name> <newdll> <outblob> [--manifest <m>]
python Tools/AssemblyStore/store.py verify   <blob> [--manifest <m>] [<name>=<sha256> ...]
```

设计要点（为什么要"原位替换"而不是"新增条目"）：

`assemblies.blob` = `header(20)` + `entry_count×24 条目表` + `gap(实测 total_count×40 的
hash→(store,index) 映射表)` + 负载。新增条目必须同步改 gap 表和 `libxamarin-app.so` 里的
`assembly_image_cache`；**原位替换**则条目数、gap 表、版本号全不变，只在结构内重算偏移/大小。
详见 `docs/audit/adr-0001-mod-injection.md` §D3。

**`XALZ` 头的第二个 u32 是 `descriptor_index`**（`libxamarin-app.so` 里 16 字节尺寸表的下标），
重建时必须原样保留——写成 0 会触发
`Compressed assembly ... is larger than when the application was built`（Phase 0 的坑）。

已验证（Phase 0.5 重跑，见 `docs/audit/phase0.5-evidence-index.md` §2）：

- LZ4 编码器 58/58 条目往返一致；
- 替换 `StardewValley` 后，57 个条目与原 blob **逐字节相同**，被替换条目**解压后**与补丁文件逐字节相同；
- `entry_count=58` / `total_count=65` / `gap=2600` / `version=1` / 全部 58 个 `descriptor_index` 不变；
- 58 个条目解压后都是合法 PE；`verify` 对错误哈希返回非 0。

已知取舍：自研贪心 LZ4 压缩率约 40%，原厂（LZ4 HC）约 33%；被替换的条目会变大
（`StardewValley` 9,280,000 → 9,261,568 原始字节，存储 3,114,006 → 3,709,295，blob 5,588,326 → 6,183,615）。
体积变化不影响加载（校验用的是 `descriptor_index` 对应的**上限**）。

### 架构存储（`assemblies.arm64_v8a.blob`）

同一个 manifest 里有 65 行，用 `Blob ID` 列区分：`000` = 主存储（58 条），`001` = 架构存储（7 条）。
`store.py` 以前写死 `000`，于是 `extract` 架构 blob 会把 `System.Private.CoreLib`
命名成 `_Microsoft.Android.Resource.Designer`（内容与文件名不符）。
现在按 `entry_count` 自动判定 `Blob ID`，判定不唯一时直接报错；也可用 `--blob-id 001` 显式指定。

## 离线回归

```powershell
python Tools/tests/test_mod_pipeline.py                       # 27 项
python Tools/tests/test_mod_pipeline.py --dotnet <dotnet.exe> # 指定 SDK
```

覆盖：默认加载 API 必须是 `loadfrom`（负向对照 `--loader-api load` 可复现 `load(byte[])`）、
注入后结构校验、MVID 变更、幂等重跑、容器原位替换的全部不变量、
`verify` 的负向用例、`Loader.Start()` 成功返回 null 且落盘报告、
以及"Runtime 仍能用 Android 参考程序集编译"。

## 完整链路（E1 需要，尚未在真机执行）

```text
extract blob → ModInjector patch → store.py replace → 放回 apkroot
   → gradlew assembleDebug（apktool + zipalign + apksigner）
   → adb uninstall / install → 放入 Mods/*.dll → 启动 → 取 loader.log / loader-report.txt / probe.txt
```

Phase 0.5 已经把 `.tmp/phase0.5/out/` 里的测试包准备好（`sv.full.dll`、`assemblies.full.blob`、
`mods/*.dll`），但**没有**装机、**没有**构建 APK——见 `docs/audit/phase0.5-report.md` §7。
