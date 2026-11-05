# Phase 1.1 报告：Loader 加固（离线实现完成 / 设备验证仍 BLOCKED）

- 报告日期：2026-11-05
- 阶段：**Phase 1.1**（Phase 1 的最小可用 Loader 之后的加固阶段，不是 Phase 2）
- 基线：`f2e86b467203cda77f6f76c3cca6ff629460656e`（branch `main` = `origin/main`，开始时工作区干净）
- 上一阶段：[`phase1-report.md`](phase1-report.md)（历史事实未改动，仅加了交叉引用）
- 设计文档：[`../architecture/phase1-loader-design.md`](../architecture/phase1-loader-design.md)
- 规范文档：[`../modding/manifest-spec.md`](../modding/manifest-spec.md) §3.4 / §5 / §8 / §9
- 证据索引：[`phase1.1-evidence-index.md`](phase1.1-evidence-index.md)

### 状态

| 维度 | 状态 |
| --- | --- |
| **Offline Implementation** | **COMPLETE**（四个问题全部按 Fail-Closed 语义实现） |
| **Host End-to-End（桌面宿主）** | **PASS**：`test_loader` 206/206 宿主检查 + 19/19 驱动检查 |
| **AndroidRefsDir 编译（目标引用集）** | **PASS**：`ModFramework.Runtime` 与 `ExampleMod.HelloWorld` 均 0 warning / 0 error |
| **红基线（修复前代码跑新测试）** | **PASS**：4/4 缺陷在旧代码上复现，证明回归测试非自证 |
| **Android Mono 集成（E1/E2）** | **BLOCKED**：无 ARM64 物理设备；未获装机授权（与 Phase 1 相同） |
| **远程 GitHub Actions** | **NOT RUN**：本任务不 commit / 不 push，没有可声称的远程结果 |
| **Overall Phase 1.1** | **Offline Hardening Complete / Device Validation Blocked** |

> 明确不宣称：没有 Harmony/方法补丁，不宣称 SMAPI 兼容，没有在真实设备上跑过任何 mod，
> 也没有把桌面宿主结果当成 Android Mono 运行证据。

---

## 1. 基线核对

| 项 | 值 |
| --- | --- |
| 仓库根 | `D:\Download\DSH\StardewValley`（本次任务唯一写入位置；隐私扫描记为 INFO 级） |
| remote | `origin https://github.com/Hiyomi-Kuro/StardewValley.git` |
| branch | `main` |
| HEAD | `f2e86b467203cda77f6f76c3cca6ff629460656e` |
| `origin/main` | `f2e86b4`（与 HEAD 相同；**没有** fetch / pull / rebase） |
| 初始 `git status` | 干净（无 dirty、无 untracked） |
| 工具链 | 项目私有 SDK `.tmp/phase0.5/dotnet/dotnet.exe`（8.0.404）；系统 `dotnet` 无 SDK |
| 参考程序集 | `.tmp/phase0.5/refs` 中 65 个游戏程序集（gitignored） |
| Python | 3.12.10 |

执行前重新读取了 `Loader.cs` / `CheckCompatibility` / `RunLegacyProbe` / `LoadPrivateAssemblies` /
`ModManifest.cs` / `ModLogger.cs` / `HostTests.cs` / `ErrorCodes.cs` / CI workflow，
行号与任务描述一致；**未发现远程或本地 HEAD 前进**，因此不存在覆盖后续改动的风险。

---

## 2. 四个问题的根因与修复

### 问题 A：正常 Loader 会执行绕过 Manifest 的 `LegacyProbe` DLL

**根因**：`RunCore` 在写完 `result=ok` 之后无条件调用 `RunLegacyProbe(modsRoot)`。
它只看 `<Mods>/MonoRuntimeProbe.dll` 是否存在，就直接 `Assembly.LoadFrom` + 反射调用
`MonoRuntimeProbe.Probe.Run`。这条路径不经过 `ModDiscovery`、不要求 `manifest.json`、
不做依赖解析、不受 `IModEntry` 契约约束——等于给 Mods 根目录开了一个额外的代码执行入口。

**修复**

| 位置 | 变化 |
| --- | --- |
| `Loader.cs` | 删除 `LegacyProbeAssembly` 常量与 `RunLegacyProbe` 方法（无残留死代码） |
| `Loader.cs` | `probe=` 行保留，值固定为 `not-run-by-loader`（旧报告读者看到明确状态而不是字段消失） |
| `Mod/MonoRuntimeProbe/README.md` | §4 重写：明确"正常 Loader 不再执行"，并给出两种**显式**启动方式 |
| `README.md` | 目录树与功能边界都标注"需显式启动；Loader 不再自动调用" |

**保留**：`Mod/MonoRuntimeProbe` 与 `MonoRuntimeProbe.Host` 项目**没有删除**，Phase 0 的历史
证据和后续设备诊断仍然需要它们。没有任何"环境变量开关"或隐式自动行为。

**回归测试**：`legacy_probe_*` 共 8 项（见 §6），其中包括一个反证：
显式调用探针**会**产生 marker，因此"marker 不存在"这条断言不是恒真。

### 问题 B：显式 `gameVersion` 约束是 Fail-Open

**根因**：`CheckCompatibility` 里三条 `return null`——游戏版本未知、游戏版本不是 SemVer、
框架版本解析失败——都只写一条 warning 就放行。对**明确要求了版本**的 mod，这等于在版本
无法确认时绕过它自己声明的兼容性保护。

**修复**（`Loader.cs`）

`DetectGameVersion` 从"两个布尔/字符串"改成三态 + 来源：

