# Phase 1 报告：最小可用 Mod Loader（离线实现完成 / 设备验证 BLOCKED）

- 报告日期：2026-11-05
- 阶段：**Phase 1**
- 前置门禁：Phase 0.5.1 Gate 0 = PASS（见 `phase0.5.1-report.md` §8）
- 设计文档：`../architecture/phase1-loader-design.md`、`../architecture/phase1-runtime-constraints.md`
- 格式文档：`../modding/manifest-spec.md`、`../modding/creating-a-mod.md`

### 状态

| 维度 | 状态 |
| --- | --- |
| **Offline Implementation** | **COMPLETE**（Manifest / 依赖 / Loader / 契约 / HelloWorld / 测试 / 文档全部落地） |
| **Host End-to-End（桌面宿主）** | **PASS**（125 项内部检查 + HelloWorld 真实链路） |
| **Android Mono 集成（E1/E2）** | **BLOCKED**：无 ARM64 物理设备；未获装机授权；模拟器上未重跑 |
| **Overall Phase 1** | **Offline Implementation Complete / Device Validation Blocked**（**不是**全端到端验收完成） |

> 明确不宣称：没有 Harmony/方法补丁，不宣称 SMAPI 兼容，没有 Events/Content/Data/Save API，
> 没有在真实设备上跑过任何 mod。

---

## 1. 唯一验收目标与达成情况

目标：受信任的 HelloWorld C# Mod 完成
`扫描 → 发现 Manifest → 校验 → 版本兼容 → 依赖排序 → 加载 DLL → 校验入口 → 实例化 → Initialize → 带 mod id 的日志`，
且单个 mod 失败被隔离。

| # | 环节 | 宿主证据 | 设备证据 |
| --- | --- | --- | --- |
| 1 | 扫描 Mods 根目录（一层） | `discovery_*` 6 项 | BLOCKED |
| 2 | 发现子目录 + Manifest | `discovery_valid_manifest` | BLOCKED |
| 3 | 校验 Manifest / ID / 版本 / 入口 | `manifest_fixture_*` 25 项 | BLOCKED |
| 4 | 游戏/框架版本兼容 | `host_game_version_mismatch_rejected` 等 2 项 | BLOCKED |
| 5 | 依赖解析 + 确定性顺序 | `dep_*` 11 项 + `host_load_order` | BLOCKED |
| 6 | `Assembly.LoadFrom` 加载 mod DLL | `host_good_initialized` | BLOCKED |
| 7 | 入口类型符合 `IModEntry` | `host_not_an_entry` / `host_abstract_entry` / `host_no_default_ctor` / `host_internal_entry` | BLOCKED |
| 8 | 实例化 + 调用 `Initialize` | `host_entry_really_ran`（marker 文件） | BLOCKED |
| 9 | 通过 `IModContext` 写带 mod id 的日志 | `host_mod_log_*` 4 项 | BLOCKED |
| 10 | 失败隔离 + 下游阻止 | `host_throwing_initialize` / `host_dependent_blocked` / `host_unrelated_mod_unaffected` | BLOCKED |
| 11 | 离线宿主 + 设备分别给证据 | ✅ 宿主 | ❌ BLOCKED |

**结论**：1–10 在**宿主**层面全部有可复核证据；11 只有宿主一半。
因此 Phase 1 **不能**标 complete。

---

## 2. 架构

```
Mods/  (ModFramework.Runtime.dll 所在目录)
├── ModFramework.Runtime.dll        ← Bootstrap 加载它，Loader 在这里跑
├── loader.log / loader-report.txt  ← 每次运行覆盖
├── logs/<mod id>.log               ← 每个 mod 追加
└── <任意目录>/manifest.json + entry dll (+ privateAssemblies)
```

`ModFramework.Runtime` 内部分层（详见 `phase1-loader-design.md` §2）：

| 层 | 类型 | 只依赖 |
| --- | --- | --- |
| 公共契约 | `IModEntry` / `IModContext` / `IModLogger` / `ModLogLevel` | BCL |
| 解析 | `JsonParser` / `SemVersion` / `VersionRange` / `ManifestReader` | 契约无关 |
| 图 | `DependencyResolver`（环检测 + Kahn 排序） | `ModManifest` |
| 发现 | `ModDiscovery` | `ManifestReader` |
| 执行 | `Loader` / `ModLogger` / `ModContext` / `ModRecord` | 以上 |
| 诊断 | `ErrorCodes`（稳定码表） | 无 |

