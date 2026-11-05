# Mod Manifest 规范（schemaVersion 1）

- 阶段：Phase 1
- 实现：`Mod/ModFramework.Runtime/ModManifest.cs`（权威）
- 机器可读 schema：`docs/modding/manifest.schema.json`（与实现交叉校验）
- 测试夹具：`Tools/tests/fixtures/manifests/`（25 个用例 + `index.json` 期望表）

> 冲突时以实现为准，并同步修 schema 与本文。

---

## 1. 文件与位置

- 文件名固定为 `manifest.json`，位于 mod 包目录**根**。
- 编码必须是 UTF-8（无 BOM 要求；含无效 UTF-8 字节会被拒绝）。
- 大小上限 **256 KiB**（262,144 字节），超出直接拒绝，不解析。
- 严格 JSON：不允许注释、尾随逗号、`NaN`/`Infinity`、重复 key。
- 未知字段一律**拒绝**（`schemaVersion 1` 的策略：宁可报错，也不让拼写错误被忽略）。
  新增字段 = 提升 `schemaVersion`。

## 2. 目录布局

```text
<Mods 根目录>/                       = ModFramework.Runtime.dll 所在目录
├── ModFramework.Runtime.dll         框架本体（由 Bootstrap 加载）
├── loader.log / loader-report.txt   框架输出（每次运行覆盖）
├── logs/<mod id>.log                每个 mod 自己的日志（追加）
└── ExampleMod.HelloWorld/           = 一个 mod 包（目录名任意）
    ├── manifest.json
    ├── ExampleMod.HelloWorld.dll    入口程序集（entryAssembly）
    └── FixtureDependency.dll        可选：privateAssemblies 里声明的私有依赖
```

发现规则（实现见 `ModDiscovery.cs`）：

| 规则 | 说明 |
| --- | --- |
| 只扫**一层**子目录 | 不递归；`<Mods>/*/manifest.json` |
| 目录名以 `.` 或 `_` 开头 → 忽略 | 隐藏目录、临时目录、框架自用目录 |
| 没有 `manifest.json` → 忽略 | 不会加载"裸 dll" |
| 目录名排序 = 处理顺序 | 枚举顺序不影响结果 |
| 同一 `id` 出现两次（大小写不敏感）→ **两个都拒绝** | 不随机挑一个 |
| 目录名与 `id` 无需一致 | 目录名只是给人看的 |

## 3. 字段

| 字段 | 类型 | 必需 | 约束 |
| --- | --- | --- | --- |
| `schemaVersion` | number | ✅ | 必须为 `1` |
| `id` | string | ✅ | 1–64 字符；首字符 `A-Za-z0-9`；其余 `A-Za-z0-9._-`；不得含 `..`；大小写不敏感唯一 |
| `name` | string | ✅ | 1–128 字符，非空白，无控制字符 |
| `version` | string | ✅ | 严格 SemVer 2.0.0（见 §4） |
| `entryAssembly` | string | ✅ | 纯文件名（禁止 `/` `\` `:` `..`），必须以 `.dll` 结尾；必须在包目录内 |
| `entryType` | string | ✅ | 1–256 字符；字符集 `A-Za-z0-9._+` `` ` ``；必须是完整类型名 |
| `author` | string | — | ≤256 字符 |
| `description` | string | — | ≤2048 字符 |
| `website` | string | — | ≤512 字符；**不会被打开** |
| `gameVersion` | string | — | SemVer 范围（§5），默认 `*` |
| `frameworkVersion` | string | — | SemVer 范围（§5），默认 `*` |
| `dependencies` | array | — | ≤64 项，见 §6 |
| `privateAssemblies` | array | — | ≤32 项，纯 `.dll` 文件名，不得与 `entryAssembly` 相同、不得重复 |

### 3.1 最小示例

```json
{
  "schemaVersion": 1,
  "id": "ExampleMod.HelloWorld",
  "name": "Hello World",
  "version": "1.0.0",
  "entryAssembly": "ExampleMod.HelloWorld.dll",
  "entryType": "ExampleMod.HelloWorld.HelloWorldEntry"
}
```

### 3.2 完整示例

见 `Tools/tests/fixtures/manifests/valid.full.json`。

### 3.3 `privateAssemblies` 为什么必须显式声明

`Assembly.LoadFrom` **不会**去被加载文件所在目录找它的依赖（宿主测试实测），
而 `Directory.GetFiles` 在裁剪后的运行时里不存在，无法枚举。
所以 Loader 只加载 manifest 里列出的私有程序集，顺序无关（两轮 fixpoint）。

不声明会怎样：`Initialize` 里第一次用到该依赖时抛 `FileNotFoundException`，
该 mod 以 `MF0309` 失败——不会影响其他 mod，但也不会"自动修好"。

