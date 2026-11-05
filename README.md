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
  MonoRuntimeProbe*/                          Phase 0 的运行时探针（诊断用，需显式启动；Loader 不再自动调用）
Tools/                                        构建期工具与测试
  ModInjector/                                IL 注入：把 Bootstrap 合并进 StardewValley.dll
  AssemblyStore/                              assemblies.blob 的提取 / 原位替换 / 校验
  tests/                                      离线测试套件（见下）
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
| Mod Framework：Manifest 发现 / 校验 / 依赖排序 / 加载 / 入口调用 | ✅ **离线实现完成**（宿主测试 206 项） |
| Mod Framework：Phase 1.1 加固 | ✅ **离线实现完成**：裸 DLL 不执行 · 版本门禁 Fail-Closed · 加载前程序集身份预检 · 日志降级进报告（见 [`docs/audit/phase1.1-report.md`](docs/audit/phase1.1-report.md)） |
| Mod Framework：ARM64 真机验证 | ❌ **BLOCKED**（无物理设备；只有 x86_64 模拟器） |
| Mod Framework：设备上验证 `AssemblyName` 元数据读取与 SHA-256 | ❌ **BLOCKED**（只有编译期证据，见 phase1.1 报告 §3） |
| Harmony / 方法补丁 / 事件钩子 / SMAPI 兼容 | ❌ **未实现**（Phase 2 及以后） |
| 游戏内 Mod 管理器 / 配置 UI / 自动下载 | ❌ **未实现** |

> 现在能保证的是"受信任的、符合规范的 mod 包会被发现、校验、按依赖顺序加载并调用其
> `IModEntry.Initialize`，失败被隔离"。**不能**保证在真机上跑过——那一步仍缺设备。
> `.NET` mod dll 在游戏进程内以完整权限执行，manifest 校验**不是**安全沙箱。

### Loader 的四条硬语义（Phase 1.1）

| 语义 | 含义 |
| --- | --- |
| 只有 manifest 包会执行 | Mods 根目录下的裸 DLL（含历史遗留的 `MonoRuntimeProbe.dll`）不会被加载；报告固定写 `probe=not-run-by-loader`。探针改为显式诊断启动，见 [`Mod/MonoRuntimeProbe/README.md`](Mod/MonoRuntimeProbe/README.md) |
| 版本门禁 Fail-Closed | `gameVersion`/`frameworkVersion` 只有 `"*"` 才免除校验；显式范围遇到版本未知/无法解析 → 拒绝（`MF0503`/`MF0504`/`MF0505`），拒绝发生在入口 DLL 加载之前 |
| 加载前身份预检 | 所有 `entryAssembly`/`privateAssemblies` 在**第一次 `Assembly.LoadFrom` 之前**完成元数据读取与冲突判定；同名不同身份/内容 → 双方拒绝（`MF0206`），同身份同内容 → 确定性共享一个代表文件 |
| 日志降级可见 | `logs/<id>.log` 写不进去时报告记 `log-status=degraded` + 异常类型（不含路径），mod 状态仍是 `Initialized` |

冲突与版本规则写进 [`docs/modding/manifest-spec.md`](docs/modding/manifest-spec.md) §3.4 / §5。

## 前置条件

| 依赖 | 说明 |
| --- | --- |
| JDK 11+ | `java`、`keytool` 可用 |
| Android SDK `build-tools` | 需要 `aapt2`、`zipalign`、`apksigner`；优先 35.0.0，否则取最高版本 |
| `ANDROID_HOME` | 可不设（依次找 `ANDROID_HOME` → `ANDROID_SDK_ROOT` → `%LOCALAPPDATA%\Android\Sdk`） |
| 网络 | 首次构建下载 Gradle 发行包与 Kotlin/apktool 依赖（之后走缓存） |
| .NET SDK 8（只有改 `Mod/` 或跑 Loader 测试时需要） | 本机可用项目私有 SDK：`.tmp/phase0.5/dotnet/dotnet.exe`；系统 SDK 也行 |
| Python 3.10+（只有跑 `Tools/tests/*.py` 时需要） | 只用标准库（CI 里的 YAML 检查会用到 PyYAML，没有则退化为行级检查） |
| 游戏自带的 65 个程序集（只有编 `Mod/` 与跑完整套件时需要） | 从仓库里的 blob 解出来：见下方"构建" |

