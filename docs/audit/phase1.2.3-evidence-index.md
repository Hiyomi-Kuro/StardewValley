# Phase 1.2.3 证据索引

- 日期：2026-11-14（+08:00）
- 分支 / 基线：`main` / `62477fbe3ec8ade4888558ad1256679af219dc3b`（`origin/main` 同值）
- 工作区：Phase 1.2.2 + 1.2.3 改动未提交；本阶段没有 commit / push
- 设备：`6d72…`，vivo V1963A / PD1963，Android 10 / API 29 / arm64-v8a（完整 serial 只在本地
  `.tmp/phase1.2.3/20261114-125621/device/01-devices.txt` 与备份 meta 中，未写入本索引）
- 关联：[阶段报告](phase1.2.3-report.md) · [真机验收报告](phase1.2.3-device-acceptance-report.md)

所有原始文件在 `.tmp/phase1.2.3/20261114-125621/`（`.gitignore` 排除，不入库）与项目外备份目录。
本文件只说明“哪个文件能证明什么”，不复制大段正文。

---

## 1. 本轮真实产生的证据

### 1.1 设备识别与只读基线

| 证据 | 内容 | 能证明什么 |
| --- | --- | --- |
| `device/01-devices.txt` | `adb devices -l` | 当时有 3 个 transport；物理机 `6d72…` 与两个 MuMu 区分开 |
| `device/02-getprop.txt` | 全量 `getprop` | V1963A / PD1963 / Android 10 / API 29 / arm64-v8a / 非 qemu / `ro.debuggable=0` |
| `device/03-pm-path.txt` | `pm path` | 安装路径 |
| `device/04-dumpsys-package-full.txt`、`05-package-summary.txt` | `dumpsys package` | 239 / 1.6.15.0、targetSdk 34、firstInstallTime |
| `device/06-apk-sha256.txt` | 设备端 base.apk SHA-256 | `97c38ed7…5d90`，与 `final/` 测试包一致 |
| `device/07-pidof.txt` | `pidof` | 部署前游戏未运行（exit 1、空输出） |
| `device/10-files-ls.txt` … `13-tx-ls.txt` | files / Mods / Saves / `.mods-deploy` | Mods 存在、Saves 空、事务目录原本不存在 |
| `device/20-mods-tree.txt`、`21-mods-sha256.txt` | Mods 递归列表与逐文件哈希 | 部署前 17 文件基线 |

### 1.2 备份

