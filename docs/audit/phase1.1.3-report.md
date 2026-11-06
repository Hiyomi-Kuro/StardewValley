# Phase 1.1.3 报告：Assembly Snapshot Deduplication Integrity & Fingerprint Collision Resistance

- 阶段：Phase 1.1.3（只做四个主题：去重去指纹授权化 / 快照完整性 Fail-Closed / claim 与加载身份绑定 / 确定性测试与文档）
- 上一阶段：[phase1.1.2-report.md](phase1.1.2-report.md)
- 证据索引：[phase1.1.3-evidence-index.md](phase1.1.3-evidence-index.md)
- 基线快照：`76aa836ea935d03aa64fcdd7ad164f9cf88626e6`

---

## 1. 总状态

| 维度 | 结果 |
| --- | --- |
| 功能修复（去重 / 完整性 / 身份） | **COMPLETE**（本地实现 + 确定性测试） |
| 桌面 .NET 测试与构建 | **COMPLETE**（427/427，AndroidRefsDir 编译通过） |
| 目标 Mono / ARM64 **运行时**验证 | **BLOCKED**（无 ARM64 设备，且本阶段禁止装机） |
| 远程 CI | **NOT RUN / AWAITING USER PUSH**（本阶段不 commit、不 push） |
| 交付动作 | 仅本地修改；未 `git commit`、未 `git push`、未创建 Release、未 adb 安装 |

总状态：**COMPLETE（本地）**，其中 P113-16 的运行时部分为 **BLOCKED**。

---

## 2. 工作区与基线（A 级）

| 项 | 值 |
| --- | --- |
| 仓库根 | `D:/Download/DSH/StardewValley`（`git rev-parse --show-toplevel`） |
| remote | `origin https://github.com/Hiyomi-Kuro/StardewValley.git`（只读使用） |
| branch | `main` |
| 开始 HEAD | `76aa836ea935d03aa64fcdd7ad164f9cf88626e6` |
| 结束 HEAD | `76aa836ea935d03aa64fcdd7ad164f9cf88626e6`（未提交，工作区改动） |
| origin/main | `76aa836ea935d03aa64fcdd7ad164f9cf88626e6`（**未前进**，基线未漂移） |
| 开始时工作区 | `git status --porcelain` 为空：无 dirty、无 untracked |
| 结束时工作区 | 14 改（7 个 `ModFramework.Runtime` 源码 + 1 个测试注册 + 4 个文档 + `README.md` + CI 注释）+ 3 新增（1 个测试文件 + 2 个审计文档）；清单见 §11 |
| 用户改动保护 | 未使用 `git reset --hard` / `git clean` / 强制 checkout；未删除或移动用户文件 |

### 2.1 本机实际工具链（与上一阶段不同，必须写清）

上一阶段使用的项目私有 SDK（`.tmp/phase0.5/dotnet/dotnet.exe`）与引用程序集目录（`.tmp/phase0.5/refs`）
在本机**已不存在**（`.tmp/` 只剩 `p112` 与 `phase1.1.2` 的日志）。本阶段实际使用的是：

| 工具 | 版本 / 路径 | 性质 |
| --- | --- | --- |
| .NET SDK | `10.0.401`（`D:\Download\Codex\MuMu\tools\dotnet10\dotnet.exe`） | 复用本机已有 SDK（**未安装任何新 SDK**） |
| 运行时 | `.NETCore.App 10.0.12`（同一目录），测试以 `DOTNET_ROLL_FORWARD=Major` 运行 `net8.0` 产物 | 项目 TFM 未改 |
| Python | 3.12.10 | 复用 |
| PowerShell | Windows PowerShell 5.1（`powershell -NoProfile -ExecutionPolicy Bypass`，与 `run-all.ps1` 自带用法一致） | 复用 |
| 65 个游戏引用程序集 | 从**仓库内已有** `app/build/outputs/apk/debug/app-debug.apk` 的 XABA 容器解出（`assemblies/assemblies.blob` 58 条 + `assemblies/assemblies.arm64_v8a.blob` 7 条），落在 `.tmp/p113/refs`（`.gitignore` 覆盖） | 未下载、未入库、未上传 |

