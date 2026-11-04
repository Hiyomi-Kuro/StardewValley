# Phase 0.5 证据索引

- 日期：2026-11-05
- 对应报告：`phase0.5-report.md`
- 工作目录：`D:\Download\DSH\StardewValley`，branch `main`，HEAD `fa4f5c3baa64a0d1c5c10c1ff3f5d314df3a022a`
- 本阶段证据根：`.tmp/phase0.5/`（gitignored；**删除后按本文命令可重建的项标 ✅，不可重建的标 ⚠️**）

---

## 0. 证据等级

| 级 | 含义 | 能否复现 |
| --- | --- | --- |
| **A** | 直接读仓库源码/二进制得到的事实 | ✅ 任何人在同一 HEAD 可复现 |
| **B** | 本阶段实际执行的离线测试（构建、注入、容器、契约） | ✅ 按 §5 命令可复现 |
| **C** | 本阶段读取的设备状态（只读 adb） | ✅ 同一设备可复现；换设备结果会变 |
| **D** | Phase 0 记录、原始日志已丢失（`.tmp/phase0/` 被删） | ❌ 只剩文档记录 |
| **E** | 外部参考（网页/第三方项目），未独立验证 | ❌ 仅引用 |

> 任何"通过"结论都必须落到 A–C；D 只能写成"记录"，不能写成"已验证"。

---

## 1. Phase 0 证据的可得性（重要）

| 证据 | 原路径 | 现状 | 影响 |
| --- | --- | --- | --- |
| 探针设备报告（8/8 PASS） | `.tmp/phase0/evidence/probe-device-androidrefs.txt` | **不存在** | `Game1 576/532`、`T00 supported=True` 等只能记 D |
| 旧探针报告（3 项失败） | `.tmp/phase0/evidence/probe-device.txt` | 不存在 | D |
| loader 设备日志 | `.tmp/phase0/evidence/loader-device-androidrefs.log` | 不存在 | D |
| 产物哈希清单 | `.tmp/phase0/evidence/artifact_hashes.txt` | 不存在 | D（但 blob/dll 哈希可在本机重算，见 §2.2） |
| 65 个程序集 + 身份表 | `.tmp/phase0/extract/blob000/`、`_identity2.tsv` | 不存在 | 可用 `store.py extract` 重建（✅） |
| 重建脚本（`xa_reloc.py` 等） | `.tmp/phase0/work/` | **不存在** | ⚠️ ELF 重定位级证据（AOT `normal`）无法按原样重放 |
| 装机日志/截图 | `.work/logs-launch.log` 等 | 存在，但是 **2026-11-02** 的旧日志（不含 Phase 0 模组实验） | 不能当 Phase 0 证据 |

结论：**Phase 0 的运行时结论一律降级为 D（记录）**，除本阶段重新验证过的部分（§2）。

---

## 2. 本阶段（Phase 0.5）证据

### 2.1 环境与非侵入式准备

| 项 | 值 | 证据 |
| --- | --- | --- |
| 私有 .NET SDK | 8.0.404，zip 281,358,929 字节，解压到 `.tmp/phase0.5/dotnet/` | `ev-file`：`.tmp/phase0.5/evidence/artifact-hashes.txt`（`783417b4…`）；日志：`.tmp/phase0.5/logs/baseline.txt` |
| 系统改动 | 无（未改 PATH、未装全局 SDK） | 命令即证据（`dotnet --list-sdks` 仍为空） |
| 用户批准 | 已在会话中获得（"批准：装项目私有 .NET SDK 8"） | 会话记录 |

### 2.2 程序集提取与身份（✅ B/A）