| 状态 | 触发条件 | 报告字段 |
| --- | --- | --- |
| `Known` | 找到名为 `StardewValley` 的程序集且版本可解析 | `game-version-state=known` |
| `Invalid` | 找到该程序集但版本不是合法 SemVer | `game-version-state=invalid` |
| `Unknown` | 完全没有该程序集 | `game-version-state=unknown` |

`game-version-source=` 记录 `entry-assembly` / `appdomain` / `none` / `override`。
`MapVersion` 不再"解析不了就返回 null 变成 unknown"，而是把检测值原样带出来交给分类，
因此"检测到但非法"不会再塌缩成"未知"。

门禁语义（`CheckCompatibility`）：

| manifest | 实际版本 | 结果 | 码 |
| --- | --- | --- | --- |
| `gameVersion: "*"` | 任意 | 放行 | — |
| 显式范围 | 已知 + 满足 | 放行 | — |
| 显式范围 | 已知 + 不满足 | 拒绝 | `MF0501`（不变） |
| 显式范围 | 未知 | 拒绝 | `MF0503`（新） |
| 显式范围 | 检测值非法 | 拒绝 | `MF0504`（新） |
| `frameworkVersion: "*"` | 任意 | 放行 | — |
| 显式框架范围 | 运行时版本无法解析 | 拒绝 | `MF0505`（新） |
| 显式框架范围 | 已知 + 不满足 | 拒绝 | `MF0502`（不变） |

错误码分配说明：任务示例里提到的 `MF0502` 在 Phase 1 已被 `FrameworkVersionUnsupported`
占用，**没有覆盖既有码**，而是新分配 `MF0503`–`MF0505`（`ErrorCodes.cs` 注释里写明）。

拒绝发生在 `Assembly.LoadFrom` / 构造函数 / `Initialize` 之前——测试用三种独立证据断言：
`history` 里没有 `AssemblyLoaded`、入口 marker 文件不存在、程序集不在 `AppDomain` 里。

`GameVersionOverride` / `FrameworkVersionOverride` 仍是 `internal` 测试 seam，
每个用例在 `finally` 里恢复，不污染其他测试。

### 问题 C：私有程序集缺少跨 Mod 的全局身份冲突预检

这是本阶段工作量最大的一项，见 §3（关键发现）与 §4（设计）。

**根因**：`LoadPrivateAssemblies` 在逐个 mod 初始化时直接 `Assembly.LoadFrom`，
没有任何"全体先看一眼"的阶段。当两个 mod 携带同名但不同版本/内容的库时，结果由
**加载顺序**决定。

**红基线实测**（旧代码，`.tmp/phase1.1/logs/red-baseline.txt`）：

```
- id=Red.VersionB state=Failed code=MF0202
  message=private assembly could not be loaded (1 pending):
          DiffVersionLib.v2.dll: FileLoadException: Assembly with same name is already loaded
```

也就是说旧行为不是"两个都跑"，而是**先加载者赢、后者拿到一个完全不提真实原因的
`FileLoadException`**。这个失败信息会误导 mod 作者去查自己的 dll。

**修复顺序**（现在是 `RunCore` 里的显式阶段，不再在循环内部）：

```
发现 Manifest → 依赖图解析 → 版本门禁 → 全局程序集身份预检 → 冲突/依赖阻断传播 → 才允许 LoadFrom
```

**新错误码**：`MF0205`（读不出身份 / 不是托管程序集）、`MF0206`（跨 mod 身份或内容冲突）、
`MF0207`（与宿主已加载程序集冲突）。

### 问题 D：`ModLogger.IsBroken` 没有进入 Loader 报告

**根因**：`ModLogger` 自己知道写失败（`_broken`），但 `Loader` 从不检查，
所以"日志已经不再落盘"这件事对使用者完全不可见。

**修复**

| 位置 | 变化 |
| --- | --- |
| `ModLogger.cs` | 记录首个失败的**异常类型名**（`IOException` 等），不记录消息/路径；`DescribeStatus()` 输出 `ok` / `degraded(<Type>)` |
| `ModLogger.cs` | `internal static Action<string,string>? AppendOverride`：唯一注入点，`null` 即生产路径 |
| `ModState.cs` | `ModRecord.LogStatus` / `LogFailureType` / `SetLogStatus` / `SharedAssemblies` |
| `Loader.cs` | `LoadAndInitialize` 的 `finally` 里统一落状态：成功、失败、异常三条路径都记 |
| `Loader.cs` | 报告新增 `logging=healthy|degraded (n of m mods)`、每 mod `log-status=`、degraded 时额外 `log-error=<Type>` 与一条 warning |
| `Loader.cs` | `WriteReport` 返回 bool；两个文件都写不出时 `Run` 返回 `MF0905` 诊断串，不谎称已落盘 |

关键语义：**日志失败不改变 mod 状态**。`Initialize` 正常返回 → 仍然 `Initialized`，
只是同时出现 `log-status=degraded log-error=IOException` 与一条 warning。
"一次失败后停止写文件"的熔断策略保留：测试断言注入的 append 只被调用 **1 次**（4 次日志调用中）。

---

## 3. 关键发现：裁剪后的运行时没有 `AssemblyName.GetAssemblyName`

这是本阶段唯一"设计被编译期证据推翻"的地方，值得单独记录，因为它决定了 §4 的实现形态。

### 3.1 验证方法