> 结论：本阶段没有扩大本机安装面；`.tmp/p113/refs` 与所有日志都在 `.tmp/` 下，不随仓库分发。

---

## 3. 原始缺陷：精确位置与数据流

### 3.1 缺陷 A：`TryStage` 把「指纹命中」当成「内容相同」

`Mod/ModFramework.Runtime/AssemblyStaging.cs`（基线版本）：

```csharp
private readonly Dictionary<string, string> _byFingerprint = new(StringComparer.Ordinal);
...
var fingerprint = ComputeFingerprint(bytes, claim.Digest.StartsWith("sha256:", ...));
if (_byFingerprint.TryGetValue(fingerprint, out existing) && existing != null)
{
    claim.StagedPath = existing;              // ← 命中即共享，没有任何字节比较
    claim.SnapshotFingerprint = fingerprint;
    return true;
}
```

三个后果：

1. **索引结构无法表达"一个指纹下有多份不同内容"**：value 是单个路径。
2. **共享分支没有长度检查、没有字节比较**：一个碰撞（真实 SHA-256 碰撞，或注入的碰撞）会让第二个
   claim 直接指向第一个 claim 的快照。
3. **同一函数既做去重键、又做完整性值**：`VerifyStagedFile` 用记录的指纹重新计算同一函数，
   所以"碰撞"必然同时骗过索引与加载前校验，弱算法（`sha1:` 回退）也被当成内容相等证明。
   另外写入路径是「先 `File.Delete` 再 `CreateNew`」，同 ModId 同声明名的不同内容会互相覆盖。

### 3.2 缺陷 B：preflight 的完整字节比较补不上跨 SimpleName 的别名

`AssemblyPreflight.Analyze` 按 `claim.Identity.Name`（SimpleName）分组，`ResolveWithinMods` 里
`CompareClaimContent` 比较的是 **`claim.Path`（mod 声明的原文件）**。于是：

```
TryRead(A) ─► TryStage(A) 写 snapshotA        ┐
TryRead(B) ─► TryStage(B) 指纹命中 → 指向 A 的  │ 别名发生在分组之前
Preflight: 组"A" = {A}，组"B" = {B}            │ 每组只有一个成员
           ResolveWithinMods 对 count<=1 直接 return
           ⇒ 没有任何代码问过"B 的 staged 文件是不是 B 的字节"
Loader: LoadFrom(claimB.StagedPath) → 实际是 A 的字节
```

关键点：**分组键是 SimpleName，跨组永远不会互相比较**；而"共享"的传播（把 canonical 的
`StagedPath` 写给同组其它 claim）只在**组内**发生。因此：

- 两个不同 SimpleName、不同内容、指纹相同的 private assembly，会进入不同组；
- 第二组是单成员组，preflight 没有比较对象；
- entry assembly 的"文件名 vs 程序集名"检查（`MF0203`）对 private assembly 完全不适用。

### 3.3 实际数据流（本阶段逐段核对）

```
原始文件 bytes
  └─ AssemblyClaimReader.TryRead        → Identity / Length / Digest（同一 buffer）
      └─ AssemblyStagingArea.TryStage   → dedup key →(候选) → 完整字节比较 → StagedPath + IntegrityDigest
          └─ AssemblyPreflight.Analyze  → 按 SimpleName 分组：身份比较 + 声明文件完整字节比较
              └─ Loader.TryResolveClaim → VerifySnapshot(长度 + SHA-256) → Assembly.LoadFrom(StagedPath)
                  └─ VerifyLoadedIdentity(Name/Version/Culture/Token) → Entry/Private 初始化
```

---

## 4. Red 证据：同一套测试在旧决策下确定性失败

### 4.1 制造碰撞的方式

`AssemblyStagingArea.DedupKeyForTests`（`internal`，仅测试可见）替换**去重键函数**：
生产代码里它只影响"找候选"，完整性摘要始终是真实 SHA-256。

