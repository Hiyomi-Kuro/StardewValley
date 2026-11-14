# Phase 1.1.9 证据索引

- 日期：2026-11-14（+08:00）
- 基线：`f5c582125809828cbcb534392a26ef62a928b639`（`main` = `origin/main`，ahead/behind 0/0）
- 配套报告：[`phase1.1.9-device-acceptance-report.md`](phase1.1.9-device-acceptance-report.md)
- 本文件作用：把"结论"和"可复核的输入"分开；每条结论都能找到命令、退出码、产物或哈希。
- 重要前提：本会话**没有**执行 A/B/C；02:42–02:50 的安装与启动由本会话之外的进程完成（报告 §6）。

---

## 0. 证据等级

| 等级 | 状态 | 说明 |
| --- | --- | --- |
| CI（GitHub Actions） | ✅ **3/3 SUCCESS** | Run #10，head = 当前 HEAD |
| Desktop .NET（Windows） | ✅ 重跑 | 45/45 驱动 + **486/486** host（含 P114 修复后） |
| AndroidRefsDir（65 refs） | ✅ | `.tmp/p113/refs`；P114 现已使用 |
| 构建期 IL 注入 | ✅ | 干净链路，MVID `d9490d1f-…` |
| APK 静态验收 | ✅ | 35 PASS / 0 FAIL / 1 BLOCK |
| 签名密钥 | ✅ | 长期库 SHA-256 / 证书 DER / ACL |
| Emulator E0 | ✅ **PASS（只读）** | `127.0.0.1:16384` |
| Emulator 运行时 | 🟡 **OBSERVED（非本会话授权流程）** | 真实设备输出；11 PASS / 3 FAIL |
| ARM64 physical | ⛔ **BLOCKED** | 无设备 |

> 铁律：`本地 PASS ≠ CI PASS`；`构建 PASS ≠ 已安装`；`静态验收 PASS ≠ Loader 跑过`；
> `Desktop PASS ≠ MonoVM PASS`；`Emulator ≠ ARM64 physical`。

---

## 1. 命令 → 退出码 → 结论

日志目录：`.tmp/phase1.1.9/logs/`（`.tmp/` 不入库）。

| # | 命令 | 退出码 | 结论 | 证据 |
| ---: | --- | ---: | --- | --- |
| 1 | `git status` / `log` / `branch -vv` / `remote -v` | 0 | HEAD=`origin/main`=`f5c5821`，干净 | 报告 §1 |
| 2 | GitHub API `…/actions/runs?per_page=5` | 0 | Run #10 = `f5c5821`，`success` | phase1.1.8 §13.1 |
| 3 | GitHub API `…/runs/42775889987/jobs` | 0 | 3 jobs / 全部步骤 success | 同上 |
| 4 | `keytool -list -v -storepass:env` | 0 | 指纹 `85:8E:F6:2D:…:FA:E5:FF` | 报告 §2 |
| 5 | `icacls`（长期库目录 / 文件） | 0 | 目录仅 3 主体；继承已断 | 报告 §2 |
| 6 | `Tools/device-precheck.ps1 -Serial 127.0.0.1:16384 -HashFiles "…, …"` | 0 | E0 PASS；`NOT-ARM64-PHYSICAL` | `logs/e0/device-precheck-127.0.0.1_16384.txt` |
| 7 | `adb shell` 只读采集 | 0 | 见报告 §3 | `logs/e0/*.txt` |
| 8 | `adb pull …/Mods` | 0 | 11 files / 172,191 B | 备份 `device/Mods/` |
| 9 | `adb pull …/Saves` | 0 | 0 files（空） | 备份 `device/Saves/` |
| 10 | `adb exec-out su -c cat …/startup_preferences` | 0 | 13,752 B | 备份 `device-appdata/…` |
| 11 | 备份清单 + 双端 SHA-256 + 读回 | 0 | 12 文件 / 185,943 B，0 不一致 | `<备份根>/MANIFEST.json` |
| 12 | `python Tools/validate-test-backup.py --root <备份根>` | 0 | `RESULT: OK` | 报告 §4 |
| 13 | `gradlew --offline :app:assembleModdedDebug --rerun-tasks …` | 0 | APK `97c38ed7…` | `logs/gradle-final.log` |
| 14 | `python Tools/verify_modded_apk.py …` | 0 | 35/0/1，`UPDATE_INCOMPATIBLE_RISK` | `logs/verify-modded-apk.log` |
| 15 | `zipalign -c -p 4`（35 / 36） | 0 / 0 | 通过 | 报告 §5.3 |
| 16 | `apksigner verify --print-certs` | 0 | v1+v2+v3，signer `858ef62d…` | 报告 §5.3 |
| 17 | `run-all.ps1 -Refs .tmp/p113/refs` | 0 | **9 suites, 9 passed** | `logs/run-all-final.log` |
| 18 | `test_loader.py --refs` | 0 | 45/45 + 486/486 | `logs/run-all-final/test-loader.txt` |
| 19 | `test_loader.py --refs`（P114 修复后） | 0 | 45/45 + 486/486，`p114_suite_no_failures` | `logs/test_loader-p114fix.log` |
| 20 | `dotnet build Mod/P114.RuntimeProbe -p:AndroidRefsDir=…` | 0 | 探针 `ed0671a7…`，memberref 签名长度 10 | 报告 §7.1 |
| 21 | AssemblyRef/MemberRef dump（`System.Reflection.Metadata`） | 0 | 旧探针 `System.Runtime` / len 6；新探针 `System.Private.CoreLib` / len 10 | 报告 §7.1 |
| 22 | 只读 `cat` 设备上的 `loader-report.txt` / `probe-output` / `P114.RuntimeProbe.log` | 0 | R-01/02/13 FAIL，其余 PASS | `device-observed/*` |
| 23 | `git diff --check` | 0 | 干净 | 报告 §8.1 |

