# 星露谷物语（Android）· 可构建工程 + Mod Framework

## 目录

```
settings.gradle.kts / build.gradle.kts / gradle.properties
gradle/wrapper/…、gradlew、gradlew.bat        Gradle 8.10.2 wrapper
app/                                          唯一的模块：内容 + 构建逻辑
  build.gradle.kts                             assembleDebug（JavaExec）
  apktool.yml                                  apktool 元数据（versionCode/Name、doNotCompress）
  src/main/
    AndroidManifest.xml                        文本清单 ← 补丁作用点
    res/                                       资源
    assets/Content/…                           343.8 MB 游戏资源（>100 MB 的 music.xwb 以 .partNNN 分片存放）
    jniLibs/arm64-v8a/…                        41.8 MB 原生库（Mono + AOT）
    prebuilt/classes{,2}.dex                   Java 层预编译 dex（原样，不反编译）
    apkroot/assemblies/…                       要放到 APK 根目录的文件（.NET 程序集等）
    kotlin/com/kaori/svpatch/                  构建逻辑（Kotlin，约 400 行，不进 APK）
Mod/                                          Mod Framework 的 C# 工程（.NET 8）
  ModFramework.Runtime/                       在游戏进程内跑的框架：契约 + Manifest + 依赖 + Loader
  ExampleMod.HelloWorld/                      参考 mod（manifest.json + IModEntry 实现）
  P114.RuntimeProbe/                          Phase 1.1.4 的运行时诊断 mod（普通 manifest mod；默认不部署）
  P114.RuntimeProbe.Dependency/               上面的私有依赖 fixture（无 manifest.json）
  P114.RuntimeProbe.Fixtures/                 Assembly.LoadFrom 研究用 fixture（参数化构建）
  MonoRuntimeProbe*/                          Phase 0 的运行时探针（安全入口 RunSafe；Loader 不再自动调用）
Tools/                                        构建期工具与测试
  ModInjector/                                IL 注入：把 Bootstrap 合并进 StardewValley.dll
  AssemblyStore/                              assemblies.blob 的提取 / 原位替换 / 校验
  find-dotnet.ps1                             发现可用的 .NET SDK（不写死用户路径）
  deploy-apk-safe.ps1                         非破坏性部署门禁（默认只读；安装/推送需显式批准）
  validate-test-backup.py                     只读复核设备备份（size + SHA-256 + 计数）
  tests/                                      离线测试套件（见下）
final/                                        稳定交付副本（APK 本体被 .gitignore 忽略，只入库说明与校验和）
docs/
  modding/                                    Manifest 规范、写一个 mod
  architecture/                               Phase 1 运行时约束与 Loader 设计
  audit/                                      阶段报告、证据索引、ADR、可行性分析
```

| 目录 | 内容 | 原因 |
| --- | --- | --- |
| `prebuilt/` | `classes*.dex` | 这些是原版 Java 层字节码，没有源码可编译；AGP 不接受预编译 dex 输入 |
| `apkroot/` | `assemblies/*.blob`、`rc.bin` | .NET Android 运行时要求它们位于 APK **根目录**，AGP 不支持向根目录放任意文件 |

超过 GitHub 单文件 100 MB 限制的资源按原位置切分（如 `assets/Content/XACT/music.xwb.part000`），
构建时自动拼接回原文件并校验 SHA-256，分片不会进 APK。

构建时由 `app/src/main/kotlin` 里的构建逻辑把 `app/src/main` 的内容映射成 apktool 的布局，再打包：

```
AndroidManifest.xml → decoded/AndroidManifest.xml
res/                → decoded/res/
assets/             → decoded/assets/
jniLibs/            → decoded/lib/
prebuilt/*.dex      → decoded/classes*.dex
apkroot/            → decoded/unknown/    （apktool 会把 unknown/ 的内容放到 APK 根）
apktool.yml         → decoded/apktool.yml
```

### 当前功能边界（务必先看）

