# Phase 1.2.2 证据索引

- 日期：2026-11-14（+08:00）
- 基线：`62477fbe3ec8ade4888558ad1256679af219dc3b`（main = origin/main，开工前核对）
- 关联：[Phase 1.2.2 报告](phase1.2.2-report.md) · [手动真机验收计划](phase1.2.2-manual-device-acceptance-plan.md)
- 状态：**主机侧证据齐全；真机证据不存在（等待用户）**

所有原始日志都在本机 `.tmp/phase1.2.2/`（`.gitignore` 已排除 `.tmp/`），**不入库**。
本文件只描述"哪个文件能证明什么"，不复制日志正文。

---

## 1. 主机侧证据（本轮真实产生）

| 本地证据 | 内容 | 能证明什么 | 不能证明什么 |
| --- | --- | --- | --- |
| `.tmp/phase1.2.2/logs/baseline-test_deploy_mods_safe.txt` | 未修改提交状态下的基线执行 | Phase 1.2.1 提交时套件 29 项里 14 通过 / 15 失败，且收尾崩溃 | 不能证明工具可用 |
| `.tmp/phase1.2.2/logs/run1-…` … `run5-…` | 六轮迭代的完整输出 | 每个缺陷的发现与修复顺序 | 中间轮次不作为验收结果 |
| `.tmp/phase1.2.2/logs/run5-test_deploy_mods_safe.txt` | **最终**全套件输出 | 工具在 fake adb 下的行为（含失败路径） | 不能代表真机 |
| `.tmp/phase1.2.2/logs/test-results.log` | 最后一次运行的逐项 PASS/FAIL/SKIP 行 | 用例级结果 | — |
| `.tmp/phase1.2.2/logs/test-summary.json` | 最后一次运行的机器可读汇总 | 总数/通过/失败/跳过 | — |
| `.tmp/phase1.2.2/logs/inspect-jsonout.json` | 工具 `-JsonOut` 产物 | JSON 契约与落盘能力 | — |
| `.tmp/phase1.2.2/logs/phase122-device-precheck.txt` | `test_device_precheck.ps1` | 既有 adb 解析自测 10/10 | 不连设备 |
| `.tmp/phase1.2.2/logs/phase122-deploy-safe.txt` | `test_deploy_safe.ps1` | 既有 APK 部署门禁 95/95 | 不连设备 |
| `.tmp/phase1.2.2/logs/phase122-ci-offline.txt` | `test_ci_offline.py` | 语法/UTF-8/CI workflow/隐私等 13 项 | 不等于 GitHub Actions 实跑 |
| `.tmp/phase1.2.2/logs/phase122-privacy.txt` | `privacy-scan.py` | 隐私扫描退出码 0 | — |
| `.tmp/phase1.2.2/logs/phase122-p114.txt` | `test_p114_probe.py` | P114 静态安全审计退出码 0 | 不连设备 |
| `.tmp/phase1.2.2/logs/manual-adb*.log`、`logs/enc-check*.log` | 排查过程中的 fake adb 命令轨迹 | UTF-8 编码链与 `/sdcard` 链接问题的复现 | 只用于排查 |
| `.tmp/phase1.2.2/<case>-<rand>/` | 每个用例独立的虚拟设备树（含 `Saves/save1.dat`、keystore、APK 哨兵） | 用例隔离与"没碰敏感路径"的现场 | 不是真实设备 |
| `.tmp/phase1.2.2/fake-adb.exe`、`fixtures-*/fake-adb-worker.ps1`、`fixtures-*/fake-adb-launcher.cs` | 测试脚本现编的 fake adb | 测试不依赖真实 adb | — |
| `.tmp/phase1.2.2/manual/` | 手工复现用的 fake 设备与脚本（`manual-apply.ps1` 等） | 单条路径的快速复现 | 不是验收证据 |

> 说明：`manual/` 与 `probe-*.ps1` 属于排查脚手架，保留供复现；不是交付物，可随时删除。