`Loader` 的公共面只有两个方法：`Start()`（Bootstrap 调用，无参）和 `Run(string modsRoot)`
（测试/自定义宿主）。**不能**再加一个叫 `Start` 的重载：Bootstrap 的 IL 用
`Type.GetMethod("Start")`，多态会抛 `AmbiguousMatchException`。

---

## 3. Manifest 格式（schemaVersion 1）

权威实现 `Mod/ModFramework.Runtime/ModManifest.cs`；机器可读 schema
`docs/modding/manifest.schema.json`；25 个夹具 `Tools/tests/fixtures/manifests/`。

最小字段：`schemaVersion, id, name, version, entryAssembly, entryType`；
可选：`author, description, website, gameVersion, frameworkVersion, dependencies, privateAssemblies`。

关键决策：

| 决策 | 理由 |
| --- | --- |
| 未知字段**拒绝**（不宽容） | 拼写错误（`entryAssebly`）会静默变成"缺字段"，更难查；新增字段就升 schemaVersion |
| `entryAssembly` 只能是**纯文件名** | 一次性消灭绝对路径、`..`、分隔符混淆、包外指向 |
| 重复 JSON key **拒绝** | "最后一个赢"会让两个读者看到不同内容 |
| 版本用**自写 SemVer** | 运行时没有 `Version.TryParse`；且 `Version` 的排序不符合 SemVer 预发布规则 |
| 范围只支持 **AND** | `||` 会让解析/测试面翻倍，本阶段不需要 |
| `privateAssemblies` **显式声明** | `LoadFrom` 不探测同目录依赖（实测），`Directory.GetFiles` 又不存在 |

---

## 4. ModEntry 契约与上下文

```csharp
public interface IModEntry { void Initialize(IModContext context); }

public interface IModContext {
    string ModId { get; } string ModName { get; } string ModVersion { get; }
    string GameVersion { get; } string FrameworkVersion { get; }
    string ModDirectory { get; } IModLogger Log { get; }
}

public interface IModLogger {
    void Info(string message); void Warn(string message);
    void Error(string message); void Error(string message, Exception exception);
}
```

- **无参数构造函数 + `Initialize(context)`**，不做构造函数注入（没有运行时理由支持更复杂的方案）。
- 同步契约：在 UI 线程调用；不支持 async（当前 Mono 同步上下文未验证，不引入）。
- `IModContext` **刻意不暴露** `Game1` 或任何游戏类型：那需要先验证调用时机与线程，本阶段没有证据。
- 类型身份：mod 必须引用**同一份** `ModFramework.Runtime`；若包里带了第二份，
  `IsAssignableFrom` 失败，Loader 以 `MF0204` 拒绝并说明"来自哪个程序集"。

---

## 5. 依赖解析算法

1. 建立 `id → manifest`（大小写不敏感）。
2. 逐条检查依赖：缺失（必需）→ `MF0401`；版本不满足 → `MF0402`；可选缺失 → 记 warning。
3. 边方向：依赖 → 被依赖者（`A ← B` 表示 B 依赖 A）。
4. DFS 找环（白/灰/黑），把整条环写进报告（`a -> b -> a`），环上节点标 `MF0403`。
5. Kahn 拓扑排序，**就绪集合按 `id` 序号排序**；有问题的节点统一排到末尾并按 id 排序。
6. 运行期：某个依赖没 `Initialized` → 下游 `MF0404`，不执行其入口。

确定性有测试：`dep_deterministic`（同一集合不同声明顺序）、`discovery_deterministic`
（两个目录用相反顺序创建）。

---

## 6. 加载时序

见 `phase1-loader-design.md` §3（含 ASCII 流程图）。要点：

- **先全部发现 + 校验，再解析依赖，最后按拓扑序加载/初始化**——不在扫描阶段执行任何 mod 代码。
- 每个 mod 的阶段都有独立 `try/catch`，异常被转换成稳定错误码 + 该 mod 的日志。
- `AssemblyLoaded` 与 `Initialized` 是两个状态，报告里分别可见。
- 同一 Mods 根目录**每进程只初始化一次**；第二次调用返回 `null` 并写
  `result=already-initialized`（测试：`host_second_run_does_not_reinitialize`，日志行数不变）。
- 重入（mod 在 `Initialize` 里再调 `Run`）被静态标志挡住 → `MF0901`（测试：`host_reentrancy_guard`）。

---

## 7. 新增 / 修改文件

### 7.1 运行时（`Mod/ModFramework.Runtime/`）

