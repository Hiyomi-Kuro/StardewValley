# Phase 1.1.7 报告：Bootstrap-Integrated APK Build Pipeline 与 Approval-Gated In-Place Upgrade

**本阶段目标**：把仓库里已有但从未接入构建的 `Tools/ModInjector` + `Tools/AssemblyStore/store.py`
接进正式 APK 构建入口，产出**注入 `ModFramework.Bootstrap` 的 mod-enabled APK**，并完成静态验收
与只读设备预检。默认不安装、不卸载、不启动。

**一句话结论**：mod-enabled APK 已真实构建、逐条验收通过（Bootstrap 注入、descriptor index、
尺寸上限、APK 内容、zipalign、apksigner 全部通过），但**签名不连续**——设备上那份 APK 的签名证书
是 `611c8fa4…`，本机现存唯一的 keystore 给出 `858ef62d…`，原来那把 key 已不在本机。因此安装门禁
是 `UPDATE_INCOMPATIBLE_RISK`，未执行任何设备写入。

---

## 0. 总状态

| 项 | 值 |
| --- | --- |
| 阶段状态 | **`UPDATE_INCOMPATIBLE_RISK`**（构建与静态验收 PASS；安装门禁 BLOCKED 于签名） |
| 同义标记 | `SIGNING_KEY_UNAVAILABLE`（签过装机 APK 的那把 key 不在本机） |
| B（安装/更新批准） | **NOT REQUESTED**：§6 规定只有签名完全一致才可请求，本阶段不满足前置条件 |
| C（启动/运行时取证批准） | **NOT REQUESTED** |
| 设备写入次数 | **0** |
| `adb install` / `uninstall` / `pm clear` / `am start` / `force-stop` | **均未执行** |
| 本地测试 | 9/9 suites PASS（见 §10） |
| 工作区 | 12 modified + 3 added，**未 commit、未 push** |

状态选择依据（任务书 §10）：构建成功但安装门禁被签名阻断，不属于"等待批准"，也不属于"构建失败"，
而是任务书明确列出的 `UPDATE_INCOMPATIBLE_RISK` / `SIGNING_KEY_UNAVAILABLE` 分支。

---

## 1. Git 基线与远程状态（P117-01）

### 1.1 Gate 0 无损核对

仓库 `D:\Download\DSH\StardewValley`，`origin` = `https://github.com/Hiyomi-Kuro/StardewValley.git`。

| 项 | 实测值 | 与任务书 §0.1 对照 |
| --- | --- | --- |
| branch | `main` | 一致 |
| `HEAD` | `35e3dc8f3b9f191abbf1c69affd905283c982a92` | 一致（预期 `35e3dc8`） |
| `origin/main` | `35e3dc8f3b9f191abbf1c69affd905283c982a92` | 一致 |
| ahead / behind | `0 / 0` | — |
| 开库时 `git status --short` | 空（工作区干净） | 无用户未提交改动 |

本次未执行 `git fetch`（远程跟踪引用已与 HEAD 相同，且任务书只允许 `fetch --prune` 刷新跟踪信息；
没有可用网络调用需求）。GitHub Actions run `42251974337`（run #8，success，3/3 jobs）为任务书 §0.1
记录值，本阶段**未重新查询 GitHub API**，也未引用它作为本阶段的验证结论。

### 1.2 本阶段结束时的 Git 状态

```
ahead/behind origin/main: 0/0      HEAD 未变: 35e3dc8
 M .github/workflows/ci.yml
 M .gitignore
 M Mod/ModFramework.Runtime/Loader.cs
 M Mod/P114.RuntimeProbe/README.md
 M README.md
 M Tools/ModInjector/Program.cs
 M Tools/README.md
 M Tools/tests/ModFramework.Tests/HostTests.cs
 M Tools/tests/run-all.ps1
 M app/build.gradle.kts
 M app/src/main/kotlin/com/kaori/svpatch/Main.kt
 M app/src/main/kotlin/com/kaori/svpatch/Signing.kt
 M docs/architecture/phase1-loader-design.md
 M docs/audit/adr-0001-mod-injection.md
?? Tools/build_modded_apk.py
?? Tools/tests/test_modded_build.py
?? Tools/verify_modded_apk.py
?? docs/audit/phase1.1.7-evidence-index.md
?? docs/audit/phase1.1.7-report.md
```

diffstat：**14 files changed, 507 insertions(+), 28 deletions(-)**；另有 **5 个新增文件**未计入 diffstat。

**DSH 没有 `git commit`，也没有 `git push`。** 本地未 push 是预期状态。

---

## 2. Phase 1.1.7-A：Mods 根一致性修正（P117-02）

### 2.1 问题

| 来源 | 修正前 | 修正后 |
| --- | --- | --- |
| Bootstrap（注入 IL 的 `Ldstr`） | `/sdcard/Android/data/<pkg>/files/Mods` | 不变 |
| `Loader.DefaultModsRoot()`：`Location` 可用 | `Location` 的父目录 | 不变（仅加归一化） |
| `Loader.DefaultModsRoot()`：`Location` 为空/异常 | **`/data/data/<pkg>/files/Mods`** | **`/sdcard/Android/data/<pkg>/files/Mods`** |

