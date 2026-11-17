# Phase 2.0.1 证据索引

| 项 | 值 |
| --- | --- |
| 证据根 | `.tmp/phase2.0.1/20261114-2053/`（gitignore；**不入库、不上传**） |
| 设备 | V1963A / Android 10 / API 29 / USB `6d7200a` |
| 产物 | `app/build/outputs/apk/modded/app-modded-debug.apk` `c571a25b…02a` |
| Runtime | `d2604b67…b8ce`（130,560 B） |
| 项目外备份 | `%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-2053\`（57 文件，逐文件双端一致） |

> 证据目录里没有 APK、游戏程序集、密钥、存档或设备账号信息。设备路径只保留包名与设备内绝对路径；
> 主机路径已按项目惯例脱敏（报告中用 `<home>`/叶名）。下面是本轮关键文件及其 SHA-256。
>
> **两次 Mods 部署**：先用 `80877ff1…` 的 Runtime 完成安装与首轮验收；随后加入文档注释导致确定性
> MVID 变化（`80877ff1…` → `d2604b67…`），为保证“设备字节 = 最终源码构建”，用最终构建重新部署
> （事务 `20261114T133014Z-78bc0c3a`）并重跑真机验收。设备最终 Runtime 为 `d2604b67…`；
> 第一次事务 `20261114T131519Z-e5b43656` 仍保留在 `.mods-deploy/` 中作为历史。

---

## 1. 主机回归日志

| 文件 | SHA-256（前缀） | 内容 |
| --- | --- | --- |
| `logs/test_loader-pre-fix.log` | `e0167e28…` | **修复前** host：551 checks / 532 passed / 19 failed（含 A–C 与过时断言） |
| `logs/test_loader-final-ci.log` | `2cc36e56…` | CI 同口径（无 `--refs`）：546/546 host，driver 52（2 skipped） |
| `logs/test_loader-final-refs.log` | `09132a84…` | 完整 host（含 `--refs`）：552/552，driver 53/53 |
| `logs/injector-tests.log` | `1abd2043…` | InjectorTests 29/29（含 bypass/overload 负向用例） |
| `logs/test_mod_pipeline.log` | `2fdea50e…` | pipeline 39/39（注入、幂等、混合状态、容器重建） |
| `logs/test_modded_build.log` | `e1d6460c…` | modded-build 55/55 |
| `logs/test_ci_offline.log` | `9abe238a…` | offline 13/13 |
| `logs/test_deploy_safe2.log` | `764421bc…` | fake-adb 部署门禁 101/101（含 split / 哈希不匹配 / 首装后证明） |
| `logs/host-final-perf.log` | `258f5715…` | 直接 host 运行（含 `--perf-report`）：553/553；最终源码 |
| `logs/host-final.log` | `ce8d9a03…` | 最终源码 host 回归：552/552（含 `--refs`，无 perf 项） |
| `logs/host-after-fix2.log` | `87545876…` | 修复后首轮的 host 运行（含 `--perf-report`）：553/553（历史） |
| `logs/gradle-modded2.log` | `85b9fdc6…` | 成功的 C APK 构建（keystore 显式指定） |

## 2. 性能证据

| 文件 | SHA-256（前缀） | 内容 |
| --- | --- | --- |
| `host/perf.json` | `5f7bb826…` | .NET 8.0.11 / Win10 26100 / X64 / 8 逻辑核；0 与 4 订阅各 200k Tick，**0 B/tick**（最终源码构建） |

## 3. APK 身份/准入证据

| 文件 | SHA-256（前缀） | 内容 |
| --- | --- | --- |
| `host/verify-pre-install.json` | `50eab8d4…` | 安装前：`INSTALL_CANDIDATE`；40 pass / 0 fail / 1 unknown（post-install 未发生） |
| `logs/verify-pre-install.log` | `e423e98e…` | 同上的人类可读输出 |
| `host/verify-post-install.json` | `c94835a7…` | 安装后：41/41，`post_install_hash_matches=true` |
| `logs/verify-post-install.log` | `385de2cf…` | 同上的人类可读输出 |
| `host/apk-StardewValley.dll` | `e536bbcd…` | 从**最终 APK** 解出的注入程序集（9,262,592 B） |
| `host/apk-assemblies.blob` | `903fb94a…` | 最终 APK 内的 `assemblies.blob`（6,184,102 B） |
| `device/installed-after.apk` | `c571a25b…` | 安装后从设备拉回的 `base.apk` |
| `device/installed-before.apk`（= 备份 `apk/base.apk`） | `d4f60d57…` | 安装前设备 APK（Phase 2.0 C） |

## 4. 部署与安装证据

| 文件 | SHA-256（前缀） | 内容 |
| --- | --- | --- |
| `device/install-modded.txt` | `003ff908…` | `SAME` 签名门禁 → `install -r` → `Success` → `post-install: MATCH` |
| `device/mods-plan.txt` | `ca311c15…` | 只读计划：1 REPLACE + 14 SKIP |
| `device/mods-deploy.txt` | `c161c1fc…` | 事务 `20261114T133014Z-78bc0c3a`，committed，15/15 |
| `device/mods-deploy.json` | `216f163f…` | 同事务的机器可读结果 |
| `device/deploy-journal.txt` | `d0501aa7…` | 设备端 journal：`state=committed`，旧 Runtime 在 `backup/` |
| `device/mods-before.sha256.txt` / `mods-after.sha256.txt` | `b1290ca9…` / `54391e33…` | 运行前后 Mods 全量哈希（载荷 8 文件 + 运行期产物） |

## 5. 真机运行证据

| 文件 | SHA-256（前缀） | 内容 |
| --- | --- | --- |
| `device/loader-report.txt` | `c892f988…` | 3/3 初始化、`events=ready`、session 1、7 订阅、2 mods、P114 |
| `device/alpha-snapshot1.txt` | `987c63d8…` | Tick 600、launch=1、`launched_at_tick_count=0`、顺序、线程 1 |
| `device/alpha-snapshot2.txt` | `5fa99094…` | Tick 1200、dispatch 1201、`diagnostics_mods=2` |
| `device/beta-snapshot2.txt` | `6a110dc0…` | 故障 3、健康 1200、重复 2/1/0、延迟 0→1196、double dispose 1 |
| `device/beta-log.txt` | `894e87b8…` | 3 次故障只写 2 条 ERROR（有界） |
| `device/saves-before.txt` / `saves-after.txt` | `67a00440…`（同一哈希） | 0 文件、mtime 09:15 未变 |
| `device/title.png` | `490884c7…` | 正常标题界面 |
| `device/logcat.txt` | `287f431a…` | 无 FATAL/AndroidRuntime/MissingMethod/TypeLoad/InvalidProgram |
| `device/threads.txt` | `ef70b7a2…` | `ps -T` 线程表（事件全在 thread 1） |
| `device/pid.txt` / `pid-after-stop.txt` | `1f636f8b…` / 空文件 | 12601 → 已停止 |
| `logs/run-device2.log` | `8ad83188…` | 最终 Runtime 部署后的真机运行脚本输出 |

## 6. 复现命令（主机）

```powershell
# host 完整回归（含 --refs；需要 .tmp/phase0.5/refs 的 65 个程序集）
python Tools/tests/test_loader.py --dotnet .tmp/dotnet-sdk/dotnet/dotnet.exe --refs .tmp/phase0.5/refs

