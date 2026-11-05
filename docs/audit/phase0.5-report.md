# Phase 0.5 报告：审计收尾 · 文档/实现一致性 · Bootstrap 链复核 · ARM64 准入准备

- 报告日期：2026-11-05
- 阶段：**Phase 0.5**
- 后续修订：**Phase 0.5.1 复核并修正了本报告的证据等级表述**，见 `phase0.5.1-report.md`。
  本文件中的"模拟器 PASS"一律指 **RECORDED PASS（D 级历史记录）**，不是本阶段的直接运行证据。

### 总状态（三个维度分开看，不得合并成"完成"）

| 维度 | 状态 | 依据 |
| --- | --- | --- |
| **Offline Audit**（文档一致性、代码修复、离线回归、注入/容器复核） | **COMPLETE** | §4–§6、§8、§11；本轮测试有日志可复核 |
| **Device Validation**（ARM64 物理设备 E1/E2） | **BLOCKED: no ARM64 physical device** | 只有 x86_64 MuMu 模拟器（§7.1，C 级） |
| **Overall Phase 0.5** | **PARTIALLY COMPLETE**（不是 fully complete） | 真机验证缺失；模拟器结果只是 D 级历史记录 |

- 是否启动 Phase 1：**Phase 0.5 期间未启动**（Phase 1 于 Phase 0.5.1 门禁通过后开始，见 `phase1-report.md`）
- 关联：`phase0.5-evidence-index.md`（证据索引）、`phase0.5-device-checklist.md`（真机准入）、
  `phase0-report.md`、`phase0-status.md`、`runtime-feasibility.md`、`adr-0001-mod-injection.md`

> **证据等级口径**（与 `phase0.5-evidence-index.md` §0 一致）：
> A = 直接读源码/二进制；B = 本阶段实际执行的离线测试；C = 本阶段只读设备输出；
> D = Phase 0 历史记录、原始日志已丢失、不可独立复现；E = 外部参考。
> **D 级只能写"记录"，不能写"已验证/当前通过"。**

---

## 1. 一句话结论

Phase 0 的**技术结论在源码/编译期层面成立**（`Assembly.LoadFrom` 是唯一可用的外部程序集加载 API；
`Load(byte[])`/`Reflection.Emit` 在裁剪后的运行时里不可用），但"它能加载并执行外部 DLL"这条**运行时结论**
只有 **D 级历史记录**（模拟器、原始日志已丢失、本阶段未重跑），不得当作当前已验证事实。
此外 Phase 0 的**文档与实现之间存在 4 处实质矛盾**、**工具链有 3 个真实缺陷**（其中一个会让默认构建静默不加载模组），
且**全部 Phase 0 原始证据文件已随 `.tmp/` 清空而丢失**。本阶段已修文档、修代码、补回归测试（27 项离线检查全通过），
并把"模拟器记录"与"真机通过"严格分开：E1/E2 只有 **D 级模拟器记录**，真机为 **BLOCKED**。

---

## 2. 审计基线（阶段 A）

| 项 | 值 |
| --- | --- |
| 仓库根 | `D:\Download\DSH\StardewValley` |
| remote | `origin https://github.com/Hiyomi-Kuro/StardewValley.git` |
| branch | `main`（`main...origin/main`） |
| HEAD（开始 = 结束） | `fa4f5c3baa64a0d1c5c10c1ff3f5d314df3a022a`（"2026-11-04 23:17:00"） |
| 工作区 | **开始：干净**（0 modified / 0 untracked，仅 `.gitignore` 覆盖的 `.tmp/ .work/ app/build/` 等） |
| 与审计参考基线 | **一致**（任务书给的 `fa4f5c3…` 正是当前 HEAD，无新增提交需要比对） |
| 未跟踪/忽略 | `.gradle/ .kotlin/ .work/ app/build/ Tools/AssemblyStore/__pycache__/ AGENTS.md` |
| 本阶段产物位置 | `.tmp/phase0.5/`（gitignored，符合 `AGENTS.md` 10.1 约定） |

基线原始输出：`.tmp/phase0.5/logs/baseline.txt`。

> 结论：本阶段没有需要"保护/隔离"的用户未提交改动——开始时工作区是干净的，
> 结束时全部差异都来自本阶段（`git status` 见 §12）。

---

## 3. 本阶段执行环境（含新建的私有 SDK）

| 项 | 值 | 说明 |
| --- | --- | --- |
| OS / 工作目录 | Windows / `D:\Download\DSH\StardewValley` | |
| .NET | **本机仍无全局 SDK**（只有运行时 8.0.4） | 经用户批准，装**项目私有 SDK 8.0.404** 到 `.tmp/phase0.5/dotnet/`（zip 281,358,929 字节；不改 PATH、不装全局，删目录即还原） |
| Python | 3.12.10（无 `dnfile`/`lz4`，`store.py` 自带 LZ4） | |
| adb | `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe` | |
| 设备 | **MuMu 12 模拟器**：x86_64 + `libnb.so`（ARM 翻译），Android 12 / API 32 | 不是 ARM64 真机，见 §7 |

> 私有 SDK 的下载与解压命令见 `phase0.5-evidence-index.md` §2.1；zip 未删除，可复现。
> 该 SDK 属于"工具链"，不属于交付物，`.tmp/` 可整体删除。

---

## 4. 文档一致性审计（阶段 B）

### 4.1 逐项核对结果

