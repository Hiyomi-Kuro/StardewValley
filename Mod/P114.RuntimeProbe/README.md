# P114.RuntimeProbe

Phase 1.1.4 的**普通诊断 Mod**：把上一阶段留下的"目标 MonoVM 运行时未知项"变成可复现、可审计的
运行时观察。

> **默认不安装、不自动加载。** 它和任何第三方 mod 走同一条链路：Loader 只在 `<Mods>/*/manifest.json`
> 存在时才发现它；本仓库的 APK 构建、Bootstrap、Loader 源码与所有脚本都不引用这个工程。
> 要跑它，必须由人把包复制进 `<Mods>/` 并启动游戏。

## 1. 安全契约（本阶段硬约束）

| 项 | 状态 |
| --- | --- |
| P/Invoke / `DllImport` | ❌ 没有 |
| `mprotect` / RWX / 机器码写入 | ❌ 没有 |
| `Reflection.Emit` / `DynamicMethod` / Harmony | ❌ 没有 |
| 调用 `Game1` / 推进游戏世界 | ❌ 没有（连类型都不碰） |
| 读写 Saves | ❌ 没有 |
| 访问网络 | ❌ 没有 |
| 报告里的绝对路径 | ❌ 没有（只写末段文件名） |
| 报告写入失败的后果 | 记一条 `Error` 日志，**不抛**，游戏继续 |

`R-14` 还额外在运行时证明了两件事：一个损坏 fixture 的 `Assembly.LoadFrom` 失败会被单独记录，
以及一次故意抛出的异常会被用例包装捕获——两者都不会中断其余用例。

历史危险实验（T11 `mprotect` 探页、T12 写 ARM64 跳转桩）**不在**这个 mod 里，也不在
`MonoRuntimeProbe` 的默认编译里，见 `Mod/MonoRuntimeProbe/README.md`。

## 2. 结构

```text
Mod/
├── P114.RuntimeProbe/                入口 mod（有 manifest.json，可被 Loader 发现）
│   ├── P114.RuntimeProbe.csproj
│   ├── manifest.json                 id=P114.RuntimeProbe，声明 1 个 privateAssembly
│   ├── P114Entry.cs                  IModEntry 实现 + 报告写出
│   └── SafeProbes.cs                 R-01 … R-14 的实现
├── P114.RuntimeProbe.Dependency/     私有依赖 fixture（无 manifest.json，不会被当成 mod）
│   └── DependencyMarker.cs           marker = p114-private-dependency-alive
└── P114.RuntimeProbe.Fixtures/       Assembly.LoadFrom 研究用 fixture（参数化构建）
    └── FixtureMarker.cs              variant A / B 的返回值不同
```

打包后的 mod 目录长这样（`probe-fixtures/` 由打包步骤放入）：

```text
P114.RuntimeProbe/
├── manifest.json
├── P114.RuntimeProbe.dll
├── P114.RuntimeProbe.Dependency.dll
├── probe-fixtures/                   ← 不在 manifest 里，Loader 不会自动加载
│   ├── same-a/P114Same.dll           ┐ 同一份字节的两份拷贝
│   ├── same-b/P114Same.dll           ┘
│   ├── diff-a/P114Diff.dll           ┐ 同名同版本、不同字节
│   ├── diff-b/P114Diff.dll           ┘
│   ├── alpha/P114Alpha.dll           ┐ 不同简单名
│   ├── beta/P114Beta.dll             ┘
│   └── corrupt/P114Broken.dll        ← 64 字节 0x00，只给 R-14 制造一次真实加载失败
└── probe-output/phase1.1.4.txt       ← 运行时生成
```

`probe-fixtures/` 是**子目录**，而 `ModDiscovery` 只扫 `<Mods>/` 的一层子目录；`privateAssemblies`
又只列了依赖 dll。所以 fixture 既不会被当成 mod 包，也不会被 Loader 预加载——只有探针自己
`Assembly.LoadFrom` 它们。

## 3. 构建与打包