不是查文档，而是**用游戏自己的 65 个参考程序集编译**。做法是写一个每 API 一个方法的探针
（`ApiProbe`），用 `-p:AndroidRefsDir=<refs>` 编译；同时写一个**对照组**，包含本项目已知
被裁剪的成员（`Directory.GetFiles`、`Version.TryParse`）。对照组的用意是：如果它们也能编过，
说明这个验证方法本身没有分辨力。

### 3.2 结果

| API | 目标引用集 | 结论 |
| --- | --- | --- |
| `Assembly.LoadFrom(string)` | ✅ | 唯一可用的外部程序集加载方式 |
| `AssemblyName.Name/.Version/.CultureName/.FullName/.GetPublicKeyToken()` | ✅ | 对**已加载**程序集可用 |
| `Assembly.IsDynamic` / `Assembly.Location` | ✅ | 可用 |
| `AppDomain.CurrentDomain.GetAssemblies()` | ✅ | 可用 |
| `SHA256.HashData(byte[])` | ✅ | 可用 |
| `SHA256.HashData(Stream[, Span<byte>])` | ❌ | **被裁掉** |
| `SHA256.Create()` / `HashAlgorithm` / `IncrementalHash.CreateHash` | ❌（`HashAlgorithmName.SHA256` 也被裁） | 不可用 |
| `FileInfo` / `DirectoryInfo` | ❌ | 不可用 |
| `Directory.GetFiles`（对照） | ❌ | 对照组符合预期 |
| `Version.TryParse`（对照） | ❌ | 对照组符合预期 |
| **`AssemblyName.GetAssemblyName(string)`** | **❌** | **不存在**：它只在 `System.Private.CoreLib` 里作为 `internal static` 出现 |

结论：**目标运行时无法"不加载就读出程序集身份"**。任务里预判的两种退路
（`Mono.Cecil` 进 Runtime、或"先加载再检查"）都不能接受：前者要新增依赖且必须证明能在
裁剪运行时里跑，后者直接摧毁预检的意义。

### 3.3 采用的方案

在 Runtime 内实现一个**只读、无外部依赖**的 PE/CLI 元数据读取器（`AssemblyMetadata.cs`）：

- 解析 DOS/PE 头、可选头（PE32/PE32+）、节表、CLI 头、元数据根（`BSJB`）、`#~` 表流；
- 按 ECMA-335 表布局**算出** `Assembly` 表（0x20）行 0 的偏移（走过 0x00–0x1F 全部表，
  含 12 种 coded index 与 heap 索引宽度）；
- 从中读出 `Name` / `Major` / `Minor` / `Build` / `Revision` / `Culture` / `PublicKey` blob。

它同时充当"这是不是一个托管程序集"的判定器：坏文件、`.so`、只有 `MZ` 没有 PE 的文件都在
这里被拒绝（`MF0205`），不需要 `Assembly.LoadFrom` 兜底。

内容身份：`SHA256.HashData(byte[])`（存在）算 `sha256:<hex>`；若该调用在设备上抛异常，
退化为确定性的 `fnv1a64:<len>:<hex>`，并在报告前缀里写明用了哪一种。**不是跳过检查**。

**公钥**：`PublicKeyToken` 定义是公钥的 SHA-1 末 8 字节，而 SHA-1 同样被裁掉了。
因此身份比较使用"公钥的 SHA-256 指纹"，只用于**相等性**判定——语义上等价于 token 相等，
且不依赖不存在的算法。

### 3.4 尚未验证（BLOCKED，不写成 PASS）

| 项 | 状态 | 说明 |
| --- | --- | --- |
| PE/CLI 解析器在 MonoVM 上对**游戏真实程序集**的行为 | **BLOCKED** | 只有编译期证据 + 桌面宿主对真实 dll 的实测；设备未跑 |
| `SHA256.HashData` 在设备上是否真的可用（而不是编译期存在） | **BLOCKED** | 有 `libaot-System.Security.Cryptography.dll.so` 与原生 provider，但未验证；失败时按 §3.3 退化 |
| `AppDomain.GetAssemblies()` 在 Mono 上是否包含 `StardewValley` | **BLOCKED** | 与 Phase 1 §11 #3 相同，未变 |

这三项都不影响"冲突预检发生在加载之前"这一结构性结论——预检本身不依赖游戏程序集。

---

## 4. 程序集身份预检设计

### 4.1 输入与顺序

预检的输入是**已经通过 manifest 校验、依赖解析、版本门禁**的 mod 列表（按 mod id 序号排序，
与目录枚举顺序无关）。输出是一张表：每个 mod 的每个声明文件 → 允许怎么加载 / 为什么被拒绝。

预检只做三件事：读字节、算摘要、比较。**不**加载程序集、**不**解析类型、**不**触发静态构造函数。

### 4.2 冲突策略（书面决策）

| 情形 | 决策 | 码 |
| --- | --- | --- |
| 声明文件缺失 | 拒绝该 mod | `MF0201` |
| 文件不可读 / 损坏 / 非托管 / 超过 64 MiB | 拒绝该 mod | `MF0205` |
| 两个 mod 声明相同简单名，版本/Culture/公钥/内容任一不同 | **拒绝全部直接拥有者**（绝不 first-loaded-wins） | `MF0206` |
| 两个 mod 声明完全相同的身份与内容 | 共享：`(mod id, 声明文件名, 路径)` 序号最小者为代表，只 `LoadFrom` 一次 | — |
| 与宿主已加载程序集同名 + 同身份 + 内容可验证且相同 | 复用宿主实例，不加载包内副本 | — |
| 与宿主同名 + 同身份但宿主文件不可读 | 复用 + 报告 `preflight-warning`（内容相等性无法证明，显式记录、不静默） | — |
| 与宿主同名 + 同身份但内容不同 | 拒绝 | `MF0207` |
| 与宿主同名但身份不同 | 拒绝 | `MF0207` |

