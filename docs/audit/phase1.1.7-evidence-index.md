# Phase 1.1.7 证据索引

- 日期：2026-11-07 / 08
- 基线：`35e3dc8f3b9f191abbf1c69affd905283c982a92`（`main`，= `origin/main`，0/0）
- 配套报告：[`phase1.1.7-report.md`](phase1.1.7-report.md)
- 本文件作用：把"结论"与"可复核的输入"分开；每条结论都能找到命令、产物或哈希。
- 与前一阶段的差异：本阶段**没有**在设备上安装或启动任何东西，因此没有设备侧运行时证据；
  Emulator 一级只到 E0（只读预检）。

---

## 0. 证据等级与本阶段实际拿到的等级

| 等级 | 状态 | 说明 |
| --- | --- | --- |
| Desktop .NET | ✅ 重跑 | `test_loader.py` 45/45 驱动 + **481/481** host（含新增 8 条 Mods 根断言）；`test_mod_pipeline.py` 27/27 |
| AndroidRefsDir | ✅ 使用（65 refs） | 解包后注入与 Cecil 解析都用这 65 个程序集；本阶段未重编译运行时 |
| **构建期 IL 注入** | ✅ **新增** | patched DLL 在 staging 与"从新 blob 重新解出"两个位置各 verify 一次 |
| **APK 静态验收** | ✅ **新增** | 3,672 条目逐 CRC 比对；注入程序集从 APK 里取出再 verify |
| **Emulator** | ⚠️ **仅 E0** | 只读预检通过；APK **未安装**、应用**未启动** → 运行时表征仍 BLOCKED |
| **ARM64 physical** | ⛔ BLOCKED | 无设备；Emulator 不计入 |
| CI | ⚠️ 本地复现 | 未查询远程 Actions run；`test_ci_offline` / `test_modded_build` / `test_p114_probe` / `privacy-scan` 在本机真实执行 |

> 铁律不变：`构建 PASS ≠ 已安装`；`静态验收 PASS ≠ Loader 跑过`；`Desktop PASS ≠ MonoVM PASS`；
> `E0 PASS ≠ Mod 已运行`；`Emulator ≠ ARM64 physical`。本阶段没有任何 Emulator 值被写成 ARM64 证据，
> 也没有任何"构建成功"被写成"运行时成功"。

---

## 1. 命令 → 结论 → 日志

