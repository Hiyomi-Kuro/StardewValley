# Phase 0 结论备忘（一页版）

- 日期：2026-11-04
- 范围：星露谷 Android 1.6.15.0（`com.chucklefish.stardewvalley`，versionCode 239）
- 状态：**Phase 0 交付完成；Phase 1 未启动（门控未满足）**
- 详细过程：`phase0-status.md`（40 KB，按轮次记录，含全部失败路径）

---

## 1. 一句话结论

**C# 模组框架在技术上可行**：外部 DLL 能加载、能执行、能反射访问游戏类型。
**方法补丁（Harmony 式）在当前工具链下不可行**：缺 `Reflection.Emit`，原生替代缺指令缓存刷新手段。
建议 Phase 5 改用游戏自带扩展点（`ModHooks` / `GameRunner`），与桌面 SMAPI 的实际做法一致。

---

## 2. 能力矩阵（全部为设备实测）

| 能力 | 结论 | 证据 |
| --- | --- | --- |
| 运行时身份 | MonoVM `8.0.1124.51707`，.NET for Android，TFM net8.0 | `runtime-feasibility.md` §3 |
| JIT | ✅ 存在且启用（`IsDynamicCodeSupported=True`） | 探针 T00 |
| AOT 模式 | `normal`（混合，非 full）；65 个 `libaot-*.so` | 重定位解析 |
| 程序集容器 | `XABA v1` + `XALZ`(LZ4)，65 个程序集已全部提取并识别 | `decompile-report.md` §1–2 |
| 加载外部 DLL | ✅ **仅** `Assembly.LoadFrom(string)` | 探针 T06 + Loader |
| `Assembly.Load(byte[])` | ❌ 被 ILLink 裁剪 | 编译期 CS1503 + 运行时 |
| 执行 IL（静态/实例/泛型/事件） | ✅ 全部通过 | 探针 T01–T04 |
| 反射（字段/方法/属性） | ✅ 通过 | 探针 T05 |
| **反射访问游戏类型** | ✅ `Game1` 576 方法 / 532 字段 | 探针 T07 |
| 模组静态引用游戏类型 | ✅ `using StardewValley;` 编译通过 | `Mod/ExampleMod.HelloWorld` |
| 写文件 | ✅ 应用私有目录 | `loader.log` / `probe.txt` |
| `Reflection.Emit` | ❌ `DynamicMethod` 与 `AssemblyBuilder` 均不可用 | 探针 T09/T10 + 编译期 |
| 原生 detour 内存前提 | ✅ 取址 + `mprotect` RWX/还原 均成功 | 探针 T11 |
| 原生 detour 实际生效 | ❌ 直接写跳转桩 → 原生崩溃；缺刷缓存手段 | 探针 T12 |

---

## 3. 三条硬约束（违反即失败）

1. **必须用游戏的程序集当参考程序集编译**（`-p:AndroidRefsDir=…`）。
   用桌面 net8.0 编译会让 `Assembly.Load(byte[])`、`Directory.GetFiles`、
   `RuntimeMethodHandle.GetFunctionPointer` 这类"桌面有、Android 没有"的成员一路混到运行时才炸。
2. **一个方法只许碰一个"未证实"的 BCL 成员**。裁剪的连坐是方法级的：
   方法体里有一个坏引用，整个方法的 JIT 就失败，方法内 `try/catch` 拦不住。
3. **模组目录必须由应用自己创建**（或放应用私有目录）。
   由 root/shell 创建的 `/sdcard/Android/data/<pkg>/…` 在应用进程内 `File.Exists` 返回 **false**
   （不是抛异常，极易误判）。

---

## 4. 门控

| 实验 | 状态 |
| --- | --- |
| E1 加载外部 DLL 并执行 IL | ✅ 通过（**模拟器**） |
| E2 反射访问游戏类型 | ✅ 通过（**模拟器**） |
| E3 事件钩子 | ✅ 间接通过（`OnCreate` 注入执行 + 事件用例） |
| E4 去 AOT 回退 | 未做（已被 E5 结论取代，无必要） |
| E5 Harmony / 方法补丁 | ❌ 判定不可行（当前工具链） |
| E6 独立管理器存储通道 | 未做（需第二台设备或 Android 14） |

**Phase 1 不启动**：E1/E2 只在 MuMu 12 模拟器（x86_64 + ARM 翻译）复现，
按任务书 §2.3 不能替代 ARM64 真机。

---

## 5. 需要项目方决策的三件事

### D1. 提供一台 ARM64 真机（阻塞 Phase 1）

没有它，E1/E2 无法从"模拟器通过"变成"真机通过"。这是唯一的硬阻塞。

### D2. 是否引入 NDK（决定方法补丁这条线是否保留）

现状：原生 detour 的内存前提成立，但 AArch64 改代码后必须由用户态刷指令缓存，
而三个运行时/系统库**都没有导出相关符号**，本机也没有 NDK。

若要做：装 NDK → 写 ~20 行 `libmodflush.so`（`__builtin___clear_cache`）→ 随 APK 打包 → 重做 T12。
代价：新工具链依赖（约 1 GB）+ 每 ABI 一个 .so + 真机验证。
**不建议现在就做**——先按 D3 走扩展点路线，确有需要再回来。

### D3. Phase 5 的目标形态

建议从"Harmony 式方法补丁"改为**游戏自带扩展点**：

| 扩展点 | 用途 |
| --- | --- |
| `StardewValley.ModHooks` | 游戏内建钩子，SMAPI 桌面版的主力路径 |
| `GameRunner` / `SGame` 覆写 | 接管 Update/Draw 循环 |
| `ContentCoordinator` | 资源加载管线 |

这条路线不依赖 `Reflection.Emit`、不依赖原生补丁，且与桌面 SMAPI 的实际架构一致。

---

## 6. 交付物

| 文件 | 内容 |
| --- | --- |
| `docs/audit/runtime-feasibility.md` | 技术可行性报告（38 KB，含 §6.5/§7.2 的实测修订） |
| `docs/audit/decompile-report.md` | 反编译与程序集审计 |
| `docs/audit/adr-0001-mod-injection.md` | 架构决策记录（注入点、加载链、Harmony 评估、许可证） |
| `docs/audit/bcl-surface.md` | BCL 可用面与模组编译规范 |
| `docs/audit/evidence-index.md` | 证据索引与复现命令 |
| `docs/audit/phase0-status.md` | 按轮次的完整过程记录（含全部失败路径） |
| `Mod/MonoRuntimeProbe/` | 探针（T00–T07 + T11 通过，T12 崩溃） |
| `Mod/ModFramework.Runtime/` | 最小 Loader（加载 + 报告） |
| `Mod/ExampleMod.HelloWorld/` | 模组编译期验证 |
| `Tools/ModInjector/` | Cecil IL 注入 + IL 自检（`IlChecks`） |
| `Tools/AssemblyStore/` | `assemblies.blob` 读写（XABA/XALZ/LZ4） |
| `Tools/deploy-apk.ps1` | 装机脚本（含设备端 APK 哈希证明） |

---

## 7. 环境说明

- 设备：MuMu 12 模拟器，Android 12 / API 32，x86_64 + ARM 翻译层。**非真机**。
- 本机：无 .NET SDK（用项目内私有 SDK `.tmp/phase0/dotnet`）、无 Ghidra、无 NDK。
- 仓库：所有改动在工作区未提交；`.tmp/` 已 gitignore；原版 APK 与 blob 未改动。
