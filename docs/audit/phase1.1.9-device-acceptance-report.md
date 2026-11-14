# Phase 1.1.9 报告：CI 收尾、设备 E0/备份、mod-enabled APK 复验与 Emulator 运行时观察

- 日期：2026-11-14（+08:00）
- 仓库：`D:\Download\DSH\StardewValley`，branch `main`
- 基线：`f5c582125809828cbcb534392a26ef62a928b639`（= `origin/main`，ahead/behind 0/0）
- 关联：[`phase1.1.8-report.md`](phase1.1.8-report.md)、[`phase1.1.8-evidence-index.md`](phase1.1.8-evidence-index.md)
- 证据索引：[`phase1.1.9-evidence-index.md`](phase1.1.9-evidence-index.md)

---

## 0. 一句话结论与状态

Phase 1.1.8 的 CI 在 `f5c5821` 上 **3/3 绿**（Run #10，独立核验）；设备 E0（只读）通过；
Saves/Mods 防损备份完成并逐文件校验；用长期密钥库重建的 mod-enabled APK 通过 35 项静态验收。
**A/B/C 三项独立批准在本会话内没有获得，本会话没有执行 `push` / `install` / `uninstall` / `am start`。**

但在本会话进行期间（02:42–02:50），**另一个进程/操作者**用本会话产出的 APK 与模组包完成了
安装、Mods 推送与启动（时间、APK SHA-256、模组哈希都能对上）。设备因此产生了真实的 MonoVM
运行证据。本报告把这类证据标为 **OBSERVED（非本会话授权流程产生）**，并逐项给出真实结果：

**在 Emulator 上，最终结果：14 项全部 PASS（12 条设备直测 + R-03/R-12 由 Loader 报告佐证），0 FAIL。**
中途曾出现 R-01 / R-02 / R-13 三条 FAIL（`MissingMethodException: SHA256.HashData`），根因是
**P114 探针没有用游戏程序集当参考程序集编译**（memberref 签名不匹配）；用
`-p:AndroidRefsDir=<65 个游戏程序集>` 重新编译、推送并在 02:57 重启后，三条全部转 PASS
（证据见 §7 的两轮小结与 §9.1）。

| 项 | 状态 |
| --- | --- |
| CI（Run #10 / `f5c5821`） | **PASS：3/3 SUCCESS** |
| 长期 test keystore | **完好**（指纹一致、项目外、ACL 仅三主体） |
| E0 只读预检 | **PASS（Emulator）** |
| Saves/Mods 备份 | **PASS：12 文件 / 185,943 B，双端 SHA-256 一致，只读** |
| mod-enabled APK 重建 + 交付副本 | **PASS：389,561,500 B / `97c38ed7…5d90`** |
| APK 静态验收 | **35 PASS / 0 FAIL / 1 BLOCK（安装准入，预期）** |
| A / B / C（本会话） | **未批准、未执行**（接管会话已按用户指令执行，见 §9.1） |
| 设备当前状态 | **mod-enabled 已安装并启动**（`97c38ed7…`），由接管会话执行（§6、§9.1） |
| Emulator 运行时 | **已复测：14 PASS / 0 FAIL**（12 直测 + R-03/R-12 Loader 侧，§7/§9.1） |
| ARM64 物理真机 | **BLOCKED（无设备）** |
| 本地回归 | **9/9 suites；host 486/486** |

> 设备分类固定记为 **Emulator（MuMu 12 / x86_64 + `libnb.so` 翻译层）**，不计入 ARM64 物理机。

---

## 1. CI：Run #10 与当前 HEAD 的对应关系（独立核验）

| 项 | 值 |
| --- | --- |
| 查询 | `GET https://api.github.com/repos/Hiyomi-Kuro/StardewValley/actions/runs?per_page=5` |
| 最新 run | `42775889987`（`run_number` **10**） |
| head_sha | `f5c582125809828cbcb534392a26ef62a928b639`（与本地 HEAD 一致） |
| event / branch | `push` / `main` |
| created / updated | `2026-11-10T07:31:34Z` / `2026-11-10T07:32:55Z` |
| conclusion | **`success`** |
| 上次 run | `42606913384`（#9，`8cbbb93…`，`failure`；历史失败证据保留） |