两个 fixture 都是**可读取、可加载的真实托管程序集**，由编译产物按同长度 ASCII 改名得到
（`P112Entry` → `Q113EntA1` / `R113EntB2`，长度相同、字节不同、SimpleName 不同）。

### 4.2 Red 方法：决策级回退（测试文件两轮完全相同）

`.\tmp\p113\red\apply_red.py` 对**最终实现**做 3 处决策级替换，保留全部 seam 与类型，
使同一个测试文件在 Red / Green 都能编译：

1. 候选遍历在第一个候选处直接 `match = candidate; return true;`（跳过长度与字节比较）；
2. 记录的完整性值 = 去重键（"一个函数兼任两职"，即 1.1.2 的契约）；
3. 加载前校验改用同一个去重键函数重算。

回退后文件的 SHA-256 为 `69a250119991cd68848bf9f867142c4c8c5e707cc953de57b21512f2cf1b232e`；
还原后为 `d94d9d3c8552beda9a3451cc5ab366bf3ab73701871da6a405389277e90a76b3`（与 Green 备份逐字节一致）。

### 4.3 Red 结果（A 级）

`421 checks, 385 passed, 36 failed` — 日志 `.tmp/p113/logs/red-collision.txt`。
36 条失败全部来自目标行为断言，没有一条来自编译错误、fixture 构建失败或环境缺失。

最具说明力的三条：

| 断言 | Red 实际结果 |
| --- | --- |
| `p113_b_entries_only_never_load_a_foreign_assembly` | `MF0210 … the loaded assembly is "E113EntE1" but this claim was preflighted as "F113EntF2" (different simple name)` → **错误程序集确实进入了进程**，只是被新增的加载后身份核对发现 |
| `p113_a_second_snapshot_is_its_own_bytes` | 第二个 claim 的快照 SHA-256 = 第一个 claim 的字节（`15adec68…` vs 期望 `5d555945…`） |
| `p113_b_both_mods_initialize` | 两个 mod 都因 `MF0202`（`the snapshot file is 4608 bytes, expected 4096`）失败：private claim 被别名到 entry 快照 |

其余失败覆盖：同键下不能共存两份内容、第三个 claim 找到错误候选、不同长度被共享、
不可读候选被"猜"为相同、`MaxFiles`/`MaxTotalBytes` 在碰撞下失效、同 ModId 不同声明名互相覆盖、
跨 SimpleName 的 entry/private 字节错误、顺序无关分类失败、`sha256:` 完整性值缺失、
无 SHA-256 时不 Fail-Closed。

---

## 5. 最终去重结构与比较策略

```csharp
internal sealed class SnapshotEntry { string Path; long Length; string IntegrityDigest; }

private readonly Dictionary<string, List<SnapshotEntry>> _byDedupKey;   // 一个键 → 候选列表
```

`TryStage` 的判定顺序：

| 步骤 | 行为 |
| --- | --- |
| 1 | `dedupKey = ComputeDedupKey(bytes)`：`sha256:` → 无 SHA-256 时 `sha1:` → 都没有则空串（**不做索引**，每个 claim 各自落一份） |
| 2 | 遍历该键的候选：长度不同 → 跳过（可证明不同）；长度相同 → `ContentIdentity.CompareFileToBuffer(candidate.Path, bytes)` |
| 3 | `Same` → 共享（`StagedPath` / `IntegrityDigest` 取自该候选）；`Different` → 看下一个候选；`Unverifiable` → **该 claim 失败**（不猜、不新建） |
| 4 | 无候选匹配 → 预算检查（`MaxFiles` / `MaxTotalBytes` / 单文件 64 MiB）→ 算 SHA-256 完整性摘要 → 分配**未被占用**的路径 → `CreateNew` 写入 → 读回逐字节比较 → 入桶 |
| 5 | 预算与失败：一切异常删除半成品并 fail-closed，不回退到声明路径 |

要点：