### 1.1 本会话**未**执行的设备命令

`adb push`、`adb install`、`install -r`、`uninstall`、`pm clear`、`am start`、`am force-stop`、
`logcat -c`、创建/修改/删除设备文件。唯一输入事件：`input keyevent KEYCODE_WAKEUP`（02:29）。
02:42:29 的安装不属于本会话（报告 §6）。

---

## 2. 设备事实

### 2.1 备份时（02:18–02:22）

| 项 | 值 |
| --- | --- |
| serial | `127.0.0.1:16384`（另一 transport `emulator-5554`） |
| ABI / bridge | `x86_64` / `libnb.so` → `NOT-ARM64-PHYSICAL` |
| Android | 12 / API 32 / 2026-04-05 |
| 型号 | vivo V2309A / PD2309（MuMu 12） |
| 已装 APK | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| Saves | 目录存在，**0 文件** |
| Mods | 11 文件 / 172,191 B |

### 2.2 现在（02:52）

| 项 | 值 |
| --- | --- |
| 已装 APK | **`97c38ed7eb6ac719d7a5ee941abac3766307ad03205569bed19db5a48e7d5d90`**（本会话构建） |
| first/last install | **2026-11-14 02:42:29** |
| UID | `u0_a153`（卸载重装） |
| Mods | Runtime `d796c290…`、探针 `dc1a385f…`、Dependency `9cb379ad…`；fixtures 仍为旧版 |
| 运行证据 | `loader-report.txt` 2,614 B / `probe-output` 5,562 B（02:50:44） |
| 进程 | 运行中（02:50:41 启动） |
| Saves | 仍为空 |

---

## 3. 备份证据

| 项 | 值 |
| --- | --- |
| 根目录 | `%USERPROFILE%\Documents\StardewValley-TestBackups\MuMu12\20261114-021822\` |
| `MANIFEST.json` | `label=MuMu12`、`serial=127.0.0.1:16384`、`file_count=12`、`total_bytes=185,943`、`problems=0` |
| 逐文件摘要 | `SHA256SUMS.txt`（12 行） |
| 恢复说明 | `RESTORE.md` |
| 采集日志 | `evidence/` |
| 只读 | 12/12 `IsReadOnly=True` |
| 复核 | `python Tools/validate-test-backup.py --root "<备份根>"` → `RESULT: OK` |

---

## 4. 构建产物身份

| 项 | 值 |
| --- | --- |
| 构建输出 | `app/build/outputs/apk/modded/app-modded-debug.apk` |
| 交付副本 | `final/StardewValley-1.6.15.0-modded-test-debug.apk` |
| 大小 / SHA-256 | 389,561,500 B / `97c38ed7eb6ac719d7a5ee941abac3766307ad03205569bed19db5a48e7d5d90` |
| signer | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` |
| 注入 blob | 6,183,615 B / `cc99045da0bdda2f072dbe3ea5628c98312f203484976ec82d60537da87751a8` |
| patched DLL | 9,261,568 B / `01a6ca21b26e0bd2db2d6552c86e3dfa4def208f8a8e8925064360fe41b66019` |
| MVID | `d9490d1f-00a6-45c2-8d23-1be57a99271e` |
| baseline | 388,967,580 B / `65e1e2af…051f9d`（未覆盖） |

> 非字节可复现：每次注入生成新 MVID，摘要只对应 2026-11-14 02:39 这一次构建。

---

## 5. 运行时证据（OBSERVED）

