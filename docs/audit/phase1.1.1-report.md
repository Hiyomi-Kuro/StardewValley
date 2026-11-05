# Phase 1.1.1 报告：Assembly Preflight Correctness & Security Hardening

- 阶段：**Phase 1.1.1**（Phase 1.1 的定点修复，不是重写 Loader，不是重新实施 Phase 1.1，不是 Phase 2）
- 代码：`Mod/ModFramework.Runtime/`
- 基线：本地 HEAD `675b05807454ad7be9125672e71e83423a7e64dc`（= 编写时的 `origin/main`）
- 交付方式：**DSH 只做本地工作并交付，不执行 `git commit` / `git push`**
- 关联文档：[`phase1.1-report.md`](phase1.1-report.md)（上一阶段）、[`phase1.1.1-evidence-index.md`](phase1.1.1-evidence-index.md)（本阶段证据索引）、[`../architecture/phase1-loader-design.md`](../architecture/phase1-loader-design.md)、[`../architecture/phase1-runtime-constraints.md`](../architecture/phase1-runtime-constraints.md)、[`../modding/manifest-spec.md`](../modding/manifest-spec.md)

---

## 1. 总状态

| 维度 | 状态 |
| --- | --- |
| 离线实现（A–F 六项修复） | ✅ **COMPLETE** |
| 离线测试（Red → Green） | ✅ **COMPLETE**：Red 32 项失败 → Green 0 项失败 |
| 目标引用编译（`AndroidRefsDir`） | ✅ Runtime + ExampleMod.HelloWorld 均 0 warning / 0 error |
| 65 程序集解析差分 | ✅ 65/65 一致（其中 58 个强名称签名的 Token 全部与 BCL 相同） |
| 远程 CI（GitHub Actions） | ⛔ **NOT RUN**（本地交付时，等待用户自行 Push；本阶段不 Push） |
| ARM64 真机 E1/E2 | ⛔ **BLOCKED**（无物理设备，本阶段不装机） |

**没有**把桌面 .NET 测试写成 Android Mono 运行证据；**没有**把 `AndroidRefsDir` 编译通过写成真机运行通过；**没有**声称第三方 Mod 生态兼容性已验证。

---

## 2. 工作区与基线

| 项 | 值 |
| --- | --- |
| 工作区 | `D:\Download\DSH\StardewValley`（读写前已核实仓库身份与可写性） |
| remote | `https://github.com/Hiyomi-Kuro/StardewValley.git` |
| branch | `main` |
| 开始 HEAD | `675b05807454ad7be9125672e71e83423a7e64dc` |
| `origin/main`（只读核对） | `675b05807454ad7be9125672e71e83423a7e64dc`（与本地一致，未前进） |
| 开始 `git status --short --branch` | `## main...origin/main`（**无 dirty、无 untracked**） |
| 结束时 HEAD | 仍为 `675b058`（DSH 未提交，改动全部在工作区） |
| 工具链 | 私有 SDK `8.0.404`（`.tmp/phase0.5/dotnet/dotnet.exe`）、65 个参考程序集（`.tmp/phase0.5/refs`）、Python 3.12.10 |

开始时已存在的 dirty/untracked 文件：**无**，因此没有需要保护的既有改动；本阶段也没有执行任何 `git reset` / `git clean` / 强制 checkout / 批量删除。

---

## 3. A–F：复现证据、根因、修复

### A. 依赖失败传播与全局预检的顺序误伤无关 Mod

**复现（Phase 1.1 基线上真实失败）**

| 场景 | 基线结果 | 期望 |
| --- | --- | --- |
| A1：A 依赖 B（B 版本门禁失败），A 的私有程序集与健康 C 冲突 | A、C **都** `MF0206`；C 未初始化 | A `MF0404`；C `Initialized` |
| A2：B/C 直接冲突，A 依赖 B 且与健康 D 冲突 | A、D **都** `MF0206`；D 未初始化 | B/C `MF0206`；A `MF0404`；D `Initialized` |

**根因**：`Loader.RunCore` 只把"版本门禁失败"排除在预检输入之外；"依赖了失败 mod" 要等到逐个处理阶段才由 `FirstFailedDependency` 发现。因此注定不能运行的 mod 仍带着自己的程序集声明进入全局冲突组；而且第一次预检的 rejection 被永久写入 `Rejections`，没有任何重算路径。

