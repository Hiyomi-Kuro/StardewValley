# 反编译与静态分析报告（Phase 0）

- 目标：`com.chucklefish.stardewvalley` 1.6.15.0 (239)
- 分析对象：`assemblies/*.blob` 内 65 个托管程序集、`classes*.dex`、`arm64-v8a` 原生库
- 工具：REA（JADX 0.7.1 / managed metadata / ELF symbol+relocation 级）、Python `dnfile` 0.18.0、自写解析器
- 时间：2026-11-04
- 限制：本机**无 Ghidra**（未配置 `GHIDRA_INSTALL_DIR`）、**无 .NET SDK**（无 `ilspycmd`）。因此：
  - 托管侧给到 **IL 指令级**（不是 C# 源码级）；
  - 原生侧给到 **符号 / 字符串 / 重定位级**（不是反编译级）。

---

## 1. 程序集容器

`assemblies.blob` 是 .NET for Android 的 **AssemblyStore v1**：

```
offset 0     : 'XABA'  magic (0x41424158)
offset 4     : u32 version = 1
offset 8     : u32 entry_count = 58
offset 12    : u32 total_count = 65          ; 两个 store 的条目总数
offset 20..  : entry[58]，每项 24 字节
               { data_offset, data_size, debug_off, debug_size, cfg_off, cfg_size }
payload      : 'XALZ' + u32 descriptor_index + u32 uncompressed_length + LZ4 block
```

- 全部 58 个条目成功解压，**每个都是合法 PE**（`MZ` + `PE\0\0`）。
- `assemblies.manifest` 的 `Hash 32/64` 列与标准 xxHash 不符（实现已用官方测试向量验证正确），算法 **未知**；提取正确性由"PE 合法 + 程序集身份自洽 + 版本与 TFM 一致"确认。
- 第二个 store（`assemblies.arm64_v8a.blob`，7 条目）承载架构相关程序集，`System.Private.CoreLib` 在其中。

---

## 2. 程序集清单（节选，全部 65 见 `.tmp/phase0/extract/blob000/_identity2.tsv`）

| Assembly | Version | TFM | 备注 |
| --- | --- | --- | --- |
| StardewValley | 1.6.15.24354 | net8.0 | 游戏主体，9.28 MB |
| StardewValley.GameData | 1.6.15.24354 | net8.0 | 1.6 的数据模型 |
| MonoGame.Framework | 1.0.0.0 | net8.0 | 2.0 MB |
| xTile | 1.0.0.0 | net45 | 地图/瓦片 |
| Mono.Android | 0.0.0.0 | net8.0 | JNI 绑定 |
| Java.Interop | 7.0.0.0 | net7.0 | |
| mscorlib / System / System.Core | 4.0.0.0 | net8.0 | **门面（facade）** |
| System.Private.CoreLib | 8.0.0.0 | net8.0 | 架构 blob |
| Lidgren.Network | 2012.1.7.0 | net45 | 多人网络 |
| BmFont | 0.0.0.0 | net48 | 位图字体 |
| System.Data.HashFunction.xxHash | 1.8.2.2 | net45 | 运行时自带的 xxHash |

> 注意：`System.Data.HashFunction.*` 出现在 **运行时** 程序集里，说明游戏或某个依赖在运行时用了它（不是构建期工具）。这与 `assemblies.manifest` 里 hash 列的算法问题无关，但值得在后续审计中确认是谁在用。

---

## 3. `StardewValley.dll` 结构

