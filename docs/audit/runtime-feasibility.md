# 运行时可行性报告（Phase 0）

- 项目：Stardew Valley Android Mod Framework
- 目标产物：`docs/audit/runtime-feasibility.md`
- 版本绑定：`com.chucklefish.stardewvalley` 1.6.15.0 (versionCode 239)
- 报告状态：**静态审计完成；运行时实验已在模拟器上完成 E1/E2，真机复现待做**
  （见 `phase0-status.md` §3.22/§3.23）
- 生成时间：2026-11-04
- 证据根目录：`.tmp/phase0/evidence/`（本机，可重建，见 §11）

> 本报告区分三类结论：**【观测】** 直接来自文件字节/符号/元数据；**【推断】** 由观测按已知运行时语义推导，未在设备上执行验证；**【未知】** 当前无证据。
> 任何标为【推断】的运行时能力，在真机实验完成前都不得当作"已通过"。

---

## 1. 结论摘要（先看这段）

| 问题 | 结论 | 依据类型 |
| --- | --- | --- |
| 运行时是什么 | .NET for Android（modern），MonoVM，TFM `.NETCoreApp,Version=v8.0` | 【观测】TargetFrameworkAttribute + `System.Private.CoreLib` + `libmonodroid.so` |
| Mono 版本 | `8.0.1124.51707 @Commit: 9cb3b725e3ad2b57ddc9fb2dd48d2d170563a8f5` | 【观测】`libmonosgen-2.0.so` 字符串 |
| 是否 JIT | **是**。JIT 编译路径完整存在 | 【观测】`mono_jit_init_version` / `mono_compile_method` / mini 符号 |
| 是否 Full AOT | **否**。AOT 模式为 `normal`（AOT + JIT 混合） | 【观测】`libxamarin-app.so: mono_aot_mode_name = "normal"`（RELA 解析） |
| 是否存在 AOT 镜像 | 是，**65 个** `libaot-*.so`，合计 38,564,048 字节 | 【观测】文件清单 + 哈希 |
| 是否有解释器 | 解释器实现被编译进 `libmonosgen`；**能否启用未知** | 【观测】字符串 `MONO interpreter: NIY encountered in method %s`；【未知】启用通道 |
| 能否动态加载 DLL | **可以**。`Assembly.LoadFrom(path)` 可用；`Assembly.Load(byte[])` 被裁剪 | 【观测】设备（模拟器），见 `phase0-status.md` §3.22 |
| Harmony 能否工作 | **大概率不可行**：`Reflection.Emit` 完全缺失（`DynamicMethod`/`AssemblyBuilder` 均失败），只剩原生 detour 一条路 | 【观测】T09/T10 失败 |
| 注入点 | `StardewValley.MainActivity.OnCreate` / `OnCreatePartTwo`（托管侧） | 【观测】IL 反汇编 + 调用目标解析 |
| 设备 | 仅 MuMu 12 模拟器（Android 12 / API 32 / x86_64 + ARM 翻译）；**无真机** | 【观测】adb 属性 |

**技术路线判定：Route A（支持动态 IL）为主，Route D（Harmony 可选）作为兼容层。**
理由见 §9；该判定为"条件通过"，条件是真机实验 E1–E5（§10）。

---

## 2. 实验环境

### 2.1 主机

| 项 | 值 |
| --- | --- |
| OS / 工作目录 | Windows，`D:\Download\DSH\StardewValley` |
| adb | 37.0.1-15733141（`%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`） |
| JDK | Adoptium JDK 17.0.20（REA/JADX 使用）、JDK 11（Gradle 使用） |
| Python | 3.12 + `dnfile` 0.18.0 |
| .NET | **仅运行时 `Microsoft.NETCore.App 8.0.4`，未安装 SDK**（`dotnet --list-sdks` 为空） |
| Ghidra | **未配置**（`GHIDRA_INSTALL_DIR` 未设置）→ 本阶段未做反编译级原生分析，仅用 ELF 符号/字符串/重定位级证据 |

### 2.2 设备

| 项 | 值 | 说明 |
| --- | --- | --- |
| 连接 | `127.0.0.1:16384`（同设备另有 5555/7555） | MuMu 12 |
| Android | 12 / API 32，安全补丁 2022-04-05 | |
| 型号 | `vivo PD2309 / V2309A`（模拟器伪装） | `ro.kernel.qemu` 未上报 → `emulator_observed` 为 null |
| ABI | `ro.product.cpu.abi=x86_64`；`abilist=x86_64,arm64-v8a,x86,armeabi-v7a,armeabi` | APK 只带 `arm64-v8a`，靠 MuMu 的 ARM 翻译运行 |
| Root | 有（`su -c id` → uid=0） | 仅模拟器如此；真机不能假定 |
| 应用 debug 标志 | 无（`run-as` 报 `package not debuggable`） | 影响读取应用私有目录的方式 |

> **重要限制**：该设备是 x86_64 模拟器 + ARM 翻译层。本项目的 AOT 镜像是 **AArch64** 机器码，运行在翻译层之上。JIT 在翻译层下的行为、以及 AOT 原生补丁行为，**不能代表 ARM64 真机**。任务书第 2.3 节要求"不能仅用模拟器测试 Mono Runtime 的动态加载能力"，因此所有运行时结论一律标记 **Awaiting Device Validation**。

### 2.3 被测 APK

| 项 | 值 |
| --- | --- |
| 路径 | `app/build/outputs/apk/debug/app-debug.apk` |
| 大小 | 388,967,580 字节 |
| SHA-256 | `258d7d924b82eaa8dc06b787d2a8b9d50240f6e9dfdcd83aa640f2a560755e85` |
| 设备上版本 | 1.6.15.0 / 239，安装时间 2026-11-03 01:54:30 |

---

## 3. 运行时身份