Phase 1.1.6 已实测游戏冷启动会把内部 `files/Mods` 迁移到外部目录；旧 fallback 会让 Loader 去读一个
Bootstrap 永远不会写的目录。

### 2.2 改动（`Mod/ModFramework.Runtime/Loader.cs`）

- 新增常量 `internal const string ExternalModsRoot`（唯一字面值来源）；
- `DefaultModsRoot()` 只负责取 `typeof(Loader).Assembly.Location`，取不到就返回该常量；
- 新增纯函数 `internal static string ResolveModsRoot(string? location)`：取父目录并
  `PathRules.Normalize`（`\` → `/`），`null` / 空串 / 只有文件名 / `GetDirectoryName` 抛异常都返回外部路径；
- 生产默认值不变：`Location` 可用时仍是它的父目录。

### 2.3 测试（host，桌面 .NET）

`Tools/tests/ModFramework.Tests/HostTests.cs` 新增 `ModsRootConsistency`，8 条断言：

| 断言 | 覆盖 |
| --- | --- |
| `modsroot_external_constant` | 常量等于外部路径 |
| `modsroot_null_location` / `modsroot_empty_location` | Location 为 null / 空串 |
| `modsroot_bare_file_location` | Location 只有文件名（无父目录） |
| `modsroot_location_parent` | Location 指向外部 Mods → 取父目录 |
| `modsroot_normalizes_separators` | `C:\mods\test\x.dll` → `C:/mods/test` |
| `modsroot_never_private_fallback` | 四类分支都不产生 `/data/data/...` |
| `modsroot_default_uses_location` | 生产默认值仍是 Location 父目录 |

`test_loader.py`：**45/45 驱动检查 + 481/481 host 检查**（原 473 + 新增 8），0 failed。

### 2.4 注入器侧的一致性

`Tools/ModInjector/Program.cs` 的 `Verify` 新增：

- `mods-dir` / `mods-dir-expect` / `mods-dir-ok`：从 `Start()` 里的 `Ldstr` 反推烘焙目录，必须等于
  期望值（默认外部 Mods 根，可用 `--mods-dir` 覆盖）；
- 默认拒绝 `/data/data/...` 开头的烘焙路径，除非显式 `--allow-private-mods-dir`（诊断构建）。
- `patch` 结束时的自校验现在会把 `--mods-dir` / `--runtime-dll` 传给 `verify`，避免"用自定义
  Mods 根 patch 后自校验必然失败"的假阴性。

`Assembly.LoadFrom(string)` 仍是唯一默认 API；`Assembly.Load(byte[])` 只在 `--loader-api load`
诊断路径存在，`Verify` 会因 `allow-bytes-api` 缺失而失败。

### 2.5 `bootstrap-report.txt`：评估结论是不实现

任务书 §3 要求评估"非致命、低依赖的 `bootstrap-report.txt` 阶段诊断"，并且只有在能证明安全时才实现。
**本阶段的结论是保留当前 catch 语义、不实现该文件**，理由：

1. `Bootstrap.Start()` 是 `ModInjector` 用 Cecil 逐条发射的 IL，现有结构是"整个方法体一个 try 区 +
   一个 `catch (Exception) { pop; leave ret }`"。要在 catch 内写文件，必须再嵌一层 try/catch
   （`File.AppendAllText` 自身可能抛 `IOException`），而 `IlChecks` 目前只建模单层处理器；
   任何发射错误都会让 `Start()` 变成 `InvalidProgramException`，正好违反"诊断失败绝不阻止游戏启动"。
2. 现有替代证据已经足够：`Loader` 在**真正运行时**会写 `<Mods>/loader-report.txt` 与
   `<Mods>/loader.log`；`Start()` 的每个阶段失败都带不同消息（`stage-1:file-missing` …
   `stage-5:invoke-failed`），一旦拿到 `loader-report.txt` 缺失 + 游戏正常启动，就能判定是 Bootstrap
   阶段失败而不是 Loader 内部失败。
3. `--no-catch` 仍然只是诊断开关，**不是**正式构建默认值；本阶段的 mod-enabled 构建没有使用它。

该限制已写进 §14 的未决项，需要在设备上有一次真实启动证据后再决定是否加诊断。

---

## 3. Phase 1.1.7-B：mod-enabled 构建入口（P117-05）

### 3.1 两个入口，互不覆盖

| | `assembleDebug`（baseline） | `assembleModdedDebug`（mod-enabled） |
| --- | --- | --- |
| 输入 blob | `app/src/main/apkroot/assemblies/assemblies.blob` 原样 | `.tmp/build-modded/modded-assemblies.blob` |
| 产物 | `app/build/outputs/apk/debug/app-debug.apk` | `app/build/outputs/apk/modded/app-modded-debug.apk` |
| work dir | `.tmp/build` | `.tmp/build-modded` |
| keystore | `<work>/debug.keystore`（缺失时生成） | 必须已存在（`--require-existing-keystore`） |
| 依赖 | `classes` | `classes` + `prepareModdedAssemblies` |

`assemble` 任务仍然只依赖 `assembleDebug`：mod-enabled 不会被 `./gradlew assemble` 意外触发。
`Main.kt` 的注入是**可选参数**，缺省完全不注入，所以 baseline 行为与 Phase 1.1.6 逐字节相同
（见 §6.4 的 baseline SHA 复核）。

实际执行的构建命令：

```powershell
.\gradlew.bat --offline --console=plain :app:assembleModdedDebug `
  "-Pdotnet=D:\Download\DSH\tasks\20261106-p115-recursive-deploy-a1\work\dotnet\dotnet.exe" `
  "-Pkeystore=.work/debug.keystore"