| 文件 | 说明 |
| --- | --- |
| `Contracts.cs`（新） | `IModEntry` / `IModContext` / `IModLogger` / `ModLogLevel` |
| `Json.cs`（新） | 严格 JSON 解析器（深度 16、256 KiB、重复 key 拒绝、`\u` 转义） |
| `SemVersion.cs`（新） | SemVer 2.0.0 + 范围语法（`*` / `=` / `>` / `>=` / `<` / `<=` / `^` / `~`） |
| `ModManifest.cs`（新） | manifest 模型 + 全部校验（含 `privateAssemblies`） |
| `DependencyResolver.cs`（新） | 环检测 + 确定性拓扑排序 |
| `ModDiscovery.cs`（新） | 一层目录扫描、排除规则、重复 id |
| `ModState.cs`（新） | 状态机 + `ModRecord`（含 `history`） |
| `ModLogger.cs`（新） | 每 mod 一个日志文件；写失败熔断 |
| `ModContext.cs`（新） | `IModContext` 实现 |
| `ErrorCodes.cs`（新） | 46 个稳定错误码 + 文案 |
| `Loader.cs`（重写） | 编排、兼容性门禁、私有程序集加载、报告、重入/重复保护 |
| `ModFramework.Runtime.csproj`（改） | `Version=1.0.0`、`InternalsVisibleTo=ModFramework.Tests` |

### 7.2 示例 mod

| 文件 | 说明 |
| --- | --- |
| `Mod/ExampleMod.HelloWorld/manifest.json`（新） | 真实 manifest，`entryType=ExampleMod.HelloWorld.HelloWorldEntry` |
| `Mod/ExampleMod.HelloWorld/ModEntry.cs`（重写） | `HelloWorldEntry : IModEntry`（只用 context）+ `GameBindings`（编译期证明能引用 `Game1`，不被 Loader 调用） |
| `Mod/ExampleMod.HelloWorld/ExampleMod.HelloWorld.csproj`（改） | 引用契约（`Private=false`）+ 把 manifest 复制到输出 → **输出目录就是合法 mod 包** |

### 7.3 测试与工具

| 文件 | 说明 |
| --- | --- |
| `Tools/tests/ModFramework.Tests/`（新） | 125 项检查的控制台测试宿主（不用 xunit，无新 NuGet 依赖） |
| `Tools/tests/FixtureMods/FixtureMod/`（新） | 一个 dll 内含 8 种入口形态（正常/抛异常/抽象/无无参构造/非 public/非契约/重入/依赖者） |
| `Tools/tests/FixtureMods/FixtureMod.Dependency/`（新） | 私有依赖 dll（验证 `privateAssemblies` 解析） |
| `Tools/tests/fixtures/manifests/`（新） | 25 个 manifest 夹具 + `index.json` 期望表 |
| `Tools/tests/test_loader.py`（新） | 构建 + 运行测试宿主的驱动；`--refs` 时才编示例 mod |
| `Tools/tests/run-all.ps1`（新） | 一键跑全部本地套件 |

### 7.4 文档

| 文件 | 说明 |
| --- | --- |
| `docs/modding/manifest-spec.md`（新） | 字段、版本语义、依赖规则、46 个错误码、安全边界 |
| `docs/modding/manifest.schema.json`（新） | JSON Schema（与实现交叉校验） |
| `docs/modding/creating-a-mod.md`（新） | 从零创建、构建、安装、看日志、失败对照表 |
| `docs/architecture/phase1-runtime-constraints.md`（新） | 编译期能力矩阵、LoadFrom 依赖行为、启动时序、存储约束、未验证假设 |
| `docs/architecture/phase1-loader-design.md`（新） | 组件、时序、状态机、隔离矩阵、确定性、为什么不用 AssemblyResolve |
| `docs/audit/phase1-report.md`（新） | 本文件 |
| `README.md`（改） | 当前实际功能 / 支持版本 / 构建与测试 / 明确未实现 |

---

## 8. 测试矩阵