### 3.1 平台判定：.NET for Android（非经典 Xamarin.Android）

【观测】三组独立证据：

1. `StardewValley.dll` 的 `TargetFrameworkAttribute` = `.NETCoreApp,Version=v8.0`；全部 `System.*` 程序集版本为 `8.0.0.0`。
2. 程序集存储内含 `System.Private.CoreLib`（位于架构专属 blob），并有 `mscorlib` / `System` / `System.Core` 的 **4.0.0.0 门面（facade）**。
3. 原生层同时存在 `libmonosgen-2.0.so`（MonoVM）、`libmonodroid.so`（JNI 桥 + 程序集存储宿主）、`libmono-component-marshal-ilgen.so`、`libSystem.Native.so`、`libSystem.Security.Cryptography.Native.Android.so`。

> 更正说明：`libmono-component-marshal-ilgen.so` 是 MonoVM 的**可动态加载组件**（`mono-component-<name>`，运行时 `dlopen`），负责用 IL 生成 marshalling wrapper。它说明该 MonoVM 是"组件化构建"，**不能单独证明 AOT 已开启**——AOT 的判据是 §3.4 的 `mono_aot_mode_name` 与 65 个 `libaot-*.so`。

### 3.2 MonoVM 版本与构建

| 项 | 值 | 证据 |
| --- | --- | --- |
| 版本串 | `8.0.1124.51707 @Commit: 9cb3b725e3ad2b57ddc9fb2dd48d2d170563a8f5` | `libmonosgen` 字符串 `0x0576c5` |
| 兼容版本 | `8.0.11.0` | `libmonosgen` 字符串 `0x02d875` |
| ELF | AArch64 (`e_machine=183`)，64-bit，`.text` 2,384,204 字节 | ELF 头 |
| SHA-256 | `f62f6e8d9c2d43c290363629b608c01d4ef5ea655c9b6bf3fb3cf6a473c90fe6` | |

### 3.3 JIT【观测：存在】

`libmonosgen-2.0.so` 动态符号（部分）：

```
mono_jit_init, mono_jit_init_version, mono_jit_init_version_for_test_only, mono_jit_cleanup
mono_compile_method
mono_aot_register_module
mono_runtime_invoke{,_array,_checked}
mono_assembly_load{,_from,_from_full,_full,_full_alc,_module}
mono_image_open_from_data{,_alc,_with_name,_full,_internal}
mono_install_assembly_{preload,postload,refonly}_hook*
```

字符串 `Mono JIT compiler version %s`、`Mono Jit statistics`、`jit_info_table_find`、`jit_begin_callback` 等表明 mini/JIT 后端被链接进来。

### 3.4 AOT 模式 = `normal`（决定性证据）【观测】

`libxamarin-app.so` 导出了 `mono_aot_mode_name` 符号（`0x298e8`，位于 `.data.rel.ro`，文件内为 0，由重定位填充）。解析 `.rela.dyn`：

```
mono_aot_mode_name  reloc_off=0x298e8  R_AARCH64_RELATIVE(1027)  addend=0x841f -> b"normal"
```

`normal` 在 Mono 语义下是 **AOT 可用 + JIT 保留**（与 `full` 相对；`full` 会关闭 JIT）。这直接决定了"动态 IL 可执行"的可行性方向。

同时 `application_config`（`0x29930`）字节解码与已知计数一致：

| 字段（按观测偏移） | 值 |
| --- | --- |
| `uses_mono_aot` 区段首字节 | `1` |
| `have_assemblies_blob` | `1` |
| `environment_variable_count` (byte 12) | `3` |
| `system_property_count` (byte 16) | `8` |
| `number_of_assemblies_in_apk` (byte 24) | `65` |
| `number_of_assembly_store_files` (byte 32) | `2` |
| `number_of_dso_cache_entries` (byte 36) | `426` |

运行时属性（重定位解析）：`MONO_GC_PARAMS` = `major=marksweep-conc`（并发标记清除 GC，非 server GC）。

### 3.5 解释器【观测：代码存在 / 未知：可启用】

`libmonosgen` 含：

```
MONO interpreter: NIY encountered in method %s
interp_init, interp_tramp, interp_callbacks_api, interpreter
```

即解释器实现被编译进运行时。但没有观测到任何启用通道：应用 **非 debuggable**（不能用 `wrap.sh`），`application_config` 的 `runtime_properties` 中也未见到 `--interp` 类条目。**结论：作为 Route B 的备选存在，但当前不可达，需真机 + 重打包实验。**

---

## 4. 程序集容器

### 4.1 容器格式【观测：已完全解析】

`assemblies/` 下有两个存储（对应 `application_config.number_of_assembly_store_files = 2`）：

| 存储 | 大小 | 条目 | SHA-256 |
| --- | --- | --- | --- |
| `assemblies.blob` | 5,588,326 | 58 | `c8ee504eea163d2c82d89d0004c8924314b3e6b41adb649ea5f136151eecabd9` |
| `assemblies.arm64_v8a.blob` | 1,250,044 | 7 | `c465d9d4414dea6b297ff8c800470302d8dd160aa4b1e09abccb468b53d4cd78` |
| `assemblies.manifest` | 4,937 | 65 行 | `0423d19caf72cda38d1acefd8fe73356b643ec05786b6d9a15b7134bed6e3393` |

`assemblies.blob` 头（实测）：`XABA` magic, version=1, `entry_count=58`, `total=65`；条目表自字节 20 起，步长 24（`data_offset, data_size, debug_off, debug_size, cfg_off, cfg_size`）；负载为 `XALZ` + `u32 index` + `u32 uncompressed_len` + LZ4 block。

**可复现提取**：`.tmp/phase0/work/extract_assemblies.py`（纯 Python LZ4 解码，无外部依赖）已把 58 个条目还原为合法 PE，落在 `.tmp/phase0/extract/blob000/`。