| 证据 | 内容 | 能证明什么 |
| --- | --- | --- |
| `C:\Users\<user>\Documents\StardewValley-TestBackups\V1963A\20261114-125621\` | Mods 17 文件 / 202,891 B；Saves 0；meta | 部署前快照；目标目录创建前不存在，未覆盖旧备份 |
| 备份 `meta/backup-manifest.json` + 本目录 `backup-manifest.json` | 逐文件相对路径、字节、SHA-256 | 逐文件双端核对：17/17 一致 |
| 备份 `meta/device-serial.txt` | serial / model / API / ABI | 备份与设备绑定 |

### 1.3 工具执行（真实设备）

| 证据 | 退出码 / 关键值 | 能证明什么 |
| --- | --- | --- |
| `json/10-inspect.json`、`logs/10-inspect*.txt` | 0；`deviceTouched=false`；`transactions=[]` | 只读检查可用、无未完成事务 |
| `json/11-plan-pre-fix.json` | 3；`manifest-invalid` / `manifest-unknown-field:author` | 修复前真实缺陷（证据保留） |
| `json/11-plan.json`、`logs/11-plan*.txt` | 0；11 文件、11 SKIP | 修复后源树/manifest 可计划 |
| `json/12-deploy-dryrun.json` | 0；`mode=dry-run`、`deviceTouched=false` | dry-run 未写设备；之后 `.mods-deploy` 仍不存在 |
| `json/13-deploy-apply.json`、`logs/13-deploy-apply*.txt` | 0；`mode=apply`；txid `20261114T050317Z-b29f6e23`；`journal.state=committed`；30.2 s | 生产集 apply 提交；0 added / 0 replaced |
| `json/14-verify-prod.json` | 0；11/11、missing 0、modified 0、extra 6 | 生产集哈希一致；extra 均为运行期生成物 |
| `json/15-plan-fixture.json` | 0；12 文件、1 ADD | 验收夹具制造真实 ADD，不伪造 REPLACE |
| `json/16-deploy-fixture-apply.json`、`logs/16-*.txt` | 0；txid `20261114T050505Z-9971fd24`；`committed`；36.2 s；`switched : ADD …`、`verify-remote : 12/12` | ADD 的 staging→切换→最终哈希路径真实执行 |
| `json/17-verify-fixture.json` | 0；12/12 | 夹具文件已在设备正确落位 |
| `json/18-rollback-dryrun.json` | 0；`deviceTouched=false` | 回滚 dry-run 未写设备 |
| `json/19-rollback-apply.json`、`logs/19-*.txt` | 0；7.2 s；`detail=rollback completed` | 同事务安全回滚执行 |
| `json/20-verify-after-rollback.json` | 0；11/11、ADD 已消失 | 回滚后回到生产基线 |

### 1.4 设备端事务与哈希实物

| 证据 | 内容 |
| --- | --- |
| `device/30-tx-list.txt`、`31-tx-tree.txt`、`32-journal.txt` | 生产 tx 的目录与 `committed` journal |
| `device/40-fixture-marker-sha256.txt` | ADD 标记设备端哈希 `bf258fab…24ff` |
| `device/41-fixture-journal.json.txt`、`42-fixture-tx-tree.txt` | 夹具 tx 的 `committed` journal 与 `operations` |
| `device/43-journal-after-rollback.txt` | 夹具 tx 的权威 journal：`state=rolled-back` |
| `device/44-marker-after-rollback.txt` | 标记已从 Mods 移除 |
| `device/45-rollback-dir.txt` | 标记保留在 `rollback/P114.RuntimeProbe/`（221 B） |
| `device/46-tx-list-2.txt` | 两个事务均保留，未清理 |
| `device/33-…`、`47-…`、`48-…`、`67-…`、`68-…` | 部署后/回滚后 Mods 哈希、Saves 递归（空） |
| `comparison-production.json` | 计划 11 vs 设备 17：11/11 一致；列出 6 个 extra |
| `comparison-fixture-post-rollback.json` | 回滚后 11/11 SKIP 仍在、ADD 不在 |

### 1.5 启动、Loader、探针

| 证据 | 内容 | 能证明什么 |
| --- | --- | --- |
| `device/50-prelaunch-stat.txt` | 旧报告 1273 B / mtime 09:20:16 | 采集前先固定旧报告 |
| `device/51-resolve-activity.txt`、`52-am-start.txt` | `/.MainActivity`；`Status: ok`、`WaitTime: 1291` | 启动命令与结果 |
| `device/53-launch-poll.txt` | 启动时间戳 | 有界轮询 |
| `device/60-loader-report.txt`、`61-loader-log.txt` | 1368 B、mtime 13:06:58；`mods-discovered=1`、`failed=0`、`result=ok` | 本次启动的新 Loader 结果 |
| `device/62-p114-probe-report.txt` | `observed=12 pass=12 fail=0`；R-03/R-12 NOT RUN | 本次启动的探针直接结果 |
| `device/63-p114-mod-log.txt` | 13:06:58 两条新日志 | 探针确由本次启动触发 |
| `device/64-stat-after-launch.txt` | 四个运行期文件的 mtime/size | 证明不是旧报告 |
| `device/65-foreground.txt`、`66-pid.txt` | `mResumedActivity` / pid 15512 | 游戏保持前台 |
| `device/70-title-screen.png` | 2400×1080 PNG，398,992 B，sha256 `c4994a6c…7f38` | 标题界面，无更新/隐私/实名弹窗；未进存档 |
| `device/71-logcat-pid.txt` | pid 过滤 4 行 | 无 FATAL / SIGSEGV / UnsatisfiedLinkError / dlopen failed |
| `device/72-final-state.txt` | 13:33:42 复核：pid 15512、Saves 0、MainActivity resumed | 验收结束时游戏仍在标题界面，Saves 仍为空 |

### 1.6 部署源与工具绑定

| 证据 | 内容 |
| --- | --- |
| `mods-source/` | 本轮生产部署源 11 文件（170,655 B），SHA-256 与设备完全一致 |
| `mods-source-fixture/` | 夹具源 12 文件（+221 B 标记） |
| 工具 SHA-256 | `Tools/deploy-mods-safe.ps1` 见阶段报告 §8（1.2.3 最终字节） |
| 测试 SHA-256 | `Tools/tests/test_deploy_mods_safe.ps1` 见阶段报告 §8 |
| `tool-binding.json` | 把 git HEAD、工具/测试 SHA-256、adb 版本、设备/包、txid、部署源 11 文件哈希与截图哈希写在一个文件里 |

### 1.7 主机回归

| 证据 | 结果 |
| --- | --- |
| `logs/30-test_deploy_mods_safe.txt` | fake-adb 套件 **50/50 PASS**（含新增 2 项），退出码 0 |
| `logs/30a-test-results.log`、`30b-test-summary.json` | 逐项 PASS 行与机器汇总 `total=50 passed=50 failed=0 skipped=0` |
| `logs/31-ci-offline.txt`、`34-ci-offline-after-docs.txt` | 13/13 PASS（PS 语法、UTF-8、mojibake、CI 结构等）；文档改完后再跑仍 13/13 |
| `logs/32-privacy-scan.txt`、`35-privacy-after-docs.txt` | `RESULT: OK`，退出码 0 |
| `logs/33-p114-static.txt` | 46/46 PASS，退出码 0 |
| `git diff --check` | 退出码 0 |

> 套件脚本把工作目录固定为 `.tmp/phase1.2.2`。运行前已把当时的 `logs/` 归档到
> `.tmp/phase1.2.2/logs-archived-before-1.2.3-20261114-125621/`；跑完把 1.2.2 的 48/48 旧
> `test-results.log` / `test-summary.json` 恢复，避免覆盖历史证据。本轮 50/50 的结果另存为
> `logs/30a-test-results.log`、`30b-test-summary.json`。

---

## 2. 本轮**不存在**的证据（不得声称有）

| 证据 | 状态 | 原因 |
| --- | --- | --- |
| 生产文件的 REPLACE 切换 | **NOT_RUN** | 设备已是同一份字节，计划全 SKIP；没有为制造差异修改 DLL |
| Android 11+ 真机 apply | **NOT_RUN** | 只有 API 29 设备；工具继续 fail-closed |
| 其它 OEM / 其它 ARM64 设备 | **NOT_RUN** | 只有这一台 V1963A |
| 断电/拔线/进程中断故障注入 | **NOT_RUN** | 按用户边界未做破坏性注入 |
| 本轮改动的 GitHub Actions run | **NOT_RUN** | 改动未 commit/push；`62477fb` 的历史 Run 不含本轮代码 |
| PSScriptAnalyzer | **NOT_RUN** | 本机未安装，未擅自新增依赖 |
| 大 Mod 树（数千文件）性能 | **NOT_RUN** | 本轮 11 + 1 文件 |
| 第三方文件浏览器 / `run-as` 直接读取部署后文件 | **NOT_RUN** | 应用非 debuggable；改用 Loader 报告证明加载 |

## 3. 结论边界

1. fake-adb 通过 ≠ 真机通过；本轮真机通过也**只覆盖实际执行的路径**。
2. 生产集全 SKIP，不等于验证了 REPLACE 切换；ADD 与 rollback 由夹具覆盖。
3. 一台 V1963A / Android 10 的证据 ≠ 所有 ARM64、≠ Android 11+、≠ SMAPI 兼容。
4. 工具源码通过 ≠ 应用一定能读到所有部署文件；本轮用 Loader 报告证明本次 11 文件被加载。
5. CI 历史成功 ≠ 本轮改动通过；本轮 CI 是 NOT_RUN。

## 4. 复现命令

```powershell
cd D:\Download\DSH\StardewValley

