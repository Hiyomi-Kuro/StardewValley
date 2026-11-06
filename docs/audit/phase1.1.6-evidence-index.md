# Phase 1.1.6 证据索引

- 日期：2026-11-06 / 07
- 基线：`6e90ef2ac0e1d52832ad3564d4234640cc535d14`（`main`）
- 配套报告：[`phase1.1.6-report.md`](phase1.1.6-report.md)
- 本文件作用：把"结论"与"可复核的输入"分开；每条结论都能找到命令、产物或哈希。
- **时钟**：模拟器时钟比宿主慢约 42–43 分钟；下文设备时间与 logcat 一致。

---

## 0. 证据等级与本阶段实际拿到的等级

等级定义沿用 [`phase0.5.1-evidence-index.md`](phase0.5.1-evidence-index.md) §0。

| 等级 | 状态 | 说明 |
| --- | --- | --- |
| Desktop .NET | ✅ 本阶段重跑 | `test_loader.py` 45/45 + 473/473；`test_mod_pipeline.py` 27/27 |
| AndroidRefsDir | ✅ 沿用（65 refs） | 参与 loader/mod-pipeline；本阶段未重建 APK |
| **Emulator** | ✅ **部分** | 真实推送、真实启动、真实迁移均已观察；**运行时表征仍 BLOCKED**（无 Bootstrap） |
| **ARM64 physical** | ⛔ BLOCKED | 无设备；Emulator 不计入 |
| CI | ✅ OBSERVED（只读 API） | run `42238545103`（`6e90ef2`）#7 3/3 success；job 日志未下载（403） |

> 铁律不变：`E0 PASS ≠ Mod 已运行`；`推送 PASS ≠ Loader 跑过`；`Desktop PASS ≠ MonoVM PASS`；
> `Emulator ≠ ARM64 physical`。本阶段没有任何 Emulator 值被写成 ARM64 证据。

---

## 1. 命令 → 结论 → 日志

日志与截图在 DSH 任务目录
`D:\Download\DSH\tasks\20261106-p116-emulator-p114-a1\`（**不在仓库内、不入库**）。

| # | 命令（仓库根执行） | 退出码 | 结论 | 日志/证据 |
| ---: | --- | ---: | --- | --- |
| 1 | `git fetch --prune` + `rev-parse` / `status` / `log` / `diff --stat` | 0 | HEAD=`origin/main`=`6e90ef2`，0/0，开始时干净 | `logs/git-audit-1.1.6.txt` |
| 2 | `curl .../actions/runs/42238545103` | 0 | completed/success，#7，3/3 jobs | `logs/ci-run-42238545103.json`、`logs/ci-jobs.json` |
| 3 | `python work/verify_bundle.py <bundle-build>` | 0 | 11 文件 / 172,191 B，逐文件 hash 与 manifest 一致 | `logs/bundle-verify.txt` |
| 4 | `Tools/device-precheck.ps1 -Serial emulator-5554` | 0 | `E0: PASS (read-only)`；EMULATOR-TRANSLATED | `logs/e0-device-precheck.txt` |
| 5 | `python work/e0_supplement.py` | 0 | ABI/bridge/包/UID/Mods/空间/命令集 | `logs/e0-supplement.txt` |
| 6 | `Tools/deploy-apk-safe.ps1 -Serial … -ModsSourceDirectory …`（预检） | 0 | `PRECHECK-ONLY ok`；11 files / 9 dirs / 172191 B | `logs/e0-deploy-precheck.txt` |
| 7 | `python work/check_apk_bootstrap.py` | 0 | APK blob = 仓库原始 blob → 未注入 Bootstrap | `logs/apk-bootstrap-check.txt` |
| 8 | `Tools/deploy-apk-safe.ps1 -PushModsApproved -Serial emulator-5554 …` | 0 | **PUSHED**：11/11 staged+remote hash、owner/mode、staging 清理 | `logs/push-1.1.6.txt` |
| 9 | root `find` / `sha256sum` / `stat` 独立复核 | 0 | 11 文件、hash 全同、`10152:10152`、`0644`/`0755`、staging=0 | `logs/post-push-verify-1.1.6.txt` |
| 10 | `adb shell am start -W -n …MainActivity` | 0 | Status ok / COLD / TotalTime 1769 | `logs/launch-1.1.6.txt` |
| 11 | 进程/焦点/健康检查 | 0 | pid 28116；MainActivity topResumed；无 FATAL/ANR/crash | `logs/post-launch-health-1.1.6.txt` |
| 12 | 运行时文件存在性检查 | 0 | 内部 Mods 消失；外部 Mods 出现；loader/probe 均 ABSENT | `logs/runtime-files-1.1.6.txt` |
| 13 | 外部 Mods `find` + `sha256sum` | 0 | 11 文件、hash 与 bundle 逐字符一致 | `logs/external-mods-after-launch-1.1.6.txt` |
| 14 | 过滤 logcat（package/Mono/Loader/P114/fatal） | 0 | 找到 `[SDV_INIT] … GetFilesDir … copied`；无 loader 行 | `logs/logcat-filtered-1.1.6.txt`、`logs/logcat-game-pid-1.1.6.txt` |
| 15 | `adb exec-out screencap -p` | 0 | 标题界面（NEW/LOAD） | `evidence/title-screen-1.1.6.png` |
| 16 | `test_deploy_safe.ps1` | 0 | 95/95（93 断言 + 2 SKIP） | `logs/test_deploy_safe-1.1.6.txt` |
| 17 | `test_device_precheck.ps1` | 0 | 10/10 | `logs/test_device_precheck-1.1.6.txt` |
| 18 | `test_ci_offline.py` | 0 | 13/13 | `logs/test_ci_offline-1.1.6.txt` |
| 19 | `test_p114_probe.py` | 0 | 46/46 | `logs/test_p114_probe-1.1.6.txt` |
| 20 | `test_loader.py --dotnet 8.0.404 --refs .tmp/p113/refs` | 0 | 45/45 + 473/473 | `logs/test_loader-1.1.6.txt` |
| 21 | `test_mod_pipeline.py --dotnet 8.0.404 --refs .tmp/p113/refs` | 0 | 27/27 | `logs/test_mod_pipeline-1.1.6.txt` |
| 22 | `run-all.ps1 -Dotnet … -Refs …` | 0 | 8/8 套件 PASS，0 skipped | `logs/run-all-1.1.6.txt`、`logs/run-all/` |
| 23 | `privacy-scan.py --include-untracked` | 0 | `RESULT: OK` | `logs/privacy-scan-1.1.6.txt` |
| 24 | `git diff --check` | 0 | 通过 | `logs/git-diff-check-1.1.6.txt` |

---

## 2. Git 证据

| ID | 事实 | 值 |
| --- | --- | --- |
| `G-01` | 仓库 / remote | `D:\Download\DSH\StardewValley` / `https://github.com/Hiyomi-Kuro/StardewValley.git` |
| `G-02` | branch / HEAD | `main` / `6e90ef2ac0e1d52832ad3564d4234640cc535d14` |
| `G-03` | `origin/main` | 同 HEAD |
| `G-04` | ahead / behind | 0 / 0 |
| `G-05` | 开始时工作区 | 干净 |
| `G-06` | `6e90ef2` 相对父提交 | 9 文件（7 M + 2 A），+1946 / −68 |
| `G-07` | CI run | `42238545103`，event=push，head_sha=`6e90ef2`，completed/success，3/3 jobs |
| `G-08` | CI 日志 | 未认证 logs 请求 403；本机无 `gh`；未重新下载 |

