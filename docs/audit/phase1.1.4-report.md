# Phase 1.1.4 报告：Mono Runtime Compatibility Characterization 与非破坏性设备验证准备

- 日期：2026-11-06
- 提交基线：`345d71280ce5c255c4b9db490732cd9af312c0aa`（远程 `main`）
- 本阶段性质：**本地实现 + 本地证据**。未 commit、未 push、未创建 Release、未安装/卸载/推送/启动游戏。
- 证据等级定义见 [`phase0.5.1-evidence-index.md`](phase0.5.1-evidence-index.md) §0；本报告所有结论都标注了等级。

---

## 0. 总状态

**PARTIALLY COMPLETE。**

| 主题 | 状态 | 说明 |
| --- | --- | --- |
| Git 基线协调 | ✅ COMPLETE | 本地 HEAD 已是 `345d712`，工作区干净；无 dirty/untracked 需要保护 |
| 安全探针重构（`RunSafe`，隔离 T11/T12） | ✅ COMPLETE | 默认编译无 P/Invoke / mprotect / RWX / detour / unsafe |
| P114 诊断 Mod + fixtures + private dependency | ✅ COMPLETE | 桌面 Release + AndroidRefsDir 均编译通过 |
| R-01 … R-14 运行时观察 | ⚠️ PARTIAL | **桌面宿主**全部记录；**Emulator / ARM64 真机 = NOT RUN**（无安装授权） |
| 非破坏性部署门禁 | ✅ COMPLETE | 默认只读；`-InstallApproved`/`-PushModsApproved` 双批准；签名不一致/不可读 fail-closed |
| fake adb 部署测试 | ✅ COMPLETE | 39/39，含全部危险命令负向断言 |
| E0 只读预检 | ✅ COMPLETE | `E0 PASS (read-only)`；目标为 x86_64 + `libnb.so` 翻译层模拟器 |
| ARM64 物理设备验证 | ⛔ BLOCKED | 无物理设备；Emulator 不得计入 Device 列 |
| Emulator 上运行探针 | ⛔ NOT RUN | 需要向设备写 `Mods/`，本阶段无授权 |
| 远程 CI | ✅ OBSERVED | `345d712` 的 push run = success（两个 job 均 success） |

一句话：**"探针本身安全 + 部署默认不破坏 + 桌面/编译期证据齐备"已完成；"在 MonoVM 上真实跑一遍"仍停在
批准门槛之前，ARM64 物理设备仍 BLOCKED。**

---

## 1. 基线与远程 CI

### 1.1 远程 main

| 项 | 值 |
| --- | --- |
| 远程 | `https://github.com/Hiyomi-Kuro/StardewValley.git` |
| 默认分支 | `main` |
| 基线 commit | `345d71280ce5c255c4b9db490732cd9af312c0aa` |
| 上一阶段 commit | `76aa836ea935d03aa64fcdd7ad164f9cf88626e6` |
| `345d712` vs `76aa836` | ahead 1 / behind 0；**17 个文件**（+2364 / −138） |

### 1.2 GitHub Actions（真实查询，不是推断）

查询对象：`GET /repos/Hiyomi-Kuro/StardewValley/commits/345d712…/check-runs` 与
`/actions/runs?head_sha=345d712…`（2026-11-06T19:29+08:00 采集）。

| 项 | 值 |
| --- | --- |
| run ID | `42207594776`（workflow `ci`，run_number 5，event `push`） |
| 结论 | **success** |
| job 1 | `offline checks (python, powershell, manifests, privacy)` → **success**（11:05:28Z → 11:05:44Z） |
| job 2 | `dotnet build + loader host tests` → **success**（11:05:28Z → 11:06:44Z） |
| URL | https://github.com/Hiyomi-Kuro/StardewValley/actions/runs/42207594776 |

> 与上一阶段提示词里"combined status 查询没有返回任何 status 项"不同：本次 `check-runs` 明确返回了
> 两个完成的 check，结论都是 success。这是**对具体 commit 的远程结果**，不是本地推断。
>
> 本阶段**新增**的离线测试（`test_p114_probe.py`、`test_deploy_safe.ps1`）已经写进 workflow，但
> **尚未在远程执行**——因为本阶段不 push。它们的远程状态只能记 **NOT RUN（未 push）**，
> 不能写成"CI 已通过新测试"。

### 1.3 本地仓库

| 项 | 值 |
| --- | --- |
| 仓库根 | 相对仓库根执行；本机绝对路径不入库 |
| branch / HEAD | `main` / `345d71280ce5c255c4b9db490732cd9af312c0aa` |
| `origin/main` | 同上（`git fetch --prune` 后比对，ahead 0 / behind 0） |
| 工作区（本阶段开始时） | **干净**：0 modified、0 untracked、0 stash |
| 工作区（本阶段结束时） | 仅本阶段新增/修改的文件（见 §11），未 commit |

---

## 2. Gate 0：旧工作区与 `345d712` 的无损协调

上一阶段的 DSH session 记录里，工作区曾停在 `76aa836` 并带 14 modified + 3 untracked。
本次核实的**实际状态**是：HEAD 已经是 `345d712`，工作区完全干净（`git status --porcelain -uall`
输出 0 行、`git stash list` 为空、`git worktree list` 只有主工作树）。

因此：