```

（`-Pdotnet` 指向本机私有 SDK 8.0.404；系统 PATH 上只有 .NET 运行时，没有 SDK。）

### 3.2 代码接口

| 文件 | 改动 |
| --- | --- |
| `app/build.gradle.kts` | 新增 `prepareModdedAssemblies`（Exec → `Tools/build_modded_apk.py`）与 `assembleModdedDebug`（JavaExec）；baseline 的 `assembleDebug` 原样保留 |
| `app/src/main/kotlin/com/kaori/svpatch/Main.kt` | 新增 `--bootstrap-blob` / `--keystore` / `--require-existing-keystore`；注入只写 staging 的 `decoded/unknown/assemblies/assemblies.blob`，校验 `XABA` magic，绝不写回 `app/src/main/apkroot` |
| `app/src/main/kotlin/com/kaori/svpatch/Signing.kt` | 新增 `requireExistingKeystore`：keystore 缺失时 `check(!requireExistingKeystore)` 直接失败，不生成新 key |
| `Tools/build_modded_apk.py`（新增） | 编排：解包 → 注入 → verify → 原位替换 → 逐条复核 → 输出 JSON manifest |
| `Tools/verify_modded_apk.py`（新增） | APK 静态验收 + 签名连续性判定 + 安装准入状态 |
| `Tools/tests/test_modded_build.py`（新增） | CI 可跑的静态/合成测试（49 条） |

### 3.3 编排脚本的实际步骤与结果

`python Tools/build_modded_apk.py --dotnet <sdk> --work .tmp/build-modded --out-blob … --manifest-json …`
→ **44/44 检查通过**，`RESULT: OK`。关键阶段：

| 步骤 | 结果 |
| --- | --- |
| 输入 SHA-256 | blob `c8ee504e…cabd9` / manifest `0423d19c…6e3393` / arch blob `c465d9d4…d4cd78` / `libxamarin-app.so` `11384d74…35132d9` |
| 解包 | 58（`000`）+ 7（`001`）= 65，清单名/下标一一对应、无重名 |
| `dotnet build ModInjector` | rc=0（SDK 8.0.404） |
| `ModInjector patch` | rc=0，自校验 `RESULT: OK` |
| `ModInjector verify` | rc=0，`RESULT: OK`（详见 §4） |
| `store.py replace` | rc=0，只替换既有条目 |
| 新 blob 复核 | header/version、58 / 65、gap 2600 B、descriptor 7、57/57 未变 |
| 从新 blob 重新解出再 verify | rc=0，`RESULT: OK` |
| `store.py list/verify/selftest` | 全部 rc=0（selftest 58/58 round-trip ok） |

---

## 4. 注入结果（P117-03）

`ModInjector verify` 对 staging 的 `StardewValley.dll` 输出（并对从新 blob 重新解出的同一份再验一次）：

| 项 | 值 |
| --- | --- |
| 目标方法 | `StardewValley.MainActivity::OnCreatePartTwo` |
| `bootstrap-type` | present |
| `bootstrap-start` | present（51 instr / 182 bytes） |
| `hook-call` | present |
| `hook-is-first` | **yes**（首条非 `nop` 指令就是 `call ModFramework.Bootstrap::Start`） |
| `loader-api` | **loadfrom (Assembly.LoadFrom(string))** |
| `mods-dir` | `/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods` |
| `mods-dir-ok` | yes（且不是 `/data/data/…`） |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` → `5b6d1a90-9f0e-4ca0-9557-8730cdf227df` |
| 注入后 DLL | SHA-256 `97b289181218951020d098aa61450a3aea637e8d092871da2c757a76e6946b17`，9,261,568 B |

`IlChecks.Validate` 同时对生成 IL 做了分支目标、`leave` 保护区、栈深一致性检查，未报错。

---

## 5. 容器（P117-04）