### 3.3.1 加载的是快照，不是你的文件（Phase 1.1.2）

预检读到的字节会被写成应用私有目录里的**快照**
（`<Mods>/.staging/<run>/<mod id>/<文件名>`，逐字节校验过），`Assembly.LoadFrom` 只拿这个快照。
所以：

- 运行期修改/替换自己包里的 dll **不会**影响已加载的程序集（这是故意的）；
- `Assembly.Location` 指向快照，**不是**你的包目录；取随包数据文件请用
  `IModContext.ModDirectory`（从 Phase 1 起就是契约）；
- 每个随包程序集都必须写进 `privateAssemblies`，否则不会被加载；
- 快照总量上限 256 MiB / 4096 个文件、单文件 64 MiB，超限则该 mod 以 `MF0209` 失败。

### 3.4 程序集身份与冲突策略（Phase 1.1 / 1.1.1）

Loader 在**加载任何 mod 程序集之前**做一次全局预检：只读 `entryAssembly` 与
`privateAssemblies` 的 PE/CLI 元数据（名字、版本、Culture、`Assembly.Flags` 与公钥）和文件内容，
不调用 `Assembly.LoadFrom`、不执行任何 mod 代码。

"身份相同"指 **Name + Version + Culture + 公钥字节** 四项全部一致（Name/Culture 大小写不敏感，
`neutral` 等同空）。`Assembly.Flags` 的 `0x0001` 位决定公钥 blob 是**完整公钥**还是 **Token**，
两者不会被混为一谈。

| 情形 | 结果 |
| --- | --- |
| 声明文件缺失 | 该 mod 拒绝 `MF0201` |
| 文件损坏 / 不是托管程序集 | 该 mod 拒绝 `MF0205` |
| 两个 mod 声明**相同简单名**，但身份或内容任一不同 | **双方都拒绝** `MF0206`，不会"先加载者胜" |
| 两个 mod 声明**完全相同的身份与内容** | 共享：按 `(mod id, 文件名)` 序确定一个代表文件，只加载一次；双方都可初始化 |
| 与**宿主已加载**的程序集相同身份、且宿主文件可读、内容逐字节相同 | 复用宿主实例，不加载包内副本 |
| 与宿主已加载的程序集相同身份，但宿主文件不可读 / 内容无法证明相同 | **拒绝** `MF0207`（Phase 1.1 曾"复用 + 警告"，1.1.1 改为 Fail-Closed） |
| 与宿主同名但身份不同（含公钥/Token 不同） | 拒绝 `MF0207` |
| 同一身份在 AppDomain 里有多个候选 | 拒绝 `MF0207`（不做"取第一个"） |

补充约定：

- 冲突判定是**确定性**的：与目录枚举顺序无关，结果只取决于 mod id 与声明的文件名排序。
- **内容相同只由逐字节比较决定**。SHA-256 只用作"快速排除"：摘要不同 ⇒ 内容必然不同；
  摘要相同 **不能** 推出内容相同。因此 FNV-1a 64 / CRC 之类的弱摘要**永远不是**共享许可。
  SHA-256 不可用时直接走完整字节比较；字节比较也无法完成（IO 错误、短读、文件在比较期间变化）
  时 **Fail-Closed**：拒绝共享。逐字节比较使用固定缓冲区，不会把多个 DLL 长期留在内存里。
- 被拒绝 mod 的**必需**下游按依赖失败规则阻断（`MF0404`），无关 mod 继续运行。
- **依赖失败优先于冲突归类**：一个既"依赖失败"又"处于冲突组"的 mod 会记为 `MF0404`
  （`kind=dependency-blocked`），而它的冲突声明会被重新计算——这样它不会把本来健康的
  伙伴一起拖下水。报告里的 `kind=` 区分 `root-cause` / `assembly-conflict` /
  `host-conflict` / `dependency-blocked`。
- 被拒绝只影响该 mod；报告里记录冲突键、涉及 mod id、角色（entry/private）与身份文本，
  **不写**用户的绝对路径（Phase 1.1.1 起 `locate=` / `mods-root=` / `dir=` 只保留路径末段
  或包内相对名，异常消息里的绝对路径被脱敏为 `<path>`）。

因此 mod 作者必须假设：**同名程序集只能有一个版本在场**。需要多个版本时，请改程序集名。

## 4. 版本语义（SemVer 2.0.0 子集）

- 必须是 `major.minor.patch` 三段，缺一段即拒绝（`1.0` ✗、`1.0.0.0` ✗）。
- 数字段不允许前导零（`01.0.0` ✗）。
- 允许 `-prerelease`（点分段；数字段同样禁止前导零）与 `+build`。
- 比较按 SemVer §11：`1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-beta.2 < 1.0.0-beta.11 < 1.0.0`。
- **build metadata 不参与比较**（`1.0.0+a == 1.0.0+b`）。
- 自写实现（`SemVersion.cs`）因为运行时没有 `Version.TryParse`；测试覆盖 20+ 边界。