| 文件 | 大小 | 时间戳（设备） |
| --- | ---: | --- |
| `Mods/loader-report.txt` | 2,614 | `2026-11-14T02:50:44.4801742+08:00` |
| `Mods/P114.RuntimeProbe/probe-output/phase1.1.4.txt` | 5,562 | `02:50:44.4636319` |
| `Mods/logs/P114.RuntimeProbe.log` | 498 | `02:50:44.4751079` |

结果：`cases=14 observed=12 pass=9 fail=3 blocked=0 not-run=2`；计入 Loader 侧证据后
**11 PASS / 3 FAIL**。

| 失败项 | 原文 |
| --- | --- |
| R-01 / R-02 / R-13 | `MissingMethodException: Method not found: byte[] System.Security.Cryptography.SHA256.HashData(byte[])` |

根因证据（AssemblyRef/MemberRef）：

| 程序集 | 核心库引用 | `HashData` memberref 签名长度 |
| --- | --- | --- |
| `ModFramework.Runtime.dll`（带 AndroidRefsDir） | `System.Private.CoreLib 8.0.0.0` | 10 |
| `P114.RuntimeProbe.dll`（设备上，未带） | `System.Runtime 8.0.0.0` | 6 |
| 修复后的 `P114.RuntimeProbe.dll` | `System.Private.CoreLib 8.0.0.0` | 10 |

修复后探针：21,504 B / `ed0671a784251f1031c3c058220e1aa3afb5d25a4fa834e2cc127508a186fe3c`。
**未在设备上复测**（需要 A + C）。

---

## 6. 证据文件清单

| 文件 | 内容 |
| --- | --- |
| `.tmp/phase1.1.9/logs/e0/*` | E0 输出、设备端 sha256、目录列表 |
| `.tmp/phase1.1.9/logs/gradle-final.log` | 干净链路构建 |
| `.tmp/phase1.1.9/logs/verify-modded-apk.log` | 35/0/1 |
| `.tmp/phase1.1.9/logs/run-all-final.log` + `run-all-final/` | 9 套件 |
| `.tmp/phase1.1.9/logs/test_loader-p114fix.log` | P114 修复后回归 |
| `.tmp/phase1.1.9/device-observed/*` | 设备端 loader/probe 输出副本 |
| `.tmp/phase1.1.9/device-observed/device-mods-sha256-after.txt` | 当前设备 Mods 哈希 |
| `.tmp/phase1.1.9/mods-delta-fixed.txt` | 下一次 A 推送的 delta（9 UPDATE） |
| `.tmp/phase1.1.9/mods-bundle-fixed/` | 修复后、**未推送**的模组包 |

**不入库**：APK、keystore、游戏程序集、备份本体、二进制副本。

---

## 7. 与最终汇报项的对应

| 汇报项 | 位置 |
| --- | --- |
| 1 总状态 | 报告 §0 |
| 2 CI 最新验证 | 报告 §1 |
| 3 备份路径/校验 | 报告 §4；本文件 §3 |
| 4 签名与 APK 信息 | 报告 §2 / §5；本文件 §4 |
| 5 R-01…R-14 设备矩阵 | 报告 §7；本文件 §5 |
| 6 改动文件 | 报告 §10 |
| 7 残留阻塞 | 报告 §11 |
| 8 需批准的动作 | 报告 §11 |
| 9 设备时间线 | 报告 §9 |

---

## 8. 2026-11-14 02:57 设备复测（已完成，取代 §5 的"未复测"）

§5 的 3 条 FAIL 是**第一轮**（探针按桌面引用集编译）的结果。接管会话用
`-p:AndroidRefsDir=<65 refs>` 重新编译探针、推送、重启后完成复测：

| 项 | 第一轮 02:50 | **第二轮 02:57** |
| --- | --- | --- |
| 探针 DLL | 22,016 B / `dc1a385f…` | **21,504 B / `d28ae7c6…`**（设备上实测） |
| 探针自报 | `pass=9 fail=3 not-run=2` | **`pass=12 fail=0 not-run=2`** |
| R-01 | FAIL `MissingMethodException` | **PASS** `len=32 hex=e3b0c442…b855` |
| R-02 | FAIL | **PASS** `len=32 hex=ba7816bf…15ad` |
| R-13 | FAIL | **PASS** `bytes=21504 sha256=d28ae7c6…2cdf3` |
| 计入 Loader 侧 | 11 PASS / 3 FAIL | **14 PASS / 0 FAIL** |

复测证据：`.tmp/phase1.1.9/device-evidence-r2/loader-report.txt`（1,368 B，
`2026-11-14T02:57:09`）、`device-evidence-r2/phase1.1.4.txt`（2,819 B）。

> 说明：报告 §5 提到的修复后探针 `ed0671a7…` 与本次设备上实测的 `d28ae7c6…` 是同源、
> 同参数的两次独立构建（MVID 不同），**以设备上实测通过的那份为准**。