# 只读 + 计划 + dry-run + apply + verify（使用本机 SDK adb，显式 serial）
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
$src = ".tmp\phase1.2.3\20261114-125621\mods-source"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy-mods-safe.ps1 -Action inspect -Serial 6d72… -Adb $adb -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy-mods-safe.ps1 -Action plan -Serial 6d72… -Adb $adb -Source $src -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy-mods-safe.ps1 -Action deploy -Serial 6d72… -Adb $adb -Source $src -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy-mods-safe.ps1 -Action deploy -Serial 6d72… -Adb $adb -Source $src -Apply -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy-mods-safe.ps1 -Action verify -Serial 6d72… -Adb $adb -Source $src -Json

# 主机回归
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_mods_safe.ps1
python Tools\tests\test_ci_offline.py
python Tools\privacy-scan.py
python Tools\tests\test_p114_probe.py
git diff --check
```

判定只看退出码与 `SUMMARY` 行；`SKIPPED` 不算 PASS。

## 5. 备份与清理

- 备份：`C:\Users\<user>\Documents\StardewValley-TestBackups\V1963A\20261114-125621\`（项目外，不自动删除）。
- 设备事务：`.mods-deploy/20261114T050317Z-b29f6e23`、`.mods-deploy/20261114T050505Z-9971fd24` 保留。
- 本阶段没有删除 journal、staging、backup、rollback 或任何未知文件。