1. **没有需要保护的 dirty 变更**——不存在"覆盖用户改动"的风险；
2. 逐一核对了 `76aa836..345d712` 的 17 个文件是否存在且可哈希（下表），证明这批改动完整落在磁盘上；
3. 没有执行 `git reset --hard` / `git clean` / 强制 checkout / stash / 批量覆盖；
4. 因为工作区本来就等于基线，**没有**创建 detached worktree（`§1.5` 的备选路径未被触发）；
5. 本阶段所有写入都是**新增文件**或对本阶段相关文件的定向修改，全部可枚举、可 `git diff` 复核。

| # | 文件 | SHA-256（工作区 = `345d712`） |
| --- | --- | --- |
| 1 | `.github/workflows/ci.yml` | `c298873f5759b32ba0c75a24aadd46a5c4cf137e8b8793568d3aaf321d10229a` |
| 2 | `Mod/ModFramework.Runtime/AssemblyIdentity.cs` | `ecaec5410e9b7fcdbafe5a0030c669ffa72f9d166ec6710e128804b7d42911b0` |
| 3 | `Mod/ModFramework.Runtime/AssemblyIdentityComparison.cs` | `cd839ddcd692ccd1a846f1a2e905bb99a2982cf1b53d39995bfdd5e66d3b04b2` |
| 4 | `Mod/ModFramework.Runtime/AssemblyPreflight.cs` | `881b604b1f4439d477238381c4f80ab139c1a7be31cab8de5a48c7dc6d43d8f5` |
| 5 | `Mod/ModFramework.Runtime/AssemblyStaging.cs` | `d94d9d3c8552beda9a3451cc5ab366bf3ab73701871da6a405389277e90a76b3` |
| 6 | `Mod/ModFramework.Runtime/ContentIdentity.cs` | `be98278bcdb5c705c4eab7bd37c9fdba0ef4e457204c746ff0142650fdb3d090` |
| 7 | `Mod/ModFramework.Runtime/ErrorCodes.cs` | `c6290f611b411e6dffe95b4c16ffa73e4f036708fad4a042d2e29c55ee92a5dc` |
| 8 | `Mod/ModFramework.Runtime/Loader.cs` | `97abc10e2e4b5503b9abde6b57c60ee0df0ebb2c769dbff249f29e2511991ff2` |
| 9 | `README.md` | `033d6e3d280dee4ef92b0fac53001cc63d33907c184d02edecc243ab60f8b5f8` |
| 10 | `Tools/tests/ModFramework.Tests/Phase113Tests.cs` | `fe94d35953aa065c9d7e32ef98c5532c5068d235478b7e9caa8a829db13b60b2` |
| 11 | `Tools/tests/ModFramework.Tests/Program.cs` | `d163c39223c3eb50c61d5590161ea3b91c41ff22075e9d786e8c75f57bccacf5` |
| 12 | `docs/architecture/phase1-loader-design.md` | `fd56022286ae51f8e6d0e7d9284ab40a851c6d00c6287c2365afbef7d94df64d` |
| 13 | `docs/architecture/phase1-runtime-constraints.md` | `df0b5a1a45d9dc852e2a4acb96855da99d259b0c0dd32aa05117c47827718084` |
| 14 | `docs/audit/phase1.1.3-evidence-index.md` | `aee1e063538782bab64e19ee0fc5e9e5022902dec2539797836a440d25d48d27` |
| 15 | `docs/audit/phase1.1.3-report.md` | `a4a718432abf2b0cea1de9d1f05e2aad649226017c4314066577f63592f80046` |
| 16 | `docs/modding/creating-a-mod.md` | `74d06ea5c1251dfb1031b03eb39fec05791c1a33e4be3b5d719771911698c555` |
| 17 | `docs/modding/manifest-spec.md` | `f96834aabca3974c30633375142d23a208abd13a678e2eb2a59e77e6bc3ac81d` |

> 上面 12/13/11/9 四个文件在本阶段被**有意修改**（§11 会给出新哈希）；本表记录的是
> **协调时刻**的基线哈希，用来证明"起始状态与远程 commit 一致"。

---

## 3. `Probe.Run` 的 T11/T12 风险与修复

### 3.1 问题

旧 `Mod/MonoRuntimeProbe/ProbeEntry.cs` 的 `Probe.Run(...)` 无条件执行：

- **T11**：`mprotect` 把方法所在代码页改成 RWX，再立刻还原（不写字节，但改页权限）；
- **T12**：真的往代码页写入 16 字节 ARM64 绝对跳转桩，然后调用目标方法。

`Mod/MonoRuntimeProbe/README.md` 自己记录过：**T12 在 MuMu 12 上让进程原生崩溃**（缺指令缓存刷新），
报告来不及落盘。也就是说"跑一次常规诊断"= "可能让游戏进程原生崩溃且没有任何证据留下"。

第二类风险是**入口语义**：`Run(string, bool allowGameInvocation)` 那个 `bool` 只是"是否调用
`Game1.IsMultiplayer`"，但它让"安全诊断"和"会碰游戏对象的诊断"共用了一个入口。

### 3.2 修复