- **比较的右手边永远是当前 claim 自己的 buffer**，不是另一个 claim 的 staged path。
- **"长度不同"被判为 `Different` 而不是 `Unverifiable`**：长度不同是"内容不同"的**证据**（同样字节不可能长度不同），
  所以直接跳过该候选并新建 artifact，比让整个 claim 失败更精确、且不会把善意 mod 拒之门外。
  其余无法下结论的情况（IO 异常、提前 EOF、文件在比较中变化、读到的字节数不足）一律 `Unverifiable` → fail-closed。
- **路径永不复用**：`<run>/<mod id>/<声明名>`，占用时追加 `~2`、`~3`…（上限 4096 次尝试），
  因此不同内容不会互相覆盖；仍逐条经过 `PathRules.IsInside` 检查。
- **复杂度**：每个 claim 最多与"同键且同长度"的候选做一次流式比较（16 KiB 双缓冲，不整文件载入）；
  正常（无碰撞）桶大小为 1，代价与 1.1.2 相同。碰撞只会增加比较次数，不会增加内存驻留。
- **资源边界**：预算按 **artifact** 计数（不是按 claim），所以碰撞无法绕过上限；
  共享已存在的 artifact 不消耗预算（`p113_a_sharing_still_works_at_the_file_bound`）。
- 生产代码新增 seam 仅三个 `internal` 静态成员：`DedupKeyForTests`、`SnapshotDigestUnavailableForTests`、
  `LimitsForTests`；没有任何来自 manifest / mod 配置 / 用户配置的开关。

---

## 6. 快照完整性校验器与 SHA-1 处理

| 项 | 结论 |
| --- | --- |
| 算法 | **只用 SHA-256**：`IntegrityDigest = "sha256:" + hex(SHA256(bytes))`，写入前计算、加载前重算 |
| 校验内容 | 文件存在 + 长度等于 claim 长度 + 全文件 SHA-256 等于记录值（额外字节必然改变摘要，另加长度检查） |
| SHA-1 的定位 | **只允许做去重键**（候选索引）。它从不被记录为完整性值，`VerifySnapshot` 对非 `sha256:` 的记录直接拒绝 |
| 无 SHA-256 时 | `TryStage` 直接失败（`MF0209`，消息点名缺少强摘要），**不落盘、不加载**；不存在"SHA-1 兜底继续跑"的分支 |
| 为什么不用 HMAC | HMAC 需要同进程内的密钥；能写快照的同 uid 代码同样能读进程内存里的密钥，威胁模型下不增加保证，却增加密钥生命周期与清理面。本阶段显式选择"无密钥的强摘要 + 加载前重算"，并把边界写进文档 |
| 目标 Mono 支持证据 | **B 级（编译期）**：用从 APK 解出的 65 个引用程序集 `-p:AndroidRefsDir=…` 编译通过（含 `SHA256.HashData`、`File.ReadAllBytes`、`File.OpenRead`、`Path.GetDirectoryName`、`Directory.CreateDirectory`、`Guid.NewGuid`） |
| 目标 Mono **运行时**语义 | **BLOCKED**：无 ARM64 设备、禁止装机。桌面通过**不得**当作真机通过 |
| 残余风险 | "校验通过 → `LoadFrom`"之间仍有窗口（快照与 mod 同处应用私有存储）。该窗口被压到最小、一旦发生会被拒绝，但不是原子操作；彻底关闭需要 `Assembly.Load(byte[])`，目标运行时已裁掉 |

与去重键的分离是可测试的：`p113_c_a_forced_collision_does_not_fool_the_verifier` 在**强制碰撞**下
改动快照，校验器仍然拒绝；`p113_c_the_recorded_integrity_value_is_sha256` 证明记录值不是去重键。

---

## 7. 身份核对结果（逐路径）