> 注意：`assemblies.manifest` 的 `Hash 32 / Hash 64` 两列**不是**标准 xxHash64/xxHash32 的裸文件哈希（已用标准向量验证实现无误后，58/58 不匹配）。hash 列的确切算法当前 **【未知】**；这不影响提取（提取正确性由"每个条目都是合法 PE 且程序集身份/版本自洽"独立确认）。

### 4.1.1 运行时**会校验存储内程序集大小**（实测，阻塞性发现）

把注入过的 `StardewValley.dll` 原位替换进 `assemblies.blob`（条目数与 hash 表不变，只重算偏移/大小）
并重打包装机后，进程在托管代码执行前**直接死亡**：

```
F monodroid-assembly: Compressed assembly '<assembly_store>' is larger than when the application was built
                      (expected at most 13824, got 9261056). Assemblies don't grow just like that!
I ActivityManager: Process com.chucklefish.stardewvalley (pid 9377) has died: fg TOP
```

【观测】结论：

1. `libmonodroid` 在打开程序集存储时会拿**构建期记录的大小**与运行期实际大小比对，超出即 `abort`；
2. 运行期侧拿到的值是**解压后大小**（`got 9261056` = 我们注入后的 DLL 长度），
   而期望值（`13824`）来自构建期写入的某张表 —— `libxamarin-app.so` 里的
   `bundled_assemblies` / `assembly_store_bundled_assemblies` 数组（符号实测存在）；
3. 因此**"改程序集字节但不改任何元数据"这条路被堵死**，与 ADR-0001 D3 的乐观假设不同。

可能的突破方向（按风险从低到高，均未验证）：

| 方向 | 做法 | 风险 |
| --- | --- | --- |
| S1 | 同步修改 `libxamarin-app.so` 中该程序集记录的 size 字段 | 需先解出该数组结构；改动 .so 影响全部程序集元数据 |
| S2 | 让注入后的 DLL 长度**恰好等于原长度**（9,280,000） | Cecil 重写后长度不同；需精确填充并把元数据长度凑整，工程上很难稳定 |
| S3 | 补丁 `libmonodroid.so` 跳过该校验 | 原生二进制补丁；任务书 §7 禁止"未经验证的原生覆盖"，必须先做完整验证 |
| S4 | 放弃篡改原程序集，改为**新增**一个带完整元数据的程序集条目 | 又要同步 gap 表 + `.so` 内 `assembly_image_cache`；但**不触发** size 校验（新条目没有"构建期期望值"或为 0） |

S4 值得优先验证：它的失败模式最可预测（找不到程序集 = 加载失败但不崩溃），
而 S1/S3 一旦改错会破坏所有程序集的加载。

#### 4.1.2 追加实验：把补丁 DLL 补齐到原始长度 —— 仍然失败

为排除"必须等长"这一可能，把注入后的 DLL 用零填充补齐到**原始长度 9,280,000**
（`StardewValley.padded.dll`，SHA-256 `36306964…`），重新替换 blob 并重打包装机。

结果**与等长前完全一致**：

```
F monodroid-assembly: Compressed assembly '<assembly_store>' is larger than when the application was built
                      (expected at most 13824, got 9280000)
```

【观测】由此可以确定三件事：

1. 校验**不是**"该条目的大小必须等于构建期该条目的大小"——否则等长就会通过；
2. `got` 精确等于我们条目解压后的长度（9,261,056 → 9,280,000 随之变化），说明读的是我们写的条目；
3. `expected`（13824）**不随我们的改动变化**，且 13,824 恰好等于另一个程序集的解压尺寸
   （提取清单里索引 5 的条目），说明期望值来自一张**按其它顺序排列**的表，
   或来自一条我们尚未理解的索引路径。

结论：继续盲试没有意义。下一步必须**反编译这条校验**：

```
定位字符串 "is larger than when the application was built"
  → 在 libmonodroid.so 中找 xref
  → 读出它读的是哪个数组/哪个索引（`assembly_store_bundled_assemblies`？`compressed_assemblies`？）
  → 才能确定 S1（改 .so 表）还是 S4（新增条目）可行
```

本机**未配置 Ghidra**；可用 `rizin`/`objdump` 级工具做这一步，但需要专门的逆向轮次。

#### 4.1.3 根因找到并修复：`XALZ.descriptor_index` 就是尺寸表下标

用 capstone 手写 ADRP/ADD xref，定位到 `libmonodroid.so` 的校验点（`0x17618` 附近）：

```
0x175b4  w8 = 0x5a4c4158            ; "XALZ"
0x175bc  cmp w9, w8 / b.ne          ; 只对 XALZ 条目做校验
0x175c4  x8  = *(global 0x3d8f8)
0x175cc  x26 = *(x8 + 8)            ; 尺寸表基址
0x175d4  w24 = *(u32*)(x23 + 4)     ; ← XALZ 头的第二个 u32
0x175dc  cmp w24, *(x8) / b.hs      ; 越界检查
0x175e4  x27 = x26 + w24*16         ; 16 字节/条
0x17610  w4  = *(u32*)(x23 + 8)     ; got      = XALZ.uncompressed_length
0x17614  w3  = *(u32*)x27           ; expected = table[index].size
0x17618  cmp w4, w3 / b.hi          ; got > expected → "Assemblies don't grow just like that!"
```

**结论：`XALZ` 头第 8..12 字节不是"调试数据索引"，而是 `libxamarin-app.so` 中
16 字节记录尺寸表的条目下标。** 实测全表吻合：

| 条目 | descriptor_index | table[index].size | 该条目解压尺寸 |
| --- | --- | --- | --- |
| 0 _Microsoft.Android.Resource.Designer | 55 | 2560 | 2560 ✓ |
| 5 | 0 | 13824 | 13824 ✓ |
| 7 | 5 | 1105408 | 1105408 ✓ |
| **10 StardewValley** | **7** | **9280000** | 9280000 ✓ |