| 项 | 结果 |
| --- | --- |
| magic / version / reserved | `XABA` / 1 / 0（原样） |
| entry_count | 58（未变） |
| total_count | 65（未变） |
| gap（hash→store 映射表） | 2600 B，逐字节原样 |
| `StardewValley` descriptor_index | **7**（保留；未改成 0） |
| `StardewValley` 解压长度 | 9,261,568 B |
| expected size（`libxamarin-app.so` 尺寸表 index 7） | 9,280,000 B → **9,261,568 ≤ 9,280,000，余量 18,432 B** |
| 其余 57 条 | 解压后 SHA-256 与 descriptor 全部与原始一致（57/57） |
| `assemblies.arm64_v8a.blob` | SHA-256 `c465d9d4…d4cd78`，未变 |
| `assemblies.manifest` | SHA-256 `0423d19c…6e3393`，未变 |
| 源 `assemblies.blob` | SHA-256 `c8ee504e…cabd9`，未变（构建后复核） |

**尺寸表的来源是可复核的，不是硬编码常量**：脚本在 `libxamarin-app.so` 里按
`descriptor_index * 16` 定位 16 字节表（本次表偏移 53,512），并做了一次自校验——
58 个条目中 **57 个** 的表值与各自"解压后长度"完全相等。唯一例外是 `Mono.Android`
（表值 670,208 / 实际 584,192），这是既有镜像里的一个已存在差异，与本阶段无关；StardewValley
那一条（index 7）精确等于其原始长度 9,280,000，因此它就是运行时强制的上限。

未做且不允许做的绕过：没有修改 `libxamarin-app.so` / `libmonodroid.so`，没有新增程序集条目，
没有零填充伪造尺寸，没有改 descriptor index。

---

## 6. APK 静态验收（P117-06）

### 6.1 产物

| 项 | 值 |
| --- | --- |
| 路径 | `app/build/outputs/apk/modded/app-modded-debug.apk` |
| 大小 | 389,561,500 B（371.51 MiB） |
| SHA-256 | `3ff0b7f7c8eefdf5e1791d2c48242626cbaae0067ad6cb8dadce76c04dee51d6` |
| package | `com.chucklefish.stardewvalley` |
| version | versionCode **239** / versionName **1.6.15.0** |
| native-code ABI | `arm64-v8a`（与 baseline 完全相同） |
| 容器内 blob | SHA-256 `cbb6ac8cb2d66d69f6ce354e1af13b11b48140c6c79c3bd62c6428c7dd59b74f`（6,183,615 B） |

`python Tools/verify_modded_apk.py` → **34 PASS / 0 FAIL / 1 BLOCKED**（BLOCKED 是签名，见 §7）。

### 6.2 内容未被非预期修改（逐条目 CRC 比对）

候选与 baseline 各 **3,672** 个 zip 条目，条目集合完全相同；内容不同的条目只有
`assemblies/assemblies.blob` 与 `META-INF/` 下的签名文件。其余条目 CRC + 大小逐一相等，包括：

| 条目 | CRC32 / 大小 |
| --- | --- |
| `classes.dex` | 1967592106 / 2,382,580 |
| `classes2.dex` | 2252200318 / 69,684 |
| `assemblies/assemblies.manifest` | 3290967193 / 4,937 |
| `assemblies/assemblies.arm64_v8a.blob` | 417073782 / 1,250,044 |
| `lib/arm64-v8a/libxamarin-app.so` | 3068395380 / 56,416 |
| `lib/arm64-v8a/libmonodroid.so` | 809156845 / 334,520 |
| `assets/Content/XACT/music.xwb` | 674928760 / 258,574,094 |

### 6.3 注入程序集从 APK 里重新取回并再验证

从 APK 解出 `assemblies/assemblies.blob` → 解析出 58 条目 → 取 `StardewValley`：

- 解压后 SHA-256 = `97b28918…46b17` = patched DLL（一致）；
- descriptor_index = 7（一致）；
- 其余 57 条与源 blob 逐条 SHA 一致；
- 对该 DLL 再跑一次 `ModInjector verify`（不是验 staging 副本）→ `RESULT: OK`。

### 6.4 baseline 与源树未被改动

| 项 | 值 | 结论 |
| --- | --- | --- |
| baseline `app-debug.apk` | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d`（388,967,580 B） | 与任务书 §5 记录**逐字符相同**，未被本阶段覆盖 |
| 源 `assemblies.blob` | `c8ee504e…cabd9` | 未变 |
| 源 `assemblies.arm64_v8a.blob` | `c465d9d4…d4cd78` | 未变 |
| 源 `assemblies.manifest` | `0423d19c…6e3393` | 未变 |

### 6.5 对齐与签名方案

| 检查 | 工具 | 结果 |
| --- | --- | --- |
| `zipalign -c -p 4` | build-tools 35.0.0（构建所用版本） | rc=0 |
| `zipalign -c -p 4` | build-tools 36.0.0（独立复核） | rc=0 |
| `apksigner verify --print-certs` | 35.0.0 | rc=0；v1/v2/v3 全部 true |
| `apksigner verify --print-certs` | 36.0.0 | rc=0；v1/v2/v3 全部 true |

管道自身选 35.0.0（`Sdk.kt` 里 35.0.0 优先，代码既定行为），验收脚本用 36.0.0 独立复算，
两个版本结论一致。

---

## 7. 签名连续性与安装准入（P117-05 / P117-07）

### 7.1 事实

| 项 | SHA-256 | 说明 |
| --- | --- | --- |
| baseline APK 的 signer | `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de` | 与任务书 §5、Phase 1.1.4/1.1.5/1.1.6 记录一致 |
| 设备上已安装 APK 的**字节** | `65e1e2af…051f9d`（E0 实测） | 与本地 baseline APK 逐字节相同 → 已安装包签名**也是** `611c8fa4…` |
| 候选 APK 的 signer | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` | 用 `.work/debug.keystore` 签署 |
| `.tmp/build/debug.keystore` | **不存在** | 文档记录的默认路径 |
| `.work/debug.keystore` | 存在（2,666 B，仅一个 alias `androiddebugkey`） | 证书 DER SHA-256 = `858ef62d…`，**不是** `611c8fa4…` |