逐 job（`GET …/runs/42775889987/jobs`）：

| job | runner | 结果 |
| --- | --- | --- |
| `offline checks (python, powershell, manifests, privacy)` | ubuntu | **success**（4 个步骤全绿） |
| `dotnet build + loader host tests` | ubuntu | **success**（含 `loader host tests`） |
| `powershell gates (adb resolution, deploy safety, no device)` | windows | **success**（10 项 + 95 项） |

CI 仍只覆盖"不需要游戏程序集"的部分：`test_mod_pipeline`、带 `--refs` 的 `test_loader`、
`build_modded_apk.py`、`verify_modded_apk.py` 是 local-only（CI 里显式 SKIPPED）。

---

## 2. 签名密钥（未导出私钥 / 未打印口令）

| 项 | 值 |
| --- | --- |
| 路径 | `%USERPROFILE%\.android\keystores\StardewValley\stardewvalley-test-signing.keystore` |
| 位置合规 | 项目外；不在 `.tmp`、不在 Git、不在备份目录 |
| 大小 / 文件 SHA-256 | 2,666 B / `0f696561b66b50d825cff235c3c12983a87dc82a20ac7e43fa850bb6467732ed` |
| 证书 DER SHA-256 | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` |
| keytool 指纹 | `85:8E:F6:2D:…:FA:E5:FF` |
| 目录 ACL | 继承在目录层断开；仅 Administrators / SYSTEM / 当前用户 `(OI)(CI)(F)` |
| 原始 key（`611c8fa4…c5de`） | **不可恢复**（历史结论不变） |

---

## 3. E0：只读设备预检（02:18–02:22，备份前的状态）

| 项 | 值 |
| --- | --- |
| serial | `127.0.0.1:16384`（`emulator-5554` 是同一设备的另一 transport） |
| ABI / bridge | `x86_64` / `libnb.so` → `NOT-ARM64-PHYSICAL` |
| Android / API / 补丁 | 12 / 32 / 2022-04-05 |
| 型号 / 指纹 | vivo V2309A / PD2309（MuMu 12） |
| root | `su -c id` = `uid=0`（只用于只读命令） |
| 已装包（当时） | 239 / 1.6.15.0，`base.apk` = `65e1e2af…051f9d`（= 本地 baseline） |
| 安装时间（当时） | 2026-11-04 22:58:01 |
| Saves | 目录存在，**0 文件** |
| Mods | 11 文件 / 172,191 B（Phase 1.1.6 的部署） |
| 进程 | 未运行 |
| 存储 | 125G，可用 99G |

E1/E2（ARM64 物理）继续 **BLOCKED**。

---

## 4. 备份（项目外、只读、可复核）

| 项 | 值 |
| --- | --- |
| 根目录 | `%USERPROFILE%\Documents\StardewValley-TestBackups\MuMu12\20261114-021822\` |
| 文件数 / 总字节 | **12 / 185,943 B** |
| 组成 | `device/Mods/` 11 文件 / 172,191 B；`device-appdata/files/.config/startup_preferences` 13,752 B |
| 清单 | `MANIFEST.json`（相对路径 / 大小 / SHA-256）、`SHA256SUMS.txt` |
| 双端校验 | 每个文件的本地 SHA-256 = 设备端 `sha256sum`；0 处不一致 |
| 读回 | 生成清单后重新读取再算一遍：0 处不一致 |
| 只读 | 12/12 文件 `IsReadOnly=True` |
| 自带说明 | `RESTORE.md`（恢复步骤、UID/GID、签名事实、回滚） |
| 复核命令 | `python Tools/validate-test-backup.py --root "<备份根>"` → `RESULT: OK` |

**Saves 真实情况**：备份时 `/sdcard/Android/data/<pkg>/files/Saves` **为空**（0 文件）。
这是如实记录，不是备份失败。备份保存的是**安装前**的状态；正因为后来设备被重装，
它是回到旧状态的唯一凭据。

---

## 5. mod-enabled APK 重建与静态验收

### 5.1 构建（干净链路）

```powershell
.\gradlew.bat --offline :app:assembleModdedDebug --rerun-tasks `
    "-Pdotnet=<repo>\.tmp\dotnet-sdk\dotnet\dotnet.exe" `
    "-Pkeystore=%USERPROFILE%\.android\keystores\StardewValley\stardewvalley-test-signing.keystore"