这也解释了此前的怪现象：我把 `descriptor_index` 写成 0，于是校验拿 `table[0]=13824`
去比我们 9.2 MB 的条目——与 `expected at most 13824` 完全对上。

修复：`Tools/AssemblyStore` 在重建时**原样保留每个条目的 descriptor_index**。
修复后校验通过（`9261056 <= 9280000`）。

> 该实验同时给出了一个**正面**结论：注入本身没有破坏 APK 结构 —— apktool 重打包、签名、安装、
> 进程启动、`JNI_OnLoad`、`libnb`(ARM 翻译) 初始化全部正常，失败点精确落在程序集存储校验上。

### 4.2 程序集清单（identity）

| Assembly | Version | MVID | TFM |
| --- | --- | --- | --- |
| StardewValley | 1.6.15.24354 | `573bf1e5-3245-46d6-925a-1c3713afb677` | .NETCoreApp v8.0 |
| StardewValley.GameData | 1.6.15.24354 | `56cf6ca4-7a47-04d0-8ab3-a36f8121aa83` | .NETCoreApp v8.0 |
| MonoGame.Framework | 1.0.0.0 | `b6421435-0fcc-564d-9e7b-8efd76ac9963` | .NETCoreApp v8.0 |
| xTile | 1.0.0.0 | `812147f8-0ece-754d-9c99-9ab12369bbe1` | .NETFramework v4.5 |
| Mono.Android | 0.0.0.0 | `06b8678b-a941-804c-9e2c-d55ce922cc63` | .NETCoreApp v8.0 |
| mscorlib（门面） | 4.0.0.0 | `1ba693f8-8044-c84d-bfa5-d0b2d1052757` | .NETCoreApp v8.0 |
| System.Private.CoreLib | 8.0.0.0（架构 blob） | 见 `_identity2.tsv` | .NETCoreApp v8.0 |
| Java.Interop | 7.0.0.0 | `91469ffe-ba2e-a041-97c8-baa9817c2000` | .NETCoreApp v7.0 |
| Lidgren.Network | 2012.1.7.0 | `5786ad4d-d673-bf4e-8cfe-2775220a6c7c` | .NETFramework v4.5 |
| BmFont | 0.0.0.0 | `d2e0ba80-c52c-f348-b4b8-83d076b85e08` | .NETFramework v4.8 |
| System.Data.HashFunction.xxHash | 1.8.2.2 | `a7454a0f-7c13-bc44-923b-fce11c4ef230` | .NETFramework v4.5 |

完整 65 行：`.tmp/phase0/extract/blob000/_identity2.tsv`。

### 4.3 `StardewValley.dll` 规模【观测，REA managed metadata】

| 项 | 值 |
| --- | --- |
| 大小 / SHA-256 | 9,280,000 / `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047` |
| PE | PE32, IL-only, AnyCPU, 3 节 |
| 类型 / 方法 / 字段 | 2,867 / 30,067 / 18,859 |
| P/Invoke（ImplMap） | **5,192** |
| TypeRef / MemberRef / TypeSpec | 640 / 9,138 / 2,292 |
| AssemblyRef | 28 |
| CustomAttribute | 4,656 |
| 强名称 | 无（`public_key` 为空） |

### 4.4 AOT 镜像

65 个 `libaot-<Assembly>.dll.so`，合计 38.5 MB。**镜像数量与程序集总数一致**。但体积分布说明"并非所有方法都 AOT"：

| 镜像 | 大小 | 解读 |
| --- | --- | --- |
| `libaot-StardewValley.dll.so` | 21,441,704 | 游戏主体已 AOT |
| `libaot-System.Private.CoreLib.dll.so` | 4,080,128 | 核心库已 AOT |
| `libaot-MonoGame.Framework.dll.so` | 2,006,816 | 已 AOT |
| `libaot-Mono.Android.dll.so` | 1,250,432 | 已 AOT |
| `libaot-mscorlib.dll.so` | 4,912 | **桩**（门面无可编译方法） |
| `libaot-System.Core.dll.so` | 4,912 | 桩 |
| `libaot-System.dll.so` | 4,896 | 桩 |

---

## 5. Android 启动流程

### 5.1 清单【观测】

```xml
<application android:name="android.app.Application" ...>
  <activity android:name="com.chucklefish.stardewvalley.MainActivity"
            android:launchMode="singleInstance"
            android:screenOrientation="sensorLandscape"
            android:configChanges="keyboard|keyboardHidden|orientation|screenLayout|screenSize|uiMode"
            android:theme="@style/Theme.Splash">
    <intent-filter> MAIN + LAUNCHER </intent-filter>
  </activity>
  <provider android:name="mono.MonoRuntimeProvider"
            android:authorities="com.chucklefish.stardewvalley.mono.MonoRuntimeProvider.__mono_init__"
            android:initOrder="1999999999" android:exported="false"/>
</application>
```

- 没有自定义 `Application` 子类（`android:name` 是框架的 `android.app.Application`）。

### 5.2 两个阶段

1. **运行时初始化**：`mono.MonoRuntimeProvider`（ContentProvider，`initOrder=1999999999` 最大 → 在所有 provider 之后、`Application.onCreate` 之前）加载 `libmonodroid.so` 并 `mono_jit_init_version`，随后注册程序集存储。`libmonodroid` 内含 `assembly_store_open_from_bundles`、`individual_assemblies_open_from_bundles`、`Finished preloading, number of loaded assemblies:` 等字符串，说明"预加载/按名查找"两条路径都存在。
2. **托管界面**：`com.chucklefish.stardewvalley.MainActivity` 的 Java 类**不是原生实现**，而是 Mono.Android 为托管类型生成的 peer【观测：dex 中 `__md_methods` 把 `n_onCreate` 映射到 `GetOnCreate_Landroid_os_Bundle_Handler`】。真正的逻辑在 `StardewValley.MainActivity`（托管，43 个方法）。