| 项 | 状态 |
| --- | --- |
| 支持的游戏版本 | **1.6.15.0**（versionCode 239）。版本绑定：清单/资源/程序集都强相关，不做多版本兼容 |
| 可构建的 APK | ✅ 直达标题界面，无更新/隐私/实名弹窗 |
| Mod Framework：Manifest 发现 / 校验 / 依赖排序 / 加载 / 入口调用 | ✅ **离线实现完成**（宿主测试 486 项） |
| Mod Framework：Phase 1.1 加固 | ✅ **离线实现完成**：裸 DLL 不执行 · 版本门禁 Fail-Closed · 加载前程序集身份预检 · 日志降级进报告（见 [`docs/audit/phase1.1-report.md`](docs/audit/phase1.1-report.md)） |
| Mod Framework：Phase 1.1.1 预检正确性与安全加固 | ✅ **离线实现完成**：依赖失败不再误伤无关 mod · 内容相等改逐字节判定（去掉 FNV 回退）· 宿主匹配 Fail-Closed · PE/CLI 逐层边界 · 框架版本 Unknown/Invalid 分离 · 报告与日志脱敏（见 [`docs/audit/phase1.1.1-report.md`](docs/audit/phase1.1.1-report.md)） |
| Mod Framework：Phase 1.1.2 程序集加载完整性 | ✅ **离线实现完成**：预检字节写入私有快照并只从快照 `LoadFrom` · 加载前二次校验 · 不动点显式收敛/耗尽（`MF0208`）· ECMA-335 公钥标志与 Blob 形状校验（见 [`docs/audit/phase1.1.2-report.md`](docs/audit/phase1.1.2-report.md)） |
| Mod Framework：Phase 1.1.3 快照去重完整性与指纹抗碰撞 | ✅ **离线实现完成**：fingerprint 只做候选索引（完整字节比较才共享）· 一个键下可存多份不同内容 · 快照完整性用独立 SHA-256（无 SHA-256 → Fail-Closed）· `LoadFrom` 返回身份核对（`MF0210`）（见 [`docs/audit/phase1.1.3-report.md`](docs/audit/phase1.1.3-report.md)） |
| Mod Framework：Phase 1.1.4 运行时表征与非破坏性部署 | ⚠️ **safe-only MonoVM 运行验证已在一台 ARM64 真机通过**：P114 走真实 Loader 链路；无 Root 部署已实测；Harmony / detour 仍未实现（见 [Phase 1.2 报告](docs/audit/phase1.2-arm64-device-acceptance-report.md)） |
| Mod Framework：Phase 1.1.5–1.1.9（部署链路 / MonoVM 表征 / mod-enabled 构建 / CI 收尾） | ⚠️ **Emulator 与一台 ARM64 真机均已实测**：递归 Mods 部署门禁 · assembleModdedDebug 注入构建 · 长期密钥库 · CI 历史 Run #10 为 3/3 绿 · Emulator 14/14、ARM64 14/14；ARM64 结果见 [Phase 1.2 报告](docs/audit/phase1.2-arm64-device-acceptance-report.md) |
| Mod Framework：ARM64 真机验证 | ✅ **PASS（1 台 V1963A / Android 10 / arm64-v8a）**：Loader 1/1 初始化，P114 12/12 探针直接测试 + R-03/R-12 Loader 侧证据，合计 14/14；不代表跨设备兼容（见 [Phase 1.2 报告](docs/audit/phase1.2-arm64-device-acceptance-report.md)） |
| Mod Framework：设备上验证 AssemblyName 元数据读取与 SHA-256 | ✅ **PASS（1 台 ARM64 真机）**：R-01/R-02 SHA-256、R-08 AssemblyName、R-13 DLL load/hash 均通过（见 [Phase 1.2 报告](docs/audit/phase1.2-arm64-device-acceptance-report.md)） |
| Harmony / 方法补丁 / 事件钩子 / SMAPI 兼容 | ❌ **未实现**（Phase 2 及以后） |
| 游戏内 Mod 管理器 / 配置 UI / 自动下载 | ❌ **未实现** |

> 当前已在一台 ARM64 物理设备上完成 safe-only Loader 与运行时探针验收（合并口径 14/14）。这只证明 V1963A / Android 10 的测试结果，**不代表所有 ARM64 设备兼容**；也未验证实际存档写入、长时间游玩或第三方 SMAPI Mod。详见 [Phase 1.2 报告](docs/audit/phase1.2-arm64-device-acceptance-report.md)。
> `IModEntry.Initialize`，失败被隔离"。**不能**保证在真机上跑过——那一步仍缺设备。
> .NET mod dll 在游戏进程内以完整权限执行，manifest 校验**不是**安全沙箱。

### Loader 的硬语义（Phase 1.1 / 1.1.1）