| 改动 | 位置 | 效果 |
| --- | --- | --- |
| 新增 `Probe.RunSafe(string)` | `ProbeEntry.cs` | 唯一常规入口；只做内存内验证 + 只读元数据枚举 |
| `Probe.Run(string,bool)` 只委托 | `ProbeEntry.cs` | 保留签名兼容，第二个参数被忽略（`_ = allowGameInvocation;`），不再有 Game1 调用分支 |
| T07 改为只读 | `ProbeEntry.cs` | `GameTypeSurface()` 只枚举成员数量，`allowGameInvocation=true` 这条路径**已删除** |
| T11 移入 `NativePageProbe.cs` | 新文件 | 整个文件被 `#if LEGACY_NATIVE_EXPERIMENTS` 包裹 |
| T12 留在 `DetourProbe.cs` | 原文件 | 整个文件被 `#if LEGACY_NATIVE_EXPERIMENTS` 包裹 |
| 默认构建关闭 unsafe | `MonoRuntimeProbe.csproj` | `AllowUnsafeBlocks=false`；只有 `-p:LegacyNativeExperiments=true` 时才打开并定义符号 |
| 报告脱敏 | `ProbeEntry.cs` | `basedir` 只写末段目录名，不再输出绝对路径 |

**本阶段没有执行 T11，也没有执行 T12**——在桌面、模拟器、设备上都没有。它们的代码只在
`-p:LegacyNativeExperiments=true` 才参与编译，而本阶段没有任何构建、脚本、测试或 manifest mod 使用
那个配置（`Tools/tests/test_p114_probe.py` 对此有专门断言）。

验证（`p114-static-audit`，46/46）：

- `probe_entry_has_no_native_code`：`ProbeEntry.cs` 的**代码**（去注释、去字符串）里没有
  `DllImport` / `mprotect` / `Reflection.Emit` / `DynamicMethod` / `Harmony` / `Marshal.WriteByte`；
- `probe_entry_does_not_reference_experiments`：安全聚合方法不引用 `DetourProbe` / `NativePageProbe`；
- `probe_entry_has_no_loadfrom`：安全探针不从磁盘加载任何程序集；
- `probe_detour_compiled_out_by_default` / `probe_native_page_compiled_out_by_default`：
  `#if` 出现在任何 `using` 之前，整文件被条件包裹。

---

## 4. P114 诊断 Mod

### 4.1 结构

| 路径 | 作用 | 有 `manifest.json`？ |
| --- | --- | --- |
| `Mod/P114.RuntimeProbe/` | 入口 mod：`P114RuntimeProbeEntry : IModEntry` + `SafeProbes` | ✅（唯一可被 Loader 发现的） |
| `Mod/P114.RuntimeProbe.Dependency/` | private dependency fixture（marker `p114-private-dependency-alive`） | ❌ |
| `Mod/P114.RuntimeProbe.Fixtures/` | `Assembly.LoadFrom` 研究 fixture（参数化构建） | ❌ |

### 4.2 manifest（`Mod/P114.RuntimeProbe/manifest.json`）

```json
{
  "schemaVersion": 1,
  "id": "P114.RuntimeProbe",
  "name": "P114 Runtime Compatibility Probe",
  "version": "1.1.4",
  "entryAssembly": "P114.RuntimeProbe.dll",
  "entryType": "P114.RuntimeProbe.P114RuntimeProbeEntry",
  "gameVersion": "*",
  "frameworkVersion": "*",
  "privateAssemblies": [ "P114.RuntimeProbe.Dependency.dll" ]
}
```

- `gameVersion`/`frameworkVersion` 都是 `"*"`：诊断 mod 必须在任何构建上都能跑（否则被版本门禁拒掉，
  正好违背它"测量未知"的目的）；
- `privateAssemblies` **只**声明依赖 dll；fixture（`P114Same`/`P114Diff`/`P114Alpha`/`P114Beta`）
  **不在**声明里，所以 Loader 永远不会预加载它们——它们只被探针自己 `Assembly.LoadFrom`；
- fixture 放在包内的 `probe-fixtures/` **子目录**里，而 `ModDiscovery` 只扫 `<Mods>/` 的一层子目录，
  所以即使有人把整个包当 mod 目录扫，也不会把 fixture 当成 mod 包。

### 4.3 输出

`<mod 目录>/probe-output/phase1.1.4.txt`（纯文本，UTF-8）。写入失败时只记 `context.Log.Error`，
**不抛**，游戏继续启动。报告里没有绝对路径。

### 4.4 fixtures 的构造规则（可复现）

| 场景 | 构建方式 | 落位 |
| --- | --- | --- |
| 相同身份、相同字节、不同路径（R-05） | `P114Same` 构建一次，复制两份 | `probe-fixtures/same-a/`、`same-b/` |
| 相同身份、不同字节、不同路径（R-06） | `P114Diff` 用 `-p:Variant=VARIANT_B` 构建两次（AssemblyName/Version/Culture/Token 全同，IL 字节不同，`Describe()` 返回 `variant=A`/`variant=B`） | `probe-fixtures/diff-a/`、`diff-b/` |
| 不同 SimpleName（R-07） | `P114Alpha`、`P114Beta` | `probe-fixtures/alpha/`、`beta/` |
| 损坏 fixture（R-14） | 64 字节 `0x00` | `probe-fixtures/corrupt/P114Broken.dll` |

打包由 `Tools/tests/test_loader.py::build_p114_package()` 完成：构建 → 复制 → 断言
`same-a == same-b` 且 `diff-a != diff-b`。

---

## 5. R-01 … R-14 结果

> **证据等级：Desktop .NET 8.0.4 / Windows x64 宿主**（`Tools/tests/ModFramework.Tests`，真实
> Loader 链路）。**不是** Android 证据。Emulator 与 ARM64 列见 §7。