```powershell
# 1) 探针（不需要游戏程序集：它只引用 ModFramework.Runtime）
dotnet build Mod/P114.RuntimeProbe -c Release --artifacts-path .tmp/p114/artifacts

# 2) fixture 变体（一次源码、五次构建）
dotnet build Mod/P114.RuntimeProbe.Fixtures -c Release --artifacts-path .tmp/p114/fx/same  -p:FixtureAssemblyName=P114Same
dotnet build Mod/P114.RuntimeProbe.Fixtures -c Release --artifacts-path .tmp/p114/fx/diff-a -p:FixtureAssemblyName=P114Diff
dotnet build Mod/P114.RuntimeProbe.Fixtures -c Release --artifacts-path .tmp/p114/fx/diff-b -p:FixtureAssemblyName=P114Diff -p:Variant=VARIANT_B
dotnet build Mod/P114.RuntimeProbe.Fixtures -c Release --artifacts-path .tmp/p114/fx/alpha -p:FixtureAssemblyName=P114Alpha
dotnet build Mod/P114.RuntimeProbe.Fixtures -c Release --artifacts-path .tmp/p114/fx/beta  -p:FixtureAssemblyName=P114Beta
```

`Tools/tests/test_loader.py` 会自动完成上面两步并打好完整的包（含 `probe-fixtures/` 与那个损坏
fixture），然后让 `Tools/tests/ModFramework.Tests` 用**真实 Loader** 跑一遍。手工打包时照第 2 节的
目录结构复制即可。

> `dotnet` 必须是**带 SDK** 的主机。本仓库的测试驱动按 `--dotnet` → `$env:STARDEW_DOTNET` →
> `.tmp/*/dotnet/dotnet[.exe]` → `PATH` 的顺序发现它，见 `Tools/find-dotnet.ps1`。

## 4. 在设备上运行（必须显式、必须批准）

Phase 1.1.4 / 1.1.5 **没有**在设备上运行它。Phase 1.1.6 在用户明确批准后**已经**做了
"推送 + 启动到标题界面"，但因为装机 APK 没有 Bootstrap，Loader 没有启动、探针没有执行（见 §4.1）。
下面是完整步骤：

1. 把整个 `P114.RuntimeProbe/` 目录放到游戏的 Mods 根目录（默认
   `/data/data/com.chucklefish.stardewvalley/files/Mods/`，属主必须是应用 uid）。
   Phase 1.1.5 起**只用**修复后的安全递归脚本推送，不要手工 `cp`：

   ```powershell
   pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -PushModsApproved -Serial <serial> -ModsSourceDirectory <完整 Mods 根目录>
   ```

   它递归保留完整目录树（`P114.RuntimeProbe/manifest.json`、`probe-fixtures/**`），在 staging
   阶段逐个校验远端 SHA-256 后才复制，并在最终位置再校验一次属主与权限。旧实现的
   "只枚举第一层 + 全部复制到 Mods 根目录"会破坏本 README 第 2 节要求的布局。
2. 启动游戏（`am start -W -n com.chucklefish.stardewvalley/com.chucklefish.stardewvalley.MainActivity`）；
3. 读两个文件：
   - `<Mods>/loader-report.txt`：R-03 / R-12（Loader 侧观察：`preflight-stage=complete` 是否先于
     第一条 `assembly-load=`，加载路径是否指向 `.staging`，mod 是否 `state=Initialized`）；
   - `<Mods>/P114.RuntimeProbe/probe-output/phase1.1.4.txt`：R-01 … R-14 的运行时观察。
4. 用 `adb shell` 只读地取回这两个文件（不要 `pm clear`、不要卸载——会删存档）。

### 4.1 当前设备前置条件与实测（Phase 1.1.6）

推送 Mods **只是必要条件，不是充分条件**：Loader 必须先被 `Bootstrap` 调起来。

| 事实 | 值 | 含义 |
| --- | --- | --- |
| 设备 | `emulator-5554`，Android 12 / API 32，x86_64 + `libnb.so` | Emulator（translated），不是 ARM64 physical |
| 设备端 APK SHA-256 | `65e1e2af…051f9d`（388,967,580 字节） | 与本地 `app-debug.apk` 逐字节同一份 |
| APK 内 `assemblies/assemblies.blob` | `c8ee504e…cabd9` = 仓库原始 blob | **未注入 Bootstrap 的基线 APK** |
| APK 内 `ModFramework` / `P114` / `MonoRuntimeProbe` 条目 | 0 | 运行时不认识 `Mods/` 目录 |

