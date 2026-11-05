# 证据索引（Phase 0）

> ## 证据可得性（Phase 0.5，2026-11-05）
>
> **本页列出的 `.tmp/phase0/...` 全部已不在本机**（`.tmp/` 被清空；它本来就在 `.gitignore` 里）。
> 因此：
> 1. 下表的路径是**历史记录**，不是可打开的文件；
> 2. "重建全部本机证据"的脚本（`extract_assemblies.py`、`xa_reloc.py` 等）**也一并丢失**，
>    当前无法按原样重放；
> 3. 与之相关的数字（MonoVM 版本、65 个程序集、AOT `normal`、APK/blob 哈希、探针 8/8 PASS）
>    只能当"当时的记录"引用，**不要再写成"可复核"**；
> 4. Phase 0.5 重新取证并**可复核**的部分，见 `phase0.5-evidence-index.md`。
>
> 原始 APK、`assemblies*.blob`、`jniLibs` 仍在仓库里，所以程序集提取/容器校验这类
> **从仓库输入可重建**的证据可以重新生成（Phase 0.5 已重做，见 `phase0.5-evidence-index.md` §2）。

所有证据分三类存放：

- **入库（git）**：`docs/audit/`、`Mod/`（源码与文档）
- **本机、可重建**：`.tmp/phase0/`（脚本、提取物、日志；`.tmp/` 已 gitignore）— **现已丢失**
- **外部引用**：报告里标注 URL 的网页（未独立复现的，一律标注"外部参考"）

重建全部本机证据（**脚本已不在，以下命令仅作历史记录**）：

```powershell
$py = "<python with dnfile>"
python  .tmp/phase0/work/extract_assemblies.py     # 解 blob → 65 个 DLL
python  .tmp/phase0/work/dn_identity.py            # 版本/MVID/TFM 表
python  .tmp/phase0/work/elf_probe.py  <so>        # ELF 符号/字符串
python  .tmp/phase0/work/dump_strings.py <file>    # 全量字符串
python  .tmp/phase0/work/xa_aotmode.py             # AOT 模式名 + rodata
python  .tmp/phase0/work/xa_reloc.py               # 重定位解析（决定性证据）
python  .tmp/phase0/work/mk_dexapk.py              # 生成 dex-only APK 供 JADX
```

现在**仍然可用**的等价入口（Phase 0.5 建立）：

```powershell
# 65 个程序集（58 主存储 + 7 架构存储），按清单正确命名
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.blob <out> --manifest app/src/main/apkroot/assemblies/assemblies.manifest
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.arm64_v8a.blob <out> --manifest app/src/main/apkroot/assemblies/assemblies.manifest

# 注入器 + 容器链的离线回归（27 项）
python Tools/tests/test_mod_pipeline.py
```

---

## 1. 产物与身份

| 证据 | 位置 | 证明什么 |
| --- | --- | --- |
| 产物哈希清单 | `.tmp/phase0/evidence/artifact_hashes.txt` | APK / blob / 各 .so / dex 的 SHA-256 与大小 |
| 程序集身份表 | `.tmp/phase0/extract/blob000/_identity2.tsv` | 65 个程序集的 name/version/MVID/module |
| 提取报告 | `.tmp/phase0/extract/blob000/_extract_report.txt` | 每个条目的压缩/原始大小与 hash 比对结果（hash 列算法未知，见报告 §4.1） |

关键哈希：

```
APK               258d7d924b82eaa8dc06b787d2a8b9d50240f6e9dfdcd83aa640f2a560755e85
assemblies.blob   c8ee504eea163d2c82d89d0004c8924314b3e6b41adb649ea5f136151eecabd9
libmonosgen-2.0   f62f6e8d9c2d43c290363629b608c01d4ef5ea655c9b6bf3fb3cf6a473c90fe6
libxamarin-app    11384d74daa734ed5b11cbf048978686c7afbb2a7f3224df53609805035132d9
libmonodroid      0a9e1e9cd54d7f4375aa6dec1fa227a6116450b9f90af1ee2e01f3f0bf6c8f65
StardewValley.dll 587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047
```

Phase 0.5 复核：

- `assemblies.blob` 的 `c8ee504e…` **可复核**（仓库文件，Phase 0.5 重新算过，一致）；
- `StardewValley.dll` 的 `587ff5ff…` **已复核一致**：Phase 0.5 从 blob 条目 10
  （`descriptor_index=7`）解出 9,280,000 字节，SHA-256 与记录完全相同——这条也反证了
  `Tools/AssemblyStore` 的 LZ4 解码链路是对的；
- **APK 的 `258d7d92…` 不可复核**：该路径当前产物是 `65e1e2af…`（388,967,580 字节，
  2026-11-04 22:57 构建），与文档记录**不是同一个文件**。两者大小相同但哈希不同，
  说明这条记录对应的是另一次构建；引用 APK 身份时必须重新计算，不能沿用本行。

## 2. 运行时能力（核心）