| 语义 | 含义 |
| --- | --- |
| 只有 manifest 包会执行 | Mods 根目录下的裸 DLL（含历史遗留的 `MonoRuntimeProbe.dll`）不会被加载；报告固定写 `probe=not-run-by-loader`。探针改为显式诊断启动，见 [`Mod/MonoRuntimeProbe/README.md`](Mod/MonoRuntimeProbe/README.md) |
| 版本门禁 Fail-Closed | `gameVersion`/`frameworkVersion` 只有 `"*"` 才免除校验；显式范围遇到版本未知/无法解析 → 拒绝（`MF0503`/`MF0504`/`MF0505`/`MF0506`），拒绝发生在入口 DLL 加载之前。框架版本读不到时**不再**伪造成 `0.0.0` |
| 加载前身份预检 | 所有 `entryAssembly`/`privateAssemblies` 在**第一次 `Assembly.LoadFrom` 之前**完成元数据读取与冲突判定；同名不同身份/内容 → 双方拒绝（`MF0206`），同身份同内容 → 确定性共享一个代表文件 |
| 依赖失败优先、冲突重算 | 依赖了失败 mod 的下游先被排除，再重新计算冲突；不会把无关健康 mod 一起拒绝（`preflight-rounds=` 可验证收敛） |
| 内容身份 | "内容相同"只由**逐字节比较**决定；SHA-256 仅用于快速否定，FNV/CRC 不再是共享许可。无法比较 → 拒绝（Fail-Closed） |
| 宿主程序集复用 | 必须身份四项（Name/Version/Culture/公钥字节）全等 + 宿主文件可读 + 内容逐字节相同；否则 `MF0207`，不再"复用 + warning" |
| 快照去重不是共享授权 | 只从**快照**加载（Phase 1.1.2）；去重键（`sha256:`，无 SHA-256 时退到 `sha1:`）**只找候选**，命中后必须对当前 claim 自己的字节逐字节比较，`Same` 才共享；一个键下可以有多份不同内容的快照（Phase 1.1.3） |
| 快照完整性独立于去重键 | 加载前用**独立 SHA-256 摘要**校验快照（长度 + 全文件）；运行时没有 SHA-256 → 该 mod `MF0209`，不落盘也不加载，不回退 SHA-1 |
| 加载对象身份核对 | `LoadFrom` / 缓存命中 / 宿主复用返回的 `Assembly` 身份与 claim 预检身份（Name/Version/Culture/公钥 Token）不一致 → `MF0210`，不初始化、不记成功（防御纵深；主防线是"只加载被批准的字节"） |
| 日志降级可见 | `logs/<id>.log` 写不进去时报告记 `log-status=degraded` + 异常类型（不含路径），mod 状态仍是 `Initialized` |
| 报告/日志不含绝对路径 | `locate=` / `mods-root=` / `dir=` 只保留末段或包内相对名；异常消息里的绝对路径脱敏为 `<path>` |

冲突与版本规则写进 [`docs/modding/manifest-spec.md`](docs/modding/manifest-spec.md) §3.4 / §5。

## 前置条件

| 依赖 | 说明 |
| --- | --- |
| JDK 11+ | `java`、`keytool` 可用 |
| Android SDK `build-tools` | 需要 `aapt2`、`zipalign`、`apksigner`；优先 35.0.0，否则取最高版本 |
| `ANDROID_HOME` | 可不设（依次找 `ANDROID_HOME` → `ANDROID_SDK_ROOT` → `%LOCALAPPDATA%\Android\Sdk`） |
| 网络 | 首次构建下载 Gradle 发行包与 Kotlin/apktool 依赖（之后走缓存） |
| .NET SDK 8+（只有改 `Mod/` 或跑 Loader 测试时需要） | 必须是**带 SDK** 的主机。`pwsh -File Tools/find-dotnet.ps1` 会按 `-Dotnet` → `$env:STARDEW_DOTNET` → `.tmp/*/dotnet/dotnet[.exe]` → `PATH` 的顺序发现它，并要求 `dotnet --list-sdks` 非空（只有运行时不算可用） |
| Python 3.10+（只有跑 `Tools/tests/*.py` 时需要） | 只用标准库（CI 里的 YAML 检查会用到 PyYAML，没有则退化为行级检查） |
| 游戏自带的 65 个程序集（只有编 `Mod/` 与跑完整套件时需要） | 从仓库里的 blob 解出来：见下方"构建" |

wrapper 的 `distributionUrl` 默认指向腾讯镜像（国内 services.gradle.org 常连不上），
网络通畅时可换回官方地址。

## 构建

### APK

```powershell
.\gradlew.bat assembleDebug
```

产物 `app/build/outputs/apk/debug/app-debug.apk` 是 **baseline**：不含任何 Mod 支持代码。

### mod-enabled APK（Phase 1.1.7 / 1.1.8）

要让装机后能加载 `Mods/`，必须显式构建注入版——默认构建**不会**注入：