`keytool -exportcert` + SHA-256 与 `apksigner --print-certs` 两种方式对同一把 key 得到同一个
`858ef62d…`，所以这不是工具差异，而是**两把不同的 debug key**（DN 相同，都是
`CN=Android Debug, O=Android, C=US`）。

### 7.2 结论

签过 baseline/装机 APK 的那把 key **已不在本机**（`.tmp/build` 整个目录已不存在，`.work/debug.keystore`
是 2026-11-02 另一次构建留下的另一把 key）。因此：

- 本阶段**不存在**能产出"可覆盖安装"候选包的 keystore；
- 状态标记 `SIGNING_KEY_UNAVAILABLE` / `UPDATE_INCOMPATIBLE_RISK`；
- 未卸载、未尝试 `adb install -r`、未生成新 key 冒充可升级包；
- 候选 APK 仍然被完整构建并验收（用现存唯一的 key 签名），但它**不是**可安装候选。

### 7.3 如果坚持安装会发生什么

`adb install -r` 会在校验阶段失败（`INSTALL_FAILED_UPDATE_INCOMPATIBLE`）。唯一的"绕法"是
先卸载，而卸载会删除应用私有数据与 **Saves**——任务书 §6 明令禁止，本阶段也没有这么做。

### 7.4 解锁条件

用户若能提供当初签 baseline APK 的 `debug.keystore`（或它的备份），下一次运行即可用
`-Pkeystore=<该文件>` 产出签名连续的候选包，再走 B 批准。除此之外没有安全的替代路径。

---

## 8. E0 只读设备预检（P117-08）

### 8.1 设备身份（只读）

| 项 | 值 |
| --- | --- |
| 连接串 | `127.0.0.1:16384`（MuMu 12 实例 0 的 adb 端口；`127.0.0.1:5555` / `:7555` 指向同一 VM） |
| 与 Phase 1.1.6 的 `emulator-5554` 关系 | 同一台设备：fingerprint、SDK、native bridge、`firstInstallTime` 全部一致（见下表） |
| `ro.build.fingerprint` | `vivo/PD2309/PD2309:12/V417IR/2721:user/release-keys` |
| Android / API | 12 / **32** |
| ABI | `x86_64`，`ro.dalvik.vm.native.bridge = libnb.so`，`ro.kernel.qemu` 为空 |
| 设备分类 | **NOT-ARM64-PHYSICAL**（emulator / translated / x86_64） |
| root | `adb shell id` = uid 2000(shell)；`su -c id` = uid 0（可用） |
| 应用 uid / gid | **10152**（`u0_a152`）；私有目录 `700`、`files` `771` |
| 剩余空间 | `/storage/emulated` 125G，已用 19G，可用约 **99–106 GiB**（两次只读读取） |

### 8.2 已安装包（只读）

| 项 | 值 |
| --- | --- |
| versionCode / versionName | 239 / 1.6.15.0 |
| firstInstallTime / lastUpdateTime | 2026-11-04 22:58:01（与 Phase 1.1.6 记录一致） |
| primaryCpuAbi | arm64-v8a |
| **设备端 APK SHA-256** | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| 与本地 baseline 对比 | **逐字节相同** → 已安装的仍是 baseline（未注入）APK |

### 8.3 外部 Mods 目录（只读复核）

`/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods/`：

- **11 个文件 / 172,191 字节**，与 Phase 1.1.6 的 11 / 172,191 **完全一致** → 按任务书 §5.1
  **没有重新推送**。