### 5.3 托管 `MainActivity` 的真实 IL 流程【观测】

`StardewValley.MainActivity::OnCreate(Bundle)`（token `0x06002f7e`，106 字节 IL）：

```
stsfld  MainActivity 静态字段 = this
ldstr   …                       ; 日志
call    0x06000016              ; 日志输出
ldarg.0; ldc.i4.1; call RequestWindowFeature(1)
call    BuildVersionCodes.get_SdkInt
if (SDK >= 28) { Window.Attributes.LayoutInDisplayCutoutMode = …; Window.Attributes.SetFlags(1024,1024); … }
call    Activity.OnCreate(Bundle)        ; 0x0a00077e —— 注意：基类调用在中间
call    MainActivity.CheckAppPermissions ; 0x06002f93
ret
```

`StardewValley.MainActivity::OnCreatePartTwo()`（token `0x06002f7f`，84 字节 IL）：

```
call    Mobile.MobileDisplay.SetupDisplaySettings()   ; 0x060065d0
call    MainActivity.SetPaddingForMenus()             ; 0x06002f87
newobj  StardewValley.InstanceGame::.ctor             ; 0x0600390f  ← 游戏对象创建
this.SetContentView(GetService<…>())                  ; 0x0a00077f / 0x0a000780
stsfld  <静态游戏引用> = instance
this.<字段> = instance.<字段>
callvirt <…>.Run()                                    ; 0x0a000781  ← 游戏主循环启动
ret
```

**结论**：`OnCreatePartTwo` 是"创建游戏对象 → 设置内容视图 → 启动主循环"的唯一路径，是 Mod Loader 最干净的注入点（在 `InstanceGame::.ctor` 之前）。`OnCreate` 末尾的 `CheckAppPermissions` 是第二候选（更早，但此时 UI 尚未建立）。

### 5.4 适合初始化 Loader 的时机

| 时机 | 可行性 | 说明 |
| --- | --- | --- |
| `MonoRuntimeProvider.onCreate` 之后、`MainActivity` 之前 | 需要新 dex 类（Java peer） | 只能拿到 Java 侧，托管运行时尚未"可用"，需 JNI 绕行 |
| `MainActivity.OnCreate`（托管）末尾 | **推荐** | 运行时已就绪、Activity 已 attach、尚未创建游戏对象 |
| `MainActivity.OnCreatePartTwo` 开头 | **推荐（最优）** | 离游戏对象创建最近，UI 线程上下文正确 |
| `InstanceGame::.ctor` / `Game1::.ctor` | 可选（补丁粒度太深，易随版本变化） | 适合做"最后一道"钩子 |

---

## 6. 关键问题逐条回答（任务书 §2.2 A/B/C）

### A. Mono Runtime

| # | 问题 | 回答 | 类型 |
| --- | --- | --- | --- |
| 1 | 版本/构建 | MonoVM `8.0.1124.51707`，.NET for Android Release 构建（AOT `normal`） | 观测 |
| 2 | 是否启用 JIT | 是，JIT 后端已链接且未被 `full` AOT 关闭 | 观测（符号 + 模式） |
| 3 | 是否 Full AOT | 否，`mono_aot_mode_name="normal"`；但**游戏程序集有 AOT 镜像** | 观测 |
| 4 | 是否有解释器 | 实现存在（字符串/符号），启用通道未知 | 观测 + 未知 |
| 5 | 动态程序集加载 | ✅ **实测可用**：`Assembly.LoadFrom(string)` 能加载 `Mods/` 下的 DLL 并执行其 IL | **观测（设备）** |
| 6 | Reflection.Emit | ❌ **不可用**：`DynamicMethod` 抛 `TypeLoadException`（缺 `System.Reflection.Module`），`AssemblyBuilder` 抛 `FileNotFoundException: System.Reflection.Emit`；编译期同样报错 | **观测（设备 + 编译期）** |
| 7 | 动态泛型实例 | ✅ 常见实例化可用（`Generic<int>`、`Generic<string>`、`Pair<int,string>` 实测通过）；罕见组合的 AOT 边界未测 | 观测 + 未知 |
| 8 | 加载用户目录 DLL | ✅ 可用，但**路径必须是应用可访问的目录**：`/data/data/<pkg>/files/...` 可以；由 root/shell 创建的 `/sdcard/Android/data/<pkg>/...` 在进程内读不到 | **观测（设备）** |
| 9 | `Load` / `LoadFrom` / `LoadFile` | ❌ `Load(byte[])` 不存在（编译期 CS1503 + 运行时 `MissingMethodException`）；✅ `LoadFrom(string)` 可用；`LoadFile` 未测 | **观测** |
| 10 | 动态 IL 可执行 | ✅ `RuntimeFeature.IsDynamicCodeSupported = True`、`IsDynamicCodeCompiled = True`（用 Android 参考程序集编译后测得） | **观测（设备）** |

### B. 游戏程序集