```powershell
python Tools/build_modded_apk.py --dotnet <SDK 8.0.404> --work .tmp/build-modded `
    --out-blob .tmp/build-modded/modded-assemblies.blob `
    --manifest-json .tmp/build-modded/modded-build-manifest.json

.\gradlew.bat :app:assembleModdedDebug "-Pdotnet=<SDK 8.0.404>" `
    "-Pkeystore=$env:USERPROFILE\.android\keystores\StardewValley\stardewvalley-test-signing.keystore"
```

产物是 `app/build/outputs/apk/modded/app-modded-debug.apk`，与 baseline APK 完全分开。
`-Pkeystore` 必须指向**项目外长期密钥库**（`%USERPROFILE%\.android\keystores\StardewValley\`），
不要指向 `.work/`、`.tmp/` 或 `app/build` 里的副本——那些目录会被清理。脚本不会自动生成新 key。

最近一次验收过的交付副本放在 `final/`（`StardewValley-1.6.15.0-modded-test-debug.apk` +
`SHA256SUMS.txt` + `DELIVERY.md`）。APK 本体被 `*.apk` 规则忽略，只入库说明与校验和；
每次重新构建都会变（MVID 随机），所以**以 `final/SHA256SUMS.txt` 为准**。

签名准入：signer 与已安装包**完全一致**才能 `adb install -r` 原位升级；不一致时只能在
Saves/Mods 已备份并校验后，对指定测试机卸载重装（卸载本身会删应用数据）。详见
`docs/audit/phase1.1.8-report.md` 与 `docs/audit/phase1.1.9-device-acceptance-report.md`。

### Mod Framework（C#）

用**游戏自带的程序集**当参考程序集编译，否则"用了运行时被裁掉的 BCL 成员"只会在设备上炸：

```powershell
# 0) 找到可用的 SDK（打印路径；找不到时退出码 1）
$dotnet = (pwsh -NoProfile -File Tools/find-dotnet.ps1).Trim()

# 1) 解出 65 个程序集（只需一次；.tmp/ 已 gitignore）
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.blob .tmp/p113/refs --manifest app/src/main/apkroot/assemblies/assemblies.manifest
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.arm64_v8a.blob .tmp/p113/refs --manifest app/src/main/apkroot/assemblies/assemblies.manifest

# 2) 编框架、示例 mod 与诊断 mod（AndroidRefsDir 必须是绝对路径）
$refs = (Resolve-Path .tmp/p113/refs).Path
& $dotnet build Mod/ModFramework.Runtime   -c Release -p:AndroidRefsDir=$refs
& $dotnet build Mod/ExampleMod.HelloWorld  -c Release -p:AndroidRefsDir=$refs
& $dotnet build Mod/P114.RuntimeProbe      -c Release -p:AndroidRefsDir=$refs
```

`Mod/ExampleMod.HelloWorld` 的输出目录**本身就是一个合法的 mod 包**
（`manifest.json` + `ExampleMod.HelloWorld.dll`）。怎么装到设备、怎么看日志见
[`docs/modding/creating-a-mod.md`](docs/modding/creating-a-mod.md)。

部署到设备**只走非破坏性门禁**（默认只读；安装需 `-InstallApproved`；签名不符直接停）：

```powershell
pwsh -NoProfile -File Tools/deploy-apk-safe.ps1                          # 只读预检 + 打印计划
pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -Serial <serial>         # 仍然只读
```

> 旧的 `Tools/deploy-apk.ps1` 会先 `adb uninstall`（**删存档**）。它现在默认拒绝执行，
> 必须显式传 `-LegacyDestructiveInstall` 才会走到卸载重装；新增改动不要再依赖它。

### 测试

本地全量（需要上面解出的 65 个程序集）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\run-all.ps1 -Refs .tmp\p113\refs
```

| 套件 | 命令 | 需要游戏程序集 |
| --- | --- | --- |
| CI 离线检查（13 项：Python/PS 语法、UTF-8、mojibake、manifest 夹具、schema、隐私、CI 结构） | `python Tools/tests/test_ci_offline.py` | ❌ |
| P114 静态安全审计（46 项：探针无原生代码、T11/T12 已编译隔离、部署无卸载路径） | `python Tools/tests/test_p114_probe.py` | ❌ |
| 隐私扫描 | `python Tools/privacy-scan.py` | ❌ |
| adb 解析自测（10 项，不连设备） | `powershell -File Tools/tests/test_device_precheck.ps1` | ❌ |
| 部署门禁 fake adb 自测（95 项，不连设备） | `powershell -File Tools/tests/test_deploy_safe.ps1` | ❌ |
| Loader 单元 + 宿主 + P114 探针（45 项驱动 / 486 项内部） | `python Tools/tests/test_loader.py --refs .tmp/p113/refs` | 示例 mod 与 65 程序集差分需要（缺则 SKIP） |
| 注入 / 容器 / Loader 契约（27 项） | `python Tools/tests/test_mod_pipeline.py --refs .tmp/p113/refs` | ✅ |
| E0 只读设备预检 | `powershell -File Tools/device-precheck.ps1` | ❌（需要设备） |
| 备份复核（只读） | `python Tools/validate-test-backup.py --root <backup>` | ❌ |