| 文件 | SHA-256 |
| --- | --- |
| `ModFramework.Runtime.dll` | `a35a03597e88c38804ad75b91a5df25e77e9a2a19c7934508701833ae3d90fea` |
| `P114.RuntimeProbe/manifest.json` | `05b3bb87bb700e7fa222983c4823294e0ae8ae023e9928b21e82de4204eabc3a` |
| `P114.RuntimeProbe/P114.RuntimeProbe.dll` | `e03e175266d559ec60dc5307789f0f5d69d7ccc78f2c9a9ef8764f5235dfcdf1` |
| `P114.RuntimeProbe/P114.RuntimeProbe.Dependency.dll` | `4a878c03511571d4e084824d2d585dc45561c2469558302d762c33aa3c890883` |
| `P114.RuntimeProbe/probe-fixtures/alpha/P114Alpha.dll` | `0d0f54686e9639c3016c1e825bae2df9ea11b373b547c756f6c00528c4b77bdf` |
| `P114.RuntimeProbe/probe-fixtures/beta/P114Beta.dll` | `f4097e4632521ee4874e3124a100ddeb2edb57ceda8c39707826630d2399a03d` |
| `P114.RuntimeProbe/probe-fixtures/corrupt/P114Broken.dll` | `f5a5fd42d16a20302798ef6ed309979b43003d2320d9f0e8ea9831a92759fb4b` |
| `P114.RuntimeProbe/probe-fixtures/diff-a/P114Diff.dll` | `2ccdee8fd018f4cb289bf71cd879392a8748bf37621e57f5cd32828cbae967f4` |
| `P114.RuntimeProbe/probe-fixtures/diff-b/P114Diff.dll` | `175ae9ef74496478befdd77360ed86ea670d8fbaeca32acbea3bdfef8827ddf2` |
| `P114.RuntimeProbe/probe-fixtures/same-a/P114Same.dll` | `73154fe8d1be1ef6cfde17ce979910196b6ad522cc9c2192a5ac488edd9b4c22` |
| `P114.RuntimeProbe/probe-fixtures/same-b/P114Same.dll` | `73154fe8d1be1ef6cfde17ce979910196b6ad522cc9c2192a5ac488edd9b4c22` |

> 注意：设备上这份 `ModFramework.Runtime.dll` 是 Phase 1.1.6 部署的构建，**不含**本阶段的
> `ResolveModsRoot` fallback 修正。修正已进源码，但要生效仍需一次 Mods 推送（属于写设备，
> 本阶段未获批）。它不影响 Bootstrap：Bootstrap 从外部 Mods 根 `LoadFrom`，所以 `Location`
> 本身就在正确目录，fallback 只有在该 API 不可用时才起作用。

### 8.4 Saves 与 staging

- `Saves`：只用 `test -d` 确认目录存在（`SAVES_DIR_EXISTS`），**没有列目录、没有 pull、没有读写**。
- `/data/local/tmp`：列出的内容全部是 2026-06 至 2026-10 的既有文件（codex / AndroidDex 等其它工具），
  **没有**本阶段写入的任何 staging 残留。
- 本阶段没有在设备上创建、修改或删除任何文件。

---

## 9. 批准门禁（P117-09 / P117-10）

| 门 | 状态 | 说明 |
| --- | --- | --- |
| B：安装/更新 | **NOT REQUESTED** | §6 规定只有"签名完全一致且版本兼容"才可请求；本阶段签名不连续，前置条件不满足，因此没有给出 `adb install -r` 摘要 |
| C：启动/运行时取证 | **NOT REQUESTED** | 安装未发生，C 无对象 |

**未执行的设备命令（全部）**：`adb install`、`adb uninstall`、`pm uninstall`、`pm clear`、
`am start`、`am force-stop`、`logcat`、`screencap`、任何 `push`/`pull`、任何对 Saves 的访问。

**实际执行的设备命令（全部只读）**：`getprop`、`pm path`、`dumpsys package`、`sha256sum`、
`find`、`ls`、`stat`、`df`、`test -d`、`id`、`su -c id`。

Phase 1.1.6 的 A（推送 Mods）/ C（启动）批准**没有被沿用**到本阶段。

---

## 10. 测试与静态检查（P117-11）

### 10.1 全量本地套件

`pwsh -File Tools/tests/run-all.ps1 -Dotnet <SDK 8.0.404> -Refs .tmp/p113/refs`
→ **9 suites, 9 passed, 0 failed, 0 skipped**。

| # | 套件 | 结果 | 类别 |
| --- | --- | --- | --- |
| 1 | `test_ci_offline.py` | 13/13 | CI |
| 2 | `test_modded_build.py`（新增） | **49/49** | CI |
| 3 | `test_p114_probe.py` | 46/46 | CI |
| 4 | `privacy-scan.py --include-untracked` | CRITICAL/HIGH **0**，MEDIUM 0，`RESULT: OK` | CI |
| 5 | `test_device_precheck.ps1` | 10/10 | CI |
| 6 | `test_deploy_safe.ps1` | 95/95 | CI |
| 7 | `test_mod_pipeline.py --refs` | 27/27 | **local-only** |
| 8 | `test_loader.py --refs` | 45/45 驱动 + **481/481** host | **local-only**（host 部分 CI 也跑，但无 refs 时 example-mod 检查 SKIP） |
| 9 | `git diff --check` | 干净 | CI |

### 10.2 新增的 mod-enabled 专用验收

| 命令 | 结果 | 类别 |
| --- | --- | --- |
| `python Tools/build_modded_apk.py …` | 44/44 检查，`RESULT: OK` | local-only（需要真实 65 程序集） |
| `python Tools/verify_modded_apk.py …` | 34 PASS / 0 FAIL / 1 BLOCKED（签名），exit 0 | local-only（需要 APK） |
| `python Tools/tests/test_modded_build.py` | 49/49 | CI（无游戏资产） |

`test_modded_build.py` 覆盖的静态/合成不变量：

