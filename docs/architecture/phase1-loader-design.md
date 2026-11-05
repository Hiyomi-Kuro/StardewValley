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
| `ModState.cs` | 生命周期状态机 + 每个 mod 的记录 | ✅ 单元 |
| `ModLogger.cs` | 每 mod 一个日志文件；写失败自我熔断 | ✅ 宿主 |
| `ModContext.cs` | `IModContext` 实现（只读视图） | ✅ 宿主 |
| `Contracts.cs` | `IModEntry` / `IModContext` / `IModLogger` / `ModLogLevel` | — |
| `ErrorCodes.cs` | 稳定错误码 + 文案 | ✅ 单元 |

依赖方向是单向的：`Loader` → 其它；其它之间只有 `ModManifest`/`SemVersion` 的引用。

---

## 3. 加载时序

```
Loader.Run(modsRoot)
├── 重入保护（静态标志）                     → MF0901
├── 同一 root 已初始化过？                    → 直接 null + result=already-initialized
├── 探测游戏/框架版本                        → game-version=<x|unknown>
├── ModDiscovery.Discover
│   ├── Directory.GetDirectories(root) 排序
│   ├── 过滤 . / _ 前缀、无 manifest.json
│   └── ManifestReader.Load 逐个校验          → 失败记录 MF01xx，不影响别人
├── DependencyResolver.Resolve
│   ├── 缺失/版本不符 → 该 mod MF0401/MF0402
│   ├── DFS 找环 → 环上 MF0403
│   └── Kahn 拓扑排序（就绪集按 id 排序）
├── 逐个（按拓扑序）：
│   ├── 依赖问题？            → Failed(码)
│   ├── 游戏/框架版本不符？    → Failed(MF0501/MF0502)
│   ├── 依赖未初始化？        → Failed(MF0404)
│   └── LoadAndInitialize：
│       ├── 入口文件存在/在包内            → MF0201/MF0119
│       ├── privateAssemblies 逐个 LoadFrom → MF0202
│       ├── 入口程序集 LoadFrom            → MF0202
│       ├── 程序集名 == 文件名？            → MF0203
│       ├── entryType 存在且合法            → MF0301..MF0307
│       ├── Activator.CreateInstance        → MF0308
│       ├── 是 IModEntry（类型身份）        → MF0204
│       └── Initialize(context)             → MF0309
└── 写 loader-report.txt + loader.log；返回 null
```

`Run` 的返回值只表达 **Loader 级**结果：

| 返回值 | 含义 |
| --- | --- |
| `null` | 扫描跑完了。个别 mod 失败也返回 `null`（失败在报告与日志里） |
| `"MF0902: ..."` | Mods 根目录不存在 |
| `"loader-report: loader-crashed: ..."` | Loader 自己抛了未捕获异常（已写报告） |

Bootstrap 把非 null 当失败抛异常，而它自己的 catch 会吞掉——所以**报告必须落盘**。

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
