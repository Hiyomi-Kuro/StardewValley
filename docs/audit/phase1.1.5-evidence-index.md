# Phase 1.1.5 证据索引

- 日期：2026-11-06
- 基线：`ab1e10cb8a730ce593c463bdcfb48624c570c843`
- 配套报告：[`phase1.1.5-report.md`](phase1.1.5-report.md)
- 本文件的作用：**把"结论"与"可复核的输入"分开**。每条结论都能在这里找到它的命令、产物或文件哈希。

---

## 0. 证据等级（沿用本阶段五类）

| 等级 | 含义 | 能证明 | **不能**证明 |
| --- | --- | --- | --- |
| **Desktop .NET** | Windows x64 的 .NET 运行时里真实运行 | 代码逻辑、Loader 时序、报告内容、异常隔离 | MonoVM / Android 的任何行为 |
| **AndroidRefsDir** | 用游戏的 65 个程序集当参考程序集**编译**通过 | 用到的 BCL 成员在目标引用面里存在 | 设备上行为正确 |
| **Emulator** | 真实模拟器进程运行，注明 ABI 与 native bridge | 该实例上的行为 | 原生 AArch64 行为 |
| **ARM64 physical** | 真实物理 ARM64 设备运行 | 目标运行时真实行为 | — |
| **CI** | GitHub Actions 对**具体 commit** 的远程结果 | 该 commit 在该 runner 上通过 | 本地专属套件 |

**本阶段实际拿到的等级**：

| 等级 | 状态 | 说明 |
| --- | --- | --- |
| Desktop .NET | ✅ 本阶段重跑 | `test_loader.py` 宿主 473/473；`test_mod_pipeline.py` 含 LoaderContract 契约 |
| AndroidRefsDir | ✅ 本阶段重跑 | 7 个工程用 65 个游戏程序集当参考程序集编译，rc=0 |
| Emulator | ⛔ NOT RUN | 无用户批准，未推送/未启动 |
| ARM64 physical | ⛔ BLOCKED | 无合格物理设备 |
| CI | ✅ OBSERVED | 仅 `ab1e10cb` 的历史运行；本阶段改动 NOT RUN（未 push） |

> 铁律：`deploy 脚本测试 PASS ≠ 设备上真的能跑`；
> `E0 PASS ≠ Mod 已安装或运行`；`bundle 组装 PASS ≠ Release 重新构建 PASS`；
> `Emulator PASS ≠ ARM64 physical PASS`。

---

## 1. 命令 → 结论 → 日志

日志保存在本阶段的 **DSH 任务目录**
`tasks/20261106-p115-recursive-deploy-a1/logs/`（DSH 任务目录，**不在仓库内、不入库**）。

| # | 命令（仓库根执行） | 退出码 | 结论 | 日志 |
| --- | --- | --- | --- | --- |
| 1 | `powershell -File Tools/tests/test_deploy_safe.ps1` | 0 | **95/95**（原 39 + 新 56） | `test_deploy_safe.txt` |
| 2 | `python Tools/tests/test_ci_offline.py` | 0 | 13/13 | `ci-offline.txt` |
| 3 | `python Tools/tests/test_p114_probe.py` | 0 | 46/46 | `p114-static.txt` |
| 4 | `powershell -File Tools/tests/test_device_precheck.ps1` | 0 | 10/10 | `device-precheck.txt` |
| 5 | `python Tools/privacy-scan.py --include-untracked` | 0 | `RESULT: OK` | `privacy-scan.txt` |
| 6 | `powershell -File Tools/device-precheck.ps1 -Serial emulator-5554` | 0 | `E0: PASS (read-only)` | `e0-device-precheck.txt` |
| 7 | `powershell -File Tools/deploy-apk-safe.ps1 -Serial emulator-5554` | 0 | `PRECHECK-ONLY ok`（0 写入） | `e0-deploy-precheck.txt` |
| 8 | `python Tools/tests/test_loader.py --refs .tmp/p113/refs` | 0 | **45/45 驱动 + 473/473 宿主** | `test_loader.txt` |
| 9 | `python Tools/tests/test_mod_pipeline.py --refs .tmp/p113/refs` | 0 | 27/27（需给 `--artifacts` 一个干净目录，见 §7） | `test_mod_pipeline.txt` |
| 10 | `dotnet build <7 个工程> -c Release -p:AndroidRefsDir=<abs refs>` | 0 | 7/7 rc=0 | `androidrefs-build.txt` |
| 11 | `python build_bundle.py --dotnet <sdk>` | 0 | 7 次构建 rc=0 + 14/14 审计 | `build-bundle.txt` |
| 12 | `Tools/tests/run-all.ps1 -Dotnet <sdk> -Refs …` | 0 | 8 套件全通过 | `run-all.txt` |
| 13 | `git diff --check` | 0 | 通过 | `git-diff-check.txt` |
| 14 | `git status --porcelain` | 0 | 仅本阶段有意修改的文件 | `git-status-final.txt` |