**修复**：把"候选集构建 + 依赖传播 + 预检"做成显式的不动点（见 §4）。修改 `Loader.cs`；`AssemblyPreflight.Analyze` 保持纯函数（只做一次分析，不做传播）。

**剩余风险**：轮次上限 `|plan.Order| * 2 + 4` 是防御性兜底；正常依赖图 2–3 轮收敛（测试里可见 `preflight-rounds=`）。极端构造下超限会采用最后一次计算的状态，仍然失败关闭（该拒绝的仍被拒绝），但可能不是最小集合。

### B. 内容相等依赖 FNV-1a 64 回退

**复现**

- `AssemblyClaimReader.ComputeDigest` 在 SHA-256 不可用时返回 `fnv1a64:<len>:<hex>`（基线实测 `fnv1a64:6144:a47705bb158939cb`）。
- 注入"不同字节 → 相同摘要"的测试提供器后，两个**内容不同**的 mod 被判为"同内容同身份"，**双方都成功初始化并共享**（基线实测 `CollideX/CollideY state=Initialized`）。

**根因**：摘要字符串相等被直接当作"内容相同"的证明，而 FNV-1a 64 不抗碰撞。

**修复**

- `ComputeDigest` 只用 SHA-256；不可用时返回**空**（不再退化为弱摘要）。
- 新增 `ContentIdentity.CompareFiles`：固定 16 KiB 双缓冲、逐字节比较，检测长度变化/短读/IO 异常 → `Unverifiable`。
- 摘要只作**否定**过滤（摘要不同 ⇒ 内容必然不同，对任意确定性函数都成立）；**相等必须逐字节确认**。
- 无法完成比较 → **Fail-Closed**，拒绝共享并记录 `content-unverifiable`。
- 公钥身份改为比较**字节**（见 §5），不再用指纹字符串决定同一性。

**剩余风险**：逐字节比较需要能打开两个文件；文件被独占锁定时会 Fail-Closed 拒绝（这是策略，不是缺陷）。SHA-256 的作用被削弱为"快速否定"，但正确性不依赖它。

### C. 宿主程序集身份匹配过宽、内容不可验证时仍共享

**复现（基线）**

- 宿主快照只保留 `HasPublicKey` 布尔值，`FindHostMatch` 只比较"双方是否都有公钥"。
- `FindLoadedAssembly` 只按简单名 + 版本重新搜索 AppDomain，**不比较 Culture 与强名称**，且**取第一个**。
- 宿主文件不可读时 `VerifyHostContent` 返回 `Unverifiable`，但调用方仍然 `ShareFromHost`，只写 `preflight-warning`（基线实测：mod **成功初始化**，报告 `content=unverified`）。

**修复**

- `HostAssemblyIdentity` 保留 `PublicKeyToken`（字节）、`Culture` 与**已加载实例引用** `Assembly`；不再重新搜索 AppDomain。
- 新增 `AssemblyIdentityComparison`：Name（忽略大小写）+ Version（精确四段）+ Culture（`neutral`≡空）+ 公钥字节，四项全等才算同身份。
- `ResolveAgainstHost` 要求候选**唯一**：0 个 → `MF0207`；>1 个 → `MF0207` 并写 `preflight-host-ambiguous=`；1 个但内容不可验证/不同/实例不可用 → `MF0207` 并写 `preflight-host-unverifiable-rejected=`。
- 报告区分 `preflight-host-share=`（已验证共享）/ `preflight-conflict=… kind=host` / `preflight-host-unverifiable-rejected=` / `preflight-host-ambiguous=`。

**剩余风险**：`AssemblyName.GetPublicKey()` 在裁剪运行时不存在（实测 `CS1061`），宿主只能给出 Token。为了让文件侧（完整公钥）与宿主侧（Token）可比，`PublicKeyToken.Derive` 按 ECMA-335 从完整公钥派生 Token（自实现 SHA-1，仅作编码，不作安全原语）。派生规则已用 58 个真实签名程序集与 BCL 对照（§8.2），但**未在 Mono 设备上**验证。

### D. 手写 PE/CLI 解析器边界不完整

**复现（基线接受、必须拒绝的畸形样本）**