| # | 命令 | 结果 | 退出码 | 日志 |
| --- | --- | --- | --- | --- |
| 1 | `python Tools/tests/test_loader.py --refs .tmp/phase0.5/refs` | 9/9 驱动检查；内部 **125/125 PASS** | 0 | `.tmp/phase0.5.1/logs/test-loader.txt` |
| 2 | `python Tools/tests/test_mod_pipeline.py` | **27/27 PASS**（既有流水线，未回归） | 0 | `.tmp/phase0.5.1/logs/test-mod-pipeline.txt` |
| 3 | `python Tools/tests/test_ci_offline.py` | 13/13 PASS | 0 | `.tmp/phase0.5.1/logs/ci-offline.txt` |
| 4 | `dotnet build Mod/ModFramework.Runtime`（桌面 net8.0） | 0 / 0 | 0 | `test-loader.txt` |
| 5 | `dotnet build Mod/ModFramework.Runtime -p:AndroidRefsDir=…` | 0 / 0 → **运行时用到的每个 BCL 成员都存在于裁剪后的程序集** | 0 | `test-mod-pipeline.txt` |
| 6 | `dotnet build Mod/ExampleMod.HelloWorld -p:AndroidRefsDir=…` | 0 / 0（4608 字节 + manifest） | 0 | `test-loader.txt` |
| 7 | `dotnet build Tools/tests/ModFramework.Tests` | 0 / 0 | 0 | `test-loader.txt` |
| 8 | `powershell -File Tools/tests/run-all.ps1 -Refs …` | 6 suites / 6 PASS | 0 | 汇总输出 |

### 8.1 125 项检查的分布

| 组 | 数量 | 覆盖 |
| --- | --- | --- |
| `semver_*` | 14 | 解析、拒绝、预发布优先级、build 元数据不参与比较 |
| `range_*` | 13 | `*` / `>=` `<` / `^`（三种零主次）/ `~` / 裸版本 / `=` / 逗号 / 拒绝 `||` 与部分版本 |
| `json_*` | 15 | 结构、成员顺序、`\u` 转义、重复 key、尾随内容/逗号、未闭合、坏转义/数字、深度、大小、空文档 |
| `manifest_*` | 27 | 25 个夹具 + 索引可解析 + 夹具数量下限 |
| `discovery_*` | 6 | 候选计数、隐藏/下划线忽略、坏 manifest 隔离、重复 id 双拒、确定性 |
| `dep_*` | 11 | 链、缺失、版本不符、可选缺失/存在、环（含路径）、无关 mod 不受影响、菱形、确定性 |
| `host_*` | 34 | 端到端、11 种包形态、日志内容、私有依赖、加载顺序、二次运行、重入、空/缺根、版本门禁 |
| `hello_world_*` | 5 | 示例 mod 的真实 manifest 全链路 |

### 8.2 NOT RUN / BLOCKED

| 项 | 状态 | 原因 |
| --- | --- | --- |
| Android 设备上的 E1（mod 入口真实执行） | **BLOCKED** | 无 ARM64 物理设备；模拟器需要装机授权 |
| Android 设备上的 E2（只读反射 `Game1`） | **BLOCKED** | 同上 |
| 模拟器上重跑 E1/E2 | **NOT RUN** | 需要卸载+安装（丢存档），未获授权 |
| `Tools/deploy-apk.ps1` 实跑 | **NOT RUN** | 同上 |
| 远程 CI | **NOT RUN** | 未 push |

---

## 9. HelloWorld 证据

### 9.1 宿主（可复核，B 级）

`test_loader.py --refs …` 的最后 5 项来自**真实的** `Mod/ExampleMod.HelloWorld/manifest.json`
与用 `AndroidRefsDir` 编译出的 `ExampleMod.HelloWorld.dll`：

| 检查 | 断言 |
| --- | --- |
| `hello_world_run_returns_null` | `Loader.Run` 返回 `null` |
| `hello_world_discovered` | 报告里 `id=ExampleMod.HelloWorld state=Initialized` |
| `hello_world_history_complete` | `ManifestValidated>DependenciesResolved>AssemblyLoaded>EntryValidated>Instantiated>Initialized` |
| `hello_world_log_message` | 日志含 `Hello World from "Hello World" 1.0.0` |
| `hello_world_log_versions` | 日志含 `framework=1.0.0` |

对应的报告片段（宿主临时目录）：

```
mods-discovered=1
mods-initialized=1
mods-failed=0
result=ok
mods:
  - id=ExampleMod.HelloWorld state=Initialized
    history=ManifestValidated>DependenciesResolved>AssemblyLoaded>EntryValidated>Instantiated>Initialized
```

### 9.2 模拟器

**NOT RUN**。原因：需要在设备上安装注入后的 APK（会卸载原游戏 → 丢存档），本阶段未获授权。
因此**没有任何模拟器证据**可以填。

### 9.3 ARM64 真机

**BLOCKED**。没有物理设备；且 E1/E2 的判定规则要求真实 ARM64 设备。

> 宿主测试是**逻辑证据**，不是 Android/Mono 运行证据。二者不能互相替代。

---

## 10. 关键发现：`Assembly.LoadFrom` 不解析同目录依赖

这是本阶段唯一一个"设计被测试推翻"的地方，值得单独记录。