| 文件 | 核对结论 | 处理 |
| --- | --- | --- |
| `README.md`（根） | 只讲"目录/前置条件/构建/二次开发"，**没有**任何 Phase 0 测试编号或运行时结论 → 无矛盾可修 | 未改（记录 N/A） |
| `docs/audit/phase0-report.md` | 能力矩阵正确；但标题写"全部为设备实测"易读成真机；引用的 `.tmp` 证据已不存在 | 改为"MuMu 12 模拟器实测"；加证据可得性 + §8 变更记录 |
| `docs/audit/phase0-status.md` | §3 阻塞表、§6 下一步停留在早期认识（"注入工具未实现/需要真机"），与 §3.22/§3.23/§3.26 矛盾；章节顺序怪（§3.x 在 §2 前） | 更新 §3/§6；页首加"阅读提示"（追加顺序 + 证据丢失）|
| `docs/audit/runtime-feasibility.md` | §6.5 标题写"真机"（实为模拟器）；§9.2 架构图写"新增程序集条目 + `Load(byte[])`"（与 ADR D3 实测修订相反）；§10 状态列仍全"待设备" | 修 §6.5 标题、§9.2 架构图、§10 状态；加证据可得性 + §13 变更记录 |
| `docs/audit/adr-0001-mod-injection.md` | **D1 仍以 `Assembly.Load(byte[])` 为最终方案**（已知矛盾 2）；D3 的 `Start()` 描述写 `Load(bytes)` 且没提阶段异常/吞异常；状态 Proposed 正确 | 改 D1/D3；新增 §6.2 复核表；新增 §8 变更记录；**维持 Proposed** |
| `docs/audit/bcl-surface.md` | 结论正确；但 §3 的 `-p:AndroidRefsDir=.tmp/...` **相对路径实测不生效**；只解主 blob 会缺 7 个架构程序集；§5 的 SHA 不可复现 | 修 §3 命令（两 blob + 绝对路径 + 原因）；§5 标注哈希不作身份；加 §7 变更记录 |
| `docs/audit/evidence-index.md` | 所有 `.tmp/phase0/*` 路径已失效；"重建脚本"也已丢失 | 加"证据可得性"章节；补 Phase 0.5 可用命令；逐条标注可复核/不可复核 |
| `docs/audit/decompile-report.md` | `Game1` 558/531 与探针 576/532 看似矛盾（实为统计口径不同）；`.tmp` 身份表已丢失 | §4 加口径说明；其余为静态记录，保留 |
| `Mod/MonoRuntimeProbe/README.md` | **严重过期**：写 T00–T11、T08/T09/T10"期望 pass"、T11="Task/线程"、状态"Android 真机未验证"、Bootstrap 片段用 `Load(byte[])` 且报告 json/log | 按当前源码重写（T00–T07 + T11 + T12，T08–T10 移入"已移除用例"） |
| `Mod/ModFramework.Runtime/Loader.cs` | 注释称"成功返回 null，失败返回诊断字符串"，**实现永远返回 `"loader-report: …"`** → 与注释/Bootstrap 契约矛盾 | 修实现（§6.1） |
| `Mod/MonoRuntimeProbe/*`（源码/配置） | `ProbeEntry.cs` 与 `DetourProbe.cs` 与 README 不一致（README 已重写）；`csproj` 注释仍写 `Assembly.Load(byte[])` | 修 csproj 注释；源码未动（T12 崩溃证据必须保持原样） |
| `Mod/ExampleMod.HelloWorld/*` | 入口只有 `Greet()`/`TicksSnapshot()`，**没有任何调用点** | 不改代码；在报告里明确"Mod 入口调用链未实现"（§5） |
| `Tools/ModInjector/*` | 默认 `--loader-api` 是已不可用的 `load(byte[])`；`verify` 不报加载 API；`--refs-dir` 缺失；`IlChecks` 对 `castclass` 栈模型错误 | 修（§6.2） |
| `Tools/AssemblyStore/store.py` | `names()` 写死 `Blob ID=000`，提取架构 blob 时**静默错标**（`System.Private.CoreLib` → `_Microsoft.Android.Resource.Designer`） | 修（§6.3） |
| `Tools/deploy-apk.ps1` | 全部路径指向已被清空的 `.tmp/phase0/`；python 路径不存在；无安装重试 | 修（§6.4） |
| `Tools/README.md` | 把注入的 Bootstrap 语义写成"失败就 return"（实际是分阶段 throw + 吞异常）；无 `--loader-api/--refs-dir` 文档 | 重写相关章节 |
| 构建脚本/测试 | 除上述工具外无其他测试入口；`app/src/main/kotlin` 的 apktool 流水线未改动 | 未改（不在本阶段范围） |

### 4.2 已知矛盾逐条裁定（任务书 §4）

| # | 任务书列出的疑点 | 裁定 | 依据 |
| --- | --- | --- | --- |
| 1 | 实际可用路线是 `Assembly.LoadFrom(string)`，`Load(byte[])` 不可用 | **成立** | 源码：`Loader.cs`/`Bootstrap` 注入 IL 都只用 `LoadFrom`；编译期：Android 参考程序集下 `Load(byte[])` 报 `CS1503`；运行时：`MissingMethodException`（Phase 0 记录，`phase0-status.md` §3.15） |
| 2 | ADR D1 仍保留 `Load(byte[])` 旧表述 | **确认存在，已修** | 原文 D1 第一句；改为 `LoadFrom` 并把 `byte[]` 列入"被否决方案" |
| 3 | 探针 README 仍是旧编号/旧结论 | **确认存在，已重写** | 旧 README 表 T00–T11 与 `ProbeEntry.cs`（T00–T07 + T11）不符；T11 语义写错（Task/线程 vs native-detour） |
| 4 | 失败实验必须保留 | **已保留** | T08–T10 的失败输出仍在 `phase0-status.md` §3.22；README 用"已移除用例"表保留结论，不删历史 |
| 5 | ADR 状态是否被提前 Accepted | **没有**（仍 Proposed）；本阶段也未改 | §6 门槛要求真机，当前只有模拟器 → §6.2 明确"不得因模拟器通过而改" |
| 6 | 65 个程序集/Mono 版本/JIT-AOT/BCL 面/Game1 统计/T12 等数字需原始证据 | **部分可复核、部分不可复核** | 见 §4.3 |

### 4.3 数字与结论的可复核性