| ID | 结果（桌面） | 观察值 | Emulator | ARM64 物理 |
| --- | --- | --- | --- | --- |
| R-01 | **PASS** | `len=32`，`hex=e3b0c442…b855`，`matches-vector=yes` | NOT RUN | NOT RUN |
| R-02 | **PASS** | `len=32`，`hex=ba7816bf…0015ad`，`matches-vector=yes` | NOT RUN | NOT RUN |
| R-03 | **PASS（Loader 侧）** | `preflight-stage=complete`（行 17）先于 `assembly-load=…P114.RuntimeProbe.dll`（行 23）；`state=Initialized` | NOT RUN | NOT RUN |
| R-04 | **PASS** | 同路径两次：`first=ok second=ok same-reference=True` | NOT RUN | NOT RUN |
| R-05 | **PASS** | `same-bytes=yes`，两次 `LoadFrom` 都成功，`same-reference=True`，`appdomain delta=0` | NOT RUN | NOT RUN |
| R-06 | **PASS（记录了真实行为）** | `same-bytes=no(byte 136)`；第一次 `ok`（marker=`variant=A`）；第二次 **`FileLoadException: Assembly with same name is already loaded`**；`delta=1` | NOT RUN | NOT RUN |
| R-07 | **PASS** | `name-alpha=P114Alpha`、`name-beta=P114Beta`，`independent-reference=True` | NOT RUN | NOT RUN |
| R-08 | **PASS** | `entry=name=P114.RuntimeProbe version=1.1.4.0 culture=empty token=null` | NOT RUN | NOT RUN |
| R-09 | **PASS** | `empty=False readable=True leaf=P114.RuntimeProbe.dll`；报告无绝对路径 | NOT RUN | NOT RUN |
| R-10 | **PASS** | 一次枚举 `count=54`，`entry-index=49`；顺序被记录但**不当作契约** | NOT RUN | NOT RUN |
| R-11 | **PASS** | `marker=p114-private-dependency-alive:…v1.0.0.0`，`preloaded-before-entry=True`（依赖 `assembly-load` 行 22 早于入口行 23） | NOT RUN | NOT RUN |
| R-12 | **PASS（Loader 侧 + 宿主）** | `preflight-stage=complete` 先于首条 `assembly-load=`；入口程序集 `Location` 指向 `<tmp>/.staging/<run>/P114.RuntimeProbe/P114.RuntimeProbe.dll` | NOT RUN | NOT RUN |
| R-13 | **PASS** | `loaded-from=P114.RuntimeProbe.dll bytes=22016 sha256=616f542f…22ca3`，与 loader 报告里的 `snapshot=sha256:616f542f…` **一致** | NOT RUN | NOT RUN |
| R-14 | **PASS** | `failed=0`；`corrupt-load=BadImageFormatException`、`corrupt-loaded=False`；`deliberate-throw-caught=InvalidOperationException`；`aggregate-continued-after-failures=yes` | NOT RUN | NOT RUN |

### 5.1 这些观察值意味着什么（以及不意味着什么）

1. **`SHA256.HashData(byte[])` 在桌面可用且向量正确**（R-01/R-02）。这只证明 BCL 调用形状与算法实现
   正确；**AndroidRefsDir 编译通过**只证明元数据里存在该方法（B 级）。**MonoVM 上的运行时可用性仍未验证**。
2. **`Assembly.LoadFrom` 的"同身份不同内容"在桌面上是失败的**（R-06：`FileLoadException`），
   而且 `AppDomain` 计数 +1 —— 说明失败前确实发生了某种登记。这**不是** Mono 的行为，
   更**不能**据此改 Loader 的缓存策略。
3. **R-13 是真正的端到端证据**：探针算出的 SHA-256 与 Loader 预检记录的快照摘要逐字符相同，
   证明"实际被 `LoadFrom` 的文件 = 预检批准的那份字节"在桌面路径上成立。
4. **R-05 的 `same-reference=True`（桌面）** 与 README 里"不同路径相同标识返回先加载的那一份"一致；
   Mono 上是否相同必须等设备。
5. **R-10 的顺序只被"记录"**。桌面上的顺序是"进程启动顺序"，把它当契约会让实现变得脆弱；
   报告里明确写 `order-is-a-contract=no`。

### 5.2 仍未关闭的运行时未决项（诚实清单）

| 未决项 | 现在有什么 | 还缺什么 |
| --- | --- | --- |
| 校验 → `Assembly.LoadFrom` 非原子 | 设计上接受（快照 + 加载前二次校验），R-13 证明摘要一致 | `Assembly.Load(byte[])` 在本运行时不存在 → 真机语义验证 |
| AArch64 Mono 上 `SHA256.HashData` 可用性/异常形状 | 桌面 PASS + 编译期 PASS | Emulator（需授权）/ ARM64 真机 |
| 相同内容不同路径是否返回同一实例 | 桌面 `True` | Emulator / ARM64 |
| `GetAssemblies()` 顺序、`Assembly.Location` 语义、多实例歧义 | 桌面有值 | Emulator / ARM64 |
| MuMu 12 是不是 ARM64 真机 | **不是**（`x86_64` + `libnb.so`） | 一台物理 ARM64 设备 |

---

## 6. 部署安全修复

### 6.1 原有风险

`Tools/deploy-apk.ps1` 的安装路径**无条件**先执行：

```powershell
& $adb -s $serial uninstall com.chucklefish.stardewvalley
& $adb -s $serial install --no-incremental $apk
```