"同名"按**大小写不敏感**比较（与 CLR 绑定行为一致），且**不因为版本或 token 不同就假定可共存**——
恰好相反，那正是要拒绝的情况。

### 4.3 确定性

- mod 列表按 `mod id` 序号排序；组按简单名序号排序；组内按 `(mod id, 声明文件名, 路径)` 排序。
- 代表文件的选择、拒绝集合、报告行顺序都只取决于这些排序键，**不取决于**文件系统枚举顺序。
- 测试 `conflict_deterministic_across_enumeration_order` 用"同一组包、相反创建顺序"的两个 root
  比较 `preflight-conflict=` 行集合必须逐字相同。

### 4.4 与宿主的比较

宿主快照来自 `AppDomain.CurrentDomain.GetAssemblies()`（跳过 `IsDynamic`）。
同名多条时按 `(Version, Location)` 排序后逐条比较，避免"取第一个"带来的不确定。
身份相同且 `Location` 可读时，哈希宿主文件比较内容——这样"可安全复用"和"不兼容冲突"被真正区分开，
而不是靠加载顺序。

### 4.5 失败与阻断传播

- 拒绝按 mod 隔离：`record.Fail(code, message)`，其余 mod 继续。
- 被拒绝 mod 的**必需**下游在后续循环里通过 `FirstFailedDependency` 得到 `MF0404`，不执行入口。
- 可选依赖被拒绝不阻断（可选语义优先）。
- 无关 mod 正常 `Initialized`（测试 `conflict_unrelated_still_initialized`）。

### 4.6 报告

按顺序输出（全部在第一条 `assembly-load=` **之前**）：

```
preflight=assemblies=… conflicts=… shared=… host-shared=… host-unverified=… rejected=…
preflight-conflict=<simpleName> kind=mod owners=<id(role:file),…> identities=<…>
preflight-conflict=<simpleName> kind=host owner=<id> role=… identity=…
preflight-shared=<simpleName> canonical=<modId> representative=<file> owners=<n>
preflight-host-share=<simpleName> owner=<id> role=… content=same|unverified
preflight-warning=<simpleName> …
preflight-stage=complete
```

以及加载时的 `assembly-load=<modId> file=<name> canonical=<谁>`
和每个 mod 的 `shared=<file> -> <owner|host>`。
所有身份文本都是"路径无关"的：只有程序集名、版本、Culture、公钥指纹与内容摘要。

---

## 5. 新增 / 修改文件清单

### 5.1 运行时（`Mod/ModFramework.Runtime/`）

| 文件 | 类型 | 说明 |
| --- | --- | --- |
| `AssemblyMetadata.cs` | **新增** | 只读 PE/CLI 元数据读取器（`Assembly` 表行 0）+ `AssemblyFileIdentity` |
| `AssemblyIdentity.cs` | **新增** | `AssemblyClaim`/`AssemblyResolution`、声明文件读取与内容摘要、宿主身份快照 |
| `AssemblyPreflight.cs` | **新增** | 全局预检：分组建模、宿主比较、确定性代表选择、拒绝记录、报告行 |
| `PathRules.cs` | **新增** | `Normalize`/`IsInside`，Loader 与预检共用同一份包内判定 |
| `Loader.cs` | 改（重写） | 删除 LegacyProbe 执行路径；三态版本门禁；预检阶段；claim 驱动加载；日志状态；`MF0905` |
| `ErrorCodes.cs` | 改 | 新增 `MF0205`/`MF0206`/`MF0207`/`MF0503`/`MF0504`/`MF0505`/`MF0905` + 文案 |
| `ModLogger.cs` | 改 | 失败异常类型、`DescribeStatus()`、`AppendOverride` 注入点 |
| `ModState.cs` | 改 | `LogStatus`/`LogFailureType`/`SetLogStatus`/`SharedAssemblies` |

公开 Mod API（`IModEntry`/`IModContext`/`IModLogger`/`ModLogLevel`）**没有变化**：
`Contracts.cs` 未修改。`Loader` 的公共面仍然只有 `Start()` 与 `Run(string)`。

### 5.2 测试与夹具

| 文件 | 类型 | 说明 |
| --- | --- | --- |
| `Tools/tests/FixtureMods/FakeMonoRuntimeProbe/` | **新增** | 程序集名 `MonoRuntimeProbe`、含 `Probe.Run`、被调用即写 marker、**不引用任何东西** |
| `Tools/tests/FixtureMods/ConflictLib/` | **新增** | 参数化夹具：`ConflictAssemblyName`/`ConflictAssemblyVersion`/`Variant` → 同名多版本、同身份异内容 |
| `Tools/tests/FixtureMods/PlainLib/` | **新增** | 无引用库，可取名 `ModFramework.Runtime` 制造**宿主冲突**（有引用会被构建覆盖，测不到东西） |
| `Tools/tests/ModFramework.Tests/HostTests.cs` | 改 | 4 组新场景（`legacy_probe_*`/`gate_*`/`conflict_*`/`log_degraded_*`）+ 辅助断言 |
| `Tools/tests/ModFramework.Tests/UnitTests.cs` | 改 | `MetadataTests`：解析真实程序集、5 种坏输入、摘要稳定性/回退、`PathRules`、新错误码文案 |
| `Tools/tests/test_loader.py` | 改 | 构建 10 个参数化变体并放进 fixtures 目录；缺任何变体则 FAIL（不是 SKIP） |