| 样本 | 基线 |
| --- | --- |
| `NumberOfRvaAndSizes=14`（CLI 目录项不存在） | **接受** |
| CLI 头 `cb=8`（小于 72 字节） | **接受** |
| `SizeOfRawData=8`，CLI 只映射到首字节 | **接受** |
| `NumberOfSections=5000`（节表越出文件） | **接受** |
| `#Strings` 的 Size 越出 metadata 容器（文件内） | **接受** |
| `#Strings` 里名字的终止符落在 heap 之外 | **接受** |
| 程序集名含非法 UTF-8（`0xFF`） | **接受**（被替换成 `U+FFFD`） |
| `#Blob` 长度前缀指向 heap 之外 | **接受** |
| `#~` 行数据越出 table stream | **接受** |
| `Assembly` 行数 = `0x40000000` | **接受** |

**根因**：`RvaToFileOffset` 的 `size` 参数从未参与校验（只映射首字节）；RVA 区间用 `max(VirtualSize, RawSize)`；Optional Header / Data Directory 只对照文件长度而不是 Optional Header 自身；Stream Offset/Size 只对照文件；`#Strings`/`#Blob` 只以文件长度为上限；表行遍历只对照文件；算术全用 `int`（可溢出为负）；`Assembly.Flags` 从未被读取。

**修复**：`AssemblyMetadata.cs` 重写为"逐层容器"模型（见 §6），全部偏移/长度用 `long`；严格 UTF-8 校验（不依赖 `Encoding.UTF8` 的替换行为）；`Assembly` 行数必须恰为 1；读取并解释 `Assembly.Flags` 的 `0x0001` 位。

**剩余风险**：解析器只覆盖"读 Assembly 表行 0 所需"的表（`0x00–0x1F` 计算行宽，`0x20` 读取），不校验 `0x21+` 表的数据范围——那些表在 Assembly 行之后，不影响本判断；若将来要读更多表，必须补齐行宽表。样本是合成的（编译器不产生），因此"能拒绝"强于"能解析"；正向解析能力由 65 个真实程序集差分覆盖。

### E. FrameworkVersion 的异常回退伪装成有效版本

**复现（基线）**

- 强制"版本读取失败"后 `Loader.FrameworkVersion()` 返回 `"0.0.0"`（合法 SemVer）。
- 声明 `frameworkVersion: ">=1.0.0 <2.0.0"` 的 mod 因此得到 `MF0502`（"框架版本不支持"），而真实原因是"版本无法确定"。
- 报告的 `framework-version=` 直接写 `0.0.0`；`ModContext.FrameworkVersion` 也是 `0.0.0`（fixture 标记文件实测 `P111.E.Wildcard|unknown|0.0.0`）。

**修复**：`FrameworkVersionState { Known, Unknown, Invalid }` + `FrameworkVersionInfo{Text,State,Value}`。未知时 `Text="unknown"`；显式范围在 Unknown → **新增 `MF0506`**，Invalid → `MF0505`，已知不满足 → `MF0502`；`"*"` 不受影响。报告新增 `framework-version-state=`。

**剩余风险**：`FrameworkVersionOverride` / `FrameworkVersionUnavailableForTests` 是内部测试 seam（`InternalsVisibleTo` 仅测试程序集），不是 Mod 可配置入口；生产代码路径不使用它们。

### F. 异常与报告泄露用户绝对路径

**复现（基线）**

- 报告含 `locate=<运行时目录绝对路径>`、`mods-root=<用户临时目录绝对路径>`（含用户名）。
- 一个 `Initialize` 抛出 `IOException("could not open " + context.ModDirectory + "\data.bin")` 的 mod，把**完整绝对路径**写进报告 `message=` 与 `<id>.log` 的 `exception:` 行（基线实测两处均命中）。
- `ModDiscovery` 的 warning 也拼进绝对路径。

**修复**：新增 `PathScrubber`（`Describe(Exception)` = 异常类型 + 脱敏消息；`Scrub` 把绝对路径替换为 `<path>`；`Leaf` 取末段）。报告字段改为 `locate=<末段>`、`mods-root=<末段>`、`dir=<包目录名>`、`assembly=<声明文件名|host:名字>`；`ModLogger` 的 `exception:` 与 `stack:` 也脱敏；`BuildFatalReport` 的 stack 同样脱敏。**异常类型与稳定错误码保留**，诊断价值不丢。

**剩余风险**：脱敏是模式匹配（盘符 + 分隔符、UNC、POSIX 绝对路径），不是白名单。相对路径（例如 mod 自己的 `data.bin`）刻意保留。`ModContext.ModDirectory` 是给 mod 的 API，仍是绝对路径（mod 需要它定位自己的文件），不属于"报告/日志"。

---

## 4. 预检不动点（为什么不会误伤无关 Mod）