## 5. 版本范围语法

- `*` = 任意版本。
- 否则是**空格或逗号分隔的比较器**，全部必须成立（AND）。
- 比较器：`=X`、`>X`、`>=X`、`<X`、`<=X`、`^X`、`~X`、裸 `X`（等价 `=X`）。
- `^1.2.3` → `>=1.2.3 <2.0.0`；`^0.2.3` → `>=0.2.3 <0.3.0`；`^0.0.3` → `>=0.0.3 <0.0.4`。
- `~1.2.3` → `>=1.2.3 <1.3.0`。
- **不支持** `||`（析取）与 `1.6.x`（通配）——本阶段刻意不实现，`MF0117` 拒绝。

游戏版本的来源：`Assembly.GetEntryAssembly()` 或进程内名为 `StardewValley` 的程序集，
取其 `major.minor.build`（`1.6.15.24354` → `1.6.15`，revision 丢弃）。
`loader-report.txt` 用两行标注来源，便于诊断：

```
game-version=1.6.15
game-version-state=known          # known / unknown / invalid
game-version-source=appdomain     # entry-assembly / appdomain / override / none
```

**版本门禁是 Fail-Closed（Phase 1.1 起）**：

| manifest 条件 | 实际游戏版本 | 结果 |
| --- | --- | --- |
| `gameVersion` 为 `"*"`（或缺省） | 任意 | 放行，不要求版本确认 |
| 显式范围 | 已知、可解析、满足范围 | 放行 |
| 显式范围 | 已知、可解析、不满足范围 | 拒绝 `MF0501` |
| 显式范围 | **未知**（找不到 `StardewValley` 程序集） | 拒绝 `MF0503` |
| 显式范围 | 检测到但**不是合法 SemVer** | 拒绝 `MF0504` |

`frameworkVersion` 同理：`"*"` 不约束；显式范围遇到运行时框架版本无法解析 → `MF0505`，
不满足 → `MF0502`。

拒绝发生在这三件事**之前**：入口 DLL 的 `Assembly.LoadFrom`、构造函数、`Initialize`。
也就是说被版本门禁拒绝的 mod，其入口程序集不会被加载、marker 文件不会出现。

> 只有明确写 `"*"` 的 mod 才不要求游戏版本确认。想要"装了就该跑"的 mod 请写 `"*"`，
> 不要在版本未知时依赖旧的放行行为。

## 6. 依赖

```json
"dependencies": [
  { "id": "ExampleMod.Core", "version": ">=1.0.0 <2.0.0" },
  { "id": "ExampleMod.Optional", "version": "*", "optional": true }
]
```

| 情形 | 结果 |
| --- | --- |
| 必需依赖缺失 | 该 mod `Failed(MF0401)`；依赖它的 mod 也失败 |
| 依赖版本不满足范围 | 该 mod `Failed(MF0402)` |
| 可选依赖缺失 | 继续，报告里记一条 warning |
| 可选依赖存在 | **参与排序**（先初始化） |
| 循环依赖 | 环上所有 mod `Failed(MF0403)`，并打印 `a -> b -> a` |
| 依赖的 mod 初始化失败 | 下游 `Failed(MF0404)`，不执行其入口 |
| 自依赖 / 重复声明 | 直接拒绝（`MF0115` / `MF0114`） |

排序算法：Kahn 拓扑排序，就绪集合按 `id` 的序号排序（`StringComparer.Ordinal`）。
因此**同一组输入无论文件系统枚举顺序如何，加载顺序完全一致**（有测试）。

## 7. 入口契约

`entryType` 指向的类型必须同时满足：

1. 在入口程序集里存在（精确名字，`GetType(name, false, false)`）；
2. 是 `class`（不是接口/结构）；
3. 是 `public`（或嵌套 public）；
4. 不是 `abstract`；
5. 不是泛型类型；
6. 实现**本框架这份** `ModFramework.Runtime.IModEntry`；
7. 有 public 无参构造函数。

任何一条不满足都会在实例化**之前**被拒绝，并给出对应错误码。
Loader 不会"扫描程序集挑第一个类"。

`Initialize(IModContext)` 抛异常 → 该 mod `Failed(MF0309)`，异常类型/消息/堆栈写进
`logs/<id>.log`，其他无关 mod 继续。

## 8. 错误码

错误码是稳定契约（测试与未来的 UI 都会用），只增不改。