本阶段结束时的本地改动（未 commit / 未 push）：

```
 M Mod/P114.RuntimeProbe/README.md
 M Tools/README.md
 M docs/audit/phase1.1.5-evidence-index.md
 M docs/audit/phase1.1.5-report.md
?? docs/audit/phase1.1.6-report.md
?? docs/audit/phase1.1.6-evidence-index.md
```

---

## 3. Bundle 证据（P116-03）

| ID | 事实 | 值 |
| --- | --- | --- |
| `B-01` | 部署源 | `tasks/20261106-p115-recursive-deploy-a1/outputs/bundle-build/Mods` |
| `B-02` | manifest | `bundle-manifest.json`，11 / 172191，SHA-256 `d1e82c04…f63dc` |
| `B-03` | 实测文件数 / 总字节 | **11 / 172,191** |
| `B-04` | 逐文件 SHA-256 | 与 manifest、1.1.5 报告 §4.2 逐字符一致（报告 §3） |
| `B-05` | 旧产物对照 | `outputs/bundle-reuse-p114-artifacts`：11 / 170,655（未使用） |
| `B-06` | Fake ADB 测试树 | 12 路径（多 `nested/deep/one.txt`），非生产 bundle |
| `B-07` | SDK | `work/dotnet/dotnet.exe`，`--list-sdks` = 8.0.404 |
| `B-08` | 部署脚本 hash | `Tools/deploy-apk-safe.ps1` = `95671f82178e7b5cc07e5dbff2ac693cdd2089bbb9253ad6196c210f18aa3fef` |
| `B-09` | 部署测试 hash | `Tools/tests/test_deploy_safe.ps1` = `47e738ef56f01cb059362af252c81791a1fcd481bc2105fdb8736eea81c8a4b0` |

---

## 4. E0 证据（只读，推送前）