**CI（`.github/workflows/ci.yml`）只跑不需要游戏程序集的部分**：
`test_ci_offline.py`、`test_p114_probe.py`、`privacy-scan.py`、ModInjector/Runtime/P114/FixtureMod 构建、
`test_loader.py`（无 `--refs`），以及一个 Windows job 跑两个 PowerShell 门禁
（`test_device_precheck.ps1`、`test_deploy_safe.ps1`）。
依赖游戏程序集的套件（含 65 程序集的解析差分 `--diff-refs`）在 CI 里显式标 **SKIPPED**，不会伪装成通过。
任何 E1/E2 设备证据都没有——那需要一台 ARM64 物理设备。

## 二次开发

### 改 APK 打包行为

补丁是一个接口，加新改动只需实现并注册：

```kotlin
interface ApkPatch {
    val name: String                 // 构建日志里显示
    fun apply(decodedDir: Path)      // 改 .tmp/build/decoded 里的工程树
}
```

常见改法：改 `app/src/main/AndroidManifest.xml`（权限/组件）、替换
`app/src/main/assets/Content` 下的贴图或数据、往 `app/src/main/res` 加资源。
改 dex 级行为需要额外工具，本工程刻意不碰。

### 写一个 mod

- 格式与错误码：[`docs/modding/manifest-spec.md`](docs/modding/manifest-spec.md)
- 从零开始：[`docs/modding/creating-a-mod.md`](docs/modding/creating-a-mod.md)
- 能用哪些 BCL API（重要）：[`docs/architecture/phase1-runtime-constraints.md`](docs/architecture/phase1-runtime-constraints.md)
- 需要"测量运行时"而不是"改玩法"时，看 [`Mod/P114.RuntimeProbe/README.md`](Mod/P114.RuntimeProbe/README.md)：
  它是一个**普通 manifest mod**，演示了 private dependency、只读诊断、以及把报告写到自己包目录的完整做法。

最小可用代码：

```csharp
using ModFramework.Runtime;

public sealed class Entry : IModEntry
{
    public void Initialize(IModContext context)
        => context.Log.Info("hello from " + context.ModId);
}
```

### 改 Loader

先读 [`docs/architecture/phase1-loader-design.md`](docs/architecture/phase1-loader-design.md)。
三条硬约束：

1. `Mod/ModFramework.Runtime` 里**不能**用 `System.Linq`、`System.Text.Json`、`FileInfo`/`DirectoryInfo`、
   `Directory.GetFiles`、`Environment.CurrentDirectory`、`Version.TryParse`——运行时没有。
   改完必须用 `-p:AndroidRefsDir=…` 编一遍。
2. 不要给 `Loader` 加第二个叫 `Start` 的方法：Bootstrap 的 IL 用 `Type.GetMethod("Start")`。
3. **不要试图"顺手"用桌面 .NET 的便利 API 读程序集身份**。裁剪后的运行时里：

   | 想用 | 实际 | 替代 |
   | --- | --- | --- |
   | `AssemblyName.GetAssemblyName(path)` | ❌ 被裁掉 | `AssemblyMetadata.cs` 自己读 PE/CLI 元数据 |
   | `AssemblyName.GetPublicKey()` | ❌ 被裁掉（Phase 1.1.1 实测） | `GetPublicKeyToken()`；完整公钥 → Token 由 `PublicKeyToken.cs` 按 ECMA-335 派生 |
   | `SHA256.Create()` / `HashAlgorithm` | ❌ 被裁掉 | `SHA256.HashData(byte[])`（存在）；**没有**弱摘要回退，拿不到就退回逐字节比较 |
   | `FileInfo` / `DirectoryInfo` | ❌ 被裁掉 | `File.OpenRead` + `FileStream.Length` |
   | `Assembly.LoadFrom` | ✅ 存在 | 唯一可用的外部程序集加载方式 |

   这几条不是文档推测：`docs/audit/phase1.1-evidence-index.md` 里有针对游戏 65 个参考程序集的
   编译期证据（含"这些 API 不存在"的对照实验）。
