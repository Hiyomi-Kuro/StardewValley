# Phase 1.1.8 证据索引

- 日期：2026-11-09
- 基线：`8cbbb93358698ab66f702ca0b33e13011c1ac0b2`（`main`，= `origin/main`，ahead/behind 0/0）
- 配套报告：[`phase1.1.8-report.md`](phase1.1.8-report.md)
- 本文件作用：把"结论"与"可复核的输入"分开；每条结论都能找到命令、退出码、产物或哈希。
- 与上一阶段的差异：本阶段**没有**任何设备操作（读或写），因此没有设备侧证据；
  Emulator 一级连 E0 都没有跑到（门禁要求 CI 先绿）。

---

## 0. 证据等级与本阶段实际拿到的等级

| 等级 | 状态 | 说明 |
| --- | --- | --- |
| Desktop .NET（Windows） | ✅ 重跑 | `test_loader.py` 45/45 驱动 + **486/486** host（含 5 条新增路径断言） |
| Desktop .NET（Linux/Ubuntu） | ⚠️ **NOT RUN** | 本机无 WSL/Linux `dotnet`；只能由 push 后的 Actions 判定 |
| AndroidRefsDir | ✅ 使用（65 refs） | 注入、容器重建、Cecil 解析均用 `.tmp/build-modded/refs` |
| 构建期 IL 注入 | ✅ 重跑 | `--rerun-tasks` 全量重建，新 MVID `e7202336-…` |
| APK 静态验收 | ✅ | 3,672 条目、注入 blob 从 APK 内重新解出再 `verify` |
| 签名密钥 | ✅ 审计 + 持久化 | 候选全部不匹配 → `TEST_SIGNING_KEY_PRESERVED` |
| **Emulator** | ⛔ **未执行** | E0 未跑、未安装、未启动 → R-01…R-14 全部 NOT RUN |
| **ARM64 physical** | ⛔ BLOCKED | 无设备；Emulator 不计入 |
| CI | ⚠️ **AWAITING_USER_PUSH_AND_CI** | 修复未 push；Actions #9 仍是失败基线 |

> 铁律不变：`本地 PASS ≠ CI PASS`；`构建 PASS ≠ 已安装`；`静态验收 PASS ≠ Loader 跑过`；
> `Desktop PASS ≠ MonoVM PASS`；`Emulator ≠ ARM64 physical`。

---

## 1. 命令 → 退出码 → 结论 → 日志