### 5.3 CI 与文档

| 文件 | 类型 | 说明 |
| --- | --- | --- |
| `.github/workflows/ci.yml` | 改 | 新增 3 个夹具工程的构建，注释说明 `test_loader.py` 会构建变体并跑完整回归 |
| `README.md` | 改 | 功能边界补 Phase 1.1；新增"Loader 的四条硬语义"；测试数 125 → 206；"改 Loader"补裁剪 API 对照表 |
| `Mod/MonoRuntimeProbe/README.md` | 改 | 顶部标注"不属于常规 Loader 流程"；§4 重写为显式启动的两条路径 |
| `docs/modding/manifest-spec.md` | 改 | 新增 §3.4 程序集身份与冲突策略；§5 改为 Fail-Closed 语义表；§8 补 7 个错误码；§9 补 4 条边界 |
| `docs/architecture/phase1-loader-design.md` | 改 | 组件表补 4 个新文件；§3 时序图重画（含预检与 `preflight-stage=complete`）；`Run` 返回值补 `MF0905` |
| `docs/audit/phase1-report.md` | 改 | 仅**新增**交叉引用块与 §11 #4 的修复标注，历史事实与数字未改 |
| `docs/audit/phase1.1-report.md` | **新增** | 本文件 |
| `docs/audit/phase1.1-evidence-index.md` | **新增** | 每条证据的命令、时间、退出码、日志路径、SHA-256、等级、可复现性 |

`docs/modding/manifest.schema.json` **未修改**：本轮没有新增 manifest 字段，
只改变了 `gameVersion`/`frameworkVersion`/`privateAssemblies` 的**语义**，
而这些写在 `manifest-spec.md` 里。schema 的字段集与 `required` 保持与实现一致。

---

## 6. 新增测试清单

宿主测试从 125 项增加到 **206 项**：新增 83 项，替换掉 2 项旧的 Fail-Open 断言
（`host_game_version_unknown_does_not_block`、`host_game_version_mismatch_rejected`），净 +81。
逐项如下（"结果"来自本次本地运行，括号内为该前缀的实际检查数）。

### 6.1 P11-01 LegacyProbe（8 项）

| 测试名 | 目的 | 结果 |
| --- | --- | --- |
| `legacy_probe_run_returns_null` | 正常 Loader 仍然正常返回 | PASS |
| `legacy_probe_report_status` | 报告固定 `probe=not-run-by-loader` | PASS |
| `legacy_probe_marker_absent` | `probe-output/probe-marker.txt` 不存在（Run 未被调用） | PASS |
| `legacy_probe_output_directory_absent` | 连输出目录都没被创建 | PASS |
| `legacy_probe_assembly_not_loaded` | `AppDomain` 里没有 `MonoRuntimeProbe`（**区分"未加载"与"加载了但没跑"**） | PASS |
| `legacy_probe_neighbour_initialized` | 同一 root 里的合法 manifest mod 仍正常初始化 | PASS |
| `legacy_probe_bare_dll_not_a_package` | 裸 DLL 不被当成 mod 包（`mods-discovered` 不变） | PASS |
| `legacy_probe_marker_works_when_invoked_explicitly` | **反证**：显式调用会产生 marker，说明上面几条不是恒真 | PASS |

### 6.2 P11-02/P11-03 版本门禁（19 项）

| 测试名 | 目的 | 结果 |
| --- | --- | --- |
| `gate_game_version_unknown_rejected` | `>=1.0.0` + 未知 → `MF0503` | PASS |
| `gate_game_version_unknown_reported` | `game-version-state=unknown` 进报告 | PASS |
| `gate_game_version_source_none` | `game-version-source=none` | PASS |
| `gate_game_version_unknown_no_marker` | 入口未执行 | PASS |
| `gate_game_version_unknown_not_loaded` | 入口 DLL 未加载（`AppDomain` 断言） | PASS |
| `gate_game_version_unknown_history_stops` | `history` 中无 `AssemblyLoaded` | PASS |
| `gate_game_version_invalid_rejected` | 检测值 `not-semver` → `MF0504` | PASS |
| `gate_game_version_invalid_reported` | `game-version-state=invalid` | PASS |
| `gate_game_version_invalid_source` | `game-version-source=override` | PASS |
| `gate_game_version_invalid_not_loaded` | 非法版本被拒绝时 DLL 未加载 | PASS |
| `gate_framework_version_invalid_rejected` | 运行时框架版本无法解析 → `MF0505` | PASS |
| `gate_framework_version_invalid_not_loaded` | 该场景同样不加载 DLL | PASS |
| `gate_game_version_mismatch_rejected` | 已知 1.0.0 vs `>=99.0.0` → `MF0501`（不回归） | PASS |
| `gate_framework_version_mismatch_rejected` | `>=99.0.0` → `MF0502`（不回归） | PASS |
| `gate_game_version_wildcard_allowed` | `"*"` + 未知 → 放行 | PASS |
| `gate_game_version_wildcard_ran` | marker 存在（真的执行了） | PASS |
| `gate_framework_version_wildcard_allowed` | `frameworkVersion: "*"` 不触发门禁 | PASS |
| `gate_game_version_satisfied_allowed` | 已知 1.0.0 vs `>=1.0.0` → 放行 | PASS |
| `gate_game_version_satisfied_ran` | marker 存在 | PASS |

### 6.3 P11-04/P11-05/P11-06 程序集预检（22 项）