| 结论 | 证据文件 | 关键内容 |
| --- | --- | --- |
| MonoVM 版本 8.0.1124.51707 | `strings_libmonosgen.txt` @`0x0576c5` | `8.0.1124.51707 @Commit: 9cb3b725e3ad2b57ddc9fb2dd48d2d170563a8f5` |
| JIT 存在 | `elf_libmonosgen-2.0.so` 段（`elf_probe` 输出） | `mono_jit_init_version`、`mono_compile_method`、`mono_aot_register_module` |
| 解释器代码存在 | `strings_libmonosgen.txt` | `MONO interpreter: NIY encountered in method %s`、`interp_init` |
| **AOT 模式 = normal** | `xa_reloc.py` 输出 | `mono_aot_mode_name reloc_off=0x298e8 addend=0x841f -> b"normal"` |
| runtime 配置计数 | `xa_config.py` 输出 | `env=3, props=8, assemblies=65, stores=2, dso=426` |
| GC 参数 | `xa_reloc.py` | `MONO_GC_PARAMS = major=marksweep-conc` |
| 65 个 AOT 镜像 | `artifact_hashes.txt` + `jniLibs` 清单 | 合计 38,564,048 字节；mscorlib/System/System.Core 为 4.9 KB 桩 |

## 3. Java / 托管入口

| 结论 | REA evidence | 备注 |
| --- | --- | --- |
| `MainActivity` 是托管 peer | `ev_dea5ac0d79d869065fdf843708972b7731e7aec1f040804af213720daa962c6c` | dex `__md_methods` → `GetOnCreate_…Handler` |
| `MonoRuntimeProvider` 只有 8 个方法 | `ev_bbf33bfe5d32c329c9d982be4d0451dfd5b7340c33f4fdf198105761fb781073` | provider 生命周期标准实现 |
| `StardewValley.dll` 结构 | `ev_ecccab632f1a344150304f6526257f919d82f5302c9b0d130327bff0f5e4f72a` | 2867 types / 30067 methods / 5192 P/Invoke |
| `MainActivity` 成员 + IL | `ev_b97369ad71992453f359a218e8e4a32e5d18067195745529c8153df776a46901`（parent）<br>`ev_4444eff7389890fff55d25b3651ffb2d3a233a8d26b4e8e4a879a4f5c4145b87`（page） | `OnCreate` / `OnCreatePartTwo` 的完整指令与调用目标 |
| 设备属性 | `ev_80b72e4cc66a75092dacfc5e28441e10f36af6818c5781142aa1723ed479e43b` | Android 12 / API 32 / x86_64 |
| 安装包信息 | `ev_912c935630702732d015a86698bd5282f1d192ba60e4a390335eb38ec42bb3f8` | 1.6.15.0 / 239 |

> REA evidence ID 属于**本次 MCP 会话**；会话结束后只能靠本文档的记录与 `.tmp/phase0/` 里的原始文件复现。

## 4. 存储/权限

| 结论 | 采集命令 |
| --- | --- |
| 私有目录 0700、`files/` 0771、属主 uid 10126 | `adb shell su -c 'ls -ldn /data/data/com.chucklefish.stardewvalley{,/files}'` |
| 存档在 `/sdcard/Android/data/…/files/Saves` | `adb shell su -c 'ls /sdcard/Android/data/com.chucklefish.stardewvalley/files'` |
| `run-as` 不可用 | `adb shell run-as com.chucklefish.stardewvalley id` → `package not debuggable` |

## 5. 探针

| 证据 | 位置 | 说明 |
| --- | --- | --- |
| 桌面基线报告 | `.tmp/phase0/evidence/probe-desktop-baseline.{log,json}` | 11 pass / 1 fail（T07 无游戏程序集，预期） |
| **设备实测报告（最终）** | `.tmp/phase0/evidence/probe-device-androidrefs.txt`（**已丢失**） | **RECORDED 8/8 PASS (Evidence D)**，含 `T00 dyn-code supported=True`、`T07 Game1 576 方法/532 字段`；模拟器，非真机 |
| 设备 loader 日志 | `.tmp/phase0/evidence/loader-device-androidrefs.log`（**已丢失**） | 记录为 `Assembly.LoadFrom` 加载运行时并执行 `Loader.Start()`（D 级） |
| 早期设备报告（桌面参考程序集编译） | `.tmp/phase0/evidence/probe-device.txt` | 8 项中 3 项失败，**已被上一行取代**；保留用于对照"编译方式"的影响 |
| 探针源码 | `Mod/MonoRuntimeProbe/ProbeEntry.cs` | T00–T07（T08–T10 因 API 不可用已移除） |
| 编译期证据 | `docs/audit/bcl-surface.md` §3 | `CS0117`/`CS0234`/`CS1503` 四条错误 |
| 示例模组（编译期验证） | `Mod/ExampleMod.HelloWorld/` | 编译产物 2 个 AssemblyRef、4096 字节 |

## 6. 外部参考（未独立验证）

| 主题 | 来源 |
| --- | --- |
| .NET Android 构建属性（profiled AOT、UseInterpreter） | Microsoft Learn `dotnet/android/building-apps/build-properties` |
| Harmony 对 AOT/iOS/Android 的官方立场 | `pardeike/Harmony` issues #6 / #196 / #158 |
| Mono AOT side-by-side 行为 | mono-project.com `docs/advanced/aot` |
| MonoMod 平台支持 | `MonoMod/MonoMod` PR #331 |
| Android 先例 | `NRTnarathip/SMAPI-Android-1.6`、`NRTnarathip/SMAPILoader` |
| SMAPI 架构与许可证 | `Pathoschild/SMAPI`（LGPL-3.0） |