```
intrinsicFailures := manifest 失败 ∪ 依赖解析失败 ∪ 版本门禁失败
                     ∪ {MF0201 文件缺失, MF0205 文件损坏, MF0207 宿主冲突}
conflictSet := {}
dependencySet := {}
round := 0
loop:
    round++
    # 1. 依赖闭包（依赖原因优先）
    dependencySet := {}
    repeat until unchanged:
        for m in plan.Order:
            if m ∈ intrinsicFailures ∪ dependencySet: continue
            d := first required dependency of m in (intrinsicFailures ∪ conflictSet ∪ dependencySet)
            if d exists:
                dependencySet += m
                conflictSet -= m            # 关键：把它从冲突集合里移走
    # 2. 只对"还可能运行"的 mod 做预检
    pool := plan.Order − intrinsicFailures − dependencySet
    preflight := Analyze(pool)
    collect events
    # 3. 内在失败永久生效
    added := preflight.Rejections where code ∈ {MF0201, MF0205, MF0207} \ intrinsicFailures
    if added non-empty: intrinsicFailures += added; continue
    # 4. 剩下的只可能是对称冲突 → 用"新集合"覆盖，而不是累加
    newConflict := keys(preflight.Rejections)
    if newConflict == conflictSet: break
    conflictSet := newConflict
    if round > |plan.Order|*2+4: break        # 兜底
# 5. 终态归类（重建）
outcomes := intrinsicFailures
          ∪ { m → MF0404("required dependency … did not initialize") : m ∈ dependencySet }
          ∪ { m → 预检码 : m ∈ conflictSet }
# 6. 报告按最终归类过滤后输出事件
# 7. 之后才允许第一个 Assembly.LoadFrom
```

**为什么不会误伤**：

1. 依赖失败者**先**被移出冲突集合，它的声明不参与冲突判定（场景 A1：`PhaseA1Lib` 冲突组根本不出现）。
2. 冲突集合每轮**重算**而不是锁定；当冲突组因为另一侧被排除而只剩一个成员时，该成员在下一轮恢复可加载（场景 A2：D 恢复 `Initialized`）。
3. 只有内在失败（自己的文件坏了 / 与宿主冲突）不可撤销；对称冲突永远重新计算。
4. 轮次只依赖 `plan.Order`（拓扑序 + id 序）与每个 manifest 的依赖声明顺序 → 与目录枚举、fixture 创建顺序无关（场景 A5 用相反创建顺序比对整份 `id/state/code` 签名，逐字相同）。
5. 可选依赖永不阻断（场景 A4）；必需依赖失败才传播。
6. 报告事件按最终归类过滤：只在早期轮次成立的冲突不会出现在最终报告里，而真正被拒绝的 mod 的证据被保留。`preflight-rounds=` 记录收敛轮数。

**A2 的实际收敛过程**（`preflight-rounds=3`）

| 轮 | pool | 预检发现 | 结果 |
| --- | --- | --- | --- |
| 1 | A,B,C,D | `PhaseA2Lib`{B,C}、`PhaseA2Lib2`{A,D} | conflictSet={A,B,C,D} |
| 2 | B,C,D（A 因依赖 B 被移出） | `PhaseA2Lib`{B,C} | conflictSet={B,C} |
| 3 | B,C,D | `PhaseA2Lib`{B,C}（不变） | 收敛：A=`MF0404`、B/C=`MF0206`、D=`Initialized` |

---

## 5. 内容身份、公钥身份、宿主匹配的最终规则

### 5.1 内容身份

| 步骤 | 判据 | 结论 |
| --- | --- | --- |
| 1 | 记录长度不同 | `Different`（可证明） |
| 2 | 双方都有 SHA-256 且摘要不同 | `Different`（对任意确定性摘要都成立） |
| 3 | 逐字节比较（16 KiB 双缓冲） | 全等 → `Same`；任一字节不同 → `Different` |
| 4 | 打开失败 / 短读 / 比较期间长度变化 / IO 异常 | `Unverifiable` → **拒绝共享**（Fail-Closed） |

- SHA-256 不可用 ⇒ 摘要为空 ⇒ 跳过第 2 步，直接第 3 步；**没有** FNV/CRC 回退。
- 报告写明 `preflight-content-identity=sha256+full-bytes` 或 `full-bytes-only`，`content=` 字段写 `sha256:<hex>` 或 `unverified`。
- 内存：一次只有一个声明的整文件缓冲（≤64 MiB 上限）+ 两个 16 KiB 比较缓冲。