因此，在这台设备上**只推送 `Mods/` 再启动游戏，不会出现 `loader-report.txt`，也不会出现
`probe-output/phase1.1.4.txt`**。要拿到 R-01…R-14 的真实设备结果，必须先把带 `Bootstrap` 的
APK 装到设备上（那是**装机批准**，不在 Phase 1.1.6 的推送/启动批准范围内）。
本阶段据实把这类结果记为 **BLOCKED（前置条件缺失）**，不补造数据。

**实测到的第二个前置条件（Phase 1.1.6，Emulator）**：游戏冷启动时会把内部
`/data/data/<pkg>/files/Mods/` **迁移到外部** `/sdcard/Android/data/<pkg>/files/Mods/`，
字节不变（11/11 SHA-256 相同），日志为
`[SDV_INIT] GetExternalFilesDir passed! There are files/folders in GetFilesDir that need to be copied.`。
因此：

- 部署后的"内部路径校验"必须在**启动前**做；启动后要到外部路径看；
- 若将来 Bootstrap 读的是 `Context.FilesDir`，它在第一次冷启动后就会找不到 Mods；
- 部署路径必须与 Bootstrap 实际使用的根一致，否则"文件确实在设备上"也不等于"Loader 能读到"。

MuMu 12（x86_64 + `libnb.so` 翻译）上跑出来的结果只能记为 **Emulator**，不能写成 ARM64 真机。

## 5. 用例

| ID | 内容 | 由谁观察 |
| --- | --- | --- |
| R-01 | `SHA256.HashData(byte[])` 空向量（长度 + 是否等于标准向量） | mod |
| R-02 | `abc` 向量（`ba7816bf…0015ad`） | mod |
| R-03 | 这个 mod 是否真的经 preflight/staging/LoadFrom/Initialize 跑起来 | `loader-report.txt` |
| R-04 | 同一路径连续 `LoadFrom` 两次 | mod |
| R-05 | 相同身份/相同字节、不同路径 | mod |
| R-06 | 相同身份/不同字节、不同路径（**不预设** Mono 返回哪一种） | mod |
| R-07 | 不同 SimpleName 的两个 fixture | mod |
| R-08 | `Assembly.GetName()` 的 Name/Version/Culture/PublicKeyToken | mod |
| R-09 | `Assembly.Location` 是否可读/为空（只写文件名） | mod |
| R-10 | `AppDomain.CurrentDomain.GetAssemblies()` 数量、包含关系、顺序 | mod |
| R-11 | manifest 声明的 private dependency 是否被 Loader 先加载并可调用 | mod |
| R-12 | `preflight-stage=complete` 是否先于第一条 `assembly-load=`，路径是否 `.staging` | `loader-report.txt` + 宿主测试 |
| R-13 | 生产路径：实际被 `LoadFrom` 的那份文件能否用 SHA-256 摘要成功 | mod |
| R-14 | 单用例失败是否被隔离（损坏 fixture + 故意抛异常） | mod |

状态语义：`PASS` = 观察完成并记录了真实返回值（**不代表**行为符合桌面 .NET 的预期）；
`FAIL` = API 抛异常，异常类型已记录；`BLOCKED` = 前置条件不成立（fixture 缺失）；
`NOT RUN` = 未执行（R-03/R-12 由 Loader 侧观察，mod 自己不会声称跑过）。

## 6. 报告示例（桌面宿主，节选）

```text
P114 RuntimeProbe report
schema      : phase1.1.4/safe/1
mode        : safe-only (no P/Invoke, no mprotect, no RWX, no detour, no game calls)
mod         : P114.RuntimeProbe 1.1.4
mod-dir     : P114.RuntimeProbe
R-01 PASS : callable=yes len=32 hex=e3b0c442…b855 matches-vector=yes
R-06 PASS : same-bytes=no(byte 136) load-a=ok load-b=FileLoadException: … marker-a=variant=A …
R-11 PASS : marker=p114-private-dependency-alive:P114.RuntimeProbe.Dependency v1.0.0.0 preloaded-before-entry=True
summary: cases=14 observed=12 pass=12 fail=0 blocked=0 not-run=2
```

（桌面 .NET 上 R-06 的第二个 `LoadFrom` 抛 `FileLoadException` —— "Assembly with same name is already
loaded"。这是**桌面**观察值；Mono/Android 上的真实行为要等设备运行，本阶段不得外推。）
