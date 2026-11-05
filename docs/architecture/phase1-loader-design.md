# Phase 1 Loader 设计

- 阶段：Phase 1
- 代码：`Mod/ModFramework.Runtime/`
- 关联：`phase1-runtime-constraints.md`（能用什么 API）、`../modding/manifest-spec.md`（格式与错误码）

---

## 1. 目标与非目标

**目标**：让一个受信任的 mod 包完成
`扫描 → Manifest 校验 → 依赖解析 → 程序集加载 → 入口校验 → 实例化 → Initialize → 记日志`，
并且**任何一个 mod 出错都不能阻止无关 mod 或游戏启动**。

**非目标**（Phase 1 明确不做）：SMAPI 兼容、Harmony/方法补丁、Events/Content/Data/Save API、
配置 UI、游戏内管理器、自动下载、热重载、不受信任 mod 的沙箱。

---

## 2. 组件

| 文件 | 职责 | 可独立测试 |
| --- | --- | --- |
| `Loader.cs` | 编排：发现 → 解析 → 加载 → 初始化 → 报告；重入/重复运行保护 | 宿主测试 |
| `ModDiscovery.cs` | 只扫一层子目录，排除隐藏/临时目录，处理重复 id | ✅ 单元 |
| `ModManifest.cs` | manifest 模型 + 全部校验规则（含 `privateAssemblies`） | ✅ 单元（25 个夹具） |
| `Json.cs` | 严格 JSON 解析器（无 `System.Text.Json`）、深度/大小/重复 key 限制 | ✅ 单元 |
| `SemVersion.cs` | SemVer 2.0.0 + 版本范围（无 `Version.TryParse`） | ✅ 单元 |
| `DependencyResolver.cs` | 依赖校验、环检测、确定性拓扑排序 | ✅ 单元 |
| `ModState.cs` | 生命周期状态机 + 每个 mod 的记录（含 `log-status` / `shared`） | ✅ 单元 |
| `ModLogger.cs` | 每 mod 一个日志文件；写失败自我熔断 + 状态上报（Phase 1.1） | ✅ 宿主 |
| `ModContext.cs` | `IModContext` 实现（只读视图） | ✅ 宿主 |
| `Contracts.cs` | `IModEntry` / `IModContext` / `IModLogger` / `ModLogLevel` | — |
| `ErrorCodes.cs` | 稳定错误码 + 文案 | ✅ 单元 |
| `PathRules.cs` | 包内路径判定（`IsInside`），Loader 与预检共用 | ✅ 单元 |
| `AssemblyMetadata.cs` | **Phase 1.1**：只读 PE/CLI 元数据（Assembly 表行 0），不加载程序集；1.1.1 重写为逐层容器边界校验 + `Assembly.Flags` | ✅ 单元 |
| `AssemblyIdentity.cs` | **Phase 1.1**：声明文件 → 身份 + 内容摘要；宿主已加载程序集的同形身份；1.1.1 起摘要只用 SHA-256（无弱回退），宿主快照保留公钥字节与实例引用 | ✅ 单元 |
| `AssemblyPreflight.cs` | **Phase 1.1**：全局冲突判定、宿主冲突判定、确定性代表文件选择；1.1.1 起宿主匹配 Fail-Closed，发现项改为结构化事件 | ✅ 宿主 |
| `AssemblyIdentityComparison.cs` | **Phase 1.1.1**：唯一的身份比较规则（Name/Version/Culture/公钥字节），file↔file 与 file↔host 共用 | ✅ 单元 |
| `ContentIdentity.cs` | **Phase 1.1.1**：内容相等的最终判据（固定缓冲逐字节比较），摘要只做快速否定 | ✅ 单元 |
| `PublicKeyToken.cs` | **Phase 1.1.1**：ECMA-335 公钥 Token 派生（自实现 SHA-1，仅用于编码，不作为安全原语） | ✅ 单元 |
| `PathScrubber.cs` | **Phase 1.1.1**：报告/日志的绝对路径脱敏（异常类型保留，路径变 `<path>`） | ✅ 单元 |

