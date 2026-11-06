# Tools

构建期工具。全部离线可跑，不依赖设备。

| 工具 | 语言 | 作用 |
| --- | --- | --- |
| `ModInjector` | C# / net8.0 / Mono.Cecil | 把 Bootstrap 合并进 `StardewValley.dll`，并在指定的 `MainActivity` 方法首行插入调用 |
| `AssemblyStore` | Python 3（无第三方依赖） | 读写 `assemblies.blob`（XABA v1 + XALZ/LZ4），把改好的程序集**原位替换**回去 |
| `deploy-apk-safe.ps1` | PowerShell（ASCII） | **部署入口**：默认只读；安装需 `-InstallApproved`、推送需 `-PushModsApproved`；推送**递归**还原完整 Mods 目录树并逐文件校验远端 SHA-256；已存在相同内容跳过、不同内容 fail-closed（除非 `-OverwriteModsApproved`）；签名不一致/不可读时 fail-closed，**任何路径都不卸载、不清数据** |
| `deploy-apk.ps1` | PowerShell（ASCII） | **已弃用/危险**：Phase 0 的"构建 → 卸载重装 → 用设备端 sha256 证明装的是这个包"。卸载会删存档；现在不传 `-LegacyDestructiveInstall` 就只构建、不碰设备 |
| `device-precheck.ps1` | PowerShell（ASCII） | E0 只读设备预检（ABI/API/包信息/APK 哈希/残留文件），默认不改设备；`-ResolveAdbOnly` 只做 adb 定位自检 |
| `find-dotnet.ps1` | PowerShell（ASCII） | 发现可用的 .NET SDK（`-Dotnet` → `$env:STARDEW_DOTNET` → `.tmp/*/dotnet` → `PATH`），要求 `--list-sdks` 非空 |
| `tests/test_mod_pipeline.py` | Python 3 | 注入器 + 容器的离线回归（27 项，含负向用例；**需要游戏程序集**） |
| `tests/LoaderContract/` | C# 桌面宿主 | 验证 `Loader.Start()` 返回契约（成功返回 null + 落盘报告） |
| `tests/ModFramework.Tests/` | C# 控制台 | Loader 的单元 + 宿主 + P114 探针测试（473 项），不需要设备 |
| `tests/FixtureMods/` | C# | 测试用 mod 包（一个 dll 内含 8 种入口形态 + 一个私有依赖 dll） |
| `tests/fixtures/manifests/` | JSON | 25 个 manifest 夹具 + `index.json` 期望表 |
| `tests/test_loader.py` | Python 3 | 构建并运行 Loader 测试（含 P114 探针包）；`--refs` 时额外编示例 mod |
| `tests/test_ci_offline.py` | Python 3 | CI 可跑的 13 项检查（语法/编码/夹具/schema/隐私/CI 结构） |
| `tests/test_p114_probe.py` | Python 3 | P114 静态安全审计（46 项：默认探针路径无原生代码、T11/T12 已编译隔离、部署脚本无卸载路径） |
| `tests/test_device_precheck.ps1` | PowerShell | adb 解析自测（10 项，不连设备） |
| `tests/test_deploy_safe.ps1` | PowerShell | 部署门禁 fake adb 自测（95 项，不连设备）。fake adb **模拟设备文件系统**：断言远端目录树、每个文件 SHA-256、覆盖保护、staging 清理、UID/GID 校验、失败路径，并断言危险命令未被调用 |
| `tests/run-all.ps1` | PowerShell | 一键跑全部本地套件并输出汇总表 |
| `tests/toolchain.py` | Python 3 | 两个 Python 驱动共用的 SDK 发现（不写死用户目录） |
| `privacy-scan.py` | Python 3 | 隐私/秘密扫描（用户目录、token、私钥、设备序列号） |

**部署入口只有 `deploy-apk-safe.ps1`。** 默认运行不会写设备：

```powershell
pwsh -NoProfile -File Tools/deploy-apk-safe.ps1                 # 只读预检 + 打印将要执行的命令
pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -InstallApproved # 同签名覆盖安装（需批准）
pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -PushModsApproved -Serial <serial> -ModsSourceDirectory <完整 Mods 根目录>
```

`-ModsSourceDirectory` 指的是**完整的 Mods 根目录**（不是某个 Mod 的子目录）。脚本会：
递归枚举全部普通文件 → 按相对路径推到唯一 staging 目录 → **全部**远端 SHA-256 校验通过后
才复制到 `/data/data/<pkg>/files/Mods` → 再次逐文件校验并检查属主/权限 → 清理 staging。
目标已存在同路径文件时：内容相同跳过，内容不同 fail-closed（`-OverwriteModsApproved` 才覆盖）。
它绝不清空或替换整个 Mods 目录，也不会删掉别的 Mod。

它守住的是 Phase 0 那条教训的反面：**不要为了"证明装的是哪个包"而卸载玩家游戏**。
已安装包与本地 APK 摘要一致时根本不需要重装；签名无法证明时脚本停下来要人工核验，
而不是"卸载重装试试"。