### 5.2 公钥身份（ECMA-335）

| 侧 | 数据来源 | 表示 |
| --- | --- | --- |
| 文件 | `Assembly` 表 `Flags.PublicKey(0x0001)` = 1 | `PublicKey` = 完整 blob；同时派生 `PublicKeyToken` = SHA-1(blob) 末 8 字节**倒序** |
| 文件 | 同上 = 0 且 blob 非空 | blob 本身就是 Token，直接作为 `PublicKeyToken`（不派生、不猜测） |
| 宿主 | `AssemblyName.GetPublicKeyToken()` | `PublicKeyToken`（完整公钥不可得） |

比较：双方都有完整公钥 → 比完整公钥；否则双方都有 Token → 比 Token；双方都未签名 → 相等；**表示不匹配 → 不相等**（不猜测）。判断**只用字节**，`HasPublicKey` 仅用于报告文本。

### 5.3 宿主匹配与安全边界

1. 候选 = 快照中同简单名的全部已加载程序集；逐个用 5.2 + Name/Version/Culture 比身份。
2. 候选数 0 → `MF0207`（身份不符，报告列出宿主版本/Culture/Token 便于诊断）。
3. 候选数 >1 → `MF0207`（歧义，`preflight-host-ambiguous=`）——不"取第一个"。
4. 候选唯一 → 要求 `Location` 非空、文件存在、长度可读、内容逐字节相同；任一不满足 → `MF0207`（`preflight-host-unverifiable-rejected=`），**不再** `ShareFromHost`。
5. 只有全部通过才 `ShareFromHost`，并把**快照里那个 `Assembly` 实例**交给 mod（不重新搜索 AppDomain），因此 mod 拿到的是宿主同一个 Assembly 对象/契约类型，不会二次加载副本。
6. 未添加任何"框架自有程序集"白名单：没有证据就不豁免。

---

## 6. PE/CLI 解析器边界模型与新畸形样本

```
file
 └─ DOS(≥0x40) / e_lfanew / "PE\0\0" / COFF(20)       全部必须落在文件内
     └─ Optional Header（SizeOfOptionalHeader 声明的大小即容器）
         ├─ Magic ∈ {0x10B,0x20B}；SizeOfOptionalHeader ≥ 2
         ├─ NumberOfRvaAndSizes 在 Optional Header 内
         ├─ 目录数组 (count*8) 在 Optional Header 内
         └─ entry[14] 存在（count > 14）且在 Optional Header 内
             └─ Section Table (count*40) 完整在文件内；1 ≤ count ≤ 96
                 └─ RVA→文件：整段 [rva, rva+size) 必须落在某节的 **Raw Data** 内
                     （VirtualSize 只用于命中节；RawSize 决定可读字节；偏移+size ≤ 文件长度）
                     └─ CLI Header：SizeOfRawData/目录 size ≥ 72；头部自身 cb ≥ 72 且 ≤ 目录 size
                         └─ Metadata Root：≥20 字节、BSJB、整段由 Raw Data 支撑
                             └─ version length ∈ (0,1024]，位置在 root 内
                                 └─ Stream Header(8) 在 root 内；名字以 NUL 结尾且 ≤32 字节、UTF-8 合法
                                     └─ Stream Offset+Size ≤ root size（相对 root）
                                         ├─ #~：size ≥ 24；valid mask → 行数逐个在 stream 内；
                                         │      行数×行宽 的累加在 stream 内；Assembly 行在 stream 内
                                         │      未知必需表 → 明确拒绝（不猜行偏移）
                                         └─ #Strings/#Blob：index < heap size；
                                                终止符/长度前缀+payload 都在 heap 内；
                                                字符串严格 UTF-8
```

**新增/覆盖的畸形样本**（`SyntheticPe` 合成，152 字节级差异，从不加载进 AppDomain）

