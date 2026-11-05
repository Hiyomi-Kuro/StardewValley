# Phase 1.1.2 报告：Assembly Load Integrity & Edge-Case Verification

> 本阶段只做五件事：预检字节与加载字节的绑定、ContentIdentity 异常分支的测试闭环、
> Host 共享正向路径的端到端证据、ECMA-335 Assembly Flags/PublicKey 组合校验、
> 预检不动点的收敛证明与非收敛 Fail-Closed。
> 没有启动 Phase 2、SMAPI 兼容层、Harmony/detour、游戏事件 Hook、Content/Data API、热重载或存档 API。

---

## 1. 总状态

**COMPLETE（本地交付）** —— 五个主题全部有实现或测试结论；本地可执行的测试与构建全部通过；
两项**必须由用户或设备条件完成**的部分明确标记：远程 CI = **NOT RUN（等待用户 Push）**，
Android Mono / ARM64 真机语义 = **BLOCKED（无设备，且装配件阶段被禁止）**。

| 主题 | 结论 | 状态 |
| --- | --- | --- |
| A. 预检字节 ↔ 加载字节绑定（TOCTOU） | 声明文件不再参与加载；改为"预检 buffer → 私有快照 → 逐字节校验 → 从快照 LoadFrom → 加载前二次校验" | 功能修复 + 测试 |
| B. ContentIdentity 异常/短读/长度变化 | 12 个分支全部有确定性测试；Unverifiable 在两个调用层实测导致拒绝共享 | 纯测试补齐（原实现 Baseline PASS） |
| C. Host Assembly 正向共享 | `ReferenceEquals(host, resolved) == true`、无第二次 `LoadFrom`、AppDomain 中只有一份 | 纯测试补齐（原实现 Baseline PASS） |
| D. Assembly Flags + PublicKey/Token | 按 ECMA-335 形状校验；4 类非法组合被拒绝，合法组合零误杀 | 功能修复 + 测试 |
| E. 固定点收敛 | 给出不变量与反例（2-循环），改为显式 Converged/Exhausted；未收敛时一个 mod 都不加载 | 功能修复 + 测试 |

**通过条件自检**：功能缺陷有真实 Red → Green 证据（§7）；纯覆盖缺口如实记录为 Baseline PASS（§5、§6）；
没有未解释的失败；所有未执行项标注 BLOCKED / NOT RUN（§10、§12）。

**关于 DoD 里"若 TOCTOU 无法在目标 Mono 上可靠关闭则不得标记 COMPLETE"**：
本阶段关闭的是**"mod 声明文件在预检后被替换/改写"**这一类威胁——已用确定性测试证明
（§3.1 的 4 个替换场景 + database 读取期变化），测试里替换文件后**被执行的仍是预检批准的那份字节**。
**没有**宣称"进程内同 uid 代码无法篡改快照"：那需要 `Assembly.Load(byte[])`，
目标运行时没有（§4.1 的 `CS1503` 编译证据），因此该子项在真机上**无法验证 → BLOCKED**，
并在 §4.4 与 §12 里逐条写明。上文 COMPLETE 指"本阶段可交付的实现与桌面/编译期证据完整",
不含"真机运行期已验收"。

---

## 2. 工作区与基线