| 结论 | 状态 | 依据 |
| --- | --- | --- |
| 65 个程序集（58 主存储 + 7 架构存储） | ✅ **可复核** | 本次从两个 blob 解出 58 + 7 = 65，条目名与 manifest 一致 |
| 65 个 `libaot-*.so`，合计 38,564,048 字节 | ✅ **可复核** | `app/src/main/jniLibs/arm64-v8a` 实测 65 个、合计 38,564,048 |
| 75 个 `.so`（jniLibs） | ✅ 可复核 | 实测 75 个 |
| `StardewValley.dll` 9,280,000 字节 / SHA `587ff5ff…` / MVID `573bf1e5…` | ✅ **可复核** | 本次解出后哈希一致；REA 读到 MVID/程序集版本一致 |
| TypeDef 2867 / MethodDef 30067 / Field 18859 / ImplMap 5192 / AssemblyRef 28 / CustomAttribute 4656 | ✅ **可复核** | REA `inspect_managed_members`（evidence `ev_e3692265…` / parent `ev_12ceb610…`）与文档完全一致 |
| `MainActivity` 43 方法 / 8 字段 | ✅ 可复核 | 同上（`reported_counts`） |
| `Game1` 576 方法 / 532 字段（反射） | ⚠️ **不可复核** | 需要设备运行探针；原始 probe 输出已丢失 |
| `Game1` 558 方法 / 531 字段（声明） | ⚠️ 不可复核 | 同上（口径不同，见 decompile-report §4） |
| MonoVM `8.0.1124.51707`、AOT `normal`、GC 参数、`application_config` 计数 | ⚠️ 不可复核 | 依赖已丢失的 `strings_*`/`xa_*` 证据；`libxamarin-app.so` 仍在仓库，可重做但本阶段未做（超出任务范围） |
| T12 原生崩溃 | ⚠️ 仅记录 | 源码 `DetourProbe.cs` 在，崩溃现象只有文字记录；本轮**未重跑**（真机/模拟器都没动） |
| APK SHA `258d7d92…` | ❌ **不成立/不可复核** | 该路径当前产物是 `65e1e2af…`（388,967,580 字节）；两者大小相同、哈希不同，说明记录对应另一次构建。已修正 `evidence-index.md` |
| "MuMu 12 / Android 12 / API 32 / x86_64" | ✅ 可复核 | 本次 E0 只读预检（§7.1） |

---

## 5. Bootstrap → Loader → DLL → Mod 入口：逐跳审计（阶段 C）

### 5.1 调用链（从真实调用点）

| # | 跳 | 源码 / 证据 | 成功条件 | 失败行为 | 等级 |
| --- | --- | --- | --- | --- | --- |
| 1 | APK 启动入口 | `app/src/main/AndroidManifest.xml` L10/L13：`com.chucklefish.stardewvalley.MainActivity` + `MAIN/LAUNCHER`；`ManifestPatch.kt` 幂等校验 | 系统直接拉起游戏 Activity（不再是 byfen 壳） | 清单错误 → 启动到壳/闪退 | A（源码）+ C（Phase 0 装机记录） |
| 2 | Mono 运行时初始化 | 同文件 L16：`mono.MonoRuntimeProvider`，`initOrder=1999999999` | provider 先于 `Application.onCreate`，`libmonodroid` 初始化 MonoVM | 初始化失败 → 进程起不来（无托管栈） | A（清单）+ D（Phase 0 记录） |
| 3 | Java peer → 托管 `OnCreate` | `classes.dex` 的 `__md_methods`（`GetOnCreate_Landroid_os_Bundle_Handler`），见 `decompile-report.md` §5.1 | JNI 转发到托管 `StardewValley.MainActivity.OnCreate` | 转发失败 → 原生崩溃/无界面 | C（Phase 0 IL 记录），本轮未反编译 dex |
| 4 | `OnCreate` → `CheckAppPermissions` → `OnCreatePartTwo` | `decompile-report.md` §5.2/§5.3（IL 级）；方法存在性本轮用 Cecil 复核（`verify` 打出 `StardewValley.MainActivity::OnCreatePartTwo`） | 权限检查通过后进入"建游戏对象"路径 | 权限拒绝 → 走权限流程，`OnCreatePartTwo` 不执行 → 模组不加载 | A/B（方法存在性）+ C（IL） |
| 5 | 注入点：`OnCreatePartTwo` 首条指令 `call ModFramework.Bootstrap::Start()` | `Tools/ModInjector/Program.cs` `InsertCall`；`verify` 输出 `hook-call present at IL_0000`、`hook-is-first yes` | 首条真实指令就是调用（不是 NOP 之后） | 注入点错 → 调用不发生（Phase 0 的 MVID 事故就是这样） | **B**（本次实测） |
| 6 | `Bootstrap` stage-1 | `EmitBootstrap`：`File.Exists(<Mods>/ModFramework.Runtime.dll)` | 文件存在 | `throw stage-1:file-missing`（被 catch 吞） | B（IL 结构） |
| 7 | stage-2 | `Assembly.LoadFrom(path)` | 返回非 null `Assembly` | `throw stage-2:assembly-null` | **B**（IL 里的 `call LoadFrom`，`verify` 可读） |
| 8 | stage-3 | `asm.GetType("ModFramework.Runtime.Loader")` | 类型非 null | `throw stage-3:type-null` | B |
| 9 | stage-4 | `t.GetMethod("Start")` | 方法非 null | `throw stage-4:method-null` | B |
| 10 | stage-5 | `MethodBase.Invoke(null, null)` → `Loader.Start()` | 返回 null（成功） | 返回非 null → `throw <report>` | **B**（契约测试） |
| 11 | `Loader.Start()` 内部 | `Loader.cs`：`locate`/`mods-exists`/`write-log`/`probe-run` 四个探测，每个独立方法 + `Probe()` catch | 四项都成功 → 返回 null；报告写 `loader-report.txt` | 任一项失败 → 返回 `loader-report: …`（Bootstrap 抛、被吞） | **B**（桌面契约测试） |
| 12 | 探针加载（可选） | `RunProbe()`：`File.Exists(MonoRuntimeProbe.dll)` → `Assembly.LoadFrom` → `GetType("MonoRuntimeProbe.Probe")` → `GetMethod("Run")` → `Invoke(dir, false)` | 探针执行并写 `probe.txt` | 文件不在 → `"absent"`（算成功）；其它异常 → 该项失败 | A + C（模拟器记录） |
| 13 | 探针用例 | `ProbeEntry.cs` T00–T07、T11；`DetourProbe.cs` T12 | 各用例 PASS | 单用例失败只影响该行；T12 会**原生崩溃**（整个进程） | A + C（记录） |
| 14 | **Mod 入口调用** | `ExampleMod.HelloWorld/ModEntry.cs` 只有 `Greet()`/`TicksSnapshot()`，**全仓库无调用点** | — | — | **不存在实现**（Phase 1） |