| 项 | 值 | 证据文件 |
| --- | --- | --- |
| 主存储 | `assemblies.blob` 58 条，`entry_count=58 total=65 gap=2600 version=1` | `.tmp/phase0.5/logs/extract-blob000.txt` |
| 架构存储 | `assemblies.arm64_v8a.blob` 7 条，`desc_index 56..62`，`gap=0` | `.tmp/phase0.5/logs/extract-blob001.txt` |
| 合计 | 58 + 7 = **65**（与文档一致） | 两个目录的 DLL 计数 |
| 架构存储命名回归 | 修复前把 `System.Private.CoreLib` 写成 `_Microsoft.Android.Resource.Designer.dll` | `.tmp/phase0.5/logs/store-arm64-preextract.txt`（修复前）；修复后 §2.2 表 |
| `StardewValley.dll` | 9,280,000 字节 / SHA `587ff5ff9c258679004977bbeec359aba86649bd3e4860ddcaa428df8f07e047` | 与 Phase 0 记录**完全一致**（这条把 Phase 0 的哈希从 D 提升为 A） |
| 程序集身份 | `StardewValley 1.6.15.24354`，MVID `573bf1e5-3245-46d6-925a-1c3713afb677`；TypeDef 2867 / MethodDef 30067 / Field 18859 / ImplMap 5192 / AssemblyRef 28 / CustomAttribute 4656；`MainActivity` 43 方法 / 8 字段 | REA `inspect_managed_members`：`ev_e36922650c0be5019691b145d1ac97bbe671f0aa47a37dcd0882d40d38397ac5`（summary view），parent `ev_12ceb610d716097dbed69a6a3f9442cea08a4b94e8cdef59d7ce502265ebc01c` |
| `libaot-*.so` | 65 个，合计 38,564,048 字节 | `jniLibs/arm64-v8a` 实测（与文档一致） |
| 其他 `.so` | 75 个 | 同上 |
| `assemblies.manifest` | 4,937 字节，SHA `0423d19caf72cda38d1acefd8fe73356b643ec05786b6d9a15b7134bed6e3393` | 与文档记录一致 |

> REA evidence ID 属于当次 MCP 会话；原始事实可由同一文件 + `inspect_managed_members` 重放。

### 2.3 注入与容器（✅ B）

全部在 `.tmp/phase0.5/` 的副本上进行，仓库输入未改。

| 产物 | SHA-256 | 大小 |
| --- | --- | --- |
| `sv.full.dll`（注入后，`OnCreatePartTwo` 钩子，默认 `loadfrom`） | `5521edc9d00fb1c1691cc52201f36c089a28e9ab99074a9a9b99149d043d5b33` | 9,261,568 |
| `assemblies.full.blob`（原位替换后） | `4fdc92ae06946baacf99fab5146f94639de85ba6538c813e284bca12449cdc2c` | 6,183,615 |
| `assemblies.original.blob`（仓库 blob 副本） | `c8ee504eea163d2c82d89d0004c8924314b3e6b41adb649ea5f136151eecabd9` | 5,588,326 |
| `mods/ModFramework.Runtime.dll` | `8b77214e767d12c60aaad97c8b403b0aaf47dc233fa74682c40fa471aa8b8929` | 6,656 |
| `mods/MonoRuntimeProbe.dll` | `b330190f1add9592105e3caa40567f4560a303d4cb246901cc942076fd32e268` | 12,800 |
| `mods/MonoRuntimeProbe.Dependency.dll` | `fa1891db364676d7d7ad7742de560dbc2379a604d5cc746f0577a60ecacc0bc9` | 5,120 |
| `mods/ExampleMod.HelloWorld.dll` | `1ec12bc02bf0a7c0112da2187ca02f2c5e0bdca7f3fbbefd599632600861ed87` | 4,096 |

机器可读清单：`.tmp/phase0.5/evidence/artifact-hashes.txt`（含 APK、blob、manifest、SDK zip）。

注入自检输出（Cecil，`verify`）：

```
bootstrap-type : present
bootstrap-start: present (51 instr, 182 bytes)
hook-target    : StardewValley.MainActivity::OnCreatePartTwo
hook-call      : present at IL_0000
loader-api     : loadfrom (Assembly.LoadFrom(string))
hook-is-first  : yes
mvid           : 8a5302fe-d4db-4e36-a1bf-cc16ba5c11d0
RESULT: OK
```

容器不变量（`test_mod_pipeline.py`）：

```
rebuild_structure_unchanged            version=1 entries=58 total=65 reserved=0 gap=2600
rebuild_leaves_other_entries           57/57 untouched
rebuild_preserves_descriptor_index     all 58 XALZ descriptor indices preserved
rebuild_roundtrip_patched_entry        entry 10 reads back 9261568 bytes ...
rebuild_all_entries_are_pe             58 entries
store_verify_positive                  rc=0
store_verify_detects_wrong_hash        rc=1
```