---

## 2. 本轮**不存在**的证据（不得声称有）

| 证据 | 状态 | 原因 |
| --- | --- | --- |
| 真实设备上的 `inspect`/`plan`/`deploy`/`verify` 输出 | **不存在** | 本阶段明确禁止连接设备；由用户按手动计划产生 |
| 设备端 `Mods` 逐文件 SHA-256 与主机对照 | **不存在**（本轮） | 同上；Phase 1.2 的那份 11/11 是手工流程的旧证据 |
| 设备端 `journal.json` 实物 | **不存在**（本轮） | 同上 |
| Saves before/after 对照 | **不存在**（本轮） | 同上 |
| GitHub Actions 实际运行结果 | **不存在** | 本阶段不 commit / 不 push；CI 改动只在本地静态校验 |
| PSScriptAnalyzer 报告 | **不存在** | 本机未安装，按任务要求没有擅自安装依赖 |
| Android 11+ 任何证据 | **不存在** | 未实测；策略保持 fail-closed |
| 大 Mod 树（数千文件）性能数据 | **不存在** | 只用 3 文件夹具测量过调用次数 |

---

## 3. 结论边界（照抄到任何汇报里都不要放宽）

1. fake adb 通过 **≠** 真机通过。
2. 本机 Windows PowerShell 5.1 通过 **≠** CI 的 PowerShell 7 通过（未实跑）。
3. 一台 V1963A / Android 10 的旧证据 **≠** 所有 ARM64 设备、**≠** Android 11+、**≠** SMAPI 兼容。
4. 工具源码审计通过 **≠** 应用一定能读到部署后的文件（应用非 debuggable，无 `run-as`）。
5. `deploy-mods-safe.ps1` 从未在任何真机上执行过；Phase 1.2 的无 Root 成功是**手工**流程。

---

## 4. 复现与校验命令

```powershell
cd D:\Download\DSH\StardewValley

git status --short --branch
git rev-parse HEAD

powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_mods_safe.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_device_precheck.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_safe.ps1

python Tools/tests/test_ci_offline.py
python Tools/privacy-scan.py
python Tools/tests/test_p114_probe.py

git diff --check
```

判定只看**退出码**与 `SUMMARY` 行；日志里出现的 `SKIPPED` 一律不算通过。

---

## 5. 用户真机验收时要补进本索引的文件

按 [手动真机验收计划](phase1.2.2-manual-device-acceptance-plan.md) 执行后，把主机侧证据放到
`.tmp/phase1.2.2/device-acceptance/<yyyyMMdd-HHmmss>/`，并在下表中登记（本轮全部为空）：

| 计划步骤 | 期望文件名 | 是否已产生 |
| --- | --- | --- |
| 2 设备信息 | `01-devices.txt` … `06-package.txt` | 否 |
| 3 inspect | `10-inspect.json` / `10-inspect.txt` | 否 |
| 4 plan | `11-plan.json` / `11-plan.txt` | 否 |
| 5.1 deploy dry-run | `12-deploy-dryrun.json` / `.txt` | 否 |
| 5.2 deploy apply | `13-deploy-apply.json` / `.txt` | 否 |
| 6.1 verify | `14-verify.json` / `.txt` | 否 |
| 6.2 设备端哈希与目录 | `20-device-mods-sha256.txt`、`21-device-mods-modes.txt`、`22-device-tx-list.txt`、`23-journal.json` | 否 |
| 6.2 Saves 对照 | `24a-saves-before.txt`、`24-saves-before-after.txt` | 否 |
| 7 Loader（可选） | `30-loader-report.txt`、`31-loader-log.txt` | 否 |
| 8 rollback（可选） | `40-rollback-dryrun.json`、`41-rollback-apply.json` | 否 |

**在这些文件出现并核对之前，Phase 1.2.2 的真机验收状态保持 `AWAITING_USER_MANUAL_ACCEPTANCE`。**