`AGENTS.md` 已经写明：**卸载会删除存档**；本工程用本机 debug 证书，与官方签名不同，
所以"覆盖安装"也不可能。也就是说这个脚本的默认行为 = 每次部署都可能清掉玩家存档。

### 6.2 新的部署契约（`Tools/deploy-apk-safe.ps1`）

| # | 契约 | 实现位置 |
| --- | --- | --- |
| 1 | 默认**只读**：列设备、读属性、读包状态、打印将执行的命令 | 无 `-InstallApproved`/`-PushModsApproved` 时走 PRECHECK-ONLY |
| 2 | 安装必须 `-InstallApproved` | `if ($InstallApproved) { … }` |
| 3 | 推送 Mods 必须 `-PushModsApproved` + 显式 `-ModsSourceDirectory` | 同上 |
| 4 | **任何路径都不执行** `adb uninstall` / `pm clear` / 清数据 | 脚本里没有这些命令（静态检查 + fake adb 日志双重断言） |
| 5 | 只有能证明"本地 APK 与已安装包签名一致"时才 `install -r` | `signature-gate` |
| 6 | 签名不一致 → `SIGNATURE_MISMATCH_REQUIRES_USER_ACTION` 并停止 | `Finish 3` |
| 7 | 签名读不出 → `SIGNATURE_UNREADABLE_USE_MANUAL_VERIFICATION` 并停止 | `Finish 3` |
| 8 | 未安装时不把"包名相同"当签名相同；首次安装也要批准 | `gateState = FIRST INSTALL` 分支 |
| 9 | 安装前打印 serial / device-class / 包名 / APK SHA-256 / 签名摘要 / 是否已安装 / 是否替换 | §「planned commands」+ 上方信息块 |
| 10 | 不 force-stop、不启动游戏、不自动改 Mods | 脚本里没有 `am` 命令 |
| 11 | 设备写入使用显式目标路径（`/data/data/<pkg>/files/Mods`） | `$remoteRoot` |
| 12 | 回滚不以卸载为默认动作 | 只允许同签名 `install -r`；签名不符时输出人工步骤 |

旧脚本被保留但加了**醒目的弃用/危险横幅**，并且把"卸载重装"放在 `-LegacyDestructiveInstall`
后面：**不传这个开关就只构建、绝不碰设备**（退出码 2 + 明确提示改用安全脚本）。

### 6.3 fake adb / fake apksigner 测试（39/39 PASS）

用 `.cmd`-free 的 fake `adb`（记录每一次调用的 argv）与 fake `apksigner`（对文件算 SHA-256 当证书摘要），
在无设备环境下跑真实脚本。每个用例**不只检查退出码，还断言 fake adb 日志里没有危险命令**。

| 用例 | 断言 |
| --- | --- |
| 默认运行 | exit 0、`PRECHECK-ONLY`、日志里**没有** `install`/`pull`/`push` |
| 无设备 | exit 2、`BLOCKED reason=no-device`、只调用了 `devices` |
| 两台设备 + 无 `-Serial` | exit 2、`ambiguous-device`、没有发出任何按设备命令 |
| 两台设备 + 显式 serial | exit 0、选用显式 serial |
| 未连接的 serial | exit 2、`serial-not-attached` |
| 批准 + 签名一致 | 执行 `install -r --no-incremental`，**没有** uninstall |
| 批准 + 签名不一致 | exit 3、`SIGNATURE_MISMATCH_REQUIRES_USER_ACTION`、没有 install/uninstall |
| 批准 + 拉不到已安装 APK | exit 3、`SIGNATURE_UNREADABLE_USE_MANUAL_VERIFICATION` |
| 批准 + 本地签名读不出 | exit 3、拒绝 |
| 批准 + 本地 APK 缺失 | exit 3、`reason=apk-missing` |
| 包未安装 + 批准 | `install --no-incremental`（**不带** `-r`）、无 uninstall |
| 未批准 + 有 Mods 源目录 | 不 push |
| 批准 push 但没给源目录 | exit 3、`missing-mods-source-directory` |
| 批准 push + 源目录 | 真的 push（证明门禁不是"永远不推"）、无 uninstall |
| 路径含空格/`[]`/`()`/中文 | exit 0、报告里路径原样保留 |
| 输出脱敏 | 不含用户 profile 路径（`Users` 段被整体替换为 `<home>`），也不含临时根路径 |
| 含空格用户名的脱敏 | `<home>` 之后不残留用户名的第二段（如 ` Izuki`） |
| 静态检查 | 脚本里没有可执行的 uninstall/pm clear/force-stop 行 |

> 这套测试**抓到三个真实缺陷**（都在修复前的代码里，已修）：
> 1. `$adb = Resolve-Adb $Adb` —— PowerShell 变量名大小写不敏感，`[string]$Adb` 参数把返回对象
>    强制转成字符串，导致 `-Adb` 永远解析失败（"no adb client found"）；
> 2. `@argv`（数组）splatting 只做**位置**绑定，`-Adb`/`-OutFile` 被当成位置参数，
>    于是 `-OutFile` 收到了目录路径、`Set-Content` 试图写目录（还在仓库根目录留下了两个日志文件，
>    已删除）；
> 3. 脱敏正则 `[A-Z]:\\Users\\[^\\\s"]+` 在**用户名含空格**时只吃掉第一个词，
>    输出里会残留 ` Izuki\AppData\...`。已改为替换整个 `Users\<name>` 段（`deploy-apk-safe.ps1` 与
>    `device-precheck.ps1` 同修），并新增断言 `spaced_user_name_fully_redacted`。
> 这三个缺陷在手工试跑时都不会暴露，只有"断言 fake adb 到底收到了什么 / 输出里到底有什么"才会暴露。
>
> 第 4 个缺陷是拿**真实环境**跑默认路径时发现的：`Resolve-ApkSigner` 用
> `Get-ExeName "apksigner"`（Windows 上拼成 `apksigner.exe`）去 build-tools 里找，
> 而 Android SDK 实际提供的是 **`apksigner.bat`** → 永远 `UNKNOWN`，于是 `-InstallApproved`
> 会被 fail-closed 拒绝（安全但不可用）。已改为依次尝试
> `apksigner.bat` / `apksigner.exe` / `apksigner.cmd`，实跑后本地签名摘要正确显示为
> `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de`。