- `assembleDebug` 仍是 baseline（3 个位置参数、无 `--bootstrap-blob`、产物仍是 `app-debug.apk`）；
- `assembleModdedDebug` 显式、独立产物/work dir、不依赖 `assembleDebug`、不在 `assemble` 里；
- mod-enabled 构建传 `--require-existing-keystore`，`Signing.kt` 在 keystore 缺失时失败而不是生成；
- Loader / ModInjector / 构建脚本三处的外部 Mods 根**逐字符相同**，Loader 不含私有路径回退；
- `store.py` 的重建保留 `desc`（`XALZ` 第二个 u32）；
- **合成容器**：用真实 `store.py` 造一个 1 条目 XABA（descriptor 7），rebuild 后 descriptor 仍为 7、
  内容 round-trip、gap 与计数保留——即"descriptor index 逻辑"的 CI 可执行版本；
- 构建路径没有写回 `app/src/main/apkroot`，`--out-blob` 无默认值。

### 10.3 CI / local-only / SKIP 的口径

- **CI 可执行**：`test_ci_offline`、`test_modded_build`（新增，已加入 `.github/workflows/ci.yml`
  与 `run-all.ps1`）、`test_p114_probe`、`privacy-scan`、`test_device_precheck`、`test_deploy_safe`、
  无 refs 的 `test_loader`、`dotnet build`。
- **local-only（不进 CI）**：`test_mod_pipeline`、带 refs 的 `test_loader`（example mod / P114 探针）、
  `build_modded_apk.py`、`verify_modded_apk.py`——都需要游戏程序集或真实 APK，不得上传到 Actions。
- **本机 SKIP**：**0 条**（本次所有套件都真实执行；Phase 1.1.6 的 2 条 symlink SKIP 属于部署测试，
  本次 `test_deploy_safe.ps1` 95/95 无 skip）。

CI 绿灯不能当成本阶段的设备结论：真实注入、加载与 R-01…R-14 仍需要设备上的 B + C 批准。

---

## 11. 本次运行的环境

| 项 | 值 |
| --- | --- |
| OS | Windows 11（10.0.26100） |
| .NET SDK | **8.0.404**（`<repo>/.tmp` 之外的私有副本；系统 PATH 上只有 8.0.4 运行时，没有 SDK） |
| Python | 3.12.10 |
| JDK | Temurin 11.0.32.101（`jvmToolchain(11)`） |
| Gradle | 8.10.2（wrapper，离线缓存命中） |
| apktool | 2.11.1（Gradle 依赖缓存） |
| Android build-tools | 35.0.0（管道签名/对齐）、36.0.0（独立复核） |
| adb | `platform-tools` 37.0.1 |
| 构建非确定性说明 | 每次 `patch` 都会生成新的 MVID，因此 patched DLL / blob / APK 的 SHA-256 **每次构建都会变**；除 MVID 外流程是确定的 |

---

## 12. 修改文件清单

| 文件 | 类型 | 摘要 |
| --- | --- | --- |
| `Mod/ModFramework.Runtime/Loader.cs` | 修改 | fallback 改外部 Mods 根；新增 `ExternalModsRoot` 常量与 `ResolveModsRoot` 纯函数 |
| `Tools/ModInjector/Program.cs` | 修改 | `verify` 增加烘焙 Mods 路径校验（`mods-dir-ok`）、默认拒绝私有路径；`patch` 自校验透传 `mods-dir`/`runtime-dll` |
| `Tools/tests/ModFramework.Tests/HostTests.cs` | 修改 | 新增 `ModsRootConsistency`（8 条断言） |
| `app/build.gradle.kts` | 修改 | 新增 `prepareModdedAssemblies` + `assembleModdedDebug` |
| `app/src/main/kotlin/com/kaori/svpatch/Main.kt` | 修改 | 新增 `--bootstrap-blob` / `--keystore` / `--require-existing-keystore`；注入只写 staging |
| `app/src/main/kotlin/com/kaori/svpatch/Signing.kt` | 修改 | `requireExistingKeystore`：禁止自动生成 key |
| `Tools/build_modded_apk.py` | 新增 | 注入 + 容器重建编排（复用 store.py / ModInjector） |
| `Tools/verify_modded_apk.py` | 新增 | APK 静态验收 + 签名连续性 + 安装准入状态 |
| `Tools/tests/test_modded_build.py` | 新增 | CI 静态/合成测试（49 条） |
| `Tools/tests/run-all.ps1` | 修改 | 注册新套件；表头编号 |
| `.github/workflows/ci.yml` | 修改 | 新增 mod-enabled 静态检查步骤 |
| `.gitignore` | 修改 | 忽略 `bin/`、`obj/`（`obj/` 里的 NuGet 文件含本机用户名与绝对路径） |
| `docs/architecture/phase1-loader-design.md` | 修改 | 新增 §13（Mods 根一致性、两条构建路径、注入流程、签名门槛）+ 变更记录 |
| `docs/audit/adr-0001-mod-injection.md` | 修改 | 变更记录追加 Phase 1.1.7 一行（ADR 仍 **Proposed**） |
| `Mod/P114.RuntimeProbe/README.md` | 修改 | Mods 根改为外部目录；新增 §4.2（Bootstrap 前置条件 + mod-enabled 构建命令） |
| `Tools/README.md` | 修改 | 新增 "mod-enabled APK 构建" 一节；更新测试表与"完整链路"（不再写回 apkroot） |
| `README.md` | 修改 | "构建" 节新增 mod-enabled APK 子节（显式入口 + keystore 要求） |
| `docs/audit/phase1.1.7-report.md` | 新增 | 本文件 |
| `docs/audit/phase1.1.7-evidence-index.md` | 新增 | 证据索引 |