| 路径 | 核对内容 | 失败结果 | 测试 |
| --- | --- | --- | --- |
| Entry（`Load`） | 快照长度+SHA-256 → `LoadFrom` → 返回对象身份 vs `claim.Identity` → 文件名 vs 程序集名 | `MF0202` / `MF0210` / `MF0203` | `p113_b_entries_only_*`、`p113_c_a_tampered_*`、P112 既有用例 |
| Private（`Load`） | 同上；另核对 staged 字节解析出的身份 == claim 身份 | `MF0202` / `MF0210` | `p113_b_private_snapshot_identity_matches_its_claim`、`p113_b_privates_are_staged_verbatim` |
| `ShareFromClaim` | 组内身份 + 声明文件字节比较；若两成员 staged path 不同则**再比较两份 staged 文件**本身 | `MF0206`（fail-closed） | `p113_a_identical_bytes_*`、`p112_a_shared_artifact_*` |
| `ShareFromHost` | 复用前：身份逐项 + 宿主文件内容逐字节；复用后：`HostAssemblyIdentity.From(instance)` vs claim 身份 | `MF0207` / `MF0210` | `p112_c_host_*`、`p113_c_*` |
| `s_loadedByPath` 缓存命中 | 缓存实例身份 vs claim 身份（不因"同一路径"就免检） | `MF0210` | `p113_b_entries_only_load_twice`（缓存/共享计数） |
| 重复加载 | 同一 canonical artifact 只 `LoadFrom` 一次；不同 artifact 各自一次 | — | `p113_b_the_two_entries_are_loaded_separately`、`p112_a_shared_artifact_is_loaded_once` |

**未覆盖路径（明确列出）**：`ShareFromHost` 的"多份同身份宿主实例"只在 P111 用注入快照覆盖；
真机上 `AppDomain.GetAssemblies()` 的枚举顺序与 `Assembly.Location` 语义未验证（BLOCKED）。

---

## 8. Definition of Done 矩阵

| ID | 主题 | 结果 | 证据 |
| --- | --- | --- | --- |
| P113-01 | Fingerprint collision | **PASS** | `p113_a_collision_does_not_share_a_snapshot`、`p113_b_cross_name_entries_do_not_share_a_snapshot` |
| P113-02 | Full-byte dedup | **PASS** | `p113_a_second_snapshot_is_its_own_bytes`、`p113_a_a_different_length_is_never_shared`、`p113_b_same_name_different_content_is_still_rejected` |
| P113-03 | Multi-candidate bucket | **PASS** | `p113_a_bucket_holds_two_artifacts`、`p113_a_third_claim_finds_the_matching_candidate` |
| P113-04 | Cross-SimpleName | **PASS** | `p113_b_cross_name_entries_do_not_share_a_snapshot`、`p113_b_entries_are_staged_verbatim`、`p113_b_entries_only_*` |
| P113-05 | Private assembly | **PASS** | `p113_b_privates_are_staged_verbatim`、`p113_b_private_snapshot_identity_matches_its_claim` |
| P113-06 | Snapshot tampering | **PASS** | `p113_c_same_length_different_identity_is_refused`、`_same_identity_different_content_`、`_truncated_`、`_grown_`、`_deleted_`、`p113_c_a_tampered_snapshot_*` |
| P113-07 | Integrity verifier | **PASS** | `p113_c_a_forced_collision_does_not_fool_the_verifier`、`p113_c_the_recorded_integrity_value_is_sha256`、`p113_c_no_sha256_*`、`p113_c_dedup_key_falls_back_to_sha1_without_sha256` |
| P113-08 | Load identity | **PASS** | `p113_b_entries_only_never_load_a_foreign_assembly`（Red 中 `MF0210` 命中）、`p113_c_a_tampered_snapshot_never_initializes` |
| P113-09 | Identical sharing | **PASS** | `p113_a_identical_bytes_share_one_snapshot`、`p113_a_identical_bytes_are_counted_once`、`p112_a_shared_artifact_is_loaded_once` |
| P113-10 | Resource bounds | **PASS** | `p113_a_max_files_*`、`p113_a_max_total_bytes_*`、`p113_a_sharing_still_works_at_the_file_bound`、`p113_a_paths_do_not_overwrite` |
| P113-11 | Fail-Closed | **PASS** | `p113_c_a_deleted_snapshot_fails_the_mod`、`p113_c_the_declared_file_is_not_loaded_instead`、`p113_a_unreadable_candidate_fails_closed`、`p113_c_no_sha256_fails_closed` |
| P113-12 | Phase 1.1.2 regression | **PASS** | 427 项全绿；`427 − 67(new) = 360`，与上一阶段记录的 360 项一致；驱动 35/35、Mod Pipeline 27/27、Offline 13/13 亦与历史一致 |
| P113-13 | Determinism | **PASS** | `p113_b_order_independent_classification`、`p113_b_order_independent_loading`、`p112_e_classification_is_independent_of_discovery_order` |
| P113-14 | CI integration | **PASS** | 新测试注册在 `Program.cs` → 由 `test_loader.py` 执行 → 由 `.github/workflows/ci.yml` 的 `loader host tests` 步骤执行（无需 `--refs`） |
| P113-15 | Build | **PASS**（本地） | Runtime 桌面 Release、Runtime AndroidRefsDir Release、ExampleMod.HelloWorld AndroidRefsDir Release 均 0 warning / 0 error |
| P113-16 | Runtime evidence | **PARTIAL / BLOCKED** | SHA-256 与 `LoadFrom` 的**编译期**可用性有证据（B 级）；**运行时**语义无设备 → BLOCKED |
| P113-17 | Documentation | **PASS** | 本报告 + 证据索引 + loader 设计/运行时约束/manifest 规范/README/CI 注释已同步 |