| 测试名 | 目的 | 结果 |
| --- | --- | --- |
| `conflict_version_both_rejected` | 同名不同版本 → **双方** `MF0206` | PASS |
| `conflict_version_group_reported` | 报告出现 `preflight-conflict=DiffVersionLib kind=mod` | PASS |
| `conflict_version_neither_loaded` | `DiffVersionLib` 不在 `AppDomain`（**加载前就判定**） | PASS |
| `conflict_version_no_markers` | 两个 mod 的入口都没跑 | PASS |
| `conflict_preflight_runs_before_first_load` | 报告里 `preflight-stage=complete` 在第一条 `assembly-load=` 之前 | PASS |
| `conflict_content_both_rejected` | 同身份不同内容 → 双方 `MF0206`（不是 first-wins） | PASS |
| `conflict_content_not_loaded` | 该程序集未被加载 | PASS |
| `conflict_shared_both_initialized` | 同身份同内容 → 双方 `Initialized` | PASS |
| `conflict_shared_canonical_reported` | 代表文件确定（`canonical=Conflict.SharedA`） | PASS |
| `conflict_shared_loaded_once` | `assembly-load=` 恰好 **1** 条（只加载一个代表文件） | PASS |
| `conflict_shared_owner_noted` | 分享方报告 `shared=… -> …` | PASS |
| `conflict_entry_vs_private_rejected` | 一个 mod 的 entry 撞另一个 mod 的 private → 双方 `MF0206` | PASS |
| `conflict_entry_vs_private_not_loaded` | 该程序集未被加载 | PASS |
| `conflict_downstream_blocked` | 冲突 mod 的必需下游 → `MF0404` | PASS |
| `conflict_unrelated_still_initialized` | 无关 mod 正常运行 | PASS |
| `conflict_host_assembly_rejected` | 包内 `ModFramework.Runtime, 9.9.9.0` 撞宿主 → `MF0207` | PASS |
| `conflict_host_group_reported` | 报告 `preflight-conflict=ModFramework.Runtime kind=host` | PASS |
| `conflict_corrupt_dll_rejected` | 512 字节随机数据 → `MF0205` | PASS |
| `conflict_not_managed_dll_rejected` | 只有 `MZ` 没有 PE → `MF0205` | PASS |
| `conflict_missing_private_rejected` | 缺失的私有程序集 → `MF0201` | PASS |
| `conflict_healthy_neighbour_survives` | 同一 root 的健康 mod 不受影响 | PASS |
| `conflict_deterministic_across_enumeration_order` | 相反创建顺序 → `preflight-conflict=` 行逐字相同 | PASS |

### 6.4 P11-08 日志降级（12 项）

| 测试名 | 目的 | 结果 |
| --- | --- | --- |
| `log_degraded_state_stays_initialized` | 日志失败 **不**改变 mod 状态 | PASS |
| `log_degraded_status_recorded` | `log-status=degraded log-error=IOException` | PASS |
| `log_degraded_summary` | 顶层 `logging=degraded (1 of 1 mods)` | PASS |
| `log_degraded_warning` | 同时有一条显式 warning | PASS |
| `log_degraded_stops_after_first_failure` | 熔断：注入的 append 只被调用 **1** 次 | PASS |
| `log_degraded_entry_still_ran` | marker 存在（Initialize 确实跑完） | PASS |
| `log_degraded_init_failure_kept` | Initialize 抛异常 + 日志失败 → `MF0309` 与 degraded **同时**保留 | PASS |
| `log_degraded_on_failure_path` | 失败路径也记 `log-status=degraded` | PASS |
| `log_degraded_failure_path_single_attempt` | 异常路径同样只尝试 1 次 | PASS |
| `log_healthy_status` | 正常路径 `log-status=ok` | PASS |
| `log_healthy_summary` | 顶层 `logging=healthy` | PASS |
| `log_healthy_file_written` | 日志文件真的有 INFO 与 WARN | PASS |

### 6.5 P11-07 运行时 API 与既有回归（21 项单元 + 示例 mod）

`metadata_*` 17 项、`path_rules_*` 3 项、`errorcodes_*` 1 项。

| 测试名 | 目的 | 结果 |
| --- | --- | --- |
| `metadata_reads_managed_identity` | 自写解析器能读出真实程序集身份 | PASS |
| `metadata_name_matches_loaded` / `metadata_version_matches_loaded` | 与 `Assembly.GetName()` 一致（解析器正确性的锚点） | PASS |
| `metadata_rejects_empty` / `_text` / `_truncated` / `_mz_without_pe` | 4 种坏输入都被拒绝（不抛、不猜） | PASS |
| `metadata_digest_is_sha256` / `_is_stable` | 内容摘要为 SHA-256 且稳定 | PASS |
| `metadata_digest_fallback_is_deterministic` / `_differs` | SHA-256 不可用时回退仍确定 | PASS |
| `metadata_key_fingerprint_fallback` / `_none_for_unsigned` | 公钥指纹（含无签名） | PASS |
| `errorcodes_new_codes_have_text` | 7 个新码都有 `Describe()` 文案 | PASS |
| `path_rules_inside` / `_rejects_sibling_prefix` / `_accepts_subdirectory` | 包内判定不让 `/mods/AB` 冒充 `/mods/A` | PASS |
| `hello_world_game_version_applied` | 示例 mod 的真实 `gameVersion` 范围被真正执行（`known` 状态） | PASS |

### 6.6 未回归

原有 125 项里 **123 项原样保留并通过**（`semver_*`/`range_*`/`json_*`/`manifest_fixture_*`/
`discovery_*`/`dep_*`/`legacy host_*`/`hello_world_*`）；剩下 2 项按任务要求**替换**：