日志与产物在 DSH 任务目录
`D:\Download\DSH\tasks\20261109-p118-signing-runtime-a7f3\`（**不在仓库内、不入库**）。

| # | 命令（仓库根执行） | 退出码 | 结论 | 日志/证据 |
| ---: | --- | ---: | --- | --- |
| 1 | `git remote -v` / `rev-parse HEAD` / `rev-parse origin/main` / `rev-list --left-right --count` / `status --short` | 0 | HEAD=`origin/main`=`8cbbb93…`，0/0，工作区干净 | 报告 §1 |
| 2 | `git fetch origin` | 0 | `origin/main` 未前进 | 报告 §1 |
| 3 | `python Tools/tests/test_loader.py --dotnet <SDK 8.0.404> --refs .tmp/build-modded/refs` | 0 | 45/45 驱动 + **486/486 host** | `logs/test_loader.log` |
| 4 | `python Tools/tests/test_ci_offline.py` | 0 | 13/13 | `logs/run-all/ci-offline.txt` |
| 5 | `python Tools/tests/test_modded_build.py` | 0 | 49/49 | `logs/run-all/modded-build-static.txt` |
| 6 | `python Tools/tests/test_p114_probe.py` | 0 | 46/46 | `logs/run-all/p114-static-audit.txt` |
| 7 | `python Tools/privacy-scan.py --include-untracked` | 0 | `RESULT: OK`；CRITICAL/HIGH 0，MEDIUM 0，INFO 28 | `logs/run-all/privacy-scan.txt` |
| 8 | `powershell -File Tools/tests/run-all.ps1 -Dotnet <SDK> -Refs .tmp/build-modded/refs` | 0 | **9 suites, 9 passed, 0 failed, 0 skipped** | `logs/run-all-console.log`、`logs/run-all/` |
| 9 | `test_mod_pipeline.py --dotnet <SDK> --refs …`（经 #8） | 0 | 27/27 | `logs/run-all/test-mod-pipeline.txt` |
| 10 | `test_device_precheck.ps1` / `test_deploy_safe.ps1`（经 #8） | 0 | 10/10 / 95/95 | `logs/run-all/*.txt` |
| 11 | `keytool -list -v`（`-storepass:env`）× 3 个候选 | 0 | 指纹见 §2 | `logs/keystore-audit.txt` |
| 12 | `keytool -exportcert` + 独立 SHA-256 × 3 | 0 | DER 摘要见 §2，与 signer 同口径 | `logs/keystore-audit.txt`、`.tmp/cert-export/` |
| 13 | 文件哈希比对（来源 vs 长期库） | 0 | 逐字节相同 `0f696561…32ed` | `logs/keystore-audit.txt` |
| 14 | `icacls <长期库目录>`（`/inheritance:r` + 三个主体） | 0 | 仅 Administrators / SYSTEM / 当前用户 | `logs/keystore-audit.txt` |
| 15 | `get-childitem` 文件名搜索（7 个限定范围 + 回收站） | 0 | 候选清单见 §2；无原始 key | 报告 §3.1 |
| 16 | `.\gradlew.bat --offline :app:assembleModdedDebug --rerun-tasks -Pkeystore=<长期库>` | 0 | BUILD SUCCESSFUL 3m23s，3 actions executed | `logs/gradle-assembleModdedDebug-rerun.log` |
| 17 | `python Tools/verify_modded_apk.py --candidate … --baseline … --blob-manifest … --keystore <长期库>` | 0 | 35 PASS / 0 FAIL / 1 BLOCK（`UPDATE_INCOMPATIBLE_RISK`） | `logs/verify_modded_apk.log`、`outputs/modded-apk-verify.json` |
| 18 | `aapt2 dump badging` | 0 | `com.chucklefish.stardewvalley` / 239 / 1.6.15.0 | 报告 §4.2 |
| 19 | `Get-FileHash`（候选 APK / baseline / blob） | 0 | 见 §3 | 报告 §4.2 |
| 20 | `git diff --check` | 0 | 干净 | 报告 §11 |

### 1.1 本阶段**未**执行的命令（门禁 / 未获批）

| 命令 | 原因 |
| --- | --- |
| `adb devices` / `adb shell getprop` / E0 全链路 | 任务书 §5：CI 未绿前不进入 E0 |
| `adb pull`（Saves / Mods 备份） | 同上；且备份属于 B 的前置，尚未获批 |
| `adb push`（Mods） | A 未获批（且 CI 未绿） |
| `adb install` / `install -r` / `uninstall` / `shell pm clear` | B 未获批 |
| `am start` / `force-stop` / `screencap` / `logcat` | C 未获批 |
| 任何 GitHub 写操作（`git commit` / `git push` / API 写） | 明确禁止；由用户手动执行 |

本阶段 `adb` 进程调用次数：**0**。设备文件创建/修改/删除次数：**0**。Saves 读取次数：**0**。

---

## 2. 密钥材料事实（不含私钥、不含密码）

搜索范围与结论见报告 §3.1。候选与指纹：

| 候选（脱敏） | 大小 | 文件 SHA-256 | 证书 DER SHA-256 | 结论 |
| --- | ---: | --- | --- | --- |
| `%USERPROFILE%\.android\debug.keystore` | 2,618 B | `7df79c2cb269376f0672284a40f642d1ec6c02cad1b6758f0e2c4059f8e8dc95` | `b8849d6e0146f3fa8c95e5911f444372b6693162142e1aa93d1700a2da3be088` | 不匹配 |
| `.work\debug.keystore`（来源，保留不动） | 2,666 B | `0f696561b66b50d825cff235c3c12983a87dc82a20ac7e43fa850bb6467732ed` | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` | 不匹配（= test key） |
| `.android\keystores\StardewValley\stardewvalley-test-signing.keystore`（长期库） | 2,666 B | `0f696561b66b50d825cff235c3c12983a87dc82a20ac7e43fa850bb6467732ed` | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` | 与来源逐字节相同 |

| 项 | 值 |
| --- | --- |
| 原始 key 目标 signer | `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de` |
| 搜索结论 | `SIGNING_KEY_CANDIDATES_MISMATCH`（有候选，但都不匹配） |
| 最终 key 状态 | `TEST_SIGNING_KEY_PRESERVED` |
| 原始 key 不可恢复的依据 | 由 `Signing.kt` 在首次 baseline 构建时自动生成，唯一副本位于已删除的 `.tmp/build/`；回收站无残留 |
| 长期库 ACL | `BUILTIN\Administrators` / `NT AUTHORITY\SYSTEM` / 当前用户，均 `(OI)(CI)F`；继承已断开 |
| BitLocker / EFS | **BLOCKED**（`Get-BitLockerVolume` 需要提升权限） |
| 加密离线备份 | **PENDING USER**（需用户自选口令与介质） |
| 未做的事 | 未导出/复制/上传私钥；未猜密码；未下载任何"现成 key"；未把 keystore 放进仓库/任务目录/日志 |

---

## 3. 候选 APK 身份（本轮唯一产物）

| 项 | 值 |
| --- | --- |
| 路径 | `app/build/outputs/apk/modded/app-modded-debug.apk` |
| 大小 | 389,561,500 B |
| SHA-256 | `794e0e7f88f278feb49af76d2ddaac9974f1a0c1fbe59c8ce11f7630ab282290` |
| package / versionCode / versionName | `com.chucklefish.stardewvalley` / 239 / 1.6.15.0 |
| signer（apksigner） | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` |
| 签名方案 | v1 + v2 + v3；`zipalign -c -p 4` 通过（35.0.0 与 36.0.0） |
| 注入 blob | 6,183,615 B / `1662f0f2b06a3f4d12e0f80a0d0f6cd81f8652c81be90051523aea6ddd8199d5` |
| patched `StardewValley.dll` | 9,261,568 B / `58a592364145c555b175e34bc198b7a48e1af3da8971d9fad8c062c56638272f` |
| MVID before → after | `573bf1e5-3245-46d6-925a-1c3713afb677` → `e7202336-b6c5-46fd-ab3a-5d2f7bde0184` |
| descriptor index / 尺寸上限 | 7 / 9,280,000（patched 9,261,568 ≤ 上限） |
| baseline APK（未被覆盖） | 388,967,580 B / `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| 安装准入 | `UPDATE_INCOMPATIBLE_RISK`（signer ≠ 已装包；只能备份后卸载重装） |
| 构建 manifest | `outputs/modded-build-manifest.json`（含全部 44 项检查） |
| 验收报告 | `outputs/modded-apk-verify.json` |

---

## 4. 证据文件清单（任务目录）

| 文件 | 内容 |
| --- | --- |
| `logs/test_loader.log` | 首次 `test_loader.py` 全量输出（45 驱动 + 486 host） |
| `logs/static-suites.log` | `test_ci_offline` / `test_modded_build` / `test_p114_probe` / `privacy-scan` |
| `logs/run-all-console.log` | `run-all.ps1` 控制台输出与 9 suites 汇总 |
| `logs/run-all/*.txt` | 每个 suite 的独立日志 |
| `logs/keystore-audit.txt` | 候选、指纹、DER 摘要、长期库 ACL（无密钥/无口令） |
| `logs/gradle-assembleModdedDebug.log` | 第一次构建（`prepareModdedAssemblies` 命中缓存） |
| `logs/gradle-assembleModdedDebug-rerun.log` | `--rerun-tasks` 全量重建（新 MVID，本轮产物） |
| `logs/verify_modded_apk.log` | 静态验收与安装准入判定 |
| `outputs/modded-build-manifest.json` | 注入构建清单（44 项检查、哈希、MVID） |
| `outputs/modded-apk-verify.json` | APK 验收报告（35 PASS / 1 BLOCK） |
| `outputs/apk-StardewValley.dll` | 从新 blob 解出的 patched DLL（供复核，不入库） |
| `outputs/apk-assemblies.blob` | 新 blob 副本（6,183,615 B，供复核，不入库） |
| `outputs/artifacts-loader/` | `test_loader` 的 dotnet 构建产物 |

**不入库**：keystore 本体、私钥、密码、65 个游戏程序集、APK、大型游戏资源、`outputs/` 里的二进制副本。

---

## 5. 与任务书 §11 最终汇报项的对应

| 汇报项 | 本文件位置 |
| --- | --- |
| 1 总状态与各 Gate | 报告 §0、§12 |
| 2 main SHA / 手动 push / Actions | 报告 §1、§2.1 |
| 3 Linux 路径根因与测试结果 | 报告 §2、§8 |
| 4 keystore 搜索与指纹结论 | 报告 §3、本文件 §2 |
| 5 候选 APK 路径/大小/SHA/签名 | 报告 §4.2、本文件 §3 |
| 6 E0 与 Mods delta | 报告 §5（未执行，附计划） |
| 7 A/B/C 批准与未执行的设备命令 | 报告 §6、本文件 §1.1 |
| 8 R-01…R-14 | 报告 §7 |
| 9 测试/CI/local-only/SKIP | 报告 §8 |
| 10 修改文件与证据路径 | 报告 §11、本文件 §4 |
| 11 Saves 访问/备份/卸载范围 | 报告 §9（本阶段 0 次访问） |
| 12 DSH 未 commit/push | 报告 §11 末尾 |