---

## 2. 缺陷证据（为什么"平铺复制"是错的）

| ID | 证据 | 位置 | 等级 |
| --- | --- | --- | --- |
| `D-01` | 旧实现只枚举一层：`Get-ChildItem -LiteralPath $ModsSourceDirectory -File`（无 `-Recurse`） | `git show ab1e10cb~1:Tools/deploy-apk-safe.ps1` 第 502–517 行 | 源码 |
| `D-02` | 旧实现丢弃相对路径：`$remote = "/data/local/tmp/" + $file.Name` | 同上 | 源码 |
| `D-03` | 旧实现把一切复制到 Mods 根：`cp <staged…> $remoteRoot/` | 同上 | 源码 |
| `D-04` | Loader 只扫一级子目录、且要求各自有 `manifest.json` | `Mod/ModFramework.Runtime/ModDiscovery.cs` 第 40–80 行 | 源码 |
| `D-05` | 旧 fake adb 无法观察目录树（`push` 只打印 "1 file pushed"，`su -c` 只打印 ok） | `git show ab1e10cb~1:Tools/tests/test_deploy_safe.ps1` | 源码 |
| `D-06` | 修复后 fake adb 模拟设备文件系统并断言远端路径/哈希 | `Tools/tests/test_deploy_safe.ps1` | 本地测试 |

`git show` 复核命令：

```bash
git show ab1e10cb8a730ce593c463bdcfb48624c570c843:Tools/deploy-apk-safe.ps1 | sed -n '490,521p'
git show ab1e10cb8a730ce593c463bdcfb48624c570c843:Tools/tests/test_deploy_safe.ps1 | sed -n '118,126p'
```

> 说明：基线 `ab1e10cb` 就是本阶段开始时的工作区内容，因此"修复前"用 `HEAD` 的版本即可复核。

---

## 3. 递归部署实现证据

| ID | 证据 | 位置 | 等级 |
| --- | --- | --- | --- |
| `R-01` | 递归枚举 + 相对路径计算 | `Tools/deploy-apk-safe.ps1` `Get-ModsPlan` / `Get-RelativeSlashPath` | 源码 + 测试 |
| `R-02` | 路径穿越 / reparse point / 绝对路径拒绝 | 同上 `Get-ModsPlan` | 源码 + 测试 |
| `R-03` | POSIX 单引号转义，远端命令不拼接未受控字符串 | `Quote-Posix` / `Invoke-RootShell` | 源码 + 测试（含中文/空格/括号用例） |
| `R-04` | 唯一 staging + 全量校验后才复制 | `Invoke-ModsDeployment` 的 staging 段 | 源码 + 测试 |
| `R-05` | 最终位置二次 `sha256sum` + `stat` 属主/权限 | `verify-remote` / `verify-owner` / `verify-dirs` 输出 | 测试日志 |
| `R-06` | 冲突 fail-closed、同哈希跳过、`-OverwriteModsApproved` | `collision-check` 段 | 测试用例 `conflict_*` / `overwrite_*` / `second_deploy_*` |
| `R-07` | staging 成功/失败都清理 | `Remove-Staging` + `finally` 语义 | 测试用例 `*_cleans_staging` |
| `R-08` | 无 uninstall / pm clear / force-stop / am start / Saves / install | 静态扫描 + 每个场景的 adb 日志断言 | 测试用例 `push_never_*`、`script_has_no_destructive_invocation` |
| `R-09` | 上传前证明设备具备必需命令（`command -v` 8 项） | `Invoke-ModsDeployment` 的 tool gate | 测试用例 `push_missing_device_tool_*` |
| `R-10` | 远端命令形态在**真实设备**上可用（`stat %n`、`test -e`、`ls -1a`、`sha256sum`、嵌套单引号） | 只读探测，见 `logs\e0-remote-command-forms.txt` | Emulator 只读 |