依赖方向是单向的：`Loader` → 其它；预检三件套只依赖 `PathRules`/`ErrorCodes`/`ModManifest`，
不依赖 `Loader`（因此可以单独测试，也不会反过来加载任何东西）。

---

## 3. 加载时序

```
Loader.Run(modsRoot)
├── 重入保护（静态标志）                     → MF0901
├── 同一 root 已初始化过？                    → 直接 null + result=already-initialized
├── 探测游戏/框架版本                        → game-version + game-version-state + game-version-source
├── ModDiscovery.Discover
│   ├── Directory.GetDirectories(root) 排序
│   ├── 过滤 . / _ 前缀、无 manifest.json     ← 裸 DLL 永不被发现（Phase 1.1）
│   └── ManifestReader.Load 逐个校验          → 失败记录 MF01xx，不影响别人
├── DependencyResolver.Resolve
│   ├── 缺失/版本不符 → 该 mod MF0401/MF0402
│   ├── DFS 找环 → 环上 MF0403
│   └── Kahn 拓扑排序（就绪集按 id 排序）
├── 门禁 1：版本兼容（按拓扑序预判，不加载任何东西）
│   └── 显式范围 + 未知/非法/不满足 → MF0503 / MF0504 / MF0505 / MF0506 / MF0501 / MF0502
├── 失败集合：先归并"根因"，再做依赖传播（Phase 1.1.1）
│   ├── manifest / 依赖解析 / 版本门禁 → 根因
│   └── 必需依赖落在失败集合 → 下游 MF0404（可选依赖不阻断）
├── 门禁 2：全局程序集身份预检（Phase 1.1，一次覆盖所有仍存活的 mod）
│   ├── 只读 PE/CLI 元数据 + 内容比较（不 LoadFrom、不执行代码）
│   ├── 同名不同身份/内容        → 双方 MF0206
│   ├── 与宿主同身份但内容不可证明/候选不唯一 → MF0207（Fail-Closed）
│   ├── 损坏 / 非托管            → MF0205；文件缺失 → MF0201
│   └── **不动点**：预检拒绝 → 再传播依赖 → 重算冲突（见下）
├── preflight-stage=complete   ← 这一行之前没有任何 assembly-load=
├── 逐个（按拓扑序）：
│   ├── 已有终态归类？        → Failed(码 + kind)
│   ├── 依赖未初始化？        → Failed(MF0404)（防御性兜底）
│   └── LoadAndInitialize：
│       ├── privateAssemblies（按声明序，走预检给出的 canonical 路径）→ MF0202
│       ├── 入口程序集（同身份共享时复用已加载实例）      → MF0202
│       ├── 程序集名 == 文件名？            → MF0203
│       ├── entryType 存在且合法            → MF0301..MF0307
│       ├── Activator.CreateInstance        → MF0308
│       ├── 是 IModEntry（类型身份）        → MF0204
│       ├── Initialize(context)             → MF0309
│       └── finally：记录 log-status（不改变 mod 状态）
└── 写 loader-report.txt + loader.log；返回 null（两个文件都写不出 → MF0905）
```

> Phase 1 的时序里，版本门禁与私有程序集 `LoadFrom` 都在"逐个 mod"的循环内部，
> 因此第一个 mod 的 DLL 可能已经加载后才发现第二个 mod 的冲突。Phase 1.1 把两件事
> 提到循环之前，并用 `preflight-stage=complete` / `assembly-load=` 两行在报告里
> **可验证地**固定这个顺序。
>
> Phase 1.1.1 修的是"提到循环之前"之后剩下的顺序错误：Phase 1.1 只把**版本门禁**失败
> 排除在预检输入之外，而"依赖了失败 mod"要等到逐个处理阶段才由 `FirstFailedDependency`
> 发现。于是一个注定不能运行的 mod 仍会参与全局冲突判定，把无关的健康 mod 一起拖下水。