**结论**：链路 1–13 都有源码/离线证据；**第 14 跳（模组入口发现与调用）根本没有实现**。
所以当前系统能证明的是"能加载并执行**指定的**外部 DLL（探针）"，不是"能加载任意模组并调它的入口"。
这一点在 Phase 0 文档里容易被"HelloWorld 编译通过 + Loader 跑通"读成已经打通，特此澄清。

### 5.2 `Loader.Start()` 专项结论（任务书 §5 的六个问题）

| 问题 | 结论 | 证据 |
| --- | --- | --- |
| Bootstrap 调用点是否读取返回值？ | **读**：`Invoke` 的返回值被 `castclass string`，非 null 就 `newobj Exception(string)` + `throw` | `EmitBootstrap` stage-5；IL 校验通过 |
| 是否存在"字符串误当 bool / 非空误当成功"？ | **方向相反**：非空被当**失败**。真正的问题是**修复前** `Loader.Start()` 永远返回非空 → 成功也被当失败（每次都抛），而异常又被 catch 吞掉 → **成功/失败不可区分、报告丢失**。修复后：null=成功、非 null=失败 | 修复前实测：`loader-contract-before.txt` `return-null=false`；修复后：`return-null=true` |
| 正式与诊断模式是否共用同一返回契约？ | `Loader` **没有**模式开关，只有一套契约（修复后 = null/报告）。所谓"诊断模式"是**构建期**的 `--mode append`（生成另一段 Bootstrap IL），与 `Loader` 无关 | `Loader.cs`；`Program.cs` `minimal` 分支 |
| 报告/成功/失败/异常是否有明确区分？ | 修复后：**成功=null**；**失败=诊断字符串**（报告同时落盘 `loader-report.txt`）；**Bootstrap 阶段失败=带 stage-N 的异常**；所有异常在正式构建被吞、在 `--no-catch` 构建被 rethrow 进 logcat | §6.1；`verify` 的 `loader-api` / `hook-is-first` / `RESULT` |
| 日志失败会不会反过来阻断启动？ | **不会**。`WriteLog()` 抛异常 → 被 `Probe` 捕获 → `ok=false` → 返回报告 → Bootstrap 抛 → catch 吞 → 游戏继续。`SaveReport` 失败只加一条附注，不改判定 | 源码路径 + `Probe()` 的 `ok &=` 语义 |
| 重复/并发/部分初始化？ | `Bootstrap.Start()` 由 `OnCreatePartTwo` 调用一次（Activity `singleInstance`，UI 线程）；**没有**重入保护，也没有静态状态，重复调用只会重写 `loader.log`/`loader-report.txt`。四项探测互相独立，部分失败只影响对应项（`ok` 累积）。并发调用在当前架构下不会发生（同一 UI 线程、单一入口），但**未加锁**，若将来从其它线程调用需要自行串行化 | 源码；无并发测试（标注 Unknown） |

**是否判定为"仅文档错误"？** 不是。存在两个**代码级**缺陷：
(a) `Loader.Start()` 的实现与自身注释/Bootstrap 契约相反（永远"失败"）；
(b) `ModInjector` 默认生成的 IL 调用一个**不存在**的方法。
两者都已修，并有回归测试（§6）。

---

## 6. 代码修复清单（含回归证据）

### 6.1 `Mod/ModFramework.Runtime/Loader.cs`（返回契约）

**缺陷**：注释写"成功返回 null，失败返回诊断字符串"，实现却 `return "loader-report: " + report;`（无条件非 null）。
Bootstrap 以"非 null = 失败"处理，于是**成功也被当失败**；而正式构建的 catch 会把异常吞掉，
结果是"看不出成功、也拿不到报告"。

**修复**（最小改动，不重构）：
- `Probe(...)` 改为返回 `bool` + `ref string report`，聚合 `ok`；
- 新增 `SaveReport()`（独立方法、独立 try）：把完整报告写到 `<自身目录>/loader-report.txt`；
- `Start()` 返回 `ok ? null : "loader-report: " + report + saveNote`。

**回归证据**：
- 桌面契约宿主 `Tools/tests/LoaderContract/`：新实现 `return-null=true`、`loader-report=True`；
- 对照运行 HEAD 版实现：`return-null=false`、`loader-report=False`（`.tmp/phase0.5/logs/loader-contract-before.txt`）；
- 仍能用 Android 参考程序集编译（0 error），说明新增的 `File.WriteAllText`/`DateTimeOffset` 都在可用面内。

### 6.2 `Tools/ModInjector/`（默认加载 API + 可验证性）