wrapper 的 `distributionUrl` 默认指向腾讯镜像（国内 services.gradle.org 常连不上），
网络通畅时可换回官方地址。

## 构建

### APK

```powershell
.\gradlew.bat assembleDebug
```

### Mod Framework（C#）

用**游戏自带的程序集**当参考程序集编译，否则"用了运行时被裁掉的 BCL 成员"只会在设备上炸：

```powershell
# 1) 解出 65 个程序集（只需一次；.tmp/ 已 gitignore）
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.blob .tmp/phase0.5/refs --manifest app/src/main/apkroot/assemblies/assemblies.manifest
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.arm64_v8a.blob .tmp/phase0.5/refs --manifest app/src/main/apkroot/assemblies/assemblies.manifest

# 2) 编框架与示例 mod
.tmp/phase0.5/dotnet/dotnet.exe build Mod/ModFramework.Runtime -c Release -p:AndroidRefsDir=(Resolve-Path .tmp/phase0.5/refs)
.tmp/phase0.5/dotnet/dotnet.exe build Mod/ExampleMod.HelloWorld -c Release -p:AndroidRefsDir=(Resolve-Path .tmp/phase0.5/refs)
```

`Mod/ExampleMod.HelloWorld` 的输出目录**本身就是一个合法的 mod 包**
（`manifest.json` + `ExampleMod.HelloWorld.dll`）。怎么装到设备、怎么看日志见
[`docs/modding/creating-a-mod.md`](docs/modding/creating-a-mod.md)。

### 测试

本地全量（需要上面解出的 65 个程序集）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\run-all.ps1 -Refs .tmp\phase0.5\refs
```

| 套件 | 命令 | 需要游戏程序集 |
| --- | --- | --- |
| CI 离线检查（13 项：Python/PS 语法、UTF-8、mojibake、manifest 夹具、schema、隐私、CI 结构） | `python Tools/tests/test_ci_offline.py` | ❌ |
| 隐私扫描 | `python Tools/privacy-scan.py` | ❌ |
| adb 解析自测（10 项，不连设备） | `powershell -File Tools/tests/test_device_precheck.ps1` | ❌ |
| Loader 单元 + 宿主（206 项） | `python Tools/tests/test_loader.py --dotnet dotnet` | 示例 mod 部分需要（缺则 SKIP） |
| 注入 / 容器 / Loader 契约（27 项） | `python Tools/tests/test_mod_pipeline.py` | ✅ |
| E0 只读设备预检 | `powershell -File Tools/device-precheck.ps1` | ❌（需要设备） |

**CI（`.github/workflows/ci.yml`）只跑不需要游戏程序集的部分**：
`test_ci_offline.py`、`privacy-scan.py`、ModInjector/Runtime/FixtureMod 构建、`test_loader.py`（无 `--refs`）。
依赖游戏程序集的套件在 CI 里显式标 **SKIPPED**，不会伪装成通过。
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
   | `SHA256.Create()` / `HashAlgorithm` | ❌ 被裁掉 | `SHA256.HashData(byte[])`（存在），否则 FNV-1a 回退 |
   | `FileInfo` / `DirectoryInfo` | ❌ 被裁掉 | `File.OpenRead` + `FileStream.Length` |
   | `Assembly.LoadFrom` | ✅ 存在 | 唯一可用的外部程序集加载方式 |

   这几条不是文档推测：`docs/audit/phase1.1-evidence-index.md` 里有针对游戏 65 个参考程序集的
   编译期证据（含"这些 API 不存在"的对照实验）。