### 3.1 预检不动点（Phase 1.1.1）

两个互相依赖的集合：`dependencySet`（因必需依赖不能运行而阻断）与 `conflictSet`
（被当前预检轮次拒绝）。每轮重复下面四步，直到 `conflictSet` 不再变化：

```
round:
  1. 依赖闭包：对每个未内在失败、未在 dependencySet 的 mod，
     若它有必需依赖落在 intrinsicFailures ∪ conflictSet ∪ dependencySet
     → 加入 dependencySet，并从 conflictSet 中移除（依赖原因优先）
  2. pool = 所有 mod − intrinsicFailures − dependencySet
  3. preflight = Analyze(pool)
  4. 预检拒绝分两类：
     - intrinsic（MF0201 文件缺失 / MF0205 文件损坏 / MF0207 宿主冲突）
       → 并入 intrinsicFailures，永久生效，继续下一轮
     - 其余（只可能是 MF0206 对称冲突）→ 覆盖为**新的** conflictSet（不累加）
```

为什么不会误伤无关 mod：

1. **依赖原因优先于冲突归类**：依赖了失败者的 mod 先被移出冲突集合，它的声明不再参与冲突判定。
2. **冲突集合每轮重算而不是锁定**：冲突组因另一侧被排除而只剩一个成员时，该成员在下一轮
   不再被拒绝，于是恢复为可加载——这正是 Phase 1.1 做不到的。
3. **只有内在失败永久有效**：与别人无关的原因（自己文件坏了、与宿主冲突）不可撤销；
   对称冲突永远重新计算。
4. **确定性**：轮次只依赖 `plan.Order`（拓扑序 + id 序）与每个 manifest 自己的依赖声明顺序。
5. **收敛**：每轮要么至少并入一个内在失败，要么把 conflictSet 换成一个更小的集合；
   另有 `|plan.Order| * 2 + 4` 的轮次上限兜底（超限时用最后一次计算的状态，并写入
   `preflight-rounds=`）。
6. 所有 `Assembly.LoadFrom` 仍在循环**之后**，由 `preflight-stage=complete` 行保证。

报告保留每一轮的结构化发现，但**按最终归类过滤后再输出**：只在早期轮次成立、后来因
另一方被排除而消失的冲突不会出现在最终报告里；真正被拒绝的 mod 的证据（冲突组、宿主冲突、
不可验证、候选不唯一）会保留。

`Run` 的返回值只表达 **Loader 级**结果：

| 返回值 | 含义 |
| --- | --- |
| `null` | 扫描跑完了。个别 mod 失败也返回 `null`（失败在报告与日志里） |
| `"MF0902: ..."` | Mods 根目录不存在 |
| `"MF0905: ..."` | `loader-report.txt` 与 `loader.log` 都写不出来（扫描可能已经完成，但没有证据落盘） |
| `"loader-report: loader-crashed: ..."` | Loader 自己抛了未捕获异常（已尽力写报告） |

Bootstrap 把非 null 当失败抛异常，而它自己的 catch 会吞掉——所以**报告必须落盘**。

---

### 3.2 字节快照与加载绑定（Phase 1.1.2）

Phase 1.1.1 的预检已经"读一次、算一次"，但真正加载时仍按**路径**重新打开文件：

```
TryRead(path) ─► 身份 + 长度 + 摘要（同一 buffer）          [预检]
       ⋮   ← 这里可以替换文件 / 同长度原位改写
Assembly.LoadFrom(path) ─► 可能是另一份字节                 [加载]
```

Phase 1.1.2 让"预检批准的字节"成为**唯一的加载源**：

1. `AssemblyClaimReader.TryRead` 仍然只读一次文件；解析身份、算摘要、**写快照**都用同一个 buffer。
2. `AssemblyStagingArea.TryStage` 把该 buffer 写入应用私有目录
   `<Mods>/.staging/<run-id>/<mod id>/<声明文件名>`，随后**把文件读回来逐字节比较**；
   只有比较通过，claim 才会拿到 `StagedPath` / `SnapshotFingerprint`。