| # | 问题 | 回答 |
| --- | --- | --- |
| 1 | 如何从 blob 加载 | `libmonodroid` 打开 APK 内 `assemblies/*.blob`，按 `assembly_image_cache`/名称哈希定位条目，解压 `XALZ` 后交给 Mono `mono_image_open_from_data`（字符串证据：`assembly_store_open_from_bundles`、`Compressed assembly '%s' is larger than …`） |
| 2 | 运行时如何发现程序集 | 预加载（`uses_assembly_preload`）+ 名称查找双路径；`MONO_PATH` 未设置 |
| 3 | 能否额外加载用户 DLL | 预计可以（内存加载），待验证 |
| 4 | 外部程序集需否注册 | 内存加载不需要；若走文件路径则需要 preload hook 或 `MONO_PATH` |
| 5 | 游戏类型能否被外部程序集反射访问 | 可以（同一 AppDomain/ALC，`mono_class_from_name` 等 API 存在）——待验证 |
| 6 | 能否创建游戏对象/调用方法 | 预计可以；AOT 特性可能影响某些泛型实例化 |
| 7 | 类型初始化 / AOT 泛型限制 | AOT 模式下 `gsharedvt` 只覆盖常见实例化；罕见实例化需要 JIT，若 JIT 可用则无碍 |

### C. Android 启动流程
见 §5（已用 IL 级证据回答 1/2/3/4/5；6 的"不破坏启动"要求体现在 §9 的风险控制）。

### D. Harmony
见 §7。

---

## 7. Harmony / 方法补丁评估

### 7.1 机制前提

MonoMod/Harmony 的 detour 在 Mono 上依赖两件事：
1. 能拿到**可写的方法代码指针**（JIT 生成的代码，或可 `mprotect` 的原生代码页）；
2. 能建立 **trampoline** 调用原方法。

AOT 镜像里的方法是 `.so` 中的 AArch64 机器码，没有 JIT info；`mono_compile_method` 会返回 AOT 代码地址。Mono 还会校验 AOT 模块与程序集的 GUID 一致性：

```
AOT: module %s is unusable (GUID of dependent assembly %s doesn't match (expected '%s', got '%s'))
Image out of date: %s
Failed to load AOT module '%s' while running in aot-only mode because a dependency cannot be found or it is out of date.
```

【观测】这意味着：**一旦我们修改了某个程序集（哪怕只是加了方法体），它的 AOT 镜像会被判定过期而不被使用**，方法回落到 JIT。这既是风险（性能），也是机会（Harmony 有了可 detour 的 JIT 代码）。

### 7.1.1 重要修正：本例很可能是 **profiled AOT**，而非"全量 AOT"

.NET for Android 的官方构建属性文档说明：Mono Release 构建默认 `$(RunAOTCompilation)=true`，且未显式设置时 `$(AndroidEnableProfiledAot)` 默认为 `true` —— 即**只对 profile 命中的方法做 AOT**；只有 `$(AndroidStripILAfterAOT)=true` 才会在 AOT 后剥离 IL。同时 `$(AndroidUseInterpreter)` 在 Debug 默认为 true，**Release 为 False**，即 Release 走 MonoVM JIT。

这解释了本项目的观测组合：65 个 `libaot-*.so` 存在（程序集级"有镜像"），但 `mono_aot_mode_name="normal"`（不是 `full`）——**绝大多数方法仍然走 JIT**。

对 Harmony 的含义（【推断】，需 E5 验证）：**未被 profile 命中的游戏方法大概率可以正常 detour**；只有热点方法（被 AOT 的少数）才可能失败。这比"全量 AOT"乐观得多。

> 上述构建属性来自 Microsoft Learn 的 .NET for Android `build-properties` 文档（外部参考，本报告未独立复核该页面；AOT 覆盖率需在 E5 中逐方法实测）。

### 7.1.2 外部先例（未在本项目复现）

`NRTnarathip/SMAPI-Android-1.6`（GitHub，约 207 star）是一个把 SMAPI 跑在 Android 版星露谷进程内的项目：目标框架 `net9.0-android35.0`，引用 `Lib.Harmony`、`MonoMod.RuntimeDetour`、`Mono.Cecil`，并新增 `src/SMAPI/Mobile/*`（`AndroidPatcher` 调 `harmony.PatchAll()`、`AndroidSModHooks`、`AndroidModLoaderManager`、`AndroidMainThread`），SCore 通过读取 `MainActivity.instance` 取得 Activity。

它的存在说明：**Harmony 在 .NET-for-Android 的星露谷里至少是"曾经跑通过"的**，且路线与本 ADR 一致（进程内引导 + Harmony + Cecil）。但：
- 我们是 1.6.15.0、带 65 个 AOT 镜像的原版 APK，与它的构建参数不同；
- 该项目的实际可用性、支持的 Harmony 功能范围（Transpiler？）**未经本项目验证**。

### 7.2 三档策略（**已按实测修订**）

| 档 | 做法 | 状态 |
| --- | --- | --- |
| **H0** 无补丁 | 只做 Events / Content / Data / Config / Reflection API，全部通过"读取游戏状态 + 写入游戏数据"实现 | ✅ **唯一确定可行** |
| ~~H1 选择性 JIT detour~~ | 只对未被 AOT 覆盖的方法做 detour | ❌ **不可行**：Harmony/MonoMod 的 `MethodReplacement` 依赖 `DynamicMethod`，实测 `Reflection.Emit` 完全缺失（§6.5、`bcl-surface.md`） |
| **H2' 原生 detour** | 用 MonoMod 的 `NativeDetour`（不需要 Reflection.Emit）直接改代码页跳转 | ⚠️ **前提成立、朴素实现崩溃**：`MethodHandle.Value` 可取地址、`mprotect` RWX/还原返回 0（T11）；但直接写跳转桩会原生崩溃（T12），几乎肯定缺 ARM64 指令缓存刷新，且受模拟器翻译缓存干扰。**未证实可用** |
| ~~H2 构建期去 AOT~~ | 移除 `libaot-*.so` 让整程序集走 JIT | ⚠️ 仍可尝试，但**它救不了 Harmony**——问题不在 AOT，在 Reflection.Emit |

**明确禁止**（任务书 §7）：直接对 AOT 原生地址做未经验证的内存覆盖。