**现象**（第一次跑宿主测试就暴露）：

```
id=FixtureMod.Good state=Failed code=MF0309
  message=FileNotFoundException: Could not load file or assembly 'FixtureMod.Dependency, Version=1.0.0.0'
```

`Assembly.LoadFrom("<mod>/FixtureMod.dll")` 之后，`FixtureMod.dll` 里调用的
`FixtureMod.Dependency` **没有**从 `<mod>/` 目录被找到——.NET Core 的 `LoadFrom` 走宿主正常探测，
不是"加载目录探测"（那是 .NET Framework 的行为）。

**约束叠加**：`Directory.GetFiles` 在裁剪后的运行时不存在，无法枚举目录里的 dll。

**最终设计**：manifest 的 `privateAssemblies` 显式声明 + 入口加载前逐个 `LoadFrom`
（两轮 fixpoint，与声明顺序无关）。好处：不依赖运行时"碰巧探测"、不需要全局
`AssemblyResolve`（那会让 mod 目录变成全局搜索路径，有劫持风险）、未声明的 dll 不会被加载。

**测试**：`host_private_dependency_resolved` 断言 mod 日志里出现
`fixture-dependency-alive`，即依赖真的从包目录被加载并执行了。

---

## 11. 未解决 / 风险

| # | 项 | 状态 | 说明 |
| --- | --- | --- | --- |
| 1 | 设备端 E1/E2 | **BLOCKED** | 唯一硬阻塞 |
| 2 | `Directory.GetDirectories` 在 Mono/Android 上的**行为** | 未验证 | 只有编译期证据；设备上第一次跑就知道 |
| 3 | `AppDomain.CurrentDomain.GetAssemblies()` 在 Mono 上是否含 `StardewValley` | 未验证 | 影响 `game-version=`；找不到就不做版本判定（不误拒） |
| 4 | 同名不同版本私有依赖 | 已知限制 | 先加载者胜；不同 mod 携带同名库会互相影响。文档已写明，未做冲突拒绝 |
| 5 | `Initialize` 在 UI 线程同步执行的耗时上限 | 未验证 | 当前示例只写日志；将来需要异步/分帧时要先验证 |
| 6 | 日志文件增长 | 已知 | mod 日志追加、框架日志覆盖；没有轮转 |
| 7 | `gameVersion` 语义 | 已定 | 取 `major.minor.build`，丢弃 revision；写进 `manifest-spec.md` |

## 12. Phase 2 尚未实现（明确边界）

- Harmony / MonoMod / Reflection.Emit / native detour / 方法重写 / Hook；
- SMAPI 兼容层（不宣称兼容任何现有 SMAPI mod）；
- Events / Content / Data / Asset 管线 / Save Data API；
- 配置 UI、游戏内 Mod 管理器、独立 Android 管理器；
- 自动下载 / 市场 / 自动更新；
- 多版本游戏并行安装；
- 热重载 / 运行时卸载程序集；
- 不受信任 mod 的沙箱（**当前 mod dll 在游戏进程内以完整权限执行**）。

---

## 13. 可恢复 checkpoint

```
HEAD            : b51af45b14a9574639fa3cefe7250d86cca9ad35（未提交）
branch          : main（= origin/main）
工作区          : 16 个已跟踪文件修改 + 新增（未 commit）
Gate            : Phase 0.5.1 Gate 0 = PASS；Phase 1 Gate 1–4 = PASS（离线）；Gate 5 = 离线部分 PASS
测试            : test_ci_offline 13/13、test_device_precheck 10/10、test_mod_pipeline 27/27、
                  test_loader 9/9 + 125/125、run-all 6/6 suites
设备            : E0 PASS（C 级，x86_64 模拟器）；E1/E2 BLOCKED
下一步（按优先级）:
  1) 接一台 ARM64 物理设备 + 取得装机授权 → 按 phase0.5-device-checklist.md 跑 E0/E1/E2；
     此时才可能把 Phase 1 从 "Device Validation Blocked" 升级。
  2) 在设备上验证 Directory.GetDirectories 行为与 game-version 探测（§11 #2/#3）。
  3) 需要远程 CI 结果时：由用户批准后 commit/push，再看 .github/workflows/ci.yml 的运行。
  4) Phase 2 之前先决策：Harmony/NDK 路线（ADR 仍 Proposed）。
```

---

## 14. 变更记录

| 日期 | 变更 |
| --- | --- |
| 2026-11-05 | 初版：Phase 1 最小 Loader 的离线实现、测试矩阵、HelloWorld 宿主证据与设备 BLOCKED 状态 |