3. `Assembly.LoadFrom` 的实参永远是 `claim.StagedPath`。mod 声明的路径之后只用于
   `ContentIdentity` 的字节比较，以及预检阶段"文件在读取期间变了"的判定。
4. 加载前一刻再校验一次快照（长度 + 指纹：有 SHA-256 用 `sha256:`，运行时没有则用内置
   `sha1:` 形式）。指纹不匹配 → 该 mod 以 `MF0209` 语义的错误失败，**不加载**。
5. 同一内容只落一份快照（按指纹去重）；同一 canonical artifact 只 `LoadFrom` 一次
   （`s_loadedByPath` 的键仍是"实际加载的文件"，现在是快照路径）。

报告因此能区分三个对象：声明文件（`file=`）、快照（`snapshot=`）、最终加载对象
（`assembly-load=` 与 `canonical=`），且不含任何绝对路径。

不做的两件事，此处写清楚：

- **不能**用 `Assembly.Load(byte[])`：目标运行时把它裁掉了（本阶段重新编译得到 `CS1503`），
  所以"直接在内存里加载"不是选项。
- **不宣称**"TOCTOU 不可能"：快照与 mod 同处应用私有存储，同 uid 的代码仍可能在
  "校验通过"与"`LoadFrom`"之间替换快照。该窗口被压到最小，且一旦发生会被**发现并拒绝**；
  要做到完全原子化只能依赖 `Assembly.Load(byte[])`，目标运行时没有 → 真机语义标记 BLOCKED。

代价与边界：快照只存在于应用私有目录，单文件上限沿用 `MaxAssemblyBytes`（64 MiB），
总量上限 256 MiB / 4096 个文件，超出即 fail-closed；每次启动清理上一轮目录（尽力而为）。
`Assembly.Location` 现在指向快照，因此 mod 取自身数据文件必须用 `IModContext.ModDirectory`。

---

## 4. 状态机

```
Discovered → ManifestValidated → DependenciesResolved → AssemblyLoaded
           → EntryValidated → Instantiated → Initialized
                                   ↘ Failed(码) 或 Skipped(码)
```

- 状态只前进，不后退；`Failed`/`Skipped` 是终态。
- `AssemblyLoaded` 与 `Initialized` **刻意分开**：加载成功什么都不能证明。
- 每个记录都保存 `history=...` 字符串，报告里直接可见。

## 5. 故障隔离矩阵

| 故障 | 影响范围 | 机制 |
| --- | --- | --- |
| manifest 坏/缺字段 | 仅该 mod | 解析结果对象，不抛异常到外层 |
| 两个包 id 相同 | 两个包都失败 | `MF0120`，不随机挑 |
| 依赖缺失/版本不符 | 该 mod + 其下游 | 解析阶段标记 + 运行期 `MF0404` |
| 循环依赖 | 环上所有 mod | DFS 记录整条环 |
| 程序集同名冲突 | 冲突组内**所有直接拥有者** | `MF0206`，绝不 first-loaded-wins；依赖失败者不参与 |
| 宿主已经加载了同名程序集 | 该 mod | 身份逐项 + 内容逐字节证明后才复用，否则 `MF0207` |
| 入口 dll 缺失/改名 | 仅该 mod | `MF0201`/`MF0203` |
| 入口类型不合法 | 仅该 mod | `MF0301..MF0307` |
| 构造函数/`Initialize` 抛异常 | 仅该 mod + 其下游 | 每阶段 `try/catch`，异常写进该 mod 日志 |
| 日志写失败 | 不影响 mod | `ModLogger` 熔断，报告里记 warning |
| Loader 自身异常 | 游戏继续启动 | 顶层 `catch` + 报告落盘 |
| 重复调用 `Run` | 不重复执行 | 同 root 短路为 `already-initialized` |
| 重入（mod 在 Initialize 里再调 Run） | 第二次被拒 | 静态标志 → `MF0901` |