| # | 样本 | 期望 |
| --- | --- | --- |
| 1 | `NumberOfRvaAndSizes=14`（无 CLI 目录项） | 拒绝 |
| 2 | `SizeOfOptionalHeader=2` | 拒绝 |
| 3 | CLI `cb=8` | 拒绝 |
| 4 | CLI RVA=0x9000（不在任何节） | 拒绝 |
| 5 | `SizeOfRawData=8`（CLI 只有首字节在 Raw Data） | 拒绝 |
| 6 | `PointerToRawData=0x5000`（越出文件） | 拒绝 |
| 7 | `NumberOfSections=5000` | 拒绝 |
| 8 | `#Strings` Size 越出 metadata root | 拒绝 |
| 9 | Stream 名字 32 字节无 NUL | 拒绝 |
| 10 | `#Strings` 终止符落在 heap 之外 | 拒绝 |
| 11 | 程序集名含 `0xFF`（非法 UTF-8） | 拒绝 |
| 12 | 空程序集名（index=0） | 拒绝 |
| 13 | `#Blob` 长度前缀 `0x7E` 越出 heap | 拒绝 |
| 14 | `#~` Size=40（行数据越出 stream） | 拒绝 |
| 15 | valid mask=0（netmodule，无 Assembly 行） | 拒绝 |
| 16 | Assembly 行数 = `0x40000000` | 拒绝 |

**正向覆盖**：PE32、PE32+、Stream 顺序变化（`{2,0,1}` / 无 `#~` 在首位）、2 字节与 4 字节 heap 索引、合法 2 字节压缩 Blob 长度、`#-`（未压缩表流，索引固定 4 字节）。

**模糊测试**：固定种子 LCG，1500 个结构化变异输入 → 不抛异常、不挂起、两次解析结果一致、不接受空名字。

**差分**：65 个真实程序集，`ManagedAssemblyReader` 与 `AssemblyName.GetAssemblyName` 的 Name/Version/Culture/PublicKeyToken 全部一致（见 §8.2）。

---

## 7. 完整测试矩阵（本次本地运行）

环境：Windows，私有 .NET SDK `8.0.404`，Python `3.12.10`，65 个参考程序集。原始日志放在**仓库外**的 DSH 任务目录（`<task>\logs\`），不污染工作区。

| # | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| 1 | `python Tools/tests/test_loader.py --dotnet .tmp/phase0.5/dotnet/dotnet.exe --refs .tmp/phase0.5/refs` | 0 | **283/283 PASS**（驱动 28/28） | `GREEN-after-fix.txt` / `gate4-final-verify.txt` |
| 1b | 同上，Phase 1.1 基线源码 + 新增测试（Red 证据） | 1 | 283 checks / 251 pass / **32 FAIL** | `RED-baseline.txt` |
| 2 | `python Tools/tests/test_mod_pipeline.py --dotnet … --refs …` | 0 | **27/27 PASS** | `gate3-mod-pipeline.txt` |
| 3 | `python Tools/tests/test_ci_offline.py` | 0 | **13/13 PASS** | `gate3-ci-offline.txt` |
| 4 | `python Tools/privacy-scan.py --include-untracked` | 0 | **CRITICAL/HIGH = 0**，MEDIUM = 0，INFO = 11（历史文档里的机器路径，非密钥） | `gate3-privacy.txt` |
| 5 | `powershell -File Tools/tests/test_device_precheck.ps1` | 0 | **10/10 PASS**（不连设备） | `gate3-device-precheck.txt` |
| 6 | `powershell -File Tools/tests/run-all.ps1 -Refs …` | 0 | **6 suites / 6 PASS / 0 failed**（见 §7.1） | `gate3-run-all.txt` |
| 7 | 桌面 Runtime Release build | 0 | 0 error / 0 warning | §8.1 |
| 8 | Runtime `-p:AndroidRefsDir=…` Release build | 0 | **0 error / 0 warning** | `gate3-mod-pipeline.txt`（`runtime_compiles_against_android_refs`） |
| 9 | ExampleMod.HelloWorld `-p:AndroidRefsDir=…` Release build | 0 | **0 error / 0 warning**（并在宿主里初始化成功） | `GREEN-after-fix.txt`（`hello_world_build` / `hello_world_host_run`） |
| 10 | `git diff --check` / Python 语法 / PowerShell 语法 / schema | 0 | 全部 OK | `gate3-style-checks.txt` |
| 11 | 65 程序集解析差分（`--diff-refs`） | 0 | **65 比较 / 0 不一致**（58 个签名程序集 Token 全对） | `gate3-refs-differential.txt` |

### 7.1 run-all 与风格检查

`powershell -File Tools/tests/run-all.ps1 -Refs .tmp/phase0.5/refs` → 退出码 0（源码冻结后的最终一次运行，日志 `gate4-run-all-final.txt`）：

```
ci-offline                   PASS     1.0s
privacy-scan                 PASS     0.4s
device-precheck-selftest     PASS    10.2s
mod-pipeline                 PASS    42.0s
loader                       PASS    80.6s
git-diff-check               PASS     0.1s
6 suites, 6 passed, 0 failed, 0 skipped
```

其中 `loader` 套件内部：宿主 `283 checks, 283 passed, 0 failed`，驱动 `28 checks, 28 passed`，差分 `65 (signed 58)`。

风格检查（`gate3-style-checks.txt`）：`git diff --check` 无空白错误；`py_compile` 覆盖 `test_loader.py` / `test_mod_pipeline.py` / `test_ci_offline.py` / `privacy-scan.py` / `AssemblyStore/store.py` 全部通过；4 个 PowerShell 脚本用 `[Parser]::ParseFile` 解析无错误；`manifest.schema.json` 可解析（`schemaVersion=1`）。manifest 夹具的 25 个负向样本由 `test_loader.py` 的单元层覆盖（`invalid.*` 全部按预期错误码拒绝）。

---

## 8. 构建与差分（不等于真机运行）

### 8.1 `AndroidRefsDir` 编译

| 目标 | 命令 | 结果 |
| --- | --- | --- |
| `ModFramework.Runtime`（桌面） | `dotnet build … -c Release` | **0 个警告 / 0 个错误** |
| `ModFramework.Runtime`（目标引用） | `dotnet build … -c Release -p:AndroidRefsDir=<refs>` | **0 个警告 / 0 个错误** |
| `ExampleMod.HelloWorld`（目标引用） | `dotnet build … -c Release -p:AndroidRefsDir=<refs>` | **0 个警告 / 0 个错误** |

**注意**：这只能证明"用到的 API 在裁剪后的引用集里存在"。它**不等于** Mono 设备运行通过，也**不等于**真机行为正确。

### 8.2 65 程序集解析差分

```
refs-differential: 65 files present
  compared        : 65 (strong-name signed: 58)
  identity mismatch: 0
  token mismatch  : 0
  rejected by us  : 0
  not managed (BCL): 0