| 缺陷 | 影响 | 修复 |
| --- | --- | --- |
| `--loader-api` 缺省 = `load(byte[])` | **默认构建静默不加载模组**：`Assembly.Load(byte[])` 在裁剪运行时不存在 → `Start()` JIT 失败 → 被 catch 吞 → 游戏正常、模组永不生效 | 默认改 `loadfrom`；`--loader-api load` 保留为**复现历史失败**的开关；非法值直接报错 |
| `verify` 不报告实际注入的加载 API | 无法在构建期发现上面的回退 | `verify` 扫描 `Bootstrap.Start` 的 IL，打印 `loader-api: loadfrom(...)/load(byte[])/none`，并纳入 `RESULT`；`--allow-bytes-api` 仅供历史产物 |
| `<in.dll>` 不在其余 64 个程序集旁边时 Cecil 解析失败 | 只能把 dll 放进 65 个程序集目录才能注入 | 新增 `--refs-dir <dir[;dir]>` |
| `IlChecks` 把 `castclass` 记为 (0,0) | 栈检查对 `castclass` 少弹一个引用，可能漏报/误报 | 改为 (1,1) |
| `verify` 的 `hook-is-first` 只打印、不参与判定 | `RESULT: OK` 可能在实际不是首指令时出现 | 纳入 `ok` |
| 文件头/关键注释是 PowerShell 事故留下的乱码 | 可读性、审计可追溯性差 | 改成 ASCII/正常中文（**仅注释**，不改逻辑） |

**回归证据**（`Tools/tests/test_mod_pipeline.py`，27 项全 PASS）：
默认 patch 后 `verify` 报 `loader-api=loadfrom`；`--loader-api load` 报 `load (Assembly.Load(byte[]))`
且**单独 verify 会 FAILED（rc=1）**——这证明断言对"旧默认值"是敏感的，不是自证。

> **关于"先让测试失败"的顺序（如实说明）**：本轮第一次跑测试时，`patch` 在 Cecil 解析阶段就失败了
> （输入 dll 不在其余 64 个程序集旁边，也没有 `--refs-dir`），所以那次"失败"反映的是**测试脚手架**问题，
> 不是 `loader-api` 默认值问题。修好脚手架（加 `--refs-dir`）时，`loader-api` 的默认值修复已同时落地，
> 因此**没有留下"修复前的完整失败运行"**。替代证据是三条，强度足够：
> ① `git show HEAD:Tools/ModInjector/Program.cs` 里 `useLoadFrom` 的缺省值就是 `false`；
> ② `--loader-api load` 负向对照能稳定产出 `load (Assembly.Load(byte[]))`，且单独 `verify` 判 FAILED；
> ③ `Loader` 契约有真正的 before/after 对照运行（`loader-contract-before.txt` vs `loader-contract-host.txt`）。

### 6.3 `Tools/AssemblyStore/store.py`（架构存储错标）

**缺陷**：`names()` 写死 `Blob ID == "000"`。对 `assemblies.arm64_v8a.blob`（`Blob ID=001`，7 条）
仍返回主存储的前 7 个名字，于是 `extract` 把 `System.Private.CoreLib` 写成
`_Microsoft.Android.Resource.Designer.dll`——**内容与文件名不符，且不报错**。
这会污染 `AndroidRefsDir`（编译时引用错名文件），也会让"65 个程序集"的提取结果看起来像 58 个主存储的名字。

**修复**：`Store(..., store_id=None)`；`names()` 按 `entry_count` 自动匹配 `Blob ID`（不唯一则报错，
要求 `--blob-id`）；`extract` 在"清单名数 ≠ 条目数"或"条目不是 PE"时**拒绝写出**。

**回归证据**：`arch_store_names`（7 个名字，首个 `System.Collections.Concurrent`，含 `System.Private.CoreLib`）；
抽取前实测错标输出见 `.tmp/phase0.5/logs/store-arm64-preextract.txt`。

### 6.4 `Tools/deploy-apk.ps1`（路径失效 + 安装竞态）

- `.tmp/phase0/*` 全部改成 `.tmp/phase0.5/*`（`dotnet/ refs/ out/ artifacts/`）；
- 删除不存在的 DSH 运行时 python 路径，改用 `python`；
- 新增前置检查（缺 SDK / 缺 65 个程序集 / 缺原始 blob 备份时**明确报错**，不再静默半途失败）；
- `uninstall → install` 增加一次重试（Phase 0 记录过这个竞态）；
- 新增 `-Serial` / `-Dotnet` 参数；注释说明"默认 `HookMethod=OnCreate` 是 Phase 0 实测过的配置，
  ADR 首选是 `OnCreatePartTwo`"。

> 该脚本**本阶段没有执行**（会卸载/安装游戏）。改动属于"让它在真机阶段可用"，不是"已装机验证"。

### 6.5 新增文件

| 文件 | 作用 |
| --- | --- |
| `Tools/device-precheck.ps1` | E0 只读预检（ABI/API/包信息/APK 哈希/残留文件），**默认不写设备** |
| `Tools/tests/test_mod_pipeline.py` | 注入器 + 容器 + Loader 契约的离线回归（27 项） |
| `Tools/tests/LoaderContract/` | 桌面契约宿主（`Loader.Start()` 返回值 + 报告落盘） |

---

## 7. 设备阶段（E0/E1/E2）

### 7.1 E0 只读预检：PASS（但设备**不是** ARM64 真机）

原始输出：`.tmp/phase0.5/logs/e0/device-precheck-127.0.0.1_16384.txt`。