### 2.4 构建（✅ B）

| 工程 | 结果 | 备注 |
| --- | --- | --- |
| `Tools/ModInjector` | `0 warning / 0 error` | 依赖 Mono.Cecil 0.11.6（NuGet 缓存已有） |
| `Mod/ModFramework.Runtime`（Android refs） | `0 / 0` | 60 秒内；证明未引用被裁剪的 BCL |
| `Mod/MonoRuntimeProbe`（Android refs） | `0 error / 1 warning` | `CS0169 _staticCounter`（仅反射使用，刻意保留） |
| `Mod/ExampleMod.HelloWorld`（Android refs） | `0 / 0`，4096 字节 | 与 Phase 0 记录的大小一致 |
| `Tools/tests/LoaderContract` | `0 / 0` | 桌面契约宿主 |

### 2.5 `Loader.Start()` 契约对照（✅ B，最强的一条代码证据）

| 版本 | 输出 | 日志 |
| --- | --- | --- |
| HEAD（修复前） | `return-null=false`、`return-value=loader-report: locate=…; mods-exists=yes; write-log=ok; probe-run=absent;`、`loader-report=False` | `.tmp/phase0.5/logs/loader-contract-before.txt` |
| 修复后 | `return-null=true`、`loader-report=True`、报告正文含 `locate= … probe-run=absent` | `.tmp/phase0.5/logs/loader-contract-host.txt` |

说明：旧实现在**四项探测全部成功**时仍返回非空字符串，而 Bootstrap 把非空当失败——
即"成功被当失败处理"，且异常被 catch 吞掉。这不是文档错误，是代码缺陷。

### 2.6 设备 E0（✅ C）

原始输出：`.tmp/phase0.5/logs/e0/device-precheck-127.0.0.1_16384.txt`（由 `Tools/device-precheck.ps1` 生成）。

| 事实 | 值 |
| --- | --- |
| 可用 endpoint | `127.0.0.1:16384`（另两个 `5555/7555` → `error: closed`） |
| `ro.product.cpu.abi` | `x86_64`（**不是** arm64-v8a） |
| `ro.product.cpu.abilist` | `x86_64,arm64-v8a,x86,armeabi-v7a,armeabi` |
| `ro.dalvik.vm.native.bridge` | `libnb.so`（ARM 翻译层） |
| Android | 12 / API 32 / patch 2022-04-05 |
| 型号（伪装） | vivo `PD2309` / `V2309A` |
| Root | `su -c id` → `uid=0`（**仅模拟器**） |
| 包 | `com.chucklefish.stardewvalley`，versionCode 239 / 1.6.15.0，安装 2026-11-04 22:58:01 |
| 安装包 SHA-256 | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| 本地产物 SHA-256 | 同上（388,967,580 字节）→ 设备运行的确实是这个本地构建 |
| APK 内 blob | `c8ee504e…` = 仓库原始 blob → **未注入的基线** |
| 残留 | 无 `Mods/`、无 `loader.log` |

→ `device-class = NOT-ARM64-PHYSICAL`；E1/E2 = **BLOCKED**。

---

## 3. 结论 → 证据映射