RESULT: OK
```

意义：解析器与独立实现（BCL）在真实编译器产出的程序集上 **Name / Version / Culture / PublicKeyToken 完全一致**，其中 58 个是强名称签名程序集——这同时验证了 `Assembly.Flags` 的解释与 SHA-1 Token 派生规则。

隐私：只输出计数、文件名与错误类型；不复制、不哈希入库、不打印目录。

---

## 9. 文档、错误码与 Phase 1.1 历史

### 9.1 错误码变更

| 码 | 变更 |
| --- | --- |
| `MF0506` | **新增**：运行时框架版本**无法确定**（与 `MF0505` 无法解析、`MF0502` 已知不满足区分） |

其余错误码语义不变；`MF0207` 的适用范围扩大（增加"内容不可证明/候选不唯一"），文案已同步。

### 9.2 文档变更

| 文件 | 变更 |
| --- | --- |
| `docs/modding/manifest-spec.md` | §3.4 重写（身份四项、Flags 位、逐字节内容判定、宿主 Fail-Closed、`kind=` 分类、路径策略）；§8 增加 `MF0506`、扩充 `MF0207` |
| `docs/architecture/phase1-loader-design.md` | §2 组件表新增 4 个文件；§3 时序更新 + 新增 §3.1 不动点（含收敛与"为什么不误伤"）；§5 故障矩阵补 2 行；§11 变更记录 |
| `docs/architecture/phase1-runtime-constraints.md` | §3.1 增加程序集身份与 `SHA256.HashData` 行；§3.2 增加 `AssemblyName.GetPublicKey()` 不可用（`CS1061`） |
| `README.md` | 状态表加 Phase 1.1.1 行；"四条硬语义"扩成 8 条；测试数 206 → 283；`GetPublicKey`/弱摘要说明 |
| `.github/workflows/ci.yml` | 注释说明 1.1.1 覆盖范围；local-only SKIPPED 列表增加解析差分 |
| `Tools/tests/test_loader.py` | 新增 8 个 fixture 变体与解析差分调用；stdout 强制 UTF-8（畸形输入会产生替换字符，不能让控制台编码崩掉驱动） |
| `Tools/tests/FixtureMods/FixtureMod/Entries.cs` | 新增 `PathLeakEntry`（故意在异常消息里带绝对路径） |

### 9.3 Phase 1.1 历史

未修改 Phase 1.1 报告中的任何历史交付状态；未追加任何"用户 Push 后"的事后记录。Phase 1.1 的远程提交 `675b058` / Run #2 只作为本阶段的已知基线证据，**不代表**本阶段代码已 Push。

---

## 10. 未解决问题、BLOCKED 与需要用户批准的操作

| 项 | 状态 | 说明 |
| --- | --- | --- |
| ARM64 真机 E1/E2 | **BLOCKED** | 无物理设备；本阶段不装机、不 `adb install`、不改真实游戏目录/存档 |
| Mono 设备上的 PE 解析与 SHA-256/Token | **BLOCKED** | 只有编译期 + 桌面证据；未在设备上跑过 |
| 远程 CI | **NOT RUN** | 本地交付时等待用户自行 Push；本阶段不 commit/push |
| 签名程序集的端到端宿主复用测试 | 部分覆盖 | 没有签名夹具（不在仓库放私钥）。规则由单元测试 + 58 个真实签名程序集的 Token 差分覆盖，而非端到端 fixture |
| `Assembly.Flags` 除 `0x0001` 外的位 | 记录但未参与判定 | `Retargetable`(0x0100)、`ContentType`(0x0E00) 等不用于"允许共享"，也不额外拒绝（保守：不因它们放行，也不因它们拒绝） |
| 第三方 Mod 生态兼容性 | **未验证** | 不声称 |
| Phase 2（Harmony/detour/事件/UI） | 未开始 | 按边界要求 |

需要用户批准的操作（本阶段未执行）：`git commit` / `git push` / 创建 Release / 修改远程分支 / `adb install` / 真机部署。

---

## 11. 可恢复 checkpoint 与关键文件 SHA-256（前 16 位）

| 文件 | SHA-256 前缀 |
| --- | --- |
| `Mod/ModFramework.Runtime/AssemblyPreflight.cs` | `b99653ad64b49d72` |
| `Mod/ModFramework.Runtime/AssemblyIdentity.cs` | `21225208eb366a67` |
| `Mod/ModFramework.Runtime/AssemblyIdentityComparison.cs` | `2967b8188c990508` |
| `Mod/ModFramework.Runtime/ContentIdentity.cs` | `68d4e35f8f3430e8` |
| `Mod/ModFramework.Runtime/PublicKeyToken.cs` | `ec9e6471c8fb44ee` |
| `Mod/ModFramework.Runtime/PathScrubber.cs` | `f7200d6d483efb9a` |
| `Mod/ModFramework.Runtime/AssemblyMetadata.cs` | `76cb96d0a71b9c9d` |
| `Mod/ModFramework.Runtime/Loader.cs` | `8c07e378c726d731` |
| `Mod/ModFramework.Runtime/ModState.cs` | `d94afc4ce02011dd` |
| `Mod/ModFramework.Runtime/ModDiscovery.cs` | `9f7cd797b01f02cf` |
| `Mod/ModFramework.Runtime/ModLogger.cs` | `cebbce82e98e2f5a` |
| `Mod/ModFramework.Runtime/ErrorCodes.cs` | `05ae3ac4bba37e75` |
| `Tools/tests/ModFramework.Tests/Phase111Tests.cs` | `25598885b9cca49a` |
| `Tools/tests/ModFramework.Tests/SyntheticPe.cs` | `98975bcd753ecdc5` |
| `Tools/tests/ModFramework.Tests/RefsDifferential.cs` | `5cd80b0b6efcdea3` |
| `Tools/tests/ModFramework.Tests/Program.cs` | `01504fa3ea99d95d` |
| `Tools/tests/ModFramework.Tests/UnitTests.cs` | `271a67e6857b3ece` |
| `Tools/tests/FixtureMods/FixtureMod/Entries.cs` | `dee7f5c844eda40b` |
| `Tools/tests/test_loader.py` | `299064f1de90f067` |
| `.github/workflows/ci.yml` | `b35ee0ff63c2ce83` |

基线关键文件（Phase 1.1，未修改）：`PathRules.cs` `60EC9DBC94FAA87E…`、`ModManifest.cs` `2755E5BA81926C53…`、`DependencyResolver.cs` `D64F7538F3DF6237…`。

### 下一步建议

1. 用户审阅本报告与 diff 后自行 `git commit` / `git push`；随后可另起一次验收请求查询 Actions。
2. 真机可用时补 `E1/E2`：设备上验证 `Assembly.LoadFrom` + 元数据读取 + 签名程序集宿主复用（当前唯一 BLOCKED 的实质风险）。
3. 若将来需要读更多元数据表（例如程序集引用），必须补齐 `RowSize` 的 `0x21+` 行宽并按同样方式校验容器边界。