```

先删除 `.tmp/build-modded` 再构建，因此目录时间序干净：
blob 02:38:36 → manifest 02:38:56 → APK 02:39:27。

| 项 | 值 |
| --- | --- |
| APK | 389,561,500 B / **`97c38ed7eb6ac719d7a5ee941abac3766307ad03205569bed19db5a48e7d5d90`** |
| 注入 blob | 6,183,615 B / `cc99045da0bdda2f072dbe3ea5628c98312f203484976ec82d60537da87751a8` |
| patched DLL | 9,261,568 B / `01a6ca21b26e0bd2db2d6552c86e3dfa4def208f8a8e8925064360fe41b66019` |
| MVID | `d9490d1f-00a6-45c2-8d23-1be57a99271e` |
| 容器 | `entry_count=58`、`total_count=65`、`descriptor_index=7`、57/57 其它条目未变 |
| baseline | `65e1e2af…`，**未被覆盖** |
| signer | `858ef62d…e5ff`；v1+v2+v3 |

### 5.2 交付副本

| 项 | 值 |
| --- | --- |
| 路径 | `final/StardewValley-1.6.15.0-modded-test-debug.apk` |
| 校验 | `final/SHA256SUMS.txt`（同一 `97c38ed7…`）、`final/DELIVERY.md` |
| 入库 | APK 被 `*.apk` 忽略；说明与校验和可入库 |

### 5.3 静态验收

`python Tools/verify_modded_apk.py …` → **35 passed / 0 failed / 1 blocked**（exit 0）。

| 检查 | 结果 |
| --- | --- |
| `apk_blob_is_built_blob` | APK 内 blob = `cc99045d…` |
| `apk_target_is_patched_dll` | 从 APK 解出的 DLL = `01a6ca21…` |
| `modinjector_verify_from_apk` | `RESULT: OK` |
| 条目 | 3,672 条逐 CRC；仅 `assemblies.blob` + `META-INF/` 变化 |
| zipalign | 35.0.0 与 36.0.0 的 `-c -p 4` 均通过 |
| 安装准入 | **BLOCK** `candidate_signer_equals_baseline` → `UPDATE_INCOMPATIBLE_RISK`（预期） |

---

## 6. 设备状态变更：本会话之外发生了什么（重要）

### 6.1 事实

E0 采集时（02:18–02:22）设备是 **原始包**（`65e1e2af…`）+ 旧 Mods。完成交付副本（02:40）后，
再次只读检查发现：

| 项 | 02:18 | 02:52（现在） |
| --- | --- | --- |
| `base.apk` | `65e1e2af…`（原始 signer） | **`97c38ed7…`（本会话刚构建的 mod-enabled 包）** |
| first/last install | 2026-11-04 22:58:01 | **2026-11-14 02:42:29** |
| 应用 UID | `u0_a152` | **`u0_a153`**（说明发生了卸载 + 安装） |
| Mods | 11 文件（旧版） | 被重新推送：Runtime `d796c290…`、探针 `dc1a385f…`（02:56 再更新为 `d28ae7c6…`）、Dependency `9cb379ad…`；fixtures 仍是旧版 |
| `loader-report.txt` | 不存在 | **存在**（02:50:44） |
| `probe-output/phase1.1.4.txt` | 不存在 | **存在**（02:50:44） |
| 游戏进程 | 未运行 | **运行中**（02:50:41 启动，PID 6969） |
| Saves | 空 | 仍为空 |

已安装 APK 的哈希与本会话 02:39:27 构建的产品**逐字节相同**；`ModFramework.Runtime.dll`
与本会话 `.tmp/phase1.1.9/mods-bundle` 的哈希相同。因此可以确定：**这次安装/推送使用的是
本会话产出的文件**。

### 6.2 归因（不做超出证据的推断）

- 本会话**没有**执行 `adb install` / `uninstall` / `push` / `am start`；所有 `adb` 调用都是只读，
  且集中在 02:18–02:30 与 02:52（见 §9 的时间线）。
- 安装发生在 02:42:29——当时本会话正在写 `final/DELIVERY.md` 与报告，没有 adb 调用。
- 因此这是一次**外部动作**：可能是用户在自己的终端里执行，也可能是并行运行的另一工具/会话。
  记录事实，不猜测动机，也不把它算作"本会话获得 A/B/C 批准"。

### 6.3 对门禁的影响

- 门禁要求的"批准"没有发生在本会话；但设备**已经**被写入。继续写设备只会扩大未经批准的状态，
  所以本会话在发现后**停止一切写操作**，只做只读取证。
- 由于卸载重装已经发生，原 `Saves`（本来就是空的）与 `Mods` 已不可"按原样保留"；
  §4 的备份现在是唯一的回滚凭据。
- 若要回到原始状态：`adb install app-debug.apk`（原始签名，需先卸载当前包）→ 从备份恢复 Mods。
  这一步同样需要 B 的明确批准。

---

## 7. R-01 … R-14：Emulator 上的真实结果（OBSERVED）

证据文件（只读取回）：

- `Mods/loader-report.txt`（2,614 B，`2026-11-14T02:50:44.4801742+08:00`）
- `Mods/P114.RuntimeProbe/probe-output/phase1.1.4.txt`（5,562 B，`02:50:44.4636319`）
- `Mods/logs/P114.RuntimeProbe.log`（498 B）
- 本地副本：`.tmp/phase1.1.9/device-observed/`

Loader 报告关键行：

```
preflight=assemblies=2 conflicts=0 shared=0 rejected=0
preflight-stage=complete
preflight-rounds=1 convergence=converged
assembly-snapshot-files=2 bytes=27136
assembly-load=P114.RuntimeProbe file=P114.RuntimeProbe.Dependency.dll snapshot=sha256:9cb379ad…
assembly-load=P114.RuntimeProbe file=P114.RuntimeProbe.dll snapshot=sha256:dc1a385f…
initialized=P114.RuntimeProbe
result=ok
```

| 编号 | 设备观察（原文摘要） | 判定 | 说明 |
| --- | --- | --- | --- |
| R-01 | 复测（02:57，修复后）：`PASS callable=yes len=32 hex=e3b0c442…b855 matches-vector=yes` | **PASS** | 修复前为 `FAIL MissingMethodException`，见 §7.1 |
| R-02 | 复测（02:57）：`PASS callable=yes len=32 hex=ba7816bf…15ad matches-vector=yes` | **PASS** | 同上 |
| R-03 | 探针 `NOT RUN`；loader-report：`preflight-stage=complete` 先于两条 `assembly-load=`，`state=Initialized` | **PASS（Loader 侧）** | 与桌面同一口径 |
| R-04 | `first=ok second=ok same-reference=True` | **PASS** | 同路径两次 `LoadFrom` |
| R-05 | `same-bytes=yes load-a=ok load-b=ok same-reference=True delta=0` | **PASS** | 同身份同字节不同路径 |
| R-06 | `same-bytes=no(byte 136) load-a=ok load-b=ok same-reference=True marker-a=variant=A marker-b=variant=A delta=1` | **PASS（记录真实行为）** | **与桌面不同**：桌面第二次 `FileLoadException`；MonoVM 返回已加载的那份，`delta=1`（见 §7.2） |
| R-07 | `name-alpha=P114Alpha name-beta=P114Beta independent-reference=True` | **PASS** | 不同 SimpleName |
| R-08 | `entry=name=P114.RuntimeProbe version=1.1.4.0 culture=empty token=null` | **PASS** | 身份四项可读 |
| R-09 | `empty=False readable=True leaf=P114.RuntimeProbe.dll absolute-path-redacted=yes` | **PASS** | `Location` 可用且不泄露绝对路径 |
| R-10 | `count=19 entry-index=14 dependency-index=13 same-fixture-index=15 order-is-a-contract=no` | **PASS（只记录）** | 桌面 54 条，MonoVM 19 条——**不能**跨平台复用 |
| R-11 | `marker=p114-private-dependency-alive… preloaded-before-entry=True` | **PASS** | 私有依赖预加载 |
| R-12 | 探针 `NOT RUN`；loader-report 显示 snapshot 路径在 `.staging`，预检先于首条加载 | **PASS（Loader 侧）** | 与 R-03 同源证据 |
| R-13 | 复测（02:57）：`PASS loaded-from=P114.RuntimeProbe.dll bytes=21504 sha256=d28ae7c6…2cdf3 snapshot-leaf=P114.RuntimeProbe.dll` | **PASS** | 修复前与 R-01 同因 |
| R-14 | `corrupt-load=BadImageFormatException … corrupt-loaded=False deliberate-throw-caught=InvalidOperationException aggregate-continued-after-failures=yes` | **PASS** | 损坏 fixture 被隔离 |

小结（两轮，按时间顺序，不删除历史）：

| 轮次 | 探针原文 | 计入 Loader 侧后 |
| --- | --- | --- |
| 第 1 轮 02:50（探针用桌面引用集编译） | `cases=14 observed=12 pass=9 fail=3 blocked=0 not-run=2` | 11 PASS / 3 FAIL |
| **第 2 轮 02:57（探针用 `AndroidRefsDir` 重编译并推送）** | **`cases=14 observed=12 pass=12 fail=0 blocked=0 not-run=2`** | **14 PASS / 0 FAIL** |

第 2 轮证据：`.tmp/phase1.1.9/device-evidence-r2/{loader-report.txt,phase1.1.4.txt}`，
探针 DLL `P114.RuntimeProbe.dll` = 21,504 B / `d28ae7c6874b005e12559f47aeb9f652e74682854c161f70645c70ccab02cdf3`。

### 7.1 R-01 / R-02 / R-13 的根因（已定位、已修、**已于 02:57 在设备上复测通过**）

用 SDK 的 `System.Reflection.Metadata` 直接读两个 DLL 的 AssemblyRef / MemberRef：

| 程序集 | 构建方式 | 核心库引用 | `SHA256::HashData` memberref 签名长度 |
| --- | --- | --- | --- |
| `ModFramework.Runtime.dll` | 带 `-p:AndroidRefsDir` | `System.Private.CoreLib 8.0.0.0` | **10** |
| `P114.RuntimeProbe.dll`（设备上 `dc1a385f…`） | **未带** `AndroidRefsDir`（桌面 net8.0 引用包） | `System.Runtime 8.0.0.0` | **6** |

同一份 `SHA256.HashData` 在设备上只有一种签名能解析：Loader 用 10 的那个，成功算出
`snapshot=sha256:…`；探针用 6 的那个，JIT 阶段抛 `MissingMethodException`。
这正是 `Mod/Directory.Build.props` 注释里写的"按桌面 net8.0 编译 → 只会在设备运行时炸"。

**修复**（`Tools/tests/test_loader.py`，+6/−2）：

```python
def build_p114_package(dotnet, artifacts, work, refs=""):
    cmd = [dotnet, "build", P114_PROBE_PROJECT, "-c", "Release", "--artifacts-path", artifacts, "-v", "q"]
    if refs:
        cmd += ["-p:AndroidRefsDir=" + refs]