| 旧断言（已删除） | 新断言 |
| --- | --- |
| `host_game_version_unknown_does_not_block`（未知版本仍放行） | `gate_game_version_unknown_rejected`（`MF0503`）+ `gate_game_version_wildcard_allowed`（`"*"` 仍放行） |
| `host_game_version_mismatch_rejected`（只查 `code=MF0501` 出现） | `gate_game_version_mismatch_rejected`（改为针对具体 mod id 的断言） |

被删除的是"旧语义正确"的断言，不是"测试没跑过"——它们在新语义下必然失败。

---

## 7. 测试矩阵（本次本地运行）

| # | 命令 | 退出码 | 通过/失败/跳过 | 日志 |
| --- | --- | --- | --- | --- |
| 1 | `python Tools/tests/test_ci_offline.py` | 0 | 13 / 0 / 0 | `.tmp/phase1.1/logs/run-all/ci-offline.txt` |
| 2 | `python Tools/privacy-scan.py --include-untracked` | 0 | 0 CRITICAL/HIGH，`RESULT: OK` | `.tmp/phase1.1/logs/run-all/privacy-scan.txt` |
| 3 | `powershell -File Tools/tests/test_device_precheck.ps1` | 0 | 10 / 0 / 0 | `.tmp/phase1.1/logs/run-all/device-precheck-selftest.txt` |
| 4 | `python Tools/tests/test_mod_pipeline.py --refs …` | 0 | 27 / 0 / 0 | `.tmp/phase1.1/logs/run-all/test-mod-pipeline.txt` |
| 5 | `python Tools/tests/test_loader.py --refs …` | 0 | 驱动 19 / 0 / 0；宿主 **206 / 0 / 0** | `.tmp/phase1.1/logs/run-all/test-loader.txt` |
| 6 | `powershell -File Tools/tests/run-all.ps1 -Refs …` | 0 | 6 suites / 6 PASS | `.tmp/phase1.1/logs/run-all-console.txt` |
| 7 | `git diff --check` | 0 | 无输出（无空白错误） | 见 §9 |
| 8 | `dotnet build Mod/ModFramework.Runtime`（桌面 net8.0） | 0 | 0 warning / 0 error | §8 |
| 9 | `dotnet build Mod/ModFramework.Runtime -p:AndroidRefsDir=…` | 0 | 0 warning / 0 error | §8 |
| 10 | `dotnet build Mod/ExampleMod.HelloWorld -p:AndroidRefsDir=…` | 0 | 0 warning / 0 error | §8 |
| 11 | 红基线（旧 Runtime + 新场景驱动） | 0 | **4 / 4 缺陷复现** | `.tmp/phase1.1/logs/red-baseline.txt` |

这次运行**没有** push，所以不存在新的远程 Actions 结果；`.github/workflows/ci.yml` 的改动
只有用户下次 push 时才会被 GitHub 执行。

### 7.1 红基线（证明测试不是自证）

`.tmp/phase1.1/logs/red-baseline.txt`：

```
bare-probe-executed=True
  old report probe line: probe=<mods根>/probe-output/probe.txt
conflict-is-load-order-dependent (loser gets opaque FileLoadException)=True
  old conflict report: initialized=Red.VersionA,
    - id=Red.VersionB state=Failed code=MF0202
      message=private assembly could not be loaded (1 pending):
              DiffVersionLib.v2.dll: FileLoadException: Assembly with same name is already loaded
explicit-game-range-fail-open=True
report-lacks-log-status=True

red-baseline: 4 of 4 defects reproduced on the pre-fix runtime
```

做法：`git archive HEAD Mod/ModFramework.Runtime` 取出**修复前**的字节，用同一个
`AssemblyName`/`AssemblyVersion` 编成 `ModFramework.Runtime`，再用只依赖
`Loader.Run(string)` 这个公共 API 的驱动程序跑四个场景。
因此"新测试能在旧代码上失败"是被实测的，不是推导的。

---

## 8. 构建结果

| 构建 | 命令 | 结果 |
| --- | --- | --- |
| Runtime（桌面） | `dotnet build Mod/ModFramework.Runtime -c Release` | 成功，0 warning / 0 error |
| Runtime（目标引用集） | `… -p:AndroidRefsDir=<65 assemblies>` | 成功，0 warning / 0 error |
| 测试宿主 | `dotnet build Tools/tests/ModFramework.Tests` | 成功，0/0 |
| FixtureMod / Dependency / ConflictLib / PlainLib / FakeMonoRuntimeProbe | `dotnet build …` | 成功，0/0 |
| 示例 mod（目标引用集） | `dotnet build Mod/ExampleMod.HelloWorld -p:AndroidRefsDir=…` | 成功，0/0 |

**Runtime 的 AndroidRefsDir 构建通过本身就是 P11-07 的核心证据**：新增代码用到的每个
BCL 成员都在裁剪后的参考集里存在，否则是编译错误而不是运行时 `TypeLoadException`。

---

## 9. 其它检查

| 检查 | 结果 |
| --- | --- |
| Python 语法（`test_ci_offline.py` 第 1 项） | PASS（含 `test_loader.py` 的改动） |
| PowerShell 语法（第 2 项） | PASS（本次未改 `.ps1`） |
| tracked text UTF-8（第 3 项） | PASS（新增 `.cs`/`.md`/`.py` 全部合法 UTF-8） |
| mojibake / 私用区字符（第 3 项） | PASS，`no_mojibake_in_tracked_text` |
| manifest 夹具与 schema 自洽（第 4/5 项） | PASS（本轮未改字段集，25 个夹具不变） |
| 隐私扫描 | `RESULT: OK`，0 CRITICAL/HIGH；新增文档只写 repo 相对路径，没有用户目录绝对路径 |
| `git diff --check` | 干净 |