旧的 `Tools/deploy-apk.ps1` 保留只为审计历史构建链路：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk.ps1 -Restore   # 只还原仓库 blob
# 不允许：不传 -LegacyDestructiveInstall 时它会拒绝继续（退出码 2）
```

> **Phase 0.5 布局变更**：`.tmp/phase0/` 已清空。旧脚本仍用 `.tmp/phase0.5/`：
> `out/assemblies.original.blob`（原始 blob 备份）、`artifacts/`（构建产物）。缺任何一个都会直接报缺什么，
> 不再静默失败。默认 `-HookMethod OnCreate` 是 Phase 0 实际验证过的那一版；ADR 的首选注入点是
> `OnCreatePartTwo`，用 `-HookMethod OnCreatePartTwo` 切换。
> 私有 SDK 与 refs 目录不再假设固定路径：`Tools/find-dotnet.ps1` 与
> `python Tools/tests/test_loader.py --refs <dir>` 都接受显式路径。

---

## ModInjector

```powershell
# 构建（$dotnet 来自 `pwsh -File Tools/find-dotnet.ps1`，必须是带 SDK 的主机）
$dotnet = (pwsh -NoProfile -File Tools/find-dotnet.ps1).Trim()
& $dotnet build Tools\ModInjector\ModInjector.csproj -c Release --artifacts-path .tmp\p114\injector

# 注入
& $dotnet <out>/ModInjector.dll patch <in.dll> <out.dll> `
    [--hook-method OnCreatePartTwo] [--loader-api loadfrom|load] [--refs-dir <dir>] [--mode append] [--no-catch]

# 校验（不看写入方，重新读文件；会打印实际注入的加载 API）
& $dotnet <out>/ModInjector.dll verify <out.dll>
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

一键跑全部（需要 65 个程序集；没有则相关套件标 SKIPPED）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\run-all.ps1 -Refs .tmp\p113\refs
```

| 套件 | 命令 | 项数 | 需要游戏程序集 |
| --- | --- | --- | --- |
| CI 离线检查 | `python Tools/tests/test_ci_offline.py` | 13 | ❌ |
| P114 静态安全审计 | `python Tools/tests/test_p114_probe.py` | 46 | ❌ |
| 隐私扫描 | `python Tools/privacy-scan.py` | — | ❌ |
| adb 解析自测 | `powershell -File Tools/tests/test_device_precheck.ps1` | 10 | ❌ |
| 部署门禁 fake adb 自测 | `powershell -File Tools/tests/test_deploy_safe.ps1` | 95 | ❌ |
| Loader 单元 + 宿主 + P114 探针 | `python Tools/tests/test_loader.py [--refs <dir>]` | 45 驱动 + 473 内部 | 示例 mod 部分需要 |
| 注入 / 容器 / Loader 契约 | `python Tools/tests/test_mod_pipeline.py --refs <dir>` | 27 | ✅ |
| E0 只读预检 | `powershell -File Tools/device-precheck.ps1` | — | ❌（需要设备） |

`test_mod_pipeline.py` 覆盖：默认加载 API 必须是 `loadfrom`（负向对照 `--loader-api load` 可复现
`load(byte[])`）、注入后结构校验、MVID 变更、幂等重跑、容器原位替换的全部不变量、
`verify` 的负向用例、`Loader.Start()` 成功返回 null 且落盘报告、
以及"Runtime 仍能用 Android 参考程序集编译"。

`test_loader.py` 覆盖：SemVer / 版本范围 / JSON / 25 个 manifest 夹具 / 发现 / 依赖图（含环与确定性）/
状态机 / 错误码 / 11 种 mod 包形态的端到端 / 重复运行 / 重入 / 私有依赖解析 / 示例 mod 全链路，
并在 Phase 1.1.4 起构建 P114 探针包、用真实 Loader 跑 R-01…R-14。

`test_p114_probe.py` 只读源码，拦住两类回归：默认探针路径里出现 P/Invoke/mprotect/detour/unsafe，
以及部署脚本里出现可执行的 `uninstall` / `pm clear` / `force-stop`。

### 编码事故的防线

`Tools/`、`Mod/` 里的源文件**不要**用 `Get-Content` / `Set-Content` 往返（会把中文注释变成乱码，
甚至把下一行代码并进注释）。需要改就用编辑工具或 Python 显式 UTF-8。
CI 现在会拦住新的乱码：`test_ci_offline.py` 检查私用区字符与 24 个已知 mojibake 记号，
并检查所有会被发布的文本文件都是合法 UTF-8。

## 完整链路（E1 需要，尚未在真机执行）

```text
extract blob → ModInjector patch → store.py replace → 放回 apkroot
   → gradlew assembleDebug（apktool + zipalign + apksigner）
   → deploy-apk-safe.ps1（默认只读；-InstallApproved 同签名覆盖；-PushModsApproved 放 Mods）
   → 启动 → 取 loader.log / loader-report.txt / probe-output/phase1.1.4.txt
```

> Phase 1.1.4 起**没有** `adb uninstall` 这一步。旧的 `deploy-apk.ps1` 之所以要先卸载，
> 是因为它假设每次都是"自签名包换掉官方包"；那会删存档。现在的门禁要求"能证明签名一致才覆盖"，
> 证明不了就停下来让人判断。

Phase 0.5 已经把 `.tmp/phase0.5/out/` 里的测试包准备好（`sv.full.dll`、`assemblies.full.blob`、
`mods/*.dll`），但**没有**装机、**没有**构建 APK——见 `docs/audit/phase0.5-report.md` §7。
Phase 1.1.4 的 E0 复跑与"停在哪一步"见 `docs/audit/phase1.1.4-report.md` §7 / §11。