| 项 | 值 |
| --- | --- |
| SHA-256 | `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047` |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` |
| PE | PE32 / IL-only / AnyCPU |
| TypeDef / MethodDef / Field | 2,867 / 30,067 / 18,859 |
| ImplMap（P/Invoke） | 5,192 |
| AssemblyRef | 28 |
| CustomAttribute | 4,656 |

**P/Invoke 5,192 条**是本项目的一个重要事实：游戏大量依赖原生互操作（SDL/OpenAL/自研库）。对 Mod Framework 的含义：

- 做 native hook 的诱因存在，但**风险极高**（任务书 §7 禁止未验证的原生覆盖）；
- 反射/事件 API 应优先走托管面，原生面只在确有必要时触及。

---

## 4. 关键类型

| 类型 | 方法/字段 | 作用 |
| --- | --- | --- |
| `StardewValley.MainActivity` | 43 / 8 | Android Activity 的托管实现（Java peer 转发到此） |
| `StardewValley.Game1` | 558 / 531 | 游戏主类 |
| `StardewValley.Program` | 9 / 15 | 日志 / 异常处理（`WriteLog`, `AppendDiagnostics`, `handleException`） |
| `StardewValley.InstanceGame` | — | 由 `MainActivity.OnCreatePartTwo` `newobj` 创建 |
| `StardewValley.Mobile.MobileDisplay` | — | `SetupDisplaySettings()` |
| `Microsoft.Xna.Framework.GameComponent` / `GameTime` | — | MonoGame 基类，`Run()` 启动主循环 |

> 口径说明（Phase 0.5）：上表的"方法/字段"是**按类型自身声明**统计的；
> 探针 T07 报的 `Game1 576 方法 / 532 字段` 是**反射枚举**（`GetMethods/GetFields` +
> `NonPublic|Instance|Static`，包含继承成员）。两者不是同一口径，**不要互相引用或当成矛盾**。
> 差异逐项归属未复核。

---

## 5. 启动调用链（IL 级）

### 5.1 Java 侧

`com.chucklefish.stardewvalley.MainActivity`（dex，20 个方法）静态字段 `__md_methods`：

```
n_onCreate:(Landroid/os/Bundle;)V:GetOnCreate_Landroid_os_Bundle_Handler
n_onResume:()V:GetOnResumeHandler
n_onStop:()V:GetOnStopHandler
n_onDestroy:()V:GetOnDestroyHandler
n_onWindowFocusChanged:(Z)V:GetOnWindowFocusChanged_ZHandler
n_onPause:()V:GetOnPauseHandler
n_onRequestPermissionsResult:(I[Ljava/lang/String;[I)V:GetOnRequestPermissionsResult_…
n_onActivityResult:(IILandroid/content/Intent;)V:GetOnActivityResult_…
```

→ 所有生命周期方法都是 **JNI 转发**，真实逻辑在托管 `StardewValley.MainActivity`。

### 5.2 托管 `MainActivity.OnCreate(Bundle)` — token `0x06002f7e`，106 字节 IL

```
00  ldarg.0
01  stsfld    <静态 MainActivity 引用>
06  ldstr     "…"                       ; 日志前缀
11  call      Program/日志方法
16  ldarg.0
17  ldc.i4.1
18  call      Activity.RequestWindowFeature(1)
23  pop
24  call      BuildVersionCodes.get_SdkInt
29  ldc.i4.s  28
31  blt.s     → 50
33  ldarg.0 ; callvirt Window.Features ; callvirt Window.Attributes
44  ldc.i4.1 ; callvirt set_LayoutInDisplayCutoutMode
50  ldarg.0 ; callvirt Window ; ldc.i4 1024 ; ldc.i4 1024 ; callvirt SetFlags
92  ldarg.0 ; ldarg.1 ; call  Activity.OnCreate(Bundle)      ; ← 基类
99  ldarg.0 ; call  MainActivity.CheckAppPermissions()      ; token 0x06002f93
105 ret
```

### 5.3 托管 `MainActivity.OnCreatePartTwo()` — token `0x06002f7f`，84 字节 IL

```
00  ldstr     "…"
05  call      Program/日志方法
10  call      Mobile.MobileDisplay.SetupDisplaySettings()   ; 0x060065d0
15  ldarg.0 ; call  MainActivity.SetPaddingForMenus()        ; 0x06002f87
21  newobj    StardewValley.InstanceGame::.ctor              ; 0x0600390f  ← 游戏对象
26  stloc.0
27  ldarg.0 ; ldloc.0 ; callvirt  <set content view>          ; 0x0a00007d
34  ldtoken   <类型 0x0100009e>
39  call      <GetService>
44  callvirt  <…>                                            ; 0x0a00077f
49  castclass <类型 0x0100009e>
54  callvirt  Activity.SetContentView(View)                  ; 0x0a000780
59  ldloc.0 ; stsfld  <静态游戏引用>
65  ldarg.0 ; ldloc.0 ; ldfld ; stfld                       ; 缓存 Window/View
77  ldloc.0 ; callvirt <Game>.Run()                          ; 0x0a000781  ← 主循环
83  ret
```

### 5.4 结论

- 启动的唯一"闸门"是 `OnCreatePartTwo` 中的 `newobj InstanceGame::.ctor`。
- 该方法是 `private` + 无参数 + 无返回值，是本项目选定的注入点（见 `adr-0001`）。
- `Program` 只承担日志与全局异常处理，**不是**入口点。

---

## 6. 异常处理面（对 Mod Framework 有用）

`StardewValley.Program`：

| 方法 | token | 作用 |
| --- | --- | --- |
| `WriteLog` | `0x06003f70` | 写日志到文件 |
| `AppendDiagnostics` | `0x06003f71` | 追加诊断信息（含堆栈） |
| `handleException` | `0x06003f72` | 全局异常处理，写日志 + 弹窗 |

→ Mod Framework 的 `Monitor`/`Log` 应复用 `Program.WriteLog` 的落盘位置，保证日志集中；异常上报可参考 `handleException` 的格式。

---

## 7. 未分析 / 下一步

| 缺口 | 原因 | 下一步 |
| --- | --- | --- |
| 游戏 30,067 个方法的 C# 级反编译 | 无 ilspycmd / 无 SDK | 安装 .NET SDK 后 `dotnet tool install -g ilspycmd`，批量导出到 `.tmp/phase0/decompiled/` |
| `Game1` 的 558 个方法语义 | 同上 | 同上，重点看 `Update`/`Draw`/`DayStarted` 相关方法 |
| 原生库的反编译级分析 | 无 Ghidra | 配置 `GHIDRA_INSTALL_DIR` 后分析 `libmonodroid.so` 的程序集存储读取路径 |
| `assemblies.manifest` hash 算法 | 未知 | 对照 `dotnet/android` 源码中的 `AssemblyStoreManifest` 生成逻辑 |
| AOT 镜像内部方法表 | 未解析 | 解析 `libaot-*.so` 的 `mono_aot_module_info` 结构，得到"哪些方法真正被 AOT" |

---

## 8. 复现

```powershell
# 1. 提取程序集（纯 Python，无外部依赖）
python .tmp/phase0/work/extract_assemblies.py

# 2. 身份表（dnfile）
python .tmp/phase0/work/dn_identity.py

# 3. 关键方法 IL / 调用目标
#    REA: inspect_managed_members(StardewValley.dll, type_names=[MainActivity, Program])
#    然后 inspect_analysis_view(collection="methods")

# 4. Java 侧
#    REA: inspect_android_class(.tmp/phase0/work/dexonly.apk, "com.chucklefish.stardewvalley.MainActivity")
```
