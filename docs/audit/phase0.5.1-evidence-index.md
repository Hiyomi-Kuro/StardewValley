# Phase 0.5.1 证据索引

- 日期：2026-11-05
- 对应报告：`phase0.5.1-report.md`
- 工作目录：`D:\Download\DSH\StardewValley`，branch `main`，HEAD `b51af45b14a9574639fa3cefe7250d86cca9ad35`
- 本阶段证据根：`.tmp/phase0.5.1/logs/`（gitignored；`Tools/tests/run-all.ps1` 生成）

---

## 0. 证据等级（沿用 Phase 0.5，并收紧 D 的写法）

| 级 | 含义 | 能否复现 |
| --- | --- | --- |
| **A** | 直接读仓库源码/二进制/编译期检查得到的事实 | ✅ 同一 HEAD 可复现 |
| **B** | 本阶段实际执行的离线测试（构建、注入、容器、Loader 宿主、静态扫描） | ✅ 按 §3 命令可复现 |
| **C** | 本阶段只读读取的设备状态 | ✅ 同一设备可复现；换设备结果会变 |
| **D** | Phase 0 历史记录，原始日志已丢失 | ❌ 只能写 `RECORDED PASS (Evidence D; original logs unavailable; not independently reproducible)` |
| **E** | 外部参考，未独立验证 | ❌ 仅引用 |

硬规则：**D 级不得写 "PASS"，不得填进真机列，不得当作当前通过。**

---

## 1. 本阶段证据文件

| 文件 | 内容 | 等级 |
| --- | --- | --- |
| `.tmp/phase0.5.1/logs/ci-offline.txt` | `test_ci_offline.py` 13 项 | B |
| `.tmp/phase0.5.1/logs/privacy-scan.txt` | 隐私扫描（112 文件，0 CRITICAL/HIGH） | B |
| `.tmp/phase0.5.1/logs/device-precheck-selftest.txt` | adb 解析自测 10 项 | B |
| `.tmp/phase0.5.1/logs/test-mod-pipeline.txt` | 既有 27 项离线流水线 | B |
| `.tmp/phase0.5.1/logs/test-loader.txt` | Loader 驱动 9 项 + 内部 125 项 | B |
| `.tmp/phase0.5.1/logs/git-diff-check.txt` | `git diff --check` | B |
| `.tmp/phase0.5.1/logs/build-probe.txt` | `MonoRuntimeProbe` 用 Android refs 编译 | B |
| `.tmp/phase0.5.1/logs/e0/device-precheck.txt` | E0 只读预检（本阶段重新采集） | **C** |
| `.tmp/phase0.5.1/logs/summary.txt` | `run-all.ps1` 的汇总表（人工粘贴） | B |

> `.tmp/` 被 gitignore：删除后 §3 的命令可以重建全部 B 级证据；**C 级**需要同一台设备；
> **D 级**永远无法重建。

---

## 2. 结论 → 证据映射

| 结论（报告 §） | 等级 | 直接证据 |
| --- | --- | --- |
| 基线 HEAD=`b51af45`、工作区干净、origin/main 未前进（§1） | A | `git rev-parse` / `git status` / `git ls-remote`（只读） |
| 历史模拟器结果已改为 RECORDED PASS (D)（§2） | A | 8 个文档的 diff + CI 检查 `no_current_run_pass_for_historical_evidence` |
| `device-precheck` 无个人路径、6 级解析、脱敏（§3） | B | `device-precheck-selftest.txt`（10/10） |
| 冷启动 adb 返回空属性是真实现象（§3.2） | C | 本阶段第一次 E0 输出（6 个属性为空）与修复后 E0 输出对比 |
| 当前设备仍非 ARM64（§2.4） | C | `e0/device-precheck.txt`：`abi=x86_64`、`native-bridge=libnb.so` |
| ModInjector 乱码已修（§4） | A + B | 源码 diff（1 行）；`[error] unknown command` + 退出码 2；`no_mojibake_in_tracked_text` |
| 乱码根因是 GBK 字节被当 UTF-8 + 私用区字符（§4.1） | A | 修复脚本 `.tmp/phase0.5/work/fix_mojibake.py` 的码点断言输出 |
| 隐私扫描 0 真实泄露（§5） | B | `privacy-scan.txt` + `Tools/privacy-allowlist.json`（2 条带理由） |
| CI 工作流结构合法、无 secrets/artifact（§6） | B | `ci_workflow_static`（PyYAML 结构化断言） |
| CI 的每条命令本机可执行（§6.3） | B | `run-all.ps1` 6/6 suites |
| 27 项流水线当前仍全过（§7.1） | B | `test-mod-pipeline.txt`：`27 checks, 27 passed, 0 failed` |
| Loader 离线链路（manifest→load→init）成立（Phase 1） | B | `test-loader.txt`：`125 checks, 125 passed` |
| E1/E2 真机 | **BLOCKED** | 无 ARM64 物理设备（C 级设备分类） |

---

## 3. 一条命令重建（B 级全部）

```powershell
cd D:\Download\DSH\StardewValley
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\run-all.ps1 -Refs .tmp\phase0.5\refs
```

单独跑某一项：

```powershell
python Tools\tests\test_ci_offline.py                     # 13 项，不需要游戏程序集
python Tools\privacy-scan.py --include-untracked          # 隐私扫描
powershell -File Tools\tests\test_device_precheck.ps1     # adb 解析自测（不连设备）
python Tools\tests\test_mod_pipeline.py                   # 27 项，需要 .tmp/phase0.5/refs
python Tools\tests\test_loader.py --refs .tmp\phase0.5\refs   # Loader，需要 refs 才能编示例 mod
powershell -File Tools\device-precheck.ps1 -OutFile .tmp\phase0.5.1\logs\e0\device-precheck.txt   # C 级，需要设备
```

---

## 4. 不可复核 / 保留项

| 项 | 状态 | 原因 |
| --- | --- | --- |
| Phase 0 探针原始输出（`Game1 576/532`、T00–T07） | D | `.tmp/phase0/` 已删 |
| T12 原生崩溃 logcat | D | 同上；源码 `DetourProbe.cs` 仍在 |
| AOT `normal` 的重定位级证据 | 不可复核 | `xa_reloc.py` 已丢失 |
| 远程 GitHub Actions 运行结果 | NOT RUN | 本阶段未 push |
| 设备 E1/E2 | BLOCKED | 无 ARM64 物理设备 + 未获装机授权 |