---

## 9. 测试与构建：命令、退出码、环境、日志

工作目录一律为仓库根 `D:\Download\DSH\StardewValley`；`dotnet` 指
`D:\Download\Codex\MuMu\tools\dotnet10\dotnet.exe`（10.0.401），环境变量
`DOTNET_ROLL_FORWARD=Major`、`DOTNET_NOLOGO=1`、`DOTNET_CLI_TELEMETRY_OPTOUT=1`。

| ID | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| G-01 | `python Tools\tests\test_loader.py --dotnet <sdk> --refs .tmp\p113\refs` | 0 | 宿主 `427 checks, 427 passed, 0 failed`；驱动 `35 checks, 35 passed, 0 failed, 0 skipped` | `.tmp/p113/logs/final/test-loader.txt` |
| G-02 | 上面的驱动内含 Mod Pipeline？否——单独跑 `python Tools\tests\test_mod_pipeline.py` | 0 | `27 checks, 27 passed, 0 failed` | `.tmp/p113/logs/final/test-mod-pipeline.txt` |
| G-03 | `python Tools\tests\test_ci_offline.py` | 0 | `13 checks, 13 passed, 0 failed, 0 skipped` | `.tmp/p113/logs/final/ci-offline.txt` |
| G-04 | `python Tools\privacy-scan.py --include-untracked` | 0 | CRITICAL/HIGH 0 / MEDIUM 0，`RESULT: OK` | `.tmp/p113/logs/final/privacy-scan.txt` |
| G-05 | `powershell -File Tools\tests\test_device_precheck.ps1` | 0 | `10 checks, 10 passed` | `.tmp/p113/logs/final/device-precheck-selftest.txt` |
| G-06 | `powershell -File Tools\tests\run-all.ps1 -Dotnet <sdk> -Refs .tmp\p113\refs -LogDir .tmp\p113\logs\final` | 0 | `6 suites, 6 passed, 0 failed, 0 skipped` | `.tmp/p113/logs/final-run-all-console.txt` |
| G-07 | `git diff --check` | 0 | 无输出（无空白错误） | `.tmp/p113/logs/final/git-diff-check.txt` |
| G-08 | 65 程序集解析差分（驱动内 `--diff-refs`） | 0 | `compared: 65 (strong-name signed: 58)`、`RESULT: OK` | `.tmp/p113/logs/final/test-loader.txt` |
| G-09 | `dotnet build Mod\ModFramework.Runtime -c Release --artifacts-path .tmp\p113\artifacts` | 0 | 0 warning / 0 error | `.tmp/p113/logs/final/test-loader.txt`（`build_runtime`） |
| G-10 | `dotnet build Mod\ModFramework.Runtime -c Release -p:AndroidRefsDir=<.tmp\p113\refs>` | 0 | 0 warning / 0 error | `.tmp/p113/logs/build-runtime-androidrefs.txt` |
| G-11 | `dotnet build Mod\ExampleMod.HelloWorld -c Release -p:AndroidRefsDir=<refs>`（由驱动执行） | 0 | `hello_world_build` PASS + `hello_world_host_run` PASS | `.tmp/p113/logs/final/test-loader.txt` |
| R-01 | Red：`python Tools\tests\test_loader.py …`（决策级回退后） | 1 | `421 checks, 385 passed, 36 failed` | `.tmp/p113/logs/red-collision.txt` |