| 项 | 观测 | 判定 |
| --- | --- | --- |
| `adb devices -l` | 3 个 endpoint：`127.0.0.1:16384 / 5555 / 7555`，`product:PD2309 model:V2309A` | 只有 `16384` 可用（另两个 `error: closed`） |
| `ro.product.cpu.abi` | **x86_64** | **不是 ARM64** |
| `ro.product.cpu.abilist` | `x86_64,arm64-v8a,x86,armeabi-v7a,armeabi` | 含 arm64 是因为**翻译层**，不是本机架构 |
| `ro.dalvik.vm.native.bridge` | `libnb.so` | ARM 翻译层（MuMu 特征） |
| `ro.kernel.qemu` | 空 | 模拟器伪装过属性，不能靠它判断 |
| Android | 12 / API 32 / 安全补丁 2022-04-05 | 与 Phase 0 记录一致 |
| Root | `su -c id` → `uid=0`（可用） | **只在模拟器成立，真机不得假定** |
| 包 | `com.chucklefish.stardewvalley`，versionCode 239 / 1.6.15.0 | 已安装 |
| 安装时间 | first/lastUpdate = 2026-11-04 22:58:01 | 最近一次是 Phase 0 的基线恢复安装 |
| 安装包 SHA-256 | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` | |
| 本地产物 | `app/build/outputs/apk/debug/app-debug.apk` = 388,967,580 字节，**同一 SHA-256** | 设备上跑的**就是**这个本地构建 |
| APK 内 `assemblies/assemblies.blob` | SHA-256 `c8ee504e…` = 仓库原始 blob | **当前设备上是未注入的基线 APK** |
| 残留 | `/sdcard/.../files/Mods` 不存在；无 `loader.log` | 没有 Phase 0 的模组遗留 |

→ **E0 判定：PASS（只读）**；**`device-class = NOT-ARM64-PHYSICAL`**（x86_64 + `libnb.so`）。

### 7.2 E1 / E2

**本阶段没有重跑 E1/E2。** 下表 Emulator 列全部是 **D 级历史记录**（Phase 0 文档，原始日志已随 `.tmp/phase0/` 丢失），
只能读作"曾经记录为通过"，**不是**本阶段的 Current Run PASS，也不可独立复现。

| 项 | 模拟器（MuMu 12） | ARM64 真机 | 结论 |
| --- | --- | --- | --- |
| E1 加载外部 DLL 并执行 IL | **RECORDED PASS (Evidence D; original logs unavailable; not independently reproducible)** | **BLOCKED**：无设备 | 只有 D 级记录；真机未验证 |
| E2 反射访问 `Game1` | **RECORDED PASS (Evidence D; same caveat)**（记录：`Game1 576 方法 / 532 字段`） | **BLOCKED** | 同上；576/532 属 Historical Record / Not independently verified |
| 游戏正常进标题界面 | **RECORDED PASS (Evidence D; same caveat)** | NOT RUN | 同上 |

> Emulator 列不得填进 Device 列；D 级记录不得升级为"当前通过"。

**本阶段没有重跑 E1/E2**，原因：① 设备不是 ARM64；② 重跑需要卸载+安装（丢存档、覆盖用户游戏），
未获授权；③ 当前设备上装的是基线 APK，没有任何探针/模组残留。

测试包已准备好但**未安装**：`.tmp/phase0.5/out/`（`sv.full.dll`、`assemblies.full.blob`、`mods/*.dll`），
哈希见 `phase0.5-evidence-index.md` §2.3。装机步骤与授权门禁见 `phase0.5-device-checklist.md`。

---

## 8. ModInjector / AssemblyStore 离线链路复核（阶段 D）

### 8.1 输入与隔离

- 原始输入：`app/src/main/apkroot/assemblies/assemblies.blob`（5,588,326 字节，`c8ee504e…`）、
  `assemblies.manifest`（4,937 字节，`0423d19c…`）。
- 所有写操作只发生在 `.tmp/phase0.5/` 的副本上；**仓库 blob / APK / 原始 dll 未被覆盖**
  （测试末尾重新哈希校验，`inputs_not_modified` PASS）。

### 8.2 命令与结果（可重复）

```powershell
# 1) 解出 65 个程序集（58 + 7）
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.blob <refs> --manifest <m>
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.arm64_v8a.blob <refs> --manifest <m>

# 2) 注入（默认参数：loadfrom、OnCreatePartTwo）
.tmp/phase0.5/dotnet/dotnet.exe <out>/ModInjector.dll patch <refs>/StardewValley.dll <out>/sv.full.dll `
    --hook-method OnCreatePartTwo --refs-dir <refs>
# 3) 校验
.tmp/phase0.5/dotnet/dotnet.exe <out>/ModInjector.dll verify <out>/sv.full.dll

# 4) 原位替换 + 结构校验
python Tools/AssemblyStore/store.py replace app/src/main/apkroot/assemblies/assemblies.blob StardewValley `
    <out>/sv.full.dll <out>/assemblies.full.blob --manifest <m>
python Tools/AssemblyStore/store.py verify <out>/assemblies.full.blob --manifest <m>

# 5) 回归
python Tools/tests/test_mod_pipeline.py
```

| 检查 | 结果 |
| --- | --- |
| 注入后自检（Cecil） | `bootstrap-type present`；`bootstrap-start present (51 instr / 182 bytes)`；`hook-target StardewValley.MainActivity::OnCreatePartTwo`；`hook-call present at IL_0000`；`hook-is-first yes`；`loader-api loadfrom`；`RESULT: OK` |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` → 每次运行都换新值（示例 `a44dfe0c-…`） |
| 产物大小 | `9,280,000` → `9,261,568` 字节（**注意**：Phase 0 记录是 9,261,056；差 512 字节，见 §10 未决项 5） |
| 容器结构 | `version=1 entries=58 total=65 reserved=0 gap=2600` 全部不变 |
| 未触碰条目 | **57/57 解压后逐字节相同** |
| `descriptor_index` | **58/58 全部保留**（含 `StardewValley` 的 `7`） |
| 回读一致性 | 替换条目解压后与补丁 DLL **逐字节相同**（SHA 一致） |
| PE 合法性 | 58/58 条目都是 `MZ` |
| `store.py verify` | 正确哈希 → `RESULT: OK`（rc=0）；错误哈希 → `FAIL`（rc=1） |
| 幂等 | 对已注入的 DLL 再跑 `patch` → 走"already present"分支，`hook-is-first yes`，不重复插入 |
| 原始输入 | 哈希前后一致 |

### 8.3 未做（明确标注）

| 步骤 | 状态 | 原因 |
| --- | --- | --- |
| 把 patched blob 放回 `apkroot/` 并 `gradlew assembleDebug` | **NOT RUN** | 会临时改写仓库文件 + 需要可用的 aapt2/网络；且当前只有模拟器，产物无处验证 |
| `adb install` 测试包 | **NOT RUN** | 未经授权；卸载会删存档（`AGENTS.md` §8.1） |
| 输出 APK 的签名状态 | 只记录不验证 | 现有流水线用 `.work/debug.keystore` 自签名；签名与原版不同，覆盖安装会失败（需卸载） |

---

## 9. 状态矩阵（每格有证据；N/A = 不适用；Unknown = 无证据）

> **模拟器列全部是 D 级历史记录**：Phase 0 的原始日志已随 `.tmp/phase0/` 丢失，本阶段没有重跑，
> 因此读作 `RECORDED PASS (Evidence D; original logs unavailable; not independently reproducible)`，
> **不是** Current Run PASS。真机列一律 BLOCKED / NOT RUN。

| 项目 | 源码存在 | 离线测试 | 模拟器（D 级记录） | ARM64 真机 | 最终状态 |
| --- | --- | --- | --- | --- | --- |
| Bootstrap 触发 | 是（注入 IL） | PASS（IL 结构 + hook 首指令，§8.2） | RECORDED PASS (D) | **BLOCKED** | PARTIAL |
| Loader 初始化 | 是（`Loader.cs`） | PASS（契约宿主：null + 报告落盘） | RECORDED PASS (D) | **BLOCKED** | PARTIAL |
| Assembly.LoadFrom | 是（`Loader.cs` / 注入 IL） | PASS（IL 里就是 `LoadFrom`；Android refs 编译通过） | RECORDED PASS (D) | **BLOCKED** | PARTIAL |
| C# Mod 入口执行 | **否**（只有探针入口，无 Mod 入口分发） | PASS（探针加载路径可验证；Mod 入口无实现） | RECORDED PASS (D)（探针 8/8） | **BLOCKED** | **NOT IMPLEMENTED**（Mod 入口）/ PARTIAL（探针） |
| Game1 反射只读访问 | 是（`Probe.GameTypes`） | Unknown（需运行时） | RECORDED PASS (D)（记录 576/532） | **BLOCKED** | PARTIAL |
| ModInjector | 是 | PASS（注入相关项全过） | N/A（构建期工具） | N/A | **DONE（离线）** |
| AssemblyStore 重建/校验 | 是 | PASS（结构/条目/描述符/往返/负向） | N/A | N/A | **DONE（离线）** |
| Harmony / native detour | 是（`DetourProbe.cs`） | N/A（需运行时） | **RECORDED FAIL (D)**（T12 写跳转桩 → 原生崩溃） | **BLOCKED** | **FAIL（当前工具链）** |

---

## 9.1 Harmony / native detour 的结论边界（阶段 F）

| 项 | 本阶段结论 |
| --- | --- |
| T12 失败证据 | **保留**为"已观察到的失败"：`Mod/MonoRuntimeProbe/DetourProbe.cs` 未改动（写 16 字节 `ldr x16,#8; br x16; .quad` 桩后进程原生崩溃）。原始 logcat 已随 `.tmp` 丢失，只剩 `phase0-status.md` §3.25/§3.26 的文字记录（等级 D） |
| 环境是否匹配 | 源码与记录匹配（方法名、桩字节、流程一致）；但**运行环境是 MuMu 12 / x86_64 + houdini 翻译层**，不是 AArch64 真机——`libnb.so` 的翻译块缓存会额外干扰，这一点在真机上不存在 |
| "当前实现失败" ≠ "路线永远不可行" | 正确表述：**当前工具链下失败**。缺的是①AArch64 用户态指令缓存刷新（`dc cvau`/`ic ivau` + 屏障），②可用的 `__clear_cache` 导出或缺 NDK 自建。这两条都不是"原理上不可能" |
| 已知限制（以当前证据为准） | `Reflection.Emit`/`DynamicMethod` 不可用（编译期 + 运行时两路证据）；朴素原生 detour 崩溃；缓存刷新手段未验证；NDK 未安装 |
| 本阶段做了什么 | **没有**新增 detour 实现、**没有**安装 NDK、**没有**引入 Harmony/MonoMod |
| 架构定位建议 | Harmony/方法补丁应作为**未来独立的可选研究模块**，不得成为核心 Loader 的启动依赖；Loader 的成功与否不依赖它 |

---

## 10. 未解决 / 阻塞 / 需要批准

| # | 项 | 状态 | 说明 / 下一步 |
| --- | --- | --- | --- |
| 1 | **ARM64 真机 E1/E2** | **BLOCKED（唯一硬阻塞）** | 本机只有 x86_64 模拟器。需要一台物理 ARM64 设备 + 明确授权（安装会卸载原游戏 → 丢存档）。清单见 `phase0.5-device-checklist.md` |
| 2 | Mod 入口发现/调用 | 未实现 | 属 Phase 1；当前 `Loader` 只加载探针 |
| 3 | Phase 0 原始证据丢失 | 不可逆 | `.tmp/phase0/` 已删；建议今后把关键日志复制到 `.work/evidence/` 或随文档入库（需用户决定是否接受仓库体积） |
| 4 | `Game1` 576/532 与 558/531 的口径差 | 未逐项复核 | 两者统计方式不同；要精确核对需重新在设备跑探针或写元数据统计脚本 |
| 5 | 补丁 DLL 大小 9,261,568 vs Phase 0 记录 9,261,056 | 未解释 | 相差 512 字节。可能是 Phase 0 那次的 Bootstrap IL 版本不同（当时记录的 104 字节方法体 vs 现在 182 字节）。**不影响容器校验**（校验用的是上限），但说明"注入产物大小"不是稳定常量，文档不应把它当身份 |
| 6 | APK SHA 记录 `258d7d92…` 与当前产物 `65e1e2af…` 不符 | 已标注 | 引用 APK 身份必须每次重算 |
| 7 | `Directory.GetFiles` 等裁剪结论 | 未逐条重跑 | 探针 T08–T10 已删；三条关键裁剪（`Load(byte[])`、`Directory.GetFiles`、`RuntimeInformation`）可用编译期复核，Emit 面需重新写用例 |
| 8 | Harmony / NDK | 未决策 | 本阶段未安装 NDK、未新增 detour；定位为"未来独立可选研究模块"，不作 Loader 启动依赖 |
| 9 | `Mod/Directory.Build.props`、两个 csproj 的注释乱码 | 未清理 | 纯注释、无功能影响；避免大 diff / 二次 PowerShell 事故，留待专门清理 |
| 10 | `deploy-apk.ps1` 未执行 | NOT RUN | 需要真机 + 授权 |

---

## 11. 测试矩阵（命令 / 结果 / 退出码 / 日志）

| # | 命令 | 结果 | 退出码 | 证据 |
| --- | --- | --- | --- | --- |
| 1 | `python Tools/AssemblyStore/store.py extract ...assemblies.blob <refs>` | 58 个 DLL，名称与 manifest 一致 | 0 | `.tmp/phase0.5/logs/extract-blob000.txt` |
| 2 | `python Tools/AssemblyStore/store.py extract ...assemblies.arm64_v8a.blob <refs>` | 7 个 DLL（`System.Private.CoreLib` 等），名称正确 | 0 | `.tmp/phase0.5/logs/extract-blob001.txt` |
| 3 | `.tmp/phase0.5/dotnet/dotnet.exe build Tools/ModInjector` | 0 warning / 0 error | 0 | 测试输出 |
| 4 | `... build Mod/ModFramework.Runtime -p:AndroidRefsDir=<abs>` | 0 / 0 | 0 | 测试项 `runtime_compiles_against_android_refs` |
| 5 | `... build Mod/MonoRuntimeProbe -p:AndroidRefsDir=<abs>` | 0 error，1 warning（`CS0169 _staticCounter`，反射用） | 0 | 见 §11.1 |
| 6 | `... build Mod/ExampleMod.HelloWorld -p:AndroidRefsDir=<abs>` | 0 / 0；产物 4096 字节 | 0 | 见 §11.1 |
| 7 | `python Tools/tests/test_mod_pipeline.py` | **27/27 PASS**（等级 **B**：本阶段实际执行，日志仍在 `.tmp/phase0.5/logs/`；删除 `.tmp` 后需重跑才能复核） | 0 | `.tmp/phase0.5/logs/test-mod-pipeline.final.txt` |
| 8 | `powershell -File Tools/device-precheck.ps1` | E0 PASS（**C**）；E1/E2 BLOCKED | 0 | `.tmp/phase0.5/logs/e0/device-precheck-127.0.0.1_16384.txt` |
| 9 | `dotnet <LoaderContract.dll>`（新 Loader） | `return-null=true`、`loader-report=True` | 0 | `.tmp/phase0.5/logs/loader-contract-host.txt` |
| 10 | `dotnet <LoaderContract.dll>`（HEAD 版 Loader） | `return-null=false`（缺陷复现） | 0 | `.tmp/phase0.5/logs/loader-contract-before.txt` |
| 11 | `git diff --check` | 无空白错误 | 0 | §12 |
| 12 | 装机 / install / APK 构建 | **未执行** | — | 见 §8.3 |

### 11.1 构建日志摘要（关键行）

- `MonoRuntimeProbe`：`warning CS0169: 从不使用字段"Probe._staticCounter"` —— 该字段**只被反射读写**（T05），
  保留是刻意的，警告可接受。
- `ExampleMod.HelloWorld`：`-> .../ExampleMod.HelloWorld.dll`，0 warning / 0 error，**4096 字节**
  （与 Phase 0 记录的大小一致，哈希不同——见 `bcl-surface.md` §7）。

---

## 12. 最终工作区状态

```
## main...origin/main
 M Mod/ModFramework.Runtime/Loader.cs
 M Mod/MonoRuntimeProbe/MonoRuntimeProbe.csproj
 M Mod/MonoRuntimeProbe/README.md
 M Tools/AssemblyStore/store.py
 M Tools/ModInjector/IlChecks.cs
 M Tools/ModInjector/Program.cs
 M Tools/README.md
 M Tools/deploy-apk.ps1
 M docs/audit/adr-0001-mod-injection.md
 M docs/audit/bcl-surface.md
 M docs/audit/decompile-report.md
 M docs/audit/evidence-index.md
 M docs/audit/phase0-report.md
 M docs/audit/phase0-status.md
 M docs/audit/runtime-feasibility.md
?? Tools/device-precheck.ps1
?? Tools/tests/
?? docs/audit/phase0.5-report.md
?? docs/audit/phase0.5-evidence-index.md
?? docs/audit/phase0.5-device-checklist.md
```

- `git diff --check`：无输出（无空白/冲突标记问题）。
- 没有 `git reset` / `git clean` / 强制 checkout；没有 commit / push。
- `.tmp/phase0.5/` 是 gitignored 的本阶段工作目录（私有 SDK、证据、测试包），可整体删除；
  **删除后 §4.3 中标"✅ 可复核"的项仍可通过 `Tools/` 的命令重建**，标"⚠️ 不可复核"的项则永久只剩文档记录。

---

## 13. 本阶段没有做的事（边界确认）

- 没有启动 Phase 1：无 Manifest/依赖解析、多模组管理、Events/Content/Data/Config/Integrations、
  游戏内管理器、独立 Android 管理器。
- 没有安装 NDK、没有新增 detour、没有引入 Harmony/MonoMod。
- 没有卸载/安装/替换用户设备上的游戏，没有清数据、没有碰存档。
- 没有 `git commit` / `push` / Release。
- 没有把模拟器结果写成真机结果：E1/E2 一律标 Emulator PASS + Device BLOCKED。