---

## 7. 设备：E0 结果与分类

命令（只读）：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools\device-precheck.ps1 -OutFile <log>`

| 项 | 值 |
| --- | --- |
| serial | `emulator-5554` |
| `ro.product.cpu.abi` | **`x86_64`** |
| `ro.product.cpu.abilist` | `x86_64,arm64-v8a,x86,armeabi-v7a,armeabi` |
| `ro.dalvik.vm.native.bridge` | **`libnb.so`** |
| `ro.kernel.qemu` | （空） |
| Android / API / 补丁 | 12 / 32 / 2022-04-05 |
| 厂商 / 型号 | vivo / V2309A（PD2309） |
| `device-class` | **`NOT-ARM64-PHYSICAL (emulator/translated/x86_64)`** |
| root | `su -c id` → `uid=0`（可用） |
| 游戏包 | 已安装，versionCode **239**，versionName **1.6.15.0**，`primaryCpuAbi=arm64-v8a`（**翻译执行**，不是物理 ARM64） |
| 设备端 APK SHA-256 | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| 本地基线 APK SHA-256 | `65e1e2af…f9d`（**与设备端相同**，388,967,580 字节） |
| `Mods/` | **不存在**（`/sdcard/Android/data/<pkg>/files/Mods: No such file or directory`） |
| E0 | **PASS（只读）** |
| E1/E2 | **BLOCKED `reason=no-arm64-physical-device`** |

结论：

1. 唯一可用目标是 **x86_64 + `libnb.so` 翻译层模拟器**；Emulator 结果**不得**计入 Device 列，
   也不得声称它证明了原生 AArch64 CPU 行为。
2. 已安装包与本地基线 APK **逐字节相同**（同一 SHA-256）→ 若将来批准安装，这是"同签名覆盖"的
   充分证据，不需要卸载、不需要清数据、也不需要 `adb pull` 比对证书。
3. 本阶段**没有**在设备上跑 P114 探针：那需要往 `/data/data/<pkg>/files/Mods/` 写文件（设备写入），
   属于必须批准的类别。Emulator 列因此是 **NOT RUN**，不是 FAIL、也不是 PASS。

---

## 8. 构建、测试与命令记录

工具链（本机实测，**不写死用户路径**）：

| 工具 | 实际值 | 发现方式 |
| --- | --- | --- |
| .NET SDK | **10.0.401** | `$env:STARDEW_DOTNET` → 私有 SDK；`dotnet` on PATH 只有 8.0.4 **运行时**，没有 SDK |
| 运行时（跑测试宿主用） | 10.0.12（`DOTNET_ROLL_FORWARD=LatestMajor`） | 由测试驱动自动设置 |
| Python | 3.12.10 | PATH |
| PowerShell | 5.1.26100 | 系统 |
| adb | `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe` | `device-precheck.ps1` 的候选表 |
| apksigner | `build-tools/36.0.0/apksigner.bat` | 同上（排序取最高版本） |
| 参考程序集 | `.tmp/p113/refs`（65 个 dll） | Phase 1.1.3 解出，本阶段复核仍在 |

> 说明：Phase 1.1.3 记录里的 `.tmp/phase0.5/dotnet/dotnet.exe` **本机不存在**。
> 本阶段新增 `Tools/find-dotnet.ps1` 与 `Tools/tests/toolchain.py`，按
> `--dotnet` → `$env:STARDEW_DOTNET` → `.tmp/*/dotnet/dotnet[.exe]` → `PATH` 发现，
> 并要求 `dotnet --list-sdks` 非空（只有运行时不算可用）。文档不再出现任何用户专属绝对路径。

| # | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| 1 | `python Tools/tests/test_loader.py --refs .tmp/p113/refs` | 0 | **45/45** 驱动检查；内部宿主 **473/473** | `10-test-loader.txt` |
| 2 | `python Tools/tests/test_mod_pipeline.py --refs .tmp/p113/refs` | 0 | **27/27** | `31-mod-pipeline.txt` |
| 3 | `python Tools/tests/test_ci_offline.py` | 0 | **13/13** | `run-all-suites/ci-offline.txt` |
| 4 | `python Tools/tests/test_p114_probe.py` | 0 | **46/46** | `run-all-suites/p114-static-audit.txt` |
| 5 | `powershell -File Tools/tests/test_deploy_safe.ps1` | 0 | **39/39** | `run-all-suites/deploy-safe-fake-adb.txt` |
| 6 | `powershell -File Tools/tests/test_device_precheck.ps1` | 0 | **10/10** | `run-all-suites/device-precheck-selftest.txt` |
| 7 | `python Tools/privacy-scan.py --include-untracked` | 0 | **RESULT: OK**（INFO 项为机器路径，非密钥） | `run-all-suites/privacy-scan.txt` |
| 8 | `powershell -File Tools/tests/run-all.ps1 -Refs .tmp/p113/refs -Dotnet …` | 1 → 见下 | 首次 6/8（两个环境问题），修复后逐项复跑通过 | `30-run-all.txt` |
| 9 | `git diff --check` | 0 | 通过（仅 LF→CRLF 的换行提示） | — |
| 10 | AndroidRefsDir 编译（6 个工程） | 0 | 全部通过 | `40-androidrefs-builds.txt` |
| 11 | AndroidRefsDir 负向对照（假 refs 路径） | 1（**预期**） | `CS0246 未能找到类型 System` → 证明 `AndroidRefsDir` 真的替换了框架引用 | `41-androidrefs-negative-control.txt` |
| 12 | `Tools/device-precheck.ps1 -OutFile …` | 0 | `E0: PASS (read-only)` | `20-e0-precheck.txt` |
| 13 | 本地 APK 摘要 + 签名 | 0 | `sha256=65e1e2af…`，`signer-sha256=611c8fa4…` | `21-local-apk.txt` |
| 14 | `powershell -File Tools/deploy-apk-safe.ps1`（**对真实目标跑默认只读路径**） | 0 | `PRECHECK-ONLY ok`；识别 `emulator-5554` + `EMULATOR-TRANSLATED`；读到本地签名摘要；`signature-gate: NOT EVALUATED`；**0 设备写** | `22-deploy-gate-live-readonly.txt` |
| 15 | 扫描已构建 APK 的条目（只读） | 0 | 3672 个条目，**没有任何** `P114` / `MonoRuntimeProbe` / `ModFramework` 条目 | `23-apk-content-scan.txt` |

**关于第 8 行（run-all 首跑 6/8）**：两个失败都不是被测代码的问题，而是"用新 SDK 构建、用没有
8.0 运行时的主机运行"：

1. `p111_parser_differential_65_refs`：`dotnet <dll>` 找不到 `Microsoft.NETCore.App 8.0.0`。
   修复：测试驱动对**所有**托管运行设 `DOTNET_ROLL_FORWARD=LatestMajor`；
2. `test_mod_pipeline.py` 的 `patch_default_exit0`：同上。
   修复：`test_mod_pipeline.py` 在 `main()` 里设置同一环境变量。
3. 另外 `git diff --check` 报 `phase0.5-device-checklist.md:282: new blank line at EOF`，
   是追加内容留下的空行，已修剪。

修复后逐项复跑：`test_loader` 45/45、`test_mod_pipeline` 27/27、`git diff --check` 退出码 0。

AndroidRefsDir 编译（B 级 / 编译期证据）：

| 工程 | 结果 |
| --- | --- |
| `Mod/ModFramework.Runtime` | ✅ |
| `Mod/ExampleMod.HelloWorld` | ✅ |
| `Mod/P114.RuntimeProbe` | ✅ |
| `Mod/P114.RuntimeProbe.Dependency` | ✅ |
| `Mod/P114.RuntimeProbe.Fixtures` | ✅ |
| `Mod/MonoRuntimeProbe` | ✅ |

> 这只证明"这些成员在目标引用面里存在"。**不证明**它们在 MonoVM 上行为正确。

---

## 9. Definition of Done 验收矩阵

| ID | 主题 | 结论 | 证据 |
| --- | --- | --- | --- |
| P114-01 | Git 基线协调 | ✅ **PASS** | HEAD=origin/main=345d712；工作区 0 dirty/untracked；17 个基线文件逐一 SHA-256（§2） |
| P114-02 | 安全探针默认入口 | ✅ **PASS** | `Probe.RunSafe` 是唯一常规入口；`Probe.Run` 只委托；默认编译无 P/Invoke/mprotect/RWX/detour/unsafe（静态审计 46/46） |
| P114-03 | SHA-256 向量 | ⚠️ **PARTIAL（Desktop）** | R-01/R-02 桌面 PASS；Emulator/ARM64 = NOT RUN |
| P114-04 | Loader 集成 | ✅ **PASS（Desktop）** | 真实 preflight→staging→LoadFrom→Initialize；`state=Initialized`；`assembly-load=` 行在 `preflight-stage=complete` 之后 |
| P114-05 | LoadFrom 同路径 | ✅ **PASS（Desktop）** | R-04 `same-reference=True` |
| P114-06 | LoadFrom 不同路径 | ✅ **PASS（Desktop）** | R-05 `same-bytes=yes`/delta=0；R-06 记录 `FileLoadException`（桌面值） |
| P114-07 | 身份与枚举 | ✅ **PASS（Desktop）** | R-08/R-09/R-10；报告无绝对路径（断言 `drive-letter=False`、`/data/=False`） |
| P114-08 | Private dependency | ✅ **PASS** | R-11 marker 命中，`preloaded-before-entry=True`，依赖 `assembly-load` 行早于入口行 |
| P114-09 | 部署默认安全 | ✅ **PASS** | 默认 0 设备写；脚本内无可执行 uninstall/pm clear；fake adb 日志负向断言 |
| P114-10 | 签名门禁 | ✅ **PASS** | 同签名→仅 `install -r`；不一致/不可读→停止且无 uninstall |
| P114-11 | Fake ADB | ✅ **PASS** | 39/39，全部含危险命令负向断言 |
| P114-12 | E0 | ✅ **PASS（只读）** | `E0 PASS`；`device-class=NOT-ARM64-PHYSICAL`；E1/E2 `BLOCKED reason=no-arm64-physical-device` |
| P114-13 | AndroidRefsDir | ✅ **PASS（B 级）** | 6 个工程编译通过 + 负向对照失败证明开关有效 |
| P114-14 | Desktop 回归 | ✅ **PASS** | 宿主 427 → **473**（+46，全部为 P114 检查）；其余套件不再回归 |
| P114-15 | CI | ⚠️ **PARTIAL** | `345d712` 远程 run=success（已核实）；新增离线测试已进 workflow，但**未 push，远程状态 NOT RUN**。另：已构建 APK 的 3672 个条目里没有任何 P114/probe/framework 条目（`23-apk-content-scan.txt`） |
| P114-16 | Emulator / ARM64 | ⛔ **按预期停住** | Emulator: **NOT RUN（无安装授权）**；ARM64 physical: **BLOCKED（无设备）** |
| P114-17 | 文档 | ✅ **PASS** | 本报告 + 证据索引 + 两个 README + 架构/清单文档与实际行为一致 |
| P114-18 | 无损交付 | ✅ **PASS** | 未 commit/push/release；无 install/uninstall/push/force-stop/pm clear；未触碰 Saves |

**结论**：P114-01/02/04/05/06/07/08/09/10/11/12/13/14/17/18 达到验收；P114-03/15 为 PARTIAL
（桌面/已核实部分通过，设备与未 push 部分明确 NOT RUN）；P114-16 按设计停在批准门槛之前。

---

## 10. 修改文件清单

见 [`phase1.1.4-evidence-index.md`](phase1.1.4-evidence-index.md) §2（含 SHA-256 与用途）。

---

## 11. 仍需用户批准的精确操作

本阶段**停在**下面这一步之前。以下是**拟执行**（尚未执行）的命令与风险。

### 11.1 若要在 Emulator 上取得运行时证据（仍不是 ARM64 证据）

| 项 | 值 |
| --- | --- |
| 目标 serial | `emulator-5554`（x86_64 + `libnb.so`，**Emulator**） |
| 包名 | `com.chucklefish.stardewvalley` |
| 设备端 APK SHA-256 | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| 本地基线 APK SHA-256 | 与上面**相同** → 已是同一份包，**不需要**任何安装/卸载 |
| 本地 APK 签名摘要 | `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de`（apksigner，仅本地） |
| 需要写设备的内容 | `/data/data/com.chucklefish.stardewvalley/files/Mods/` 下的 `ModFramework.Runtime.dll`、`P114.RuntimeProbe/`（manifest + 2 个 dll + probe-fixtures/） |
| 拟执行命令 | `pwsh -File Tools/deploy-apk-safe.ps1 -Serial emulator-5554 -InstallApproved`（**不需要**，包已一致）<br>`pwsh -File Tools/deploy-apk-safe.ps1 -Serial emulator-5554 -PushModsApproved -ModsSourceDirectory <打包目录>`<br>`adb -s emulator-5554 shell "am start -W -n com.chucklefish.stardewvalley/com.chucklefish.stardewvalley.MainActivity"`<br>`adb -s emulator-5554 shell "su -c 'cat /data/data/com.chucklefish.stardewvalley/files/Mods/loader-report.txt'"`<br>`adb -s emulator-5554 shell "su -c 'cat /data/data/com.chucklefish.stardewvalley/files/Mods/P114.RuntimeProbe/probe-output/phase1.1.4.txt'"` |
| 存档风险 | **不卸载、不 `pm clear` → 存档不受影响**。启动游戏本身不修改 Saves。 |
| 回滚 | 删除 `/data/data/<pkg>/files/Mods/` 下的新文件即可（不含 Saves）。不需要重装 APK。 |
| 仍不能得到的结论 | ARM64/原生 AArch64 行为；MuMu 是翻译层，结果只能记 **Emulator** |

### 11.2 若要取得 ARM64 物理设备证据（真正的目标）

| 条件 | 现状 |
| --- | --- |
| 一台物理 ARM64 设备（`abi=arm64-v8a`、无 native bridge、非 qemu、非云手机） | ❌ 无 |
| `device-class = ARM64-PHYSICAL`（`device-precheck.ps1` 判定） | ❌ 当前为 `NOT-ARM64-PHYSICAL` |
| 用户批准安装/替换（若签名不符则**必须先备份 Saves**） | ❌ 未获得 |

在拿到这样的设备之前，**ARM64 列只能保持 BLOCKED**——不得通过改脚本、改属性或伪造输出去绕过分类。

---

## 12. 本阶段没有做的事（明确列表）

- 没有 commit / push / 创建 tag 或 Release；
- 没有 `adb install` / `install -r` / `uninstall` / `push` / `pull` / `pm clear` / `am force-stop` / `am start`；
- 没有创建或修改设备上的 `Mods/`、`Saves/`、`/data/data/` 任何内容；
- 没有运行 T11 / T12（也没有构建 `-p:LegacyNativeExperiments=true`）；
- 没有在 Emulator 或任何设备上运行 P114 探针（缺授权）；
- 没有启动 Phase 1.2、Harmony/detour 功能、SMAPI 兼容层、事件 Hook、Content/Data API、热重载、
  存档读写或任何游戏玩法功能；
- 没有修改 `ModFramework.Runtime` 的加载/缓存/预检策略（本阶段不改 Loader 语义）。