R 与 G 使用**完全相同的测试文件**（`Phase113Tests.cs`），差异只在 `AssemblyStaging.cs` 的决策。

---

## 10. 三类环境的结论必须分开看

| 环境 | 状态 | 说明 |
| --- | --- | --- |
| 桌面 .NET（Windows，.NET 10 运行时跑 net8.0 产物） | **PASS** | 上述 G-01..G-11 全部是本机真实运行结果 |
| AndroidRefsDir **编译**（目标裁剪 BCL 的引用集） | **PASS（B 级）** | 证明新代码用到的 BCL API 在目标引用集中存在；**不证明**运行时行为 |
| Android Mono / ARM64 **运行时** | **BLOCKED** | 无 ARM64 物理设备；本阶段禁止 adb 安装与真实游戏目录修改。`Assembly.LoadFrom` 去重语义、`SHA256.HashData` 实际实现、`AssemblyName.GetPublicKeyToken()` 返回形状均未在设备上验证 |
| 远程 CI | **NOT RUN** | 未 push；用户自行 push 后由 workflow 产出 |

---

## 11. 修改文件清单与关键 SHA-256

| 文件 | 变更 | SHA-256（前 16 位） | 字节 |
| --- | --- | --- | --- |
| `Mod/ModFramework.Runtime/AssemblyStaging.cs` | 重写去重索引 + 快照完整性校验器（核心修复） | `d94d9d3c8552beda` | 23331 |
| `Mod/ModFramework.Runtime/ContentIdentity.cs` | 新增 `CompareFileToBuffer`（候选比较，可 tri-state） | `be98278bcdb5c705` | 10156 |
| `Mod/ModFramework.Runtime/Loader.cs` | `VerifySnapshot` 接入、加载/缓存/宿主复用身份核对（`MF0210`）、错误码透传 | `97abc10e2e4b5503` | 72214 |
| `Mod/ModFramework.Runtime/AssemblyPreflight.cs` | 同组共享前比较两份 staged 文件；跨 SimpleName 边界写入文档 | `881b604b1f4439d4` | 28787 |
| `Mod/ModFramework.Runtime/AssemblyIdentityComparison.cs` | 新增 `SameLoadedIdentity`（按 Token 归一化比较） | `cd839ddcd692ccd1` | 8967 |
| `Mod/ModFramework.Runtime/AssemblyIdentity.cs` | `HostAssemblyIdentity.From`；`SnapshotFingerprint` 语义说明 | `ecaec5410e9b7fcd` | 14779 |
| `Mod/ModFramework.Runtime/ErrorCodes.cs` | 新增 `MF0210`（AssemblyLoadIdentityMismatch） | `c6290f611b411e6d` | 11486 |
| `Tools/tests/ModFramework.Tests/Phase113Tests.cs` | **新增** 67 项检查（A/B/C 三组） | `fe94d35953aa065c` | 52920 |
| `Tools/tests/ModFramework.Tests/Program.cs` | 注册 `Phase113Tests.Run` | `d163c39223c3eb50` | 4162 |
| `docs/architecture/phase1-loader-design.md` | §3.2 更新 + 新增 §3.3（去重/完整性/身份三分）、故障矩阵、变更记录 | `fd56022286ae51f8` | 20941 |
| `docs/architecture/phase1-runtime-constraints.md` | §4.4 快照表更新 + 1.1.3 三条约束 + SHA-256 证据等级 | `df0b5a1a45d9dc85` | 16359 |
| `docs/modding/manifest-spec.md` | §3.3.1 去重与失败码；错误码表补 `MF0208/0209/0210` | `f96834aabca3974c` | 16955 |
| `docs/modding/creating-a-mod.md` | 失败对照补 `MF0209/0210` | `74d06ea5c1251dfb` | 8078 |
| `README.md` | 状态表补 1.1.2 / 1.1.3；硬语义表补去重、完整性、身份核对 | `033d6e3d280dee4e` | 14516 |
| `.github/workflows/ci.yml` | 注释说明 1.1.3 覆盖（仅注释，步骤未变） | `c298873f5759b32b` | 4619 |
| `docs/audit/phase1.1.3-report.md` | **新增** 本报告 | — | — |
| `docs/audit/phase1.1.3-evidence-index.md` | **新增** 证据索引 | — | — |

