# Tools

构建期工具。全部离线可跑，不依赖设备。

| 工具 | 语言 | 作用 |
| --- | --- | --- |
| `ModInjector` | C# / net8.0 / Mono.Cecil | 把 Bootstrap 合并进 `StardewValley.dll`，并在指定的 `MainActivity` 方法首行插入调用 |
| `AssemblyStore` | Python 3（无第三方依赖） | 读写 `assemblies.blob`（XABA v1 + XALZ/LZ4），把改好的程序集**原位替换**回去 |
| `deploy-apk.ps1` | PowerShell（ASCII） | 一条命令完成"换 blob → 构建 → 还原 → 卸载重装 → **用设备端 sha256 证明装的是这个包**" |

`deploy-apk.ps1` 的存在是为了杜绝一类自欺：**无法确认设备上跑的是哪个 APK**。
它会把已安装 APK 的哈希与本地产物比对，不一致直接失败。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk.ps1 -Mode full -HookMethod OnCreate
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk.ps1 -Restore   # 只还原仓库 blob
```

两者配合，把"改一行 IL"变成可装机 APK 的一半工作；另一半是 apktool 重打包 + 签名（已有流水线）。

---

## ModInjector

```powershell
dotnet build Tools/ModInjector/ModInjector.csproj -c Release -o <out>

# 注入
dotnet <out>/ModInjector.dll patch <in.dll> <out.dll> \
    [--mods-dir /sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods] \
    [--runtime-dll ModFramework.Runtime.dll] \
    [--loader-type ModFramework.Runtime.Loader]

# 校验（不看写入方，重新读文件）
dotnet <out>/ModInjector.dll verify <out.dll>
```

注入的 `Bootstrap.Start()` IL 语义：

```csharp
try {
    if (!File.Exists(path)) return;
    var t = Assembly.Load(File.ReadAllBytes(path)).GetType("ModFramework.Runtime.Loader");
    if (t == null) return;
    var m = t.GetMethod("Start");
    if (m == null) return;
    m.Invoke(null, null);
} catch (Exception) { }
```

要点：

- 幂等：已存在 `ModFramework.Bootstrap` 时只检查/补插 hook call；
- 强制换新 MVID（否则 AOT 镜像与缓存会认为程序集没变）；
- 失败被 `catch` 吞掉——**任何异常都不能阻止游戏启动**。

## AssemblyStore

```powershell
python Tools/AssemblyStore/store.py list    <blob>
python Tools/AssemblyStore/store.py extract <blob> <outdir>
python Tools/AssemblyStore/store.py selftest <blob>            # LZ4 编解码往返
python Tools/AssemblyStore/store.py replace <blob> <name> <newdll> <outblob> [--manifest <manifest>]
python Tools/AssemblyStore/store.py verify  <blob> [--manifest <manifest>] [<name>=<sha256> ...]
```

设计要点（为什么要"原位替换"而不是"新增条目"）：

`assemblies.blob` = `header(20)` + `entry_count×24 条目表` + `gap(实测 total_count×40 的
hash→(store,index) 映射表)` + 负载。新增条目必须同步改 gap 表和 `libxamarin-app.so` 里的
`assembly_image_cache`；**原位替换**则条目数、gap 表、版本号全不变，只在结构内重算偏移/大小。
详见 `docs/audit/adr-0001-mod-injection.md` §D3。

已验证：

- LZ4 编码器 58/58 条目往返一致；
- 替换 `StardewValley` 后，57 个条目与原 blob **逐字节相同**，`StardewValley` 与补丁文件 SHA-256 一致；
- `entry_count` / `total_count` / `gap` / `version` 全部保持不变。

已知取舍：自研贪心 LZ4 压缩率约 45%，原厂（LZ4 HC）约 33%；被替换的条目会变大
（`StardewValley` 3,114,006 → 3,709,254 字节，blob 5.59 MB → 6.18 MB）。功能无影响。

## 完整链路（下一轮执行）

```text
extract blob → ModInjector patch → store.py replace → 放回 apkroot
   → gradlew assembleDebug（apktool + zipalign + apksigner）
   → adb uninstall / install → 放入 Mods/ModFramework.Runtime.dll → 启动 → 取日志
```