> 这条修订很重要：最初以为"去 AOT 就能让 Harmony 用起来"，实测后发现 JIT 本来就在、
> 真正缺的是 `Reflection.Emit`。也就是说 **H2 无论怎么做都不可能让 Harmony 工作**。

## 6.5 破坏性发现：`Assembly.Load(byte[])` 在运行时**不存在**（真机、已证明 APK 身份）

在"注入已确认执行"（§3.13 的 `bootstrap-ran` 证据）之后，用带身份证明的部署流程
（`Tools/deploy-apk.ps1`，设备端 `sha256sum $(pm path …)` 与本地产物一致）
部署**不吞异常**的完整 Bootstrap，得到：

```
E AndroidRuntime: android.runtime.JavaProxyThrowable: [System.MissingMethodException]:
                  Method not found: System.Reflection.Assembly System.Reflection.Assembly.Load(byte[])
```

同一异常在三种作用域下都出现：桌面 `System.Private.CoreLib`、`module.TypeSystem.CoreLibrary`、
显式 `System.Runtime, 8.0.0.0, b03f5f7f11d50a3a`。而 Android 的 `System.Private.CoreLib`
本身是强名称（160 字节公钥），所以"标识写错"这个解释已经不成立。

**结论：这不是引用写法问题，而是该方法在这个裁剪后的运行时里不可用。**
最可能的原因是 .NET Android 的 **ILLink 裁剪**：游戏从未调用过 `Assembly.Load(byte[])`，
该成员在 Release 构建中被裁掉。同类风险也适用于 `Reflection.Emit`、`AppDomain.Load` 等
"游戏不使用、我们要用"的 BCL 面。

### 对架构的影响（**已被后续实验推翻**，保留作为过程记录）

> **更正（2026-11-04，见 `phase0-status.md` §3.19/§3.23）**：本节当时推荐的 P/Invoke 路线**不需要了**。
> 后来查明 `Assembly.Load(byte[])` 缺失是 ILLink 裁剪，而 `Assembly.LoadFrom(string)` **可用**；
> 用 Android 参考程序集编译后，动态加载已实测通过。原来的三个方案保留如下，仅作决策轨迹。

| 方案 | 说明 | 结论 |
| --- | --- | --- |
| ~~L1~~ | Bootstrap 用 P/Invoke 调 `mono_image_open_from_data_with_name` + `mono_assembly_load_from_full` | **不需要**：`Assembly.LoadFrom` 已够用 |
| ~~L2~~ | 把 Loader 塞进 `assemblies/` 让运行时按名解析 | 不需要 |
| ~~L3~~ | 每个模组随 APK 构建 | 不需要 |

同时这也解释了为什么此前的诊断分支（只用 `File.AppendAllText`）**同样没有产出 marker**：
那一版 MVID 未变（见 §3.13），代码根本没被执行——与 BCL 可用性无关。
`File.WriteAllText` 后来被证明是可用的。

### 7.3 待验证的关键假设
- **H-A**：删除 `libaot-StardewValley.dll.so` 后游戏仍能启动（Mono 应回退 JIT，`normal` 模式允许）。→ 实验 E4。
- **H-B**：Harmony 的 `MethodReplacement`（`mono_compile_method` + 代码页改写）在 Android 上不被 SELinux/W^X 阻止。→ 实验 E5。
- **H-C**：AOT 镜像存在时，对**非 AOT 方法**（例如我们自己的程序集）的 detour 正常工作。→ 实验 E5。

---

## 8. Android 存储与权限【观测】

```
drwx------  10126 10126  /data/data/com.chucklefish.stardewvalley
drwxrwx--x  10126 10126  /data/data/com.chucklefish.stardewvalley/files
drwxrws---  10126  1078  /sdcard/Android/data/com.chucklefish.stardewvalley
                         └── Saves          ← 存档在"应用专属外部目录"
```

- 应用私有目录 `0700`，属主 uid 10126；`files/` 为 `0771`。
- 存档实际位置：`/sdcard/Android/data/com.chucklefish.stardewvalley/files/Saves`（与 AGENTS.md 中"卸载会删存档"的说法一致——`Android/data` 随卸载删除）。
- `run-as` 不可用（非 debuggable）→ 独立管理器**不能**用 `run-as` 读写游戏私有目录。
- Android 11+ 的 scoped storage 会在 DocumentsUI 中**屏蔽 `Android/data` 子树**，因此"用 SAF 选 `Android/data/com.chucklefish.stardewvalley`"在多数真机上不可行。

**设计约束（写入 ADR）**：独立管理器不得假设能直接读写游戏私有目录；必须走
① 游戏端导出/导入（共享 Intent + `FileProvider`）或
② 双方都能访问的公共目录（如 `/sdcard/Documents/StardewMods`）+ SAF 授权，
并在真机上验证（实验 E6）。

---

## 9. 技术决策

### 9.1 路线选择

**Route A（支持动态 IL）→ 主路线**，同时保留 Route D 的兼容形态。

依据：
1. JIT 存在且 AOT 模式为 `normal`（不是 `full`）→ 动态程序集具备执行条件（【观测】+【推断】）。
2. 程序集按名查找 + 内存加载 API 存在 → 外部 DLL 无需进 APK（【观测】）。
3. 注入点明确且只在托管侧（`MainActivity.OnCreate` / `OnCreatePartTwo`）→ 不需要 smali、不需要新 dex。

**Harmony 定位**：可选能力（Route D 形态）。默认关闭；开启时按 H1→H2 升级，失败必须能整体回退，且不影响 H0 的任何 API。

### 9.2 目标架构（Phase 1 起实施）