未修改：Phase 1.1.2 的历史报告/证据索引、`Mod/ModFramework.Runtime` 的 csproj、任何游戏侧 Gradle/apktool 代码。

---

## 12. 未解决、BLOCKED 与需要用户批准的操作

| ID | 项 | 状态 | 原因 / 下一步 |
| --- | --- | --- | --- |
| X-01 | `校验 → LoadFrom` 的原子性 | **BLOCKED** | 需要 `Assembly.Load(byte[])`（目标引用集里不存在，P112 已用 `CS1503` 证明） |
| X-02 | AArch64 Mono 上 `SHA256.HashData` 的运行时可用性与异常形状 | **BLOCKED** | 无 ARM64 设备；需真机跑一次 staging + 篡改快照的最小用例 |
| X-03 | AArch64 Mono 上 `LoadFrom` 对"同内容不同路径"是否返回同一实例 | **BLOCKED** | 桌面行为不能外推到 Mono；本阶段只用"同一 canonical 路径只加载一次"来绕开该假设 |
| X-04 | 远程 GitHub Actions | **NOT RUN** | 本阶段禁止 push；请用户自行 push 后查看 `ci` workflow |
| X-05 | 单文件 64 MiB 上限的碰撞场景 | **SKIP（未单独断言）** | 该检查先于写入且与碰撞无关（claim 读取阶段已拒绝 >64 MiB 文件）；未构造 64 MiB fixture 以免测试变慢 |
| X-06 | `MaxFiles`/`MaxTotalBytes` 的真实上限（4096 / 256 MiB） | **SKIP（用 seam 缩小后验证）** | 生产常量未改；测试通过 `LimitsForTests` 验证"上限生效"这一逻辑，而非真实临界值 |
| X-07 | `ShareFromHost` 多实例歧义的真机枚举顺序 | **BLOCKED** | 仅桌面注入快照覆盖；真机 `GetAssemblies()` 顺序未知 |
| X-08 | 快照"拒绝读取"（权限拒绝）的**确定性**用例 | **PARTIAL（静态 C + 间接断言）** | 在同一进程内无法跨平台确定性制造"文件存在但不可读"（Windows 共享锁与 Linux 语义不一致）；该分支实现上由 `try/catch → false` 覆盖，并由"删除快照""截断/增长""无 SHA-256"三条相邻用例间接证明不会返回 true |

需要用户另行批准的操作：`git push`、创建 Release、adb 装机验证、真机 ARM64 运行测试。

---

## 13. 最终原则（本阶段的实际落点）

- fingerprint **只用于找候选**；完整逐字节比较才决定是否共享。
- 快照完整性验证与去重索引**是两套独立的函数、两套独立的测试 seam**。
- 每个 Claim 的预检身份必须对应最终加载的 artifact；不确定一律 Fail-Closed。
- 只做本地交付：未 Commit、未 Push、未装机；用户工作区原有改动未被触碰。