> 隐私扫描的 INFO 级项里仍有历史文档写死仓库的绝对路径（仓库自身位置，
> 非机密）。本轮新增文档除 §1 的基线记录外一律使用相对路径，未扩大任何 allow-list。

---

## 10. P11 门禁表

| ID | 验收项 | 结果 | 证据 |
| --- | --- | --- | --- |
| P11-01 | LegacyProbe 不再执行 | **PASS** | `legacy_probe_*` 8 项；`probe=not-run-by-loader`；红基线 `bare-probe-executed=True` |
| P11-02 | `gameVersion` Fail-Closed | **PASS** | `gate_*` 19 项（含 `MF0503`/`MF0504`）；§2 问题 B |
| P11-03 | `frameworkVersion` Fail-Closed | **PASS** | `gate_framework_version_*` 4 项（含 `MF0505`） |
| P11-04 | 加载前身份预检 | **PASS** | `conflict_preflight_runs_before_first_load` + `assembly-load=` 行序；§4 |
| P11-05 | 冲突策略确定 | **PASS** | `conflict_version_*`/`conflict_content_*`/`conflict_shared_*`/`conflict_host_*`/`conflict_deterministic_*` |
| P11-06 | 依赖阻断传播 | **PASS** | `conflict_downstream_blocked` + `conflict_unrelated_still_initialized` |
| P11-07 | 运行时 API | **PASS（编译期）** | `AndroidRefsDir` 0/0；探针矩阵见 §3.2。**设备行为 BLOCKED**，见 §3.4 |
| P11-08 | Logger 状态进报告 | **PASS** | `log_*` 12 项（`log_degraded_*` 9 项 + `log_healthy_*` 3 项） |
| P11-09 | 无回归 | **PASS** | 206/206 宿主 + 19/19 驱动；`mod-pipeline` 27/27 |
| P11-10 | 文档一致 | **PASS** | README / manifest-spec §3.4·§5·§8·§9 / phase1 交叉引用 / 本报告 / 证据索引 |
| P11-11 | 本地 CI | **PASS（本地）**；远程 **NOT RUN** | `run-all` 6/6；未 push，不声称远程结果 |
| P11-12 | 设备 | **BLOCKED / NOT RUN** | 无 ARM64 物理设备；未装机；E1/E2 未执行 |

> 没有用"总测试数增加"替代逐项验收：上表每行都指向具体测试名或具体日志行。

---

## 11. 已知限制与未完成项

| # | 项 | 状态 | 说明 |
| --- | --- | --- | --- |
| 1 | Android Mono E1/E2（真机） | **BLOCKED** | 无 ARM64 物理设备、未获装机授权。桌面宿主与 AndroidRefsDir 编译**不能**替代 |
| 2 | PE/CLI 解析器在 MonoVM 上解析游戏真实程序集 | **BLOCKED** | 桌面已对真实 dll 实测；设备未验证 |
| 3 | `SHA256.HashData` 在设备上的可用性 | **BLOCKED** | 失败时按设计退化为 FNV-1a 并标注，但"退化了"这件事只有在设备上才知道 |
| 4 | 强签名程序集的 token 级比较 | 已知限制 | 运行时没有 SHA-1，用公钥 SHA-256 指纹做**相等性**判定；"双方都有强名且与宿主同名"一律按冲突处理（保守） |
| 5 | 宿主同名同身份但宿主文件不可读 | 已知限制 | 复用 + `preflight-warning`。不声称"内容相同"，只声称"身份相同"（`host-unverified` 计数可见） |
| 6 | 单个声明文件 64 MiB 上限 | 设计选择 | 超出 → `MF0205`。预检一次只保留一个文件在内存 |
| 7 | 预检耗时 | 已知成本 | 每个被接受的 mod 的每个声明 dll 读一遍并算摘要；mod 很多时会线性增长 |
| 8 | 与游戏真实 mod 生态的兼容性 | 未验证 | 本轮只用夹具与示例 mod |
| 9 | Harmony / 方法补丁 / SMAPI / 事件 / 内容 API | 未实现 | Phase 1.1 不涉及 |
| 10 | Manifest 字段集 | 未变 | 只改语义，`schemaVersion` 仍是 1；`manifest.schema.json` 未改 |

---

## 12. 明确声明

- **没有** `git commit`、`git push`、创建 Release 或修改远程分支。
- **没有** `adb install` / `uninstall`、覆盖安装、修改设备游戏目录或设备权限。
- **没有** 访问、移动、覆盖或删除任何真实存档。
- **没有** 安装新的 Android SDK / NDK / JDK / 全局 .NET SDK 或任何 NuGet 包。
- **没有** 修改 `app/src/main` 下的游戏内容、APK 构建逻辑或 dex。
- 所有写入都在仓库根（§1）内；可再生的中间产物只在 `.tmp/`（gitignored）。
- 开始时存在的 dirty/untracked 文件：**没有**（工作区干净）；本轮新增的文件全部列在 §5。

---

## 13. 变更记录

| 日期 | 变更 |
| --- | --- |
| 2026-11-05 | 初版：Phase 1.1 四个问题的根因、修复、预检设计、API 证据、206 项宿主测试与红基线 |