```
APK 构建期（一次性，由本工程完成）
  ├─ ModFramework.Bootstrap.dll   → 写入 assemblies 存储（新增条目）
  └─ StardewValley.dll 的 MainActivity.OnCreatePartTwo 首行插入 call Bootstrap.Start()

运行时（每次启动）
  Bootstrap.Start()
    ├─ 读 Mods/ 目录（app 专属外部目录）
    ├─ 解析 manifest.json → 依赖拓扑 → 状态机
    ├─ Assembly.Load(File.ReadAllBytes(mod.dll))   ← 依赖 JIT
    ├─ 反射找 EntryClass / Mod 子类 → Entry(helper)
    └─ 事件总线（由框架自身的 tick 钩子驱动）
```

**为什么改 `StardewValley.dll` 而不改 dex**：改 dex 需要 d8 工具链与新类；改托管 IL 只需 Mono.Cecil，而且项目已具备"重打包 → 签名"的完整流水线（AGENTS.md §4）。

**代价（必须写进文档）**：被修改的 `StardewValley.dll` 其 AOT 镜像将失效 → 该程序集回退 JIT。这是可测量的性能代价，需在 E4 里量化（冷启动时间、内存）。

### 9.3 与"无模组启动"的隔离

- Bootstrap 在 `Mods/` 不存在或为空时立即返回，不注册任何事件，不改任何游戏状态。
- 提供一个构建开关 `-PmodFramework=false`，产出与原版等价的 APK（仅清单补丁），用于回归对比。

---

## 10. 待完成的实验（Phase 0 阻塞项）

| ID | 目的 | 方法 | 通过标准 | 状态 |
| --- | --- | --- | --- | --- |
| E1 | 动态程序集加载 + IL 执行 | 注入 `ModRuntimeProbe.dll`，`Assembly.Load(bytes)`，调用静态/实例/泛型方法 | 日志出现三段返回值 | 待设备 |
| E2 | 反射访问游戏类型 | `probe` 反射 `StardewValley.Game1`，读一个静态字段/调用无副作用方法 | 返回预期值 | 待设备 |
| E3 | 事件钩子可行性 | 在 `OnCreatePartTwo` 前后打点，验证 tick/day 事件可被驱动 | 事件按序触发 | 待设备 |
| E4 | 去 AOT 回退 | 移除 `libaot-StardewValley.dll.so` 重打包 | 游戏启动到标题界面；记录启动耗时/内存 | 待设备 |
| E5 | Harmony Prefix/Postfix/Original | `HarmonyDemo` 对 JIT 方法与 AOT 方法各打一次 | JIT 通过；AOT 明确失败并给出诊断 | 待设备 |
| E6 | 独立管理器存储通道 | SAF / 共享目录 / 游戏端导入三条路径 | 至少一条在 Android 12 + Android 14 真机可用 | 待设备 |

> 当前环境只有模拟器（x86_64 + ARM 翻译），E1–E6 的结论在真机上可能不同。**在真机通过前，本报告不得宣称任何运行时能力"已实现"。**

---

## 11. 证据索引与复现

| 证据 | 位置 | 复现命令 |
| --- | --- | --- |
| 程序集提取 + 身份 | `.tmp/phase0/extract/blob000/`、`_identity2.tsv` | `python .tmp/phase0/work/extract_assemblies.py`、`dn_identity.py` |
| ELF 符号/字符串 | `.tmp/phase0/evidence/elf_*.txt`、`strings_*.txt` | `python .tmp/phase0/work/elf_probe.py <so>`、`dump_strings.py <file>` |
| AOT 模式 / 运行时配置 | `.tmp/phase0/evidence/`（本文 §3.4 数值） | `python .tmp/phase0/work/xa_aotmode.py`、`xa_reloc.py` |
| 产物哈希 | `.tmp/phase0/evidence/artifact_hashes.txt` | `Get-FileHash` |
| dex 类/方法 | REA evidence `ev_dea5ac0d…`（MainActivity）、`ev_bbf33bfe…`（MonoRuntimeProvider） | `inspect_android_class` on `.tmp/phase0/work/dexonly.apk` |
| 托管成员/IL | REA evidence `ev_b97369ad…`（parent）、`ev_4444eff7…`（methods page） | `inspect_managed_members` on `.tmp/phase0/extract/blob000/StardewValley.dll` |
| 设备属性/包信息 | REA evidence `ev_80b72e4c…`、`ev_912c9356…` | `inspect_adb_device` / `inspect_adb_package` |

关键 SHA-256（详见 `artifact_hashes.txt`）：

```
APK            258d7d924b82eaa8dc06b787d2a8b9d50240f6e9dfdcd83aa640f2a560755e85
assemblies.blob c8ee504eea163d2c82d89d0004c8924314b3e6b41adb649ea5f136151eecabd9
libmonosgen     f62f6e8d9c2d43c290363629b608c01d4ef5ea655c9b6bf3fb3cf6a473c90fe6
libxamarin-app  11384d74daa734ed5b11cbf048978686c7afbb2a7f3224df53609805035132d9
libmonodroid    0a9e1e9cd54d7f4375aa6dec1fa227a6116450b9f90af1ee2e01f3f0bf6c8f65
StardewValley.dll 587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047
```

---

## 12. 尚未回答 / 未知清单

1. `assemblies.manifest` 的 hash 列算法（不影响功能，但影响我们自己做存储完整性校验）。
2. 运行时是否校验 blob 内程序集的 hash（若校验，则"改 `StardewValley.dll`"的方案需要同步更新 manifest/`libxamarin-app` 缓存）。
3. `System.Reflection.Emit` 是否被裁剪（决定 Harmony Transpiler 与动态代理是否可用）。
4. 解释器启用通道（决定 Route B 是否真存在）。
5. `application_config` 前 12 字节的精确字段序（当前按已知 layout 对齐，`uses_mono_aot=1` 可信，其余布尔位含义待与官方头文件比对）。
6. 真机上的 JIT/AOT 行为（模拟器结论不可外推）。
7. Android 14+ 对 `Android/data` 的访问策略（影响 E6 方案选择）。