# CI 同口径（无 --refs）
python Tools/tests/test_loader.py --dotnet .tmp/dotnet-sdk/dotnet/dotnet.exe

# 注入、管线、静态、隐私、准入
dotnet build Tools/tests/InjectorTests/InjectorTests.csproj -c Release --artifacts-path .tmp/phase2.0.1/artifacts
dotnet .tmp/phase2.0.1/artifacts/bin/InjectorTests/release/InjectorTests.dll
python Tools/tests/test_mod_pipeline.py --dotnet .tmp/dotnet-sdk/dotnet/dotnet.exe --refs .tmp/phase0.5/refs
python Tools/tests/test_modded_build.py
python Tools/tests/test_ci_offline.py
python Tools/tests/test_p114_probe.py
python Tools/tests/test_apk_admission.py
python Tools/privacy-scan.py
git diff --check
```

```powershell
# fake-adb 部署门禁（不需要设备）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/tests/test_deploy_safe.ps1

# 构建 C APK
.\gradlew.bat --no-daemon :app:assembleModdedDebug `
  -Pdotnet=.tmp\dotnet-sdk\dotnet\dotnet.exe -Ppython=python -Pkeystore=.work/debug.keystore

# 准入（安装前：用备份的 installed APK）
python Tools/verify_modded_apk.py `
  --candidate app/build/outputs/apk/modded/app-modded-debug.apk `
  --baseline app/build/outputs/apk/debug/app-debug.apk `
  --blob-manifest .tmp/build-modded/modded-build-manifest.json `
  --installed-apk <backup>\apk\base.apk `
  --report .tmp/phase2.0.1/20261114-2053/host/verify-pre-install.json `
  --dotnet .tmp/dotnet-sdk/dotnet/dotnet.exe
```

## 7. 保留与清理

- `.tmp/phase2.0.1/` 是 gitignore 的临时证据；构建产物 `.tmp/build-modded/`、`.tmp/phase0.5/` 可重建。
- 项目外备份与设备上的 `.mods-deploy` 事务**不删除**；回退需要它们。
- 没有上传任何 APK、游戏程序集、密钥、存档或设备敏感信息。