未修改（有意）：`docs/audit/phase1.1.6-*` 等历史文档、`Tools/AssemblyStore/store.py`
（Phase 0.5 已正确）、`libxamarin-app.so`、`libmonodroid.so`、任何游戏资源。

---

## 13. Definition of Done 对照

| ID | 验收项 | 状态 | 证据 |
| --- | --- | --- | --- |
| P117-01 | Git 无损基线 | **PASS** | §1；`git status` / `diffstat` / HEAD=`origin/main` |
| P117-02 | 根路径一致 | **PASS** | §2；8 条 host 断言 + 三处字面值一致性测试 |
| P117-03 | 注入器 | **PASS** | §4；hook 首指令、MVID changed、`Assembly.LoadFrom`、烘焙路径正确 |
| P117-04 | 容器 | **PASS** | §5；descriptor 7、9,261,568 ≤ 9,280,000、57/57 未变、架构 blob/manifest 未变 |
| P117-05 | APK 构建 | **PASS** | §6.1；独立产物 389,561,500 B；baseline SHA 未变 |
| P117-06 | APK 静态验证 | **PASS** | §6；包名/版本/条目/注入程序集/zipalign/apksigner |
| P117-07 | 签名连续性 | **BLOCKED** | §7；候选 `858ef62d…` ≠ 已装 `611c8fa4…`，`SIGNING_KEY_UNAVAILABLE` |
| P117-08 | 设备 E0 | **PASS**（只读、零写入） | §8；11 文件 / 172,191 B 与 Phase 1.1.6 一致 |
| P117-09 | 安装门禁 | **PASS** | §9；未取得 B 批准，0 次 install/uninstall/clear |
| P117-10 | 启动门禁 | **PASS** | §9；未执行 `am start`，未读运行时日志 |
| P117-11 | 测试 | **PASS** | §10；9/9 suites，CI/local-only/SKIP 已分列 |
| P117-12 | 证据 | **PASS** | 本文件 + `phase1.1.7-evidence-index.md`；单文件清单 `outputs/p117-build-manifest.json`（含 APK hash/签名/构建 manifest/工具版本） |
| P117-13 | 安全 | **PASS** | 未碰 Saves、未卸载/清数据、未运行 T11/T12/detour |
| P117-14 | Git 工作流 | **PASS** | 未 commit、未 push，由用户手动 push |

---

## 14. 未决项与下一步

1. **签名 key 缺失（阻断安装）**：需要用户提供当初签 baseline APK 的 `debug.keystore`。没有它，
   任何 mod-enabled 候选包都无法 `install -r`，而卸载会删存档——这是硬约束，不是实现选择。
2. **`bootstrap-report.txt` 未实现**（§2.5）：需要先在设备上拿到一次"APK 已注入 + 启动"的真实证据，
   才能判断该诊断是否值得引入嵌套 try/catch 的 IL 风险。
3. **设备上的 `ModFramework.Runtime.dll` 仍是修正前的构建**：外部 Mods 路径的 fallback 修正只在源码里；
   要生效需要一次 Mods 推送（写设备，本阶段未获批，且当前 11 个文件与 Phase 1.1.6 完全一致，
   按 §5.1 不应重推）。
4. **R-01 … R-14 仍全部 BLOCKED**：前置条件是"注入后的 APK 已安装 + Loader 实际启动 + P114 输出文件
   真实生成"，三者都没有发生。Emulator 结果将来也只能记为 Emulator，不能冒充 ARM64 真机。
5. **构建非字节可复现**：MVID 每次随机，因此 patched DLL / blob / APK 的 SHA-256 每次构建都会变。
   若将来需要可复现构建，应把 MVID 的生成改成确定性派生（本阶段未做，避免改变 AOT 失效语义）。
6. **Phase 1.2 未启动**：没有实现 Harmony/detour、SMAPI 兼容层、事件 Hook、Content/Data API、热重载
   或任何玩法功能。

**最终原则复核**：先把现有注入能力真正接进 APK 构建——已做到；先构建、验签、只读预检，再等待安装
批准——已做到（并因签名不连续而止步于批准之前）；签名不匹配绝不卸载——遵守；不碰 Saves——遵守；
外部 Mods 路径与 Bootstrap/Loader 一致——已修正并测试；Emulator 不冒充 ARM64——遵守；
DSH 永不自动 push——遵守（本文档写作时未 commit、未 push）。