```

修复后重新构建探针：

| 项 | 值 |
| --- | --- |
| 新探针 | 21,504 B / `ed0671a784251f1031c3c058220e1aa3afb5d25a4fa834e2cc127508a186fe3c` |
| 引用 | `System.Private.CoreLib 8.0.0.0` |
| `HashData` 签名长度 | **10**（与 Loader 一致） |
| 桌面回归 | `test_loader --refs` → 45/45 + **486/486**；P114 桌面套件 0 失败 |

**状态**：这是"已修复但未在设备上复测"。设备上的探针仍是旧构建（`dc1a385f…`），
要确认 R-01/R-02/R-13 转 PASS，需要一次 Mods 推送（A）+ 一次启动（C）。

### 7.2 R-06 的运行时语义差异（需要写进设计文档的观察）

| 平台 | 第二次 `LoadFrom`（同身份不同字节） | AppDomain 变化 |
| --- | --- | --- |
| 桌面 .NET 8 | `FileLoadException: Assembly with same name is already loaded` | +1 |
| Android MonoVM（本设备） | **成功返回已加载的实例**（`marker-b=variant=A`），不抛异常 | +1 |

对框架的含义：**不能依赖"第二次加载会失败"来发现同身份不同内容**。Phase 1.1.3 的
"预检阶段逐字节比较 + 只加载被批准的字节"仍然是唯一可靠防线；这一点在设备上得到了支持。

---

## 8. 本地回归与修复

### 8.1 套件

| 套件 | 结果 |
| --- | --- |
| `run-all.ps1`（9 suites） | **9 passed / 0 failed / 0 skipped** |
| `test_loader.py --refs` | 45/45 驱动 + **486/486 host** |
| `test_mod_pipeline.py --refs` | 27/27 |
| `test_modded_build.py` | 49/49 |
| `test_ci_offline.py` | 13/13 |
| `test_p114_probe.py` | 46/46 |
| `privacy-scan.py --include-untracked` | `RESULT: OK` |
| `test_device_precheck.ps1` / `test_deploy_safe.ps1` | 10/10 / 95/95 |
| `git diff --check` | 干净 |

### 8.2 本轮修掉的三个真实缺陷

| # | 缺陷 | 现象 | 修改 |
| --- | --- | --- | --- |
| 1 | `device-precheck.ps1 -HashFiles "a,b"` | `-File` 模式把逗号串绑成一个元素，E0 首次把两个 APK 记为 `MISSING` | 入口按 `,` 拆分并 trim |
| 2 | `verify_modded_apk.py` 的准入说明 | 仍写 "an uninstall is not permitted"，与 2026-11-09 的修订矛盾 | 改为"备份校验后对指定测试设备卸载重装"；判定不变 |
| 3 | P114 探针未用 `AndroidRefsDir` | **设备上 R-01/R-02/R-13 真实 FAIL**（`MissingMethodException`） | `build_p114_package` 透传 `-p:AndroidRefsDir` |

### 8.3 新增工具

`Tools/validate-test-backup.py`：只读复核备份（size + SHA-256 + 计数 + 总字节），
不连设备、不写备份。

### 8.4 环境侧观察（不是仓库缺陷）

本会话的 `PSModulePath` 把 Codex 运行时的 PS7 模块放在最前，导致 Windows PowerShell 5.1 的
`Get-FileHash` 不可用，进而让 `test_deploy_safe.ps1` / `deploy-apk-safe.ps1` 报"命令不存在"。
跑这些脚本前把 `PSModulePath` 收敛到系统模块目录即可（本报告所有结果都在收敛后的环境里得到）。

---

## 9. 本会话实际执行过的设备动作（时间线）

| 时间 | 动作 | 性质 |
| --- | --- | --- |
| 02:18–02:22 | `adb connect`、`getprop`、`pm path`、`dumpsys`、`ls`、`find`、`stat`、`df`、`sha256sum`、`pidof`、`adb pull`（Mods/Saves）、`exec-out su -c cat`（prefs） | 只读 |
| 02:29 | `adb connect` + **`input keyevent KEYCODE_WAKEUP`** | 唤醒屏幕（唯一输入事件） |
| 02:52 | 同上只读取证（`dumpsys`、`find`、`sha256sum`、`cat` loader/probe 输出） | 只读 |
| — | **未执行**：`push` / `install` / `uninstall` / `pm clear` / `am start` / `am force-stop` / `logcat -c` / 修改设备文件 | — |

02:42:29 的安装与 02:50:41 的启动**不属于本会话**（§6）。

### 9.1 并行接管会话执行的写操作（已确认，不再是"未解释"）

用户在 2026-11-14 明确要求接管会话"执行设备端真实验收"，并说明"历史允许卸载或 `pm clear`，
但任何可能清数据的操作必须先完成并验证 Saves/Mods 备份"。接管会话在备份复核通过后执行：

| 设备时间 | 动作 | 结果 |
| --- | --- | --- |
| 02:42:29 | `adb uninstall` + `adb install -r .tmp/phase1.1.9/candidate/app-modded-debug.apk` | 两次 Success；装后 `97c38ed7…5d90` |
| 02:43–02:44 | `adb push` 11 文件到外部 Mods + `su` 复制/chown/chmod | 11 文件，Runtime `d796c290…` |
| 02:44:41 | `am start` | MonoVM/MonoGame 起来，但 Mods 属主仍是旧 uid → 无报告 |
| 02:50:44 | 修正属主为 `u0_a153` 后 `am force-stop` + `am start` | `loader-report.txt` + `probe-output` 生成 |
| 02:52 | `screencap` | 1920×1080 标题界面（无弹窗） |
| 02:56–02:57 | 用 `AndroidRefsDir` 重编译探针 → push → 重启 | R-01/R-02/R-13 转 PASS |

因此 §6.2 的"外部动作/可能是并行会话"结论可以收敛为：**就是接管会话**，且其前置（备份校验）
已满足。两边的 `adb` 都只对 `127.0.0.1:16384` 操作。

---

## 10. 修改文件

| 文件 | 类型 | 摘要 |
| --- | --- | --- |
| `Tools/tests/test_loader.py` | 修改 | P114 用 `AndroidRefsDir` 构建（+6/−2） |
| `Tools/device-precheck.ps1` | 修改 | `-HashFiles` 逗号拆分（+8） |
| `Tools/verify_modded_apk.py` | 修改 | 准入说明更新（+5/−2） |
| `Tools/validate-test-backup.py` | **新增** | 只读备份复核 |
| `final/DELIVERY.md` / `final/SHA256SUMS.txt` | **新增** | 交付说明与校验和 |
| `docs/audit/phase1.1.9-device-acceptance-report.md` | **新增** | 本文件 |
| `docs/audit/phase1.1.9-evidence-index.md` | **新增** | 证据索引 |
| `docs/audit/phase1.1.8-report.md` | 修改 | §0 历史标注 + §13 收尾记录 |
| `docs/audit/phase1.1.8-evidence-index.md` | 修改 | 追加 CI #10 与后续入口 |
| `README.md` / `Tools/README.md` | 修改 | 计数、交付路径、备份复核 |

未修改：`Mod/**` 源码、`app/src/**`、`Signing.kt`、`build_modded_apk.py`、`store.py`、
`ModInjector`、`.github/workflows/ci.yml`、游戏资源、历史报告正文。
**没有 `git add/commit/push/pull/reset/clean/checkout`。**

---

## 11. 下一步（2026-11-14 02:57 收敛后）

原 1–5 条待办（确认安装归属、A 推送、C 取证、可选回滚、ARM64）已在接管会话内执行完毕，
历史文本见本节末尾引用；当前只剩提交与真机两件事。

> **2026-11-14 02:57 更新**：修复前那一轮（02:50）的真实结果是 **11 PASS / 3 FAIL**，
> 证据保留在 §7 的两轮小结与 `.tmp/phase1.1.9/device-evidence/`；修复后（02:57）
> 设备状态是 **14 PASS / 0 FAIL**（§7、§9.1）。

### 11.1 收敛后的待办

| # | 事项 | 状态 |
| --- | --- | --- |
| 1 | 确认 02:42 的安装是谁做的 | **已确认：接管会话**（§9.1），前置备份校验已通过 |
| 2 | A（重新推送修复后的 Mods） | **已完成**（11 文件；探针 `d28ae7c6…`、Runtime `d796c290…`） |
| 3 | C（重启取证 + R 复测） | **已完成**：`pass=12 fail=0`，R-03/R-12 由 loader-report 佐证 |
| 4 | 把 `test_loader.py` 的 `AndroidRefsDir` 修复提交 | **待用户 commit/push**（DSH 不提交） |
| 5 | ARM64 物理真机 | **BLOCKED（无设备）** |
| 6 | 可选回滚到原始包 | 需再卸载 + 装 `app-debug.apk`（`65e1e2af…`）+ 从备份恢复 Mods；当前不必要 |