| ID | 事实 | 值 | 来源 |
| --- | --- | --- | --- |
| `E0-01` | 完整预检 | `E0: PASS (read-only)` | `e0-device-precheck.txt` |
| `E0-02` | 部署器只读预检 | `PRECHECK-ONLY ok` | `e0-deploy-precheck.txt` |
| `E0-03` | serial / 状态 | `emulator-5554`，`device`，`transport_id:1` | E0-01 |
| `E0-04` | 型号 | `PD2309` / `V2309A` / `PD2309`，vivo | E0-01 |
| `E0-05` | Android / API | 12 / 32，patch 2022-04-05 | E0-01 |
| `E0-06` | ABI / abilist | `x86_64` / `x86_64,arm64-v8a,x86,armeabi-v7a,armeabi` | E0-01 |
| `E0-07` | native bridge | `libnb.so` | E0-01 |
| `E0-08` | `ro.kernel.qemu` | 空 | E0-01 |
| `E0-09` | device-class | `EMULATOR-TRANSLATED` / `NOT-ARM64-PHYSICAL` | E0-01 |
| `E0-10` | 包 / 版本 | `com.chucklefish.stardewvalley`，239 / 1.6.15.0 | E0-01 |
| `E0-11` | app uid / primaryCpuAbi | `10152` / `arm64-v8a`（翻译层报告值） | `e0-supplement.txt` |
| `E0-12` | 设备端 APK SHA / 字节 | `65e1e2af…051f9d` / 388,967,580 | E0-01 |
| `E0-13` | 本地 APK SHA / 字节 | 同左 | `Get-FileHash` |
| `E0-14` | 本地签名摘要 | `611c8fa4…24c5de` | 部署器输出 |
| `E0-15` | app files 目录 | `10152:10152`，mode `771` | E0-01 |
| `E0-16` | `files/Mods`（推送前） | **不存在** | E0-01 / `e0-supplement.txt` |
| `E0-17` | 现存 Mod | 无 | E0-16 |
| `E0-18` | `/data` 可用空间 | ≈ 105.5 GiB | `e0-supplement.txt` |
| `E0-19` | root / SELinux | uid 0 可用 / `Permissive` | E0-01 |
| `E0-20` | 必需命令 | 13 项全在 | `e0-supplement.txt` |
| `E0-21` | Saves | 只 `test -d`，未读取 | `e0-supplement.txt` |

---

## 5. 前置条件证据：装机 APK 未注入 Bootstrap

| ID | 事实 | 值 |
| --- | --- | --- |
| `P-01` | 设备 APK = 本地 APK | `65e1e2af…051f9d` |
| `P-02` | APK 条目数 | 3672 |
| `P-03` | framework/probe 条目 | **0** |
| `P-04` | APK 内 `assemblies.blob` | 5,588,326 B，`c8ee504e…cabd9` |
| `P-05` | 仓库 blob | **同一 SHA-256**（未注入） |
| `P-06` | 设计契约 | `phase1-loader-design.md` §9（Bootstrap → File.Exists → LoadFrom → Loader.Run） |
| `P-07` | 结果 | 无 `loader-report.txt` / `loader.log` / `probe-output`（§6） |

---

## 6. 执行证据（A + C）

### 6.1 推送（A）

| ID | 事实 | 值 |
| --- | --- | --- |
| `A-01` | 批准 | 用户本轮明确批准 A + C |
| `A-02` | 命令 | `deploy-apk-safe.ps1 -PushModsApproved -Serial emulator-5554 -ModsSourceDirectory <bundle>\Mods` |
| `A-03` | 时间（设备） | `23:26:05–23:26:17` |
| `A-04` | 结果 | `verdict: PUSHED`；11 files written，0 identical，0 conflicting |
| `A-05` | staging | `/data/local/tmp/svmods-9d4ce611681a`，用后 `removed` |
| `A-06` | 三段校验 | `verify-staged 11/11`、`verify-remote 11/11`、`verify-owner 11/11` |
| `A-07` | 目录 | `dirs-created 10`，`verify-dirs 10/10`，owner `10152:10152` |
| `A-08` | 独立复核 | 11 文件、hash 全同、`0644`、目录 `0755`、staging=0 |
| `A-09` | APK 安装 | 未发生（未传 `-InstallApproved`） |
| `A-10` | 覆盖 | 未发生（未传 `-OverwriteModsApproved`） |

日志：`push-1.1.6.txt`、`post-push-verify-1.1.6.txt`。

### 6.2 启动（C）

| ID | 事实 | 值 |
| --- | --- | --- |
| `C-01` | 命令 | `adb -s emulator-5554 shell am start -W -n …MainActivity` |
| `C-02` | 时间（设备） | `23:26:54` |
| `C-03` | 结果 | `Status: ok`、`LaunchState: COLD`、`TotalTime: 1769`、`WaitTime: 1773` |
| `C-04` | 进程 | pid `28116` 存活 |
| `C-05` | 前台 | `topResumedActivity` / `mCurrentFocus` = `.MainActivity` |
| `C-06` | 崩溃 | 无 FATAL / AndroidRuntime / am_crash / SIGSEGV / ANR |
| `C-07` | 标题界面 | 截图 `evidence/title-screen-1.1.6.png`（SHA-256 `bf89e1c2…fcf7`） |
| `C-08` | 存档 | 未点击 NEW/LOAD，未进入/创建/保存存档 |