## 6. 确定性

要求：**同一份 Mods 目录，无论文件系统枚举顺序如何，结果必须一致**。

实现：
1. `Directory.GetDirectories` 结果按路径序号排序；
2. 依赖就绪集按 `id` 的 `StringComparer.Ordinal` 排序；
3. 有问题的 mod 统一排在报告末尾并按 id 排序；
4. 报告按记录顺序输出。

测试：`discovery_deterministic`（两个目录用相反顺序创建）、`dep_deterministic`
（同一依赖集用不同声明顺序）。

## 7. 为什么不用全局 AssemblyResolve

如果注册 `AppDomain.CurrentDomain.AssemblyResolve` 去 mod 目录找 dll：

- 它会把**所有** mod 目录变成全局搜索路径 → 任意 mod 可以劫持另一个 mod（甚至框架）的依赖；
- 失败时只能靠名字猜，诊断困难；
- 在 Mono 上 `AssemblyResolve` 的触发时机与加载上下文相关，未在设备验证。

替代方案：manifest 的 `privateAssemblies` 显式声明 + 入口加载前逐个 `LoadFrom`。
代价是作者要写清楚依赖，收益是可审计、无全局副作用、与运行时行为解耦。

## 8. 日志设计

- 框架级：`<Mods>/loader-report.txt`、`<Mods>/loader.log`（每次覆盖，避免无限增长）。
- mod 级：`<Mods>/logs/<mod id>.log`（追加），每行
  `<ISO8601> LEVEL [mod id] message`；异常额外带 `exception:` 与 `stack:`。
- 写失败：`ModLogger` 置 `_broken` 并停止写，不抛给 mod，不递归。
- 不记录设备序列号、用户目录等敏感信息（路径是应用私有目录，属于必要诊断）。

## 9. 与 Bootstrap 的契约

Bootstrap（注入进 `StardewValley.dll` 的 IL）只做四件事：

1. `File.Exists(<Mods>/ModFramework.Runtime.dll)`;
2. `Assembly.LoadFrom(<Mods>/ModFramework.Runtime.dll)`;
3. `asm.GetType("ModFramework.Runtime.Loader")` + `GetMethod("Start")`（**无参**）；
4. `Invoke(null, null)`，返回非 null 就抛异常（被自己的 catch 吞掉）。

因此 `Loader` 上**不能**有第二个叫 `Start` 的重载（`Type.GetMethod("Start")` 会抛
`AmbiguousMatchException`）。测试用的入口叫 `Run(string)`。

## 10. 扩展点（留给后续阶段，不在本阶段实现）

- `IModContext` 以后可以加 Events/Content/Data 子接口——但**现在不加空接口**；
- `Loader.Run(string)` 已经是"可指定根目录"的形态，方便将来做测试/管理器；
- 状态机与错误码是稳定的，UI 可以直接消费报告文本或未来的结构化输出。

## 11. 变更记录

| 日期 | 变更 |
| --- | --- |
| 2026-11-05 | 初版：Phase 1 最小 Loader 的设计、时序、状态机、隔离矩阵 |
| 2026-11-05 | Phase 1.1：裸 DLL 不执行、三态版本门禁、加载前程序集预检、日志降级进报告 |
| 2026-11-05 | Phase 1.1.1：预检不动点（依赖优先、冲突重算、事件按最终归类过滤）、内容相等改为逐字节判定、宿主匹配 Fail-Closed、PE/CLI 逐层边界、框架版本 Unknown/Invalid 分离、报告与日志脱敏 |
| 2026-11-06 | Phase 1.1.2：预检字节写入私有快照并只从快照 `LoadFrom`（加载前二次校验指纹）、不动点显式 Converged/Exhausted 且未收敛时 Fail-Closed（`MF0208`）、ECMA-335 PublicKey 标志与 Blob 形状校验（`MF0205`） |