| 结论（phase0.5-report §） | 等级 | 直接证据 |
| --- | --- | --- |
| 仓库干净、HEAD=`fa4f5c3…`（§2） | A | `.tmp/phase0.5/logs/baseline.txt`；`git status` |
| ADR D1 旧表述已修（§4.2 #2） | A | `docs/audit/adr-0001-mod-injection.md` diff |
| 探针 README 与源码冲突已修（§4.2 #3） | A | `Mod/MonoRuntimeProbe/README.md` vs `ProbeEntry.cs`/`DetourProbe.cs` |
| 65 个程序集可提取且命名正确（§4.3） | B | §2.2；`arch_store_names` 检查 |
| `StardewValley.dll` 哈希/身份与记录一致（§4.3） | A | 本机哈希 + REA `ev_e3692265…` |
| 文档 TypeDef/MethodDef/ImplMap 数字一致（§4.3） | A | REA `table_row_counts` |
| `Game1` 576/532 不可复核（§4.3） | D | 探针需设备，原日志已丢失 |
| Bootstrap 注入在第 1 条指令（§5） | B | `verify` 的 `hook-call IL_0000` + `hook-is-first yes` |
| 注入 IL 用 `LoadFrom`（§5 #7） | B | `verify` 的 `loader-api` 行 + `legacy_artifact_rejected_by_verify` |
| 旧默认 `load(byte[])` 会让默认构建静默失效（§6.2） | B（推断）+ A（源码） | HEAD 版 `Program.cs` 的 `useLoadFrom` 默认值；`--loader-api load` 复现 |
| `Loader.Start()` 契约缺陷与修复（§6.1） | B | §2.5 对照 |
| 容器原位替换不破坏其它条目（§8.2） | B | `rebuild_leaves_other_entries` 等 5 项 |
| 原始输入未被覆盖（§8.1） | B | `inputs_not_modified`（重算 blob/manifest 哈希） |
| 设备是 x86_64 模拟器（§7.1） | C | §2.6 |
| E1/E2 模拟器 PASS（§7.2） | D | Phase 0 文档记录，原始日志缺失 |
| E1/E2 真机 BLOCKED（§7.2） | C | §2.6 设备分类 |
| Harmony 当前工具链 FAIL（§9 末行） | D（T12 记录）+ A（源码） | `DetourProbe.cs` 存在；崩溃现象仅记录 |

---

## 4. 未复核 / Unknown / 不可复核清单

| 项 | 状态 | 原因 |
| --- | --- | --- |
| `Game1` 反射成员数（576/532） | Unknown | 需设备运行探针 |
| `Game1` 声明成员数（558/531） | Unknown（口径不同，未逐项核对） | 需写元数据统计脚本或反编译器 |
| AOT `normal` 的重定位级证据 | ⚠️ 不可复核 | `xa_reloc.py` 已丢失；`.so` 仍在仓库，可重写脚本 |
| `mono_aot_mode_name`、`application_config` 计数 | ⚠️ 不可复核 | 同上 |
| T12 原生崩溃栈 | ⚠️ 仅文字记录 | 未重跑（无测试设备授权） |
| `Directory.GetFiles`/`RuntimeInformation` 裁剪 | 部分可复核（编译期） | 探针用例已删；可用 `bcl-surface.md` §3 的方式重写编译验证 |
| 补丁 DLL 大小 9,261,568 vs 记录 9,261,056 | Unknown | 相差 512 字节；疑为 Phase 0 那次的 Bootstrap 版本不同 |
| 并发/重入行为 | Unknown | 未写并发测试；当前架构下不可达 |
| 真机签名/覆盖安装限制 | Unknown | 未装机 |

---

## 5. 一条命令重建（✅ 部分）

```powershell
cd D:\Download\DSH\StardewValley

# 0) 私有 SDK（若 .tmp/phase0.5/dotnet 不存在）
#    下载 dotnet-sdk-8.0.404-win-x64.zip 到 .tmp/phase0.5/，然后：
tar -xf .tmp\phase0.5\dotnet-sdk.zip -C .tmp\phase0.5\dotnet

# 1) 65 个程序集
$m = "app\src\main\apkroot\assemblies\assemblies.manifest"
python Tools\AssemblyStore\store.py extract app\src\main\apkroot\assemblies\assemblies.blob .tmp\phase0.5\refs --manifest $m
python Tools\AssemblyStore\store.py extract app\src\main\apkroot\assemblies\assemblies.arm64_v8a.blob .tmp\phase0.5\refs --manifest $m

# 2) 全部离线回归（27 项：注入 + 容器 + Loader 契约）
python Tools\tests\test_mod_pipeline.py

# 3) E0 只读预检（不改设备）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\device-precheck.ps1 `
    -OutFile .tmp\phase0.5\logs\e0\device-precheck.txt

# 4) 静态数字复核（可复现的文档数字）
(Get-ChildItem app\src\main\jniLibs\arm64-v8a -Filter libaot-*.so | Measure-Object Length -Sum).
    Sum                              # 期望 38564048
(Get-ChildItem app\src\main\apkroot\assemblies\assemblies.blob).Length   # 期望 5588326
```

不可重建项：Phase 0 的 `.tmp/phase0/work/*` 脚本与 `.tmp/phase0/evidence/*` 日志（见 §1/§4）。