---

## 4. Bundle 证据

| ID | 证据 | 值 |
| --- | --- | --- |
| `B-01` | bundle 根（**部署源**） | `tasks/20261106-p115-recursive-deploy-a1/outputs/bundle-build/Mods` |
| `B-02` | 文件清单（含每文件 SHA-256） | `outputs\bundle-build\bundle-manifest.json` |
| `B-03` | 构建+审计日志（7 次构建 rc=0，14/14） | `logs\build-bundle.txt` |
| `B-04` | 组装来源 | **本阶段新鲜 Release 构建**（SDK 8.0.404） |
| `B-05` | 文件数 / 总字节 | 11 / 172,191 |
| `B-06` | APK 条目检查 | 3672 条目，`p114` / `monoruntimeprobe` / `modframework` 命中 0 |
| `B-07` | 旧产物对照（**不是部署源**） | `outputs\bundle-reuse-p114-artifacts`：11 文件 / 170,655 字节，与新鲜构建字节不同 |

逐文件 SHA-256 见报告 §4.2 的表格（与 `bundle-manifest.json` 一致）。

---

## 5. 设备 E0 证据（只读）

| ID | 事实 | 值 | 来源 |
| --- | --- | --- | --- |
| `E0-01` | 完整预检日志 | `E0: PASS (read-only)` | `logs\e0-device-precheck.txt` |
| `E0-02` | 部署器只读预检 | `PRECHECK-ONLY ok` | `logs\e0-deploy-precheck.txt` |
| `E0-03` | serial | `emulator-5554`（device 状态） | E0-01 |
| `E0-04` | ABI / abilist | `x86_64` / `x86_64,arm64-v8a,x86,armeabi-v7a,armeabi` | E0-01 |
| `E0-05` | native bridge | `libnb.so` | E0-01 |
| `E0-06` | `ro.kernel.qemu` | 空 | E0-01 |
| `E0-07` | Android / API | 12 / 32 | E0-01 |
| `E0-08` | device-class | `NOT-ARM64-PHYSICAL` | E0-01 |
| `E0-09` | 包 / 版本 | `com.chucklefish.stardewvalley` / 239 / 1.6.15.0 | E0-01 |
| `E0-10` | 设备端 APK SHA-256 | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` | E0-01 |
| `E0-11` | 本地 APK SHA-256 / 字节 | 同上 / 388,967,580 | 本地 `Get-FileHash` |
| `E0-12` | 本地签名摘要 | `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de` | `apksigner verify --print-certs` |
| `E0-13` | Mods 目录 | 不存在（`/data/data/...` 与 `/sdcard/...` 均无） | E0-01 |
| `E0-14` | app files 目录 | `u0_a152:u0_a152` = `10152:10152`，mode `771` | E0-01 + `stat` |
| `E0-15` | root | `su -c id` = `uid=0` | E0-01 |
| `E0-16` | 命令可用性 | `sha256sum`/`stat`/`mkdir`/`cp`/`chown`/`chmod`/`rm`/`ls`/`id`/`tar`/`unzip` 全在 | 逐条 `command -v` |
| `E0-17` | SELinux | `Permissive`；`restorecon` 不存在 | `getenforce` / `command -v` |

---

## 6. 批准门槛证据（负向证据）

| ID | 断言 | 如何证明 |
| --- | --- | --- |
| `A-01` | 没有 `adb push` | 任务全程日志中无 push；`e0-deploy-precheck.txt` 为 PRECHECK-ONLY |
| `A-02` | 没有创建 `files/Mods` | E0-13：目录不存在（预检后仍是"不存在"） |
| `A-03` | 没有启动游戏 | 无 `am start` / `monkey` 调用 |
| `A-04` | 没有 pull 运行时文件 | 只执行了 `sha256sum` / `stat` / `ls` / `getprop` / `dumpsys` / `pm path` |
| `A-05` | 没有安装/卸载/清数据 | 未传 `-InstallApproved`；脚本静态无卸载路径 |
| `A-06` | 没有读写 Saves | 只 `ls -la` 到上层目录，未进入 `Saves/` |

---

## 7. 未完成项（明确 BLOCKED，不是 PASS）

| ID | 项 | 原因 | 解除条件 |
| --- | --- | --- | --- |
| `X-04` | Emulator R-01…R-14 | 无用户批准 | 用户分别批准推送与启动 |
| `X-05` | ARM64 physical | 无合格设备 | 真 arm64-v8a 物理设备过 E0 |

### 7.1 环境修复记录（不是产品缺陷，但影响可复现性）

本机 `.tmp/` 里 2026-11-05 的构建状态与当前 SDK/源码不一致，导致"同一命令时好时坏"。
本阶段实际做了三处**只针对 gitignored 构建目录**的修复，全部记录在案：

| 现象 | 根因 | 处置 |
| --- | --- | --- |
| `test_mod_pipeline` 报 `Could not load Mono.Cecil` | `.tmp/phase0.5/artifacts/bin/ModInjector/release` 缺少 `Mono.Cecil*.dll`（旧增量产物） | 从干净构建复制 4 个 Cecil 程序集进去 |
| `LoaderContract` 报 `MISSING-RUNTIME` | 同一目录树里 `ModFramework.Runtime.dll` 缺失 | 从本阶段新鲜构建复制 |
| `test_loader` 报 `p114_probe_build` 缺 `manifest.json`/`Dependency.dll` | `.tmp/phase0.5/artifacts-loader` 的 `obj` 状态过期，增量构建跳过了 content/dependency 复制 | 把该目录改名为 `artifacts-loader.stale-20261106`，让 `run-all` 全量重建 |

> `.tmp/` 在项目 `AGENTS.md` 里被明确标注为"可随时整体删除，构建会重建"，因此上述处置不触碰任何源码或用户数据。

---

## 8. 复现清单（最短路径）

```powershell
# 1) 静态与 fake-ADB 套件（不需要设备、不需要 SDK）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/tests/test_deploy_safe.ps1
python Tools/tests/test_ci_offline.py
python Tools/tests/test_p114_probe.py
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/tests/test_device_precheck.ps1
python Tools/privacy-scan.py --include-untracked

# 2) 只读 E0（需要设备，但零写入）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/device-precheck.ps1 -Serial emulator-5554
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk-safe.ps1 -Serial emulator-5554

# 3) 预检模式下查看"批准后会写什么"（仍然零写入）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk-safe.ps1 -Serial emulator-5554 -ModsSourceDirectory <task>\outputs\bundle-build\Mods

# 4) 有 SDK 后
python Tools/tests/test_loader.py --dotnet <sdk> --refs .tmp/p113/refs
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/tests/run-all.ps1 -Dotnet <sdk> -Refs .tmp/p113/refs
```

第 3 条是本阶段最重要的可复核点：**不批准也能看到将写入的每个相对路径、字节数与 SHA-256**。