日志：`launch-1.1.6.txt`、`post-launch-health-1.1.6.txt`。

### 6.3 运行时文件（批准范围内）

| ID | 检查 | 内部 `files/Mods` | 外部 `/sdcard/Android/data/<pkg>/files/Mods` |
| --- | --- | --- | --- |
| `R-01` | `loader-report.txt` | ABSENT | ABSENT |
| `R-02` | `loader.log` | ABSENT | ABSENT |
| `R-03` | `probe-output/` | 目录不存在 | ABSENT |
| `R-04` | `probe-output/phase1.1.4.txt` | ABSENT | ABSENT |

### 6.4 迁移观察（新）

| ID | 事实 | 值 |
| --- | --- | --- |
| `M-01` | 游戏日志 | `23:26:56.048 I/DOTNET: [SDV_INIT] GetExternalFilesDir passed! There are files/folders in GetFilesDir that need to be copied.` |
| `M-02` | 迁移前 | 内部 `files/Mods` 有 11 文件（A-08 已复核） |
| `M-03` | 迁移后 | 内部 `files/Mods` **不存在**；外部 `files/Mods` 有 **同样 11 文件、同样 SHA-256** |
| `M-04` | 属主/模式 | `10152:1078`（`u0_a152:ext_data_rw`），文件 `644`，目录 `2770`（setgid） |
| `M-05` | 不是清数据 | 内部 `files/.config`、`.local` 仍在，只有 `Mods/` 被搬走 |
| `M-06` | 排除丢失 | 外部 11 个 hash 与 §3 逐字符一致 |

日志：`external-mods-after-launch-1.1.6.txt`、`logcat-game-pid-1.1.6.txt`。

---

## 7. R-01 … R-14（Emulator）

全部 **BLOCKED**：Loader 从未启动（无 Bootstrap），因此没有 probe 输出。
没有一项记成 PASS，也没有把 Desktop 值搬过来。

| ID | Emulator | 原因 |
| --- | --- | --- |
| R-01 / R-02 | BLOCKED | 无 `probe-output` |
| R-03 / R-12 | BLOCKED | 无 `loader-report.txt` |
| R-04 … R-11 | BLOCKED | 无 `probe-output` |
| R-13 | BLOCKED | 无 `probe-output`（无法确认设备上被 LoadFrom 的文件哈希） |
| R-14 | BLOCKED | 无 `probe-output`（损坏 fixture 从未被加载） |

Desktop 参照值（Desktop .NET，仅参照）见报告 §7。

---

## 8. 安全与未执行（负向证据）

| ID | 断言 | 证明 |
| --- | --- | --- |
| `N-01` | 无 install / uninstall / pm clear / force-stop | 命令记录与批准参数；脚本静态无卸载路径 |
| `N-02` | 无 Saves 读取/写入 | 只 `test -d`；未列目录内容；未点击 LOAD |
| `N-03` | 无 T11/T12 / P-Invoke / mprotect / RWX / detour | 未执行任何此类实验 |
| `N-04` | 无 `-OverwriteModsApproved` | 批准命令未包含 |
| `N-05` | 无 `git push` / GitHub 写 | 只读 GET；本地未 commit |
| `N-06` | 未读取批准范围外文件 | 只读 §6.3 的两个路径 + 过滤 logcat + 截图 |

---

## 9. 复现清单

```powershell
# 1) 只读核对
python D:\Download\DSH\tasks\20261106-p116-emulator-p114-a1\work\verify_bundle.py `
  D:\Download\DSH\tasks\20261106-p115-recursive-deploy-a1\outputs\bundle-build
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\device-precheck.ps1 -Serial emulator-5554

# 2) 推送（需独立批准；本阶段已执行）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy-apk-safe.ps1 `
  -PushModsApproved -Serial emulator-5554 `
  -ModsSourceDirectory D:\Download\DSH\tasks\20261106-p115-recursive-deploy-a1\outputs\bundle-build\Mods

# 3) 启动（需独立批准；本阶段已执行）
adb -s emulator-5554 shell am start -W -n com.chucklefish.stardewvalley/com.chucklefish.stardewvalley.MainActivity

# 4) 本地回归
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\run-all.ps1 `
  -Dotnet D:\Download\DSH\tasks\20261106-p115-recursive-deploy-a1\work\dotnet\dotnet.exe `
  -Refs .tmp\p113\refs
```