| 项 | 值 |
| --- | --- |
| 工作区 | `D:\Download\DSH\StardewValley`（`git rev-parse --show-toplevel` 实测） |
| remote | `origin https://github.com/Hiyomi-Kuro/StardewValley.git`（只读，未 push） |
| branch | `main` |
| 开始 HEAD | `786679bbdf73e2b67689eb166acba967339fcb42` |
| 结束 HEAD | `786679bbdf73e2b67689eb166acba967339fcb42`（**未 commit**） |
| origin/main | `786679bbdf73e2b67689eb166acba967339fcb42`（未前进） |
| 开始时工作区 | `git status --porcelain` 为空：无 dirty、无 untracked |
| 用户改动保护 | 未执行 `git reset --hard` / `git clean` / 强制 checkout / 批量删除；未修改 `.git` |
| 私有 SDK | `.tmp\phase0.5\dotnet\dotnet.exe` → `8.0.404`（未安装新 SDK） |
| 参考程序集 | `.tmp\phase0.5\refs` → 65 个 dll（复用，未改动） |
| 本阶段工作目录 | `.tmp\phase1.1.2\`（日志、Red 补丁、探针；均被 `.gitignore` 排除） |

结束时工作区只有本阶段预期的 8 个修改 + 3 个新增（源码/测试）+ 文档，无临时文件入库。

---

## 3. 五个主题

### 3.1 A. 预检字节与最终加载字节的绑定（TOCTOU）

**源码现状（复核结论）**：风险**仍然存在**，与阶段文档描述一致。
`AssemblyClaimReader.TryRead` 只把 buffer 用于"解析身份 + 算摘要"，随后丢弃；
`Loader.TryResolveClaim` 用 `Assembly.LoadFrom(claim.CanonicalPath)` 重新按**路径**打开；
`ContentIdentity.CompareFiles` 比较的也是"比较那一刻"的文件内容。三者之间没有任何不可变绑定。

**确定性复现（无需真实竞态）**：`Loader.BeforeLoadForTests` 是唯一新增的测试 seam，
在"预检已完成、`LoadFrom` 尚未发出"的位置回调。用它替换文件后，旧实现的真实行为（Red 证据）：

| 场景 | 旧实现实测 |
| --- | --- |
| 同长度、合法托管、**不同身份**的替换文件（`P112Entry`→`Q112Entry`，字节数不变） | 加载了替换件 → `MF0203`（"file A112Entry.dll contains assembly Q112Entry"） |
| 同身份、不同内容的替换件 | 替换件**执行了入口** → 标记文件内容为 `P112.A.Content\|B` |
| 私有依赖被替换成 8 字节垃圾 | `MF0202 BadImageFormatException`（即：真的去读了被替换的文件） |
| 快照文件被替换 | 旧实现没有快照概念，替换的是声明文件 → 直接加载替换件 |

**修复策略（最小、可审计）**：

1. `AssemblyClaimReader.TryRead` 拿到 buffer 后立即交给 `AssemblyStagingArea.TryStage`：
   写入 `<Mods>/.staging/<run-id>/<mod id>/<声明文件名>`，**回读并逐字节比较**
   （`ContentIdentity.VerifyExactBytes`）。通过后才设置 `StagedPath` / `SnapshotFingerprint`。
2. `Loader.TryResolveClaim` 只用 `claim.StagedPath`；**没有快照就不加载**（fail-closed）。
3. 加载前再校验一次（长度 + 指纹：`sha256:`，运行时无 SHA-256 时用内置 `sha1:` 形式）。
   不符 → 该 mod 失败、不加载（`MF0209` 语义的消息）。
4. 相同内容按指纹去重（一份快照可服务多个 claim）；同一 canonical artifact 仍然只 `LoadFrom` 一次。
5. 报告新增 `assembly-snapshot=private`、`assembly-snapshot-policy=staged-then-verified`、
   `assembly-snapshot-files=/bytes=`、`assembly-load=… snapshot=<指纹>`；
   声明文件（`file=`）、快照（`snapshot=`）、最终加载对象（`canonical=`）三者可区分，且**不含绝对路径**。

**测试（Green）**：

| 检查 | 结果 |
| --- | --- |
| `p112_a_claim_has_a_snapshot` / `_snapshot_length_matches_the_claim` / `_snapshot_is_the_exact_preflighted_bytes` | PASS（快照 SHA-256 == claim.Digest） |
| `p112_a_snapshot_is_not_the_declared_path` | PASS |
| `p112_a_replaced_entry_with_same_length_identity_is_not_loaded` | PASS（仍 `Initialized`，不再 `MF0203`） |
| `p112_a_replaced_entry_content_is_not_executed` | PASS（标记仍为 `\|A`，替换件的 `\|B` 从未执行） |
| `p112_a_replaced_private_assembly_is_not_loaded` | PASS（垃圾替换件不影响加载） |
| `p112_a_modified_snapshot_is_refused` | PASS（`MF0202` + 消息 "the preflighted bytes could not be snapshotted…"；入口未执行） |
| `p112_a_shared_artifact_is_loaded_once` / `_is_reported_as_shared` | PASS（1 条 `assembly-load`，`preflight-shared=`） |
| `p112_a_preflight_short_read_is_rejected` | PASS（`MF0205`，未加载） |
| `p112_a_growth_during_read_loads_only_the_declared_length` | PASS（只加载被记录/被哈希的那一段） |

**剩余风险**：见 §4.4。**未能**证明"快照在'校验'与'加载'之间不可被替换"——
需要 `Assembly.Load(byte[])`，目标运行时没有。

### 3.2 B. ContentIdentity 的异常/短读/长度变化闭环

**源码现状**：`OpenForTests` seam 早已存在，但**全仓库测试目录零调用**（本阶段搜索确认），
即 12 个分支从未被执行过。实现本身正确 → 记录为 **Baseline PASS**，不伪造 Red。

**新增测试与实测结果**（全部 PASS）：

| # | 场景 | 期望 | 实测 |
| --- | --- | --- | --- |
| 1 | 左侧 Open 抛 IOException | Unverifiable | Unverifiable |
| 2 | 右侧 Open 抛 IOException | Unverifiable | Unverifiable |
| 3 | Read 在已读若干字节后抛 IOException | Unverifiable | Unverifiable |
| 4 | 每次只返回少量字节但最终读满（3/1 字节分片） | Same | Same |
| 5 | 声明长度内提前 EOF | Unverifiable | Unverifiable |
| 6 | Stream 初始 Length ≠ claim 记录长度 | Unverifiable | Unverifiable |
| 7 | 读完后仍有额外字节（文件增长） | Unverifiable | Unverifiable |
| 8 | 同长度不同字节 | Different | Different |
| 9 | 完全相同 | Same | Same |
| 10 | `TryGetLength` 的 Open/Length 异常 | false + 原因不含绝对路径 | false，`Z:\fabricated\placeholder\x.dll` 被替换为 `<path>` |
| 11 | Dispose 抛异常 | 不改写已确定的 Same/Different | Same / Different 均保持 |
| 12 | 调用层必须真的拒绝共享 | Mod 间 `MF0206`；宿主 `MF0207` | 两者实测拒绝，且入口未执行 |

所有 seam 都在 `try/finally` 中恢复；不依赖真实磁盘竞态。

### 3.3 C. Host Assembly 正向共享（端到端）

**源码现状**：实现正确，缺的是正向证据（Phase 1.1.1 只测拒绝）。

**新增测试与实测结果**：

| 检查 | 实测 |
| --- | --- |
| 用 `Assembly.LoadFrom(P112HostLib.dll)` 建立真实宿主实例 | OK |
| `AssemblyPreflight.Analyze` 对同身份同字节的 claim 给出 `ShareFromHost` | PASS |
| `ReferenceEquals(claim.HostAssembly, hostAssembly)` | **true** |
| `claim.CanonicalPath == hostAssembly.Location`（不是 mod 的副本路径） | PASS |
| 端到端：`Loader.Run` 后该 mod `Initialized` + 报告 `preflight-host-share=P112HostLib` | PASS |
| AppDomain 中 `P112HostLib` 的份数 | **1** |
| 报告中 `file=P112HostLib.dll` 的 `assembly-load=` 行 | **0**（证明没有额外 LoadFrom） |
| 负向回归：实例为空（身份+内容都匹配） | `MF0207` 拒绝 |

### 3.4 D. Assembly Flags 与 PublicKey/Token Blob

**ECMA-335 依据**：II.23.1.2 —— `Assembly.Flags` 的 `0x0001` 表示 `Assembly.PublicKey` 存的是
**完整公钥**，未置位则表示 **8 字节 public key token**；II.6.2.1.3 规定 token = SHA-1(公钥) 的后 8 字节**倒序**。
完整公钥 blob 是 `PublicKeyBlob`：`SigAlgID(4) + HashAlgID(4) + cbPublicKey(4) + key`，即最短 12 字节。

**修复前（Red 实测）**：实现按"blob 非空"分派，结果 4 类畸形全部**被接受**：

```
7 字节 token      → accepted: PublicKey=token:00000000000000
9 字节 token      → accepted: PublicKey=token:000000000000000000
flag 置位 + 空 blob → accepted: PublicKey=none
flag 置位 + 8 字节 → accepted: PublicKey=full(8 bytes)     ← 把 token 当完整公钥
flag 置位 + 10 字节 → accepted: PublicKey=full(10 bytes)   ← 连 12 字节头都不够
```

**修复**：`ManagedAssemblyReader.TryClassifyKeyBlob` 只接受三种形状
（无 flag+空 / 无 flag+8 字节 / flag+≥12 字节），其余拒绝并给出稳定消息。

**合法/非法矩阵（Green 实测）**：

| Flags | Blob | 结果 |
| --- | --- | --- |
| 0 | 空 | 接受，unsigned，`IsStrongNamed=false` |
| 0 | 8 字节 | 接受，`PublicKeyToken` = 该 8 字节，`Describe()` 含 `token:…` |
| 0 | 7 / 9 字节 | **拒绝**（"must be an 8-byte public key token"） |
| 0x0001 | 空 | **拒绝**（"the public key blob is empty"） |
| 0x0001 | 8 字节 | **拒绝**（"which is a public key token, not a full public key"） |
| 0x0001 | 10 字节 | **拒绝**（"too short to be a full public key"） |
| 0x0001 | 真实 RSA 公钥（本阶段用 `RSA.Create(2048)` 现生成并组装 `PublicKeyBlob`） | 接受；`PublicKey` 原始字节保留；派生 token 与 BCL `SHA1.HashData` 末 8 字节倒序**一致**，且与 `AssemblyName.SetPublicKey/GetPublicKeyToken` 的 oracle **一致** |
| 0x0001 | 200 个 0 字节 | 接受（**只校验表示形状**，不校验密钥材料；见下） |
| 0x0100 / 0x0200 / 0x4000\|0x8000 | 空 | 接受（Retargetable / ContentType / JIT 位不扩大拒绝范围） |

**刻意不做的事**：不校验保留位、不校验 `ContentType` 语义、不校验密钥材料本身。
把 200 个零字节说成"有效 RSA 公钥"是超出解析器能力的断言；同理，拒绝保留位会拒掉规范允许的装配件。
`IsStrongNamed` / `PublicKey` / `PublicKeyToken` / `Describe` / `AssemblyIdentityComparison`
的语义在同一个函数里被一次性设定，不会出现"标志位与 blob 解释不一致"的分支。

### 3.5 E. 固定点收敛与非收敛 Fail-Closed

**算法状态**（可审计）：`intrinsicFailures`（只增，永久）、`dependencySet`（每轮重算）、
`blockedByDependency`（原因）、`conflictSet`（每轮**覆盖**）、`pool = 全部 − intrinsic − dependency`、
`preflight`（本轮 `Analyze(pool)`）。

**不变量与反例**：

- `intrinsicFailures` 单调增长且有界 → 有限次。
- `conflictSet` **不单调**：第 1 轮把 `{A,B}` 记为冲突；第 2 轮 B 因"必需依赖 A 已在 conflictSet 里"
  变成依赖阻断，于是 `conflictSet` 里删掉 B、只剩 `{A}`；池里只剩 A，A 不再冲突 → `conflictSet` 变空；
  第 3 轮 B 又不再被阻断 → 池回到 `{A,B}` → 冲突重现。**周期 2 的环，不是收敛**。
- 因此"每轮至少有一个状态永久变化"这一前提**不成立**，`|plan.Order| * 2 + 4` 只是兜底数字，
  **不能**证明收敛。旧实现超限时 `break` 后直接使用当前状态做终态归类（见下）。

**修复**：

1. 显式状态：`preflight-convergence=converged|exhausted`、`preflight-rounds=`、`preflight-round-limit=`；
   超限时额外写 `preflight-exhausted-reason=`。
2. 循环检测：把 `(intrinsicFailures, dependencySet, conflictSet)` 组成状态签名；签名重复即证明
   "下一轮输入回到历史状态"，直接判定非收敛（比等满轮次更早、更准确）。
3. 非收敛 → **Fail-Closed**：所有非 intrinsic 的候选 mod 一律以 `MF0208` 拒绝，
   `Loader` 不进入任何 `LoadAndInitialize`；报告写 `preflight-not-converged=<id>` 与一条 warning。
4. 只有 `converged` 分支才使用 `dependencySet` / `conflictSet` 做终态归类。
5. `Assembly.LoadFrom` 仍在整个循环之后（`preflight-stage=complete` 行保证）。

**Red（旧逻辑）实测**：报 `preflight-convergence=converged`（根本没有这个状态），
注入 `roundLimit=1` 后 `P112.E.Owner state=Initialized` —— 即**从未收敛的预检里把一个 mod 初始化了**。

**Green 实测**：稳定图 1 轮收敛并加载 3 个 mod；冲突-依赖环 → `exhausted` + 两个 mod 都 `MF0208` + 无入口执行；
注入 `roundLimit=1` → `exhausted` + `MF0208` + 无入口执行；直接冲突 / 三 mod 冲突组 → `converged` + 全员 `MF0206`；
发现顺序变化（目录名正反序）→ 报告签名完全一致。

---

## 4. TOCTOU 最终方案：运行时语义、绑定机制、上限与依赖解析

### 4.1 运行时语义证据：为什么是"快照文件"而不是"内存字节"

| 证据 | 方法 | 结果 |
| --- | --- | --- |
| `Assembly.Load(byte[])` 在目标运行时**不存在** | 用 `.tmp/phase0.5/refs`（游戏自带 65 个裁剪程序集）编译一个 7 行探针 | `error CS1503: 参数 1: 无法从“byte[]”转换为“string”`（只剩 `Load(string)` 重载）；同一文件里的 `Assembly.LoadFrom(string)` 正常编译 |
| 新代码确实能在目标引用集上编译 | `dotnet build Mod/ModFramework.Runtime -c Release -p:AndroidRefsDir=<refs 绝对路径>` | 0 warning / 0 error |
| 桌面语义下"同路径只加载一次" | `s_loadedByPath` + 快照路径 | 1 条 `assembly-load`（共享测试） |

### 4.2 字节绑定机制（三层）

1. **单一 buffer**：身份、长度、摘要、快照内容都来自同一次 `Read`（`TryReadAll` 只读一次；
   长度变化 / 提前 EOF → `MF0205`，不产生 claim）。
2. **写后回读逐字节比较**：`VerifyExactBytes` 用新打开的句柄比对整个文件（不是摘要）。
3. **加载前二次校验**：长度 + 指纹（`sha256:` 或内置 `sha1:`），失败即拒绝加载。

### 4.3 内存 / 磁盘上限与清理

| 项 | 值 | 行为 |
| --- | --- | --- |
| 单文件 | 64 MiB（沿用 `MaxAssemblyBytes`） | 超出 → claim 拒绝 |
| 本轮总量 | 256 MiB | 超出 → 该 claim 拒绝（fail-closed） |
| 本轮文件数 | 4096 | 同上 |
| 内存 | 仍只保持**一个 claim** 的 buffer | 不把 DLL 长期留在内存 |
| 清理 | 每次启动创建新 run 目录，删除旧 run 目录（尽力而为） | 已被映射的文件删不掉不算错误 |

### 4.4 依赖解析影响（明确的行为变化）

- 私有依赖仍在**入口程序集之前**按 manifest 声明逐个 `LoadFrom`（顺序不变），
  因此"按标识已加载"仍优先命中，不依赖目录探测。
- **`Assembly.Location` 现在指向快照**，不是 mod 包目录：mod 取随包数据必须用
  `IModContext.ModDirectory`（Phase 1 起就是契约，本阶段写进 `manifest-spec.md`）。
- **未声明**的同目录 dll 本来就不会被加载（Phase 1 的既有语义）；本阶段没有放宽。
- 快照目录是 `<Mods>/.staging/`，以 `.` 开头，`ModDiscovery` 会忽略它（报告里会出现一条
  "ignored (hidden or reserved name)" 的 warning，属预期）。

### 4.5 仍是问题的那一小段

`VerifyStagedFile` 通过之后到 `Assembly.LoadFrom` 返回之前，快照文件理论上仍可被同 uid 代码替换。
本阶段**没有**把这个窗口说成"已消除"：它是"被检测 + 最小化"，不是"原子化"。
要做到原子化只有两条路：`Assembly.Load(byte[])`（目标运行时没有）或拿住文件句柄再让运行时加载
（`LoadFrom` 不接受句柄）。**结论：该子项 BLOCKED，需要在 AArch64 Mono 真机上用等价手段验证后才可宣称关闭。**

---

## 5. ContentIdentity 每个用例的真实结果

见 §3.2 的表格（12/12 PASS，逐条给出期望与实测）。补充两条实现层的观察：

- `ReadFully` 合法地循环处理 partial read：分片 3 字节 / 1 字节的两次比较结果都是 `Same`，
  说明"短读"没有被误判成 EOF。
- `Close` 吞掉 Dispose 异常，`finally` 里不会把已定的 `Same`/`Different` 改写成 `Unverifiable`
  （两条都实测）。

---

## 6. Host Share 正向测试的 ReferenceEquals 与实际加载记录

```
p112_c_host_claim_share_from_host                 ShareFromHost
p112_c_host_claim_same_instance                   ReferenceEquals=True
p112_c_host_claim_points_at_the_host_image        <host 镜像路径>
p112_c_host_share_initializes                     id=P112.C.Positive state=Initialized
p112_c_host_share_reported                        preflight-host-share=P112HostLib owner=P112.C.Positive role=private content=same
p112_c_host_share_loads_no_second_copy            loaded copies=1
p112_c_host_share_issues_no_load_from             assembly-load= 行中 file=P112HostLib.dll 出现 0 次
```

同时保留 Phase 1.1.1 的全部负向用例（身份不匹配 / Culture 不匹配 / Token 不匹配 / 内容不同 /
Location 不可读 / 候选多于一个 / 不可验证的打开失败），本阶段新增"Assembly 实例为空 → `MF0207`"。

---

## 7. Red → Green 证据链

Red 的产生方式：`Loader.BeforeLoadForTests` / `RoundLimitOverrideForTests` /
`AssemblyClaimReader.OpenForTests` / `ContentIdentity.OpenForTests` 都是**internal、仅供测试**的 seam，
生产代码里没有任何 mod 可配置的"跳过校验"开关。为了拿到"旧实现"的真实行为，
本阶段用 `.tmp/phase1.1.2/red/apply_red.py` 把**决策**临时改回旧逻辑（不是改测试），
重建后跑同一套测试，再按字节还原（脚本自带 backup/restore）。

| 轮次 | 补丁内容 | 结果 | 日志 |
| --- | --- | --- | --- |
| Red-A | 加载按声明路径 + 关闭快照与二次校验 + 不写快照 | **11 项失败**：加载了 `Q112Entry`（`MF0203`）、替换件入口被执行（`P112.A.Content\|B`）、私有依赖读到垃圾（`BadImageFormatException`）、claim 无快照 | `.tmp/phase1.1.2/logs/red-a.txt` |
| Red-D | 恢复"blob 非空即公钥"的分派 | **5 项失败**：7/9 字节 token、flag+空、flag+8、flag+10 全部被接受 | `.tmp/phase1.1.2/logs/red-d.txt` |
| Red-E | 去掉收敛状态与环检测，超限即 `break` 并归类 | **5 项失败**：报 `converged`、无 `exhausted` 原因、`roundLimit=1` 时 `P112.E.Owner` 被初始化 | `.tmp/phase1.1.2/logs/red-e.txt` |
| Green | 还原后同一套测试 | **360/360** 通过；驱动 **35/35** | `.tmp/phase1.1.2/logs/final/test-loader.txt` |

B、C 两组没有 Red：它们验证的是**已存在**的正确行为，如实记录 Baseline PASS（§3.2、§3.3）。

---

## 8. 固定点循环：不变量、收敛证明与 Exhausted 证据

- **可证部分**：`intrinsicFailures` 单调且上界 `|pool|`；每轮依赖闭包是 `(intrinsic, conflict, dependency)`
  的纯函数；因此状态签名重复 ⇒ 永久循环。签名重复检测是**充分**的非收敛判据。
- **不可证部分**：`conflictSet` 在"冲突 ↔ 依赖阻断"之间可以往返（§3.5 的 A/B 例子），
  所以不存在"每轮至少一个永久变化"的单调测度，`2n+4` 只是防御性上限。
- **因此**：保留上限，同时把"没收敛"变成一等状态；未收敛 ⇒ `MF0208` + 不加载。
- **报告一致性**：`preflight-rounds` / `preflight-round-limit` / `preflight-convergence` /
  `preflight-exhausted-reason` 与最终失败原因一一对应（测试逐条断言）。
- **加载时序**：`preflight-stage=complete` 行一定在第一条 `assembly-load=` 之前（报告行号断言 +
  端到端 `p112_e_no_load_before_the_preflight_completes`）。

---

## 9. 完整测试矩阵（本次本地运行）

运行环境：Windows 10 桌面、私有 SDK `8.0.404`、`.tmp/phase0.5/refs`（65 个游戏程序集）、
无设备、无网络依赖。日志根目录 `.tmp/phase1.1.2/logs/`。

| # | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| 1 | `python Tools/tests/test_loader.py --dotnet .tmp/phase0.5/dotnet/dotnet.exe --artifacts .tmp/p112/artifacts --workdir .tmp/p112/work --refs .tmp/phase0.5/refs` | 0 | 宿主 **360/360**；驱动 **35/35**（0 skipped） | `final/test-loader.txt` |
| 2 | 同上驱动内含 `test_mod_pipeline` | 0 | **27/27** | `final/test-mod-pipeline.txt` |
| 3 | `python Tools/tests/test_ci_offline.py` | 0 | **13/13** | `final/ci-offline.txt` |
| 4 | `python Tools/privacy-scan.py --include-untracked` | 0 | CRITICAL/HIGH 0、MEDIUM 0、`RESULT: OK`（INFO 仅历史文档里的旧仓库绝对路径） | `final/privacy-scan.txt` |
| 5 | `powershell -File Tools/tests/test_device_precheck.ps1`（自测，不接触设备） | 0 | **10/10** | `final/device-precheck-selftest.txt` |
| 6 | `powershell -File Tools/tests/run-all.ps1 -Refs .tmp/phase0.5/refs -LogDir .tmp/phase1.1.2/logs/final` | 0 | **6 suites, 6 passed, 0 failed, 0 skipped** | `final-run-all-console.txt` |
| 7 | Runtime 桌面 Release build | 0 | 0 warning / 0 error | `final/test-loader.txt`（构建段） |
| 8 | Runtime `AndroidRefsDir` Release build | 0 | 0 warning / 0 error | `build-runtime-androidrefs.txt` |
| 9 | `ExampleMod.HelloWorld` + `AndroidRefsDir` Release build | 0 | `hello_world_build` PASS；`hello_world_host_run` PASS | `final/test-loader.txt` |
| 10 | `git diff --check` | 0 | 无空白错误 | `final/git-diff-check.txt` |
| 11 | 65 程序集解析差分（Phase 1.1.1 能力保留） | 0 | `compared: 65 (strong-name signed: 58)`、`RESULT: OK` | `final/test-loader.txt` |
| 12 | `Assembly.Load(byte[])` 可用性探针 | 1（预期） | `CS1503`：只有 `Load(string)` | `probe-load-api.txt` |

`p112_*` 断言在源码里共有 90 个声明位置（A 29 / B 19 / C 11 / D 16 / E 15），
其中部分位于互斥的 if/else 分支里，本次运行实际执行并 PASS 的是 **77** 条。

### 9.1 未执行 / 不可执行

| 项 | 状态 | 原因 |
| --- | --- | --- |
| 远程 GitHub Actions | **NOT RUN** | 任务明确禁止 push；本地交付，等用户自行 Push |
| adb 装机 / 真机 mod 运行 | **NOT RUN** | 任务禁止 install/uninstall 与真实游戏目录改动 |
| AArch64 Mono 运行期语义（快照+LoadFrom、`Location` 行为） | **BLOCKED** | 无 ARM64 设备；模拟器（x86_64 + libhoudini）结论不可外推 |
| Phase 1.1.1 的 65 引用差分之外的新 PE/CLI fuzz | 已随套件执行 | `p111_d_*` fuzz 与差分都在 360 条内，未修改解析器边界逻辑 |

---

## 10. 桌面构建 / AndroidRefsDir 编译 / 真机状态（分开报告）

| 类别 | 内容 | 结论 |
| --- | --- | --- |
| 桌面 .NET 测试 | 360 条宿主测试 + 35 条驱动检查 + 27 条 Mod Pipeline + 13 条离线 + 10 条设备预检自测 | **PASS**（A 级证据） |
| 桌面 Release 构建 | `ModFramework.Runtime`（net8.0） | **PASS** |
| AndroidRefsDir 编译 | `ModFramework.Runtime`、`ExampleMod.HelloWorld` 用游戏自带 65 个裁剪程序集编译 | **PASS（编译期，B 级）** |
| Android Mono / ARM64 真机 | 未运行 | **BLOCKED / NOT RUN**——编译通过**不等于**运行期语义通过 |

---

## 11. 文档、错误码与关键文件

### 11.1 错误码变更（只新增，未重编号）

| 码 | 含义 | 何时出现 |
| --- | --- | --- |
| `MF0208` | 程序集预检未收敛，因此没有任何 mod 被加载 | 不动点达到轮次上限或状态签名重复 |
| `MF0209` | 预检字节无法快照（或快照与预检内容不再一致），因此不能加载"被批准的字节" | 写快照失败 / 回读比较失败 / 预算耗尽 / 加载前二次校验失败 / 快照区建不出来 |

`MF0201/0205/0206/0207` 的语义未变；`MF0209` 与 `MF0205` 都算"intrinsic"（与别人无关、不可通过移除别人修复）。

### 11.2 文档变更

| 文件 | 变更 |
| --- | --- |
| `docs/architecture/phase1-loader-design.md` | 新增 §3.2「字节快照与加载绑定」；§11 变更记录追加一行 |
| `docs/architecture/phase1-runtime-constraints.md` | 新增 §4.4「快照加载：Phase 1.1.2 的结论」（含 `CS1503` 证据、`Assembly.Location` 变化、剩余窗口、上限） |
| `docs/modding/manifest-spec.md` | 新增 §3.3.1「加载的是快照，不是你的文件」（`ModDirectory`、声明要求、上限、`MF0209`） |
| `docs/audit/phase1.1.2-report.md` | 本文件（新增） |
| `docs/audit/phase1.1.2-evidence-index.md` | 证据索引（新增） |
| Phase 1.1.1 的历史交付物 | **未修改**（`phase1.1.1-report.md`、`phase1.1.1-evidence-index.md` 保持原样） |

CI 无需改动：新测试挂在 `ModFramework.Tests` 的 `Program.Main` 里，`ci.yml` 第 82 行已经调用
`test_loader.py`；新 fixture 变体由 `test_loader.py` 的 `VARIANTS` 自动构建（本地已实测全部构建成功）。

### 11.3 可恢复 checkpoint（SHA-256 前 16 位）

| 文件 | SHA-256（16） | 字节 |
| --- | --- | --- |
| `Mod/ModFramework.Runtime/AssemblyStaging.cs`（新增） | `d974da5c3bbc6fb2` | 11163 |
| `Mod/ModFramework.Runtime/AssemblyIdentity.cs` | `725a54317ef49f26` | 14214 |
| `Mod/ModFramework.Runtime/AssemblyPreflight.cs` | `847f19430c18ca83` | 26808 |
| `Mod/ModFramework.Runtime/AssemblyMetadata.cs` | `9b9a9d7508e15ca0` | 41406 |
| `Mod/ModFramework.Runtime/ContentIdentity.cs` | `df3f3330a484fa0d` | 9069 |
| `Mod/ModFramework.Runtime/ErrorCodes.cs` | `c432f74565d72e37` | 10856 |
| `Mod/ModFramework.Runtime/Loader.cs` | `5fe9ccdba43c4a48` | 68749 |
| `Tools/tests/ModFramework.Tests/Phase112Tests.cs`（新增） | `a37d1dd7c17307aa` | 65801 |
| `Tools/tests/ModFramework.Tests/TestSupport.cs`（新增） | `ae7a44a3f3e3316f` | 6613 |
| `Tools/tests/ModFramework.Tests/Program.cs` | `614fe978c7507781` | 3781 |
| `Tools/tests/test_loader.py` | `8c8ead2e803a90dd` | 16145 |
| `docs/architecture/phase1-loader-design.md` | `683d1fb33e5aa1be` | — |
| `docs/architecture/phase1-runtime-constraints.md` | `d80db1c0884d5519` | — |
| `docs/modding/manifest-spec.md` | `a28b6351a148f280` | — |

（本报告与证据索引本身是自引用文件，其 SHA 以交付时 `Get-FileHash` 为准。）

---

## 12. 尚未解决、BLOCKED 与需要用户批准

| # | 项 | 状态 | 说明 / 需要的条件 |
| --- | --- | --- | --- |
| 1 | 快照文件在"校验通过 → `LoadFrom`"之间被同 uid 代码替换 | **BLOCKED** | 已检测+最小化，但非原子。彻底关闭需 `Assembly.Load(byte[])`（目标运行时无）或运行时提供句柄加载 API。需在 AArch64 Mono 真机上验证是否有其它原子手段 |
| 2 | 目标 Mono 上快照+`LoadFrom` 的完整运行期语义（`Location`、同标识去重、`AssemblyResolve` 交互） | **BLOCKED** | 无 ARM64 设备；`AndroidRefsDir` 编译成功不能替代运行期验证 |
| 3 | 远程 CI 结果 | **NOT RUN** | 用户自行 Push 后由 GitHub Actions 产出 |
| 4 | 装机 / 真实游戏 Mod 运行 | **NOT RUN** | 任务禁止；需用户另行授权 |
| 5 | 快照磁盘占用（每轮最多 256 MiB，旧轮次尽力删除） | 已知限制 | 若目标设备存储紧张，可下调 `AssemblyStagingArea.MaxTotalBytes`（需改代码，不是配置项） |
| 6 | 非收敛时"全员 `MF0208`"的可用性影响 | 已知取舍 | 一个冲突+依赖环会让该轮所有候选被拒。这是 fail-closed 的代价，报告里有明确原因行；如需"尽量加载无关 mod"，需要单独设计的求解器（不在本阶段范围） |
| 7 | 需要用户批准的操作 | — | `git commit` / `git push` / 远程 CI / 装机验证，全部等用户明确指示 |

---

## 附：最终原则执行情况

- 先复核源码，没有重复实现 Phase 1.1.1 已完成的内容（本阶段只补它没有闭环的部分）。
- 功能缺陷都先有能揭露它的测试（Red-A/D/E），测试缺口如实补齐（B/C 的 Baseline PASS）。
- 预检批准的字节就是最终加载的字节（声明文件层面，已证）；不确定时 Fail-Closed。
- 用户工作区未被 reset/clean/覆盖；没有 commit、push、装机。