日志与构建产物在 DSH 任务目录 `D:\Download\DSH\tasks\20261107-p117-bootstrap-apk-a1\`
（**不在仓库内、不入库**）。

| # | 命令（仓库根执行） | 退出码 | 结论 | 日志/证据 |
| ---: | --- | ---: | --- | --- |
| 1 | `git rev-parse` / `branch` / `status --short` / `diff --stat` / `log` | 0 | `HEAD`=`origin/main`=`35e3dc8`，0/0，开库时工作区干净 | 报告 §1 |
| 2 | `python Tools/tests/test_loader.py --dotnet <SDK 8.0.404> --refs .tmp/p113/refs` | 0 | 45/45 驱动 + 481/481 host | `logs/run-all/test-loader.txt` |
| 3 | `python Tools/tests/test_mod_pipeline.py --dotnet <SDK> --refs .tmp/p113/refs` | 0 | 27/27 | `logs/run-all/test-mod-pipeline.txt` |
| 4 | `python Tools/build_modded_apk.py --dotnet <SDK> --work .tmp/build-modded --out-blob … --manifest-json …` | 0 | 44/44 检查，`RESULT: OK`；patched 9,261,568 ≤ 9,280,000 | `logs/modded-build-manifest.json`、`logs/build/*.log` |
| 5 | `gradlew.bat --offline :app:assembleModdedDebug -Pdotnet=<SDK> -Pkeystore=.work/debug.keystore` | 0 | BUILD SUCCESSFUL；`app-modded-debug.apk` 389,561,500 B | `logs/gradle-assembleModdedDebug.log` |
| 6 | `python Tools/verify_modded_apk.py --candidate … --baseline … --blob-manifest … --keystore …` | 0 | 34 PASS / 0 FAIL / 1 BLOCKED；`UPDATE_INCOMPATIBLE_RISK` | `logs/modded-apk-verify.json` |
| 7 | `apksigner verify --print-certs`（35.0.0 与 36.0.0 各一次，候选 + baseline） | 0 | 候选 `858ef62d…`；baseline `611c8fa4…`；v1/v2/v3 均 true | 报告 §7、`logs/modded-apk-verify.json` |
| 8 | `keytool -list -v` + `keytool -exportcert` + SHA-256 | 0 | `.work/debug.keystore` 只有一个 alias，证书 DER `858ef62d…` | 报告 §7.1 |
| 9 | `zipalign -c -p 4`（35.0.0 与 36.0.0） | 0 | 两个版本都通过 | `logs/modded-apk-verify.json` |
| 10 | `Tools/device-precheck.ps1 -Serial 127.0.0.1:16384` | 0 | `E0: PASS (read-only)`；NOT-ARM64-PHYSICAL | `logs/e0-device-precheck.txt` |
| 11 | `adb shell su -c "sha256sum \$(pm path …)"` | 0 | 设备端 APK = `65e1e2af…`（= 本地 baseline） | `logs/e0-device-precheck.txt` §4 |
| 12 | `adb shell su -c "find <Mods> -type f -exec sha256sum {} +"` | 0 | 11 文件，逐文件 hash | `logs/e0-supplement-1.1.7.txt` |
| 13 | 同目录 `ls -ln` 汇总 / `stat` / `df -h` / `test -d Saves` / `ls /data/local/tmp` | 0 | 172,191 B；`10152:10152`；`700`/`771`；Saves 存在（未列目录）；无本阶段 staging 残留 | `logs/e0-supplement-1.1.7.txt` |
| 14 | `python Tools/tests/test_modded_build.py` | 0 | 49/49（含合成 XABA descriptor 往返） | `logs/run-all/modded-build-static.txt` |
| 15 | `python Tools/tests/test_ci_offline.py` | 0 | 13/13 | `logs/run-all/ci-offline.txt` |
| 16 | `python Tools/tests/test_p114_probe.py` | 0 | 46/46 | `logs/run-all/p114-static-audit.txt` |
| 17 | `python Tools/privacy-scan.py --include-untracked` | 0 | CRITICAL/HIGH 0，MEDIUM 0 | `logs/run-all/privacy-scan.txt` |
| 18 | `Tools/tests/test_device_precheck.ps1` | 0 | 10/10 | `logs/run-all/device-precheck-selftest.txt` |
| 19 | `Tools/tests/test_deploy_safe.ps1` | 0 | 95/95 | `logs/run-all/deploy-safe-fake-adb.txt` |
| 20 | `Tools/tests/run-all.ps1 -Dotnet <SDK> -Refs .tmp/p113/refs` | 0 | 9 suites, 9 passed, 0 failed, 0 skipped | `logs/run-all-console.txt` |
| 21 | `git diff --check` | 0 | 干净 | `logs/run-all/git-diff-check.txt` |

### 1.1 未执行的命令（本阶段禁止项）

`adb install`、`adb install -r`、`adb uninstall`、`pm uninstall`、`pm clear`、`am start`、
`am force-stop`、`logcat`、`screencap`、`adb push`、`adb pull`、任何对 `Saves` 的读写。
设备侧只执行了 `getprop` / `pm path` / `dumpsys package` / `sha256sum` / `find` / `ls` / `stat` /
`df` / `test -d` / `id` / `su -c id`。

---

## 2. 哈希清单（可直接复核）

### 2.1 输入（仓库内，只读）

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `app/src/main/apkroot/assemblies/assemblies.blob` | 5,588,326 | `c8ee504eea163d2c82d89d0004c8924314b3e6b41adb649ea5f136151eecabd9` |
| `app/src/main/apkroot/assemblies/assemblies.manifest` | 4,937 | `0423d19caf72cda38d1acefd8fe73356b643ec05786b6d9a15b7134bed6e3393` |
| `app/src/main/apkroot/assemblies/assemblies.arm64_v8a.blob` | 1,250,044 | `c465d9d4414dea6b297ff8c800470302d8dd160aa4b1e09abccb468b53d4cd78` |
| `app/src/main/jniLibs/arm64-v8a/libxamarin-app.so` | 56,416 | `11384d74daa734ed5b11cbf048978686c7afbb2a7f3224df53609805035132d9` |

### 2.2 注入与容器

| 对象 | 值 |
| --- | --- |
| patched `StardewValley.dll` | 9,261,568 B / `97b289181218951020d098aa61450a3aea637e8d092871da2c757a76e6946b17` |
| MVID | `573bf1e5-3245-46d6-925a-1c3713afb677` → `5b6d1a90-9f0e-4ca0-9557-8730cdf227df` |
| `Bootstrap.Start()` | 51 instr / 182 bytes |
| 新 blob | 6,183,615 B / `cbb6ac8cb2d66d69f6ce354e1af13b11b48140c6c79c3bd62c6428c7dd59b74f` |
| 容器 | XABA v1，entry_count=58，total_count=65，gap=2,600 B |
| StardewValley descriptor_index | 7 |
| expected size（`libxamarin-app.so` 表 idx 7，表偏移 53,512） | 9,280,000 B |
| 其余 57 条 | 解压后 SHA-256 + descriptor 与原始逐一相同 |

### 2.3 APK

| 对象 | 值 |
| --- | --- |
| mod-enabled APK | `app/build/outputs/apk/modded/app-modded-debug.apk`，389,561,500 B |
| SHA-256 | `3ff0b7f7c8eefdf5e1791d2c48242626cbaae0067ad6cb8dadce76c04dee51d6` |
| package / version | `com.chucklefish.stardewvalley` / 239 / 1.6.15.0 |
| native-code ABI | `arm64-v8a`（`aapt2 dump badging`，与 baseline 相同） |
| zip 条目数 | 3,672（与 baseline 相同） |
| 内容差异 | 只有 `assemblies/assemblies.blob` 与 `META-INF/*` |
| baseline APK | 388,967,580 B / `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d`（未变） |

合并后的单文件构建清单（输入 / 容器 / 注入 / blob / APK / 签名 / 设备 E0 / 工具版本 / 命令）：
`outputs/p117-build-manifest.json`（任务目录内，不入库）。

### 2.4 签名

| 对象 | 证书 SHA-256 |
| --- | --- |
| baseline / 已安装 APK 的 signer | `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de` |
| 候选 APK 的 signer | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` |
| `.tmp/build/debug.keystore` | **不存在** |
| `.work/debug.keystore` | 存在，唯一 alias `androiddebugkey`，证书 DER `858ef62d…` |

结论：签过 baseline/装机 APK 的 key 不在本机 → `SIGNING_KEY_UNAVAILABLE` / `UPDATE_INCOMPATIBLE_RISK`。

### 2.5 设备端（只读）

| 对象 | 值 |
| --- | --- |
| 连接串 | `127.0.0.1:16384`（MuMu 12 实例 0；`:5555` / `:7555` 指向同一 VM） |
| 身份 | `vivo/PD2309/PD2309:12/V417IR/2721:user/release-keys`，API 32，`x86_64` + `libnb.so` |
| 已安装 APK SHA-256 | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| 应用 uid/gid | `10152`（`u0_a152`），私有目录 `700`，`files` `771` |
| 外部 Mods | 11 文件 / 172,191 B（与 Phase 1.1.6 一致，未重推） |
| Saves | 仅 `test -d` 确认存在，未列目录 |

---

## 3. 本阶段明确没有做的事

- 没有卸载、清数据、降级安装；
- 没有 `adb install`/`install -r`（签名不连续，B 未获批）；
- 没有启动游戏、没有读 logcat、没有读任何运行时输出文件；
- 没有修改 `libxamarin-app.so` / `libmonodroid.so`、没有新增程序集条目、没有改 descriptor index、
  没有零填充；
- 没有把 65 个游戏程序集、APK、assets 或 keystore 加入 Git；
- 没有 `git commit` / `git push` / 任何 GitHub 写操作。

---

## 4. 复核命令（从零重放）

```powershell
# 1) 容器与注入（需要真实游戏程序集，local-only）
python Tools/build_modded_apk.py `
    --dotnet <dotnet-8.0.404> `
    --work .tmp/build-modded `
    --out-blob .tmp/build-modded/modded-assemblies.blob `
    --manifest-json .tmp/build-modded/modded-build-manifest.json

# 2) 构建 mod-enabled APK（baseline 不会被触碰）
.\gradlew.bat --offline --console=plain :app:assembleModdedDebug `
    "-Pdotnet=<dotnet-8.0.404>" "-Pkeystore=.work/debug.keystore"

# 3) APK 静态验收 + 签名连续性判定
python Tools/verify_modded_apk.py `
    --candidate app/build/outputs/apk/modded/app-modded-debug.apk `
    --baseline app/build/outputs/apk/debug/app-debug.apk `
    --blob-manifest .tmp/build-modded/modded-build-manifest.json `
    --report .tmp/build-modded/modded-apk-verify.json `
    --keystore .work/debug.keystore

# 4) CI 可跑的静态检查（无需游戏资产）
python Tools/tests/test_modded_build.py
```

注意：第 1、2 步每次都会生成新的 MVID，所以 patched DLL / blob / APK 的 SHA-256 **每次不同**；
只有"结构与不变量"是可重复的结论，具体哈希属于本次运行。