| 码 | 含义 |
| --- | --- |
| `MF0101` | 找不到 `manifest.json` |
| `MF0102` | manifest 读不出来（IO 错误） |
| `MF0103` | manifest 超过 256 KiB |
| `MF0104` | 不是合法 JSON |
| `MF0105` | 不是合法 UTF-8 |
| `MF0106` | 对象里有重复 key |
| `MF0107` | 不支持的 `schemaVersion` |
| `MF0108` | 缺少必需字段 |
| `MF0109` | 必需字段为空 |
| `MF0110` | 字段类型错误 / 根不是对象 |
| `MF0111` | 未知字段 |
| `MF0112` | 依赖超过 64 条 |
| `MF0113` | `id` 非法 |
| `MF0114` | 依赖重复声明 |
| `MF0115` | 依赖自己 |
| `MF0116` | 版本不是合法 SemVer |
| `MF0117` | 版本范围非法 |
| `MF0118` | `entryAssembly` / `privateAssemblies` 文件名非法 |
| `MF0119` | 入口路径解析到包外 |
| `MF0120` | mod id 重复（两个包都被拒绝） |
| `MF0121` | JSON 嵌套过深 |
| `MF0122` | 私有程序集重复声明 |
| `MF0123` | 私有程序集超过 32 条 |
| `MF0201` | 入口/私有程序集文件不存在 |
| `MF0202` | 程序集加载失败 |
| `MF0203` | 文件里的程序集名与文件名不一致 |
| `MF0204` | 契约类型来自另一份 `ModFramework.Runtime`（类型身份不一致） |
| `MF0205` | 声明文件损坏 / 不是有效托管程序集（预检读不出身份） |
| `MF0206` | 两个 mod 声明了同名但身份或内容不同的程序集（双方都拒绝） |
| `MF0207` | 包内程序集与宿主已加载的同名程序集身份冲突，或宿主内容无法证明相同 / 候选不唯一 |
| `MF0301` | `entryType` 不存在 |
| `MF0302` | `entryType` 不是 public |
| `MF0303` | `entryType` 是 abstract |
| `MF0304` | `entryType` 不是 class |
| `MF0305` | 没实现 `IModEntry` |
| `MF0306` | `entryType` 是泛型 |
| `MF0307` | 没有 public 无参构造函数 |
| `MF0308` | 构造函数抛异常 |
| `MF0309` | `Initialize` 抛异常 |
| `MF0401` | 必需依赖缺失 |
| `MF0402` | 依赖版本不满足 |
| `MF0403` | 循环依赖 |
| `MF0404` | 依赖未初始化（下游被阻止） |
| `MF0501` | 游戏版本不支持（已知版本不满足范围） |
| `MF0502` | 框架版本不支持（已知版本不满足范围） |
| `MF0503` | 游戏版本无法确定，显式范围无法校验 → 拒绝 |
| `MF0504` | 检测到的游戏版本不是合法 SemVer → 拒绝 |
| `MF0505` | 运行时框架版本无法解析，显式范围无法校验 → 拒绝 |
| `MF0506` | 运行时框架版本**无法确定**（不是 `0.0.0`），显式范围无法校验 → 拒绝 |
| `MF0901` | Loader 被重入 |
| `MF0902` | Mods 根目录不存在 |
| `MF0903` | Mods 根目录无法枚举 |
| `MF0904` | 日志写入失败（报告里体现为 `log-status=degraded`，不改变 mod 状态） |
| `MF0905` | `loader-report.txt` 与 `loader.log` 都写不出来（`Run` 返回该诊断串） |

## 9. 安全边界（必须让 mod 作者知道）

- `.NET` mod dll **在游戏进程内以完整权限执行代码**。manifest 校验只保证"包结构合法"，
  **不构成任何安全隔离**。只安装你信任的 mod。
- manifest 里的 `website` 等字段**永远不会被执行或访问**。
- `entryAssembly` / `privateAssemblies` 被限制为"包目录内的纯文件名"，
  所以 manifest 无法指向 `../` 或绝对路径的 dll。
- **只有带 manifest 的包目录才会被执行**（Phase 1.1 起）：Mods 根目录下的裸 DLL
  （包括历史遗留的 `MonoRuntimeProbe.dll`）不会被发现、加载或调用。报告里固定写
  `probe=not-run-by-loader`；需要诊断探针时按 `Mod/MonoRuntimeProbe/README.md` 显式运行。
- **版本门禁是 Fail-Closed**：除 `"*"` 外，游戏/框架版本无法确认时 mod 被拒绝，
  而不是"先跑了再说"。
- **程序集身份在第一次加载前检查**：同名不同身份/不同内容的私有库会被确定性拒绝，
  不会出现"先加载者胜"的静默覆盖。
- **日志降级可见**：`logs/<id>.log` 写不进去时，`loader-report.txt` 会记 `log-status=degraded`
  与异常类型（不含路径），mod 自身的 `Initialized` 状态不变。
