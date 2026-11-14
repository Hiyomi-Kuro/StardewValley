# Phase 1.2.3：Phase 1.2.2 交付复核与 USB 真机验收闭环

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-14（+08:00） |
| 分支 | `main` |
| 基线 HEAD | `62477fbe3ec8ade4888558ad1256679af219dc3b`（`origin/main` 同值，开工时核对） |
| 工作区 | Phase 1.2.2 + 1.2.3 改动**未提交**；本阶段无 commit / push / GitHub 写操作 |
| 作用域 | `Tools/deploy-mods-safe.ps1`、其 fake-adb 套件、设计文档、审计文档、README、AGENTS（本地） |
| 真机 | USB `6d72…` = vivo V1963A / PD1963 / Android 10 / API 29 / arm64-v8a |
| 关联 | [真机验收报告](phase1.2.3-device-acceptance-report.md) · [证据索引](phase1.2.3-evidence-index.md) · [1.2.2 报告](phase1.2.2-report.md) · [设计文档](../architecture/phase1.2.1-deployment-design.md) |

---

## 0. 结论（先看这一段）

**Phase 1.2.2 的“主机侧完成、真机待验收”已在本轮闭环为真机 PASS，但只限于这台 V1963A / Android 10 与实际执行的路径。**

| 维度 | 判定 | 依据 |
| --- | --- | --- |
| 设备识别与只读证据 | **PASS** | 物理机 `6d72…`；V1963A / API 29 / arm64-v8a；包 239 / 1.6.15.0；APK `97c38ed7…5d90` |
| 部署前备份 | **PASS** | Mods 17 文件 / 202,891 B，逐文件双端 SHA-256 17/17 一致；Saves 0；`.mods-deploy` 原不存在 |
| 工具真机执行 | **PASS（限实际路径）** | inspect / plan / dry-run / apply / verify / ADD / rollback 全部真实退出码 0 |
| 生产集部署（11 SKIP） | **PASS（未发生文件切换）** | 11/11 计划与设备哈希一致；apply 只提交空操作事务 |
| 生产文件切换（`mv` / REPLACE） | **NOT_RUN** | 目标字节已相同，没有生产文件被切换 |
| ADD 切换 | **PASS** | 验收夹具 1 个文件真实 staging→切换→12/12；随后回滚 |
| REPLACE 切换 | **NOT_RUN** | 生产集无冲突；没有为制造差异修改 DLL |
| rollback | **PASS** | dry-run 0 + apply 0；设备 journal `rolled-back`；夹具字节保留在 `rollback/` |
| Saves 保护 | **PASS** | 部署前 / apply 后 / 回滚后 / 启动后均 0 文件，目录 mtime 未变 |
| Loader | **PASS** | `mods-discovered=1`、`mods-initialized=1`、`mods-failed=0`、`result=ok` |
| P114 探针 | **PASS** | `observed=12 pass=12 fail=0`；R-03/R-12 由同次 Loader 报告佐证，合并 14 PASS |
| Android 11+ | **NOT_RUN** | 只有 API 29；apply 继续 `unverified-android-storage` fail-closed |
| 本轮改动的 CI | **NOT_RUN** | 未 commit/push；历史 `62477fb` Run `43546062266` 不含本轮代码 |
| 本轮新增回归 | **PASS** | fake-adb **50/50**；离线 13/13；privacy 0；P114 静态 46/46 |

**三件必须说清楚的事：**

1. **生产集全 SKIP 不是“验证了文件切换”。** 设备上本来就是我们部署的同一份 11 文件字节；apply 只提交了
   空操作事务。真正的写入路径由 1 个验收夹具 ADD 覆盖，并已安全回滚。
2. **本轮发现并修复了两个会让真机 plan 直接失败、fake adb 却没覆盖的 P0**（§4）。因此工具版本从 1.2.2 升到 1.2.3，
   套件从 48 项增加到 50 项；历史 48/48 仍然是历史数字，不是本轮结果。
3. **CI 不能用历史 run 冒充。** 本地 `62477fb` 的 Actions Run `43546062266` 是 3/3 success，但它在
   Phase 1.2.2/1.2.3 改动之前，也不包含新的 no-root 套件步骤。本轮改动未 push，CI = **NOT_RUN**。

---

## 1. 开工核对

```text
branch            : main
HEAD              : 62477fbe3ec8ade4888558ad1256679af219dc3b
origin/main       : 62477fbe3ec8ade4888558ad1256679af219dc3b
tracked changes   : .github/workflows/ci.yml
                    README.md
                    Tools/deploy-mods-safe.ps1
                    Tools/tests/test_deploy_mods_safe.ps1
                    docs/architecture/phase1.2.1-deployment-design.md
untracked         : docs/audit/phase1.2.2-{report,evidence-index,manual-device-acceptance-plan}.md
```

- 历史基线 `62477fb` 仍是当前 HEAD；**但不能因此认为工作区干净**：Phase 1.2.2 的修改仍在工作区，
  本轮在其上继续修复。
- 全程没有 `git reset`、`git clean`、`git commit`、`git push`；没有 GitHub 写操作。
- 历史计划中“DSH 不调用真实 adb、必须由用户手动执行”的限制，已被本轮用户授权替代；
  其余安全边界（不卸载/不重装 APK、不 `pm clear`、不清存档、不 Root、不删除未知文件）继续遵守。

## 2. 历史结论的继承与本轮区分

| 历史结论（Phase 1.2.2） | 本轮处理 |
| --- | --- |
| `test_deploy_mods_safe.ps1` 48/48 | **历史数字**。本轮新增 2 项后为 **50/50**，见 §6 |
| `test_device_precheck.ps1` 10/10 | 历史数字；本轮未改该脚本，未重跑 |
| `test_deploy_safe.ps1` 95/95 | 历史数字；本轮未改该脚本，未重跑 |
| `test_ci_offline.py` 13/13 | 本轮在修改后重跑：**13/13** |
| `privacy-scan.py` / `test_p114_probe.py` 退出码 0 | 本轮在修改后重跑：**0 / 46 项全过** |
| Phase 1.2 真机基线 V1963A / Android 10 | **设备仍是这一台**；本轮首次用工具（不是手工流程）在该设备部署 |
| Android 11+ apply fail-closed | **保持**；本轮没有设备/证据，不放开 |

Phase 1.2 的 11/11 手工部署成功仍是历史证据；本轮工具化验收不能反过来改写它。

## 3. 设备识别、备份与部署源

### 3.1 设备

`adb devices -l` 同时列出物理机 `6d72…`（V1963A）与两个 MuMu transport（V2309A）。本轮**只选物理机**，
每条命令显式 `-s 6d72…`，没有连接/唤醒/写入 MuMu。

| 项 | 实际值 |
| --- | --- |
| 型号 / 设备 / 品牌 | V1963A / PD1963 / vivo（vivo Z6） |
| Android / API / ABI | 10 / 29 / arm64-v8a（`ro.dalvik.vm.native.bridge=0`） |
| 物理机 | `ro.kernel.qemu` 未设置，`ro.hardware=qcom`，verified boot green，`ro.debuggable=0` |
| 包 | `com.chucklefish.stardewvalley`，239 / 1.6.15.0，targetSdk 34 |
| APK | `/data/app/.../base.apk`，SHA-256 `97c38ed7eb6ac719d7a5ee941abac3766307ad03205569bed19db5a48e7d5d90`，389,561,500 B |
| 部署前进程 | 未运行（`pidof` 空），本轮不需要也不曾 force-stop |

APK 哈希与 `final/StardewValley-1.6.15.0-modded-test-debug.apk` 相同；本轮没有安装/卸载/替换 APK。

### 3.2 备份（部署写入前）

| 项 | 值 |
| --- | --- |
| 位置 | `%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-125621\` |
| Mods | 17 文件 / 202,891 B；逐文件设备端 `sha256sum` 对照 **17/17 一致** |
| Saves | 目录存在、**0 文件**（空目录也记录） |
| `.mods-deploy` | **不存在**（无既有事务） |
| 备份 manifest | 相对路径、字节、SHA-256、设备绑定；目标目录创建前不存在，未覆盖 |

备份验证失败就不继续 apply；本轮通过后才进入部署。

### 3.3 部署源

| 项 | 值 |
| --- | --- |
| 来源 | `.tmp/phase1.1.9/mods-bundle-fixed`（Phase 1.1.9 `artifacts-p114fix` 构建；Phase 1.2 已在这台设备验证） |
| 本轮快照 | `.tmp/phase1.2.3/20261114-125621/mods-source` |
| 文件 / 字节 | 11 / 170,655 B |
| 内容 | 根 `ModFramework.Runtime.dll`；`P114.RuntimeProbe/manifest.json` + 入口/依赖 + probe fixtures |
| 与设备 | 11/11 哈希一致 → 计划全 SKIP |

**没有重新编译程序集。** 复用的是 Phase 1.1.9 用 `-p:AndroidRefsDir` 构建、Phase 1.2 在本设备 12/12 探针通过的字节；
因此本轮没有触发 AndroidRefsDir 编译步骤。若后续重编译，必须用 `Tools/find-dotnet.ps1` + 匹配的
`-p:AndroidRefsDir=<65 refs>`，不得写死历史 SDK 路径，也不得把不同构建轮的 DLL 混入同一个部署集。

## 4. 真机暴露的两个工具 P0 与修复

用“项目自己的完整 Mods 根”运行 `plan` 时，工具连续拒绝；这是 fake-adb 夹具（`Hello Mod.dll`、极简 manifest）
从未覆盖的形状。

### P0-1 manifest 可选元数据字段被当成“未知字段”

```text
exit=3  category=PolicyRefused  reason=manifest-invalid
detail=manifest-unknown-field:author
```

`author` / `description` / `website` 是 `schemaVersion 1` 的合法可选字段（`ModManifest.KnownFields` 与
`docs/modding/manifest-spec.md` 都允许），但工具的 `$allowed` 数组漏了它们。修复：白名单补齐 3 个字段。

### P0-2 程序集名中的点号被当成非法路径

```text
exit=3  reason=manifest-invalid  detail=unsafe-manifest-path:P114.RuntimeProbe.dll
```

原校验 `^[^/\\:.]+\.dll$` 的字符类把 `.` 也排除了，于是 `P114.RuntimeProbe.dll`、`ExampleMod.HelloWorld.dll`
这类普通 .NET 程序集名全部被拒。修复：镜像 `ModManifest.IsValidEntryAssemblyName`，只拒绝路径分隔符、
`:`、`..`、控制字符、非 `.dll` 后缀和超过 128 字符。

### 回归

- 工具版本 1.2.2 → **1.2.3**。
- fake-adb 套件新增：
  - `manifest optional metadata fields accepted`（带 author/description/website 的 manifest，exit 0）；
  - `manifest unknown field still refused`（真正未知字段仍 exit 3 `manifest-invalid`）。
- 夹具 `entryAssembly` 改为带点号的 `Metadata.Assembly.dll`，锁住 P0-2。
- 两项修复先由真机失败暴露，再由 `plan` 真机重跑验证；不是“先改代码再找理由”。

### 一个已知的报告字段偏差（P2，未改代码）

`rollback -Apply` 的 JSON `journal.state` 仍是回滚前读取的 `committed`；设备端 `journal.json` 才是权威值
（本次实际为 `rolled-back`）。退出码与 `detail` 正确。记录在案，判定以设备 journal 为准。

## 5. 真机执行结果（摘要）

完整输出、逐文件哈希、截图见 [真机验收报告](phase1.2.3-device-acceptance-report.md)。

| 步骤 | 退出码 | 关键值 |
| --- | ---: | --- |
| inspect | 0 | `deviceTouched=false`、`transactions=[]`、游戏未运行 |
| plan | 0 | 11 文件 / 170,655 B；11 SKIP；无冲突 |
| deploy dry-run | 0 | `mode=dry-run`；`.mods-deploy` 仍未创建 |
| deploy -Apply（生产） | 0 | txid `20261114T050317Z-b29f6e23`；`journal.state=committed`；30.2 s；0 added / 0 replaced |
| verify（生产） | 0 | 11/11；missing 0；modified 0；extra 6（运行期生成物） |
| plan（夹具） | 0 | 12 文件；11 SKIP + 1 ADD |
| deploy -Apply（夹具） | 0 | txid `20261114T050505Z-9971fd24`；`committed`；36.2 s；1 added；`verify-remote 12/12` |
| verify（夹具） | 0 | 12/12 |
| rollback dry-run | 0 | 不写设备 |
| rollback -Apply | 0 | 7.2 s；设备 journal `rolled-back` |
| verify（回滚后） | 0 | 11/11；ADD 已消失 |

> 耗时为端到端主机计时（含 PowerShell 启动与 adb 往返）：生产 apply 30.2 s、夹具 apply 36.2 s、rollback 7.2 s。

追加事实：

- 设备端生产计划 11/11 与 plan SHA-256 一致；夹具 ADD 设备端哈希 `bf258fab…24ff` 与源一致。
- 回滚后标记文件保留在 `.mods-deploy/<夹具 txid>/rollback/...`，没有删除。
- 两个事务目录均保留；工具不自动清理。
- Saves 在部署前、apply 后、回滚后、启动后均 0 文件，目录 mtime 未变。
- 既有 17 个 Mods 文件在部署前后逐项哈希一致；`.staging` 未被改动。

## 6. 启动、Loader 与探针

- 启动：`am start -W -n com.chucklefish.stardewvalley/.MainActivity` → `Status: ok`，`WaitTime: 1291`。
- 前台：`mResumedActivity: com.chucklefish.stardewvalley/.MainActivity`；pid 15512。
- 标题界面截图：2400×1080 PNG，398,992 B，sha256 `c4994a6c…7f38`；NEW/LOAD，无更新/隐私/实名弹窗。
- 旧 Loader 报告先固定（1273 B / `0e8cd074…` / 09:20:16）；本轮启动后变 1368 B / 13:06:58，证明采集的是新结果。
- Loader：`mods-discovered=1`、`mods-initialized=1`、`mods-failed=0`、`mods-skipped=0`、`result=ok`、
  preflight 2 assemblies / converged / snapshot 2 files。
- 探针：`cases=14 observed=12 pass=12 fail=0 blocked=0 not-run=2`；R-03/R-12 按设计 NOT RUN，
  由同次 Loader `preflight-stage=complete` / `.staging` 路径 / `initialized` 佐证；合并 **14 PASS / 0 FAIL**。
- pid 过滤 logcat 4 行，无 FATAL / SIGSEGV / UnsatisfiedLinkError / dlopen failed。
- 本轮没有进入存档、创建存档或做与验收无关的游戏操作。
- 验收结束时游戏仍停在标题界面运行（pid 15512）；本轮没有 force-stop，设备交回时可按需手动关闭。

## 7. 主机回归（本轮真实执行）

| 命令 | 结果 | 日志 |
| --- | --- | --- |
| `powershell -File Tools\tests\test_deploy_mods_safe.ps1` | **50 checks, 50 passed, 0 failed**（新增 2 项） | `.tmp/phase1.2.3/…/logs/30-test_deploy_mods_safe.txt` |
| `python Tools/tests/test_ci_offline.py` | **13/13** | `logs/31-ci-offline.txt` |
| `python Tools/privacy-scan.py` | **RESULT: OK**，退出码 0 | `logs/32-privacy-scan.txt` |
| `python Tools/tests/test_p114_probe.py` | **46/46**，退出码 0 | `logs/33-p114-static.txt` |
| `git diff --check` | 退出码 0 | — |

未重跑 `test_device_precheck.ps1` / `test_deploy_safe.ps1`：本轮没有改它们；历史 10/10、95/95 仍是历史数字，
不冒充本轮结果。没有安装 PSScriptAnalyzer 或其它新依赖。

PowerShell 5.1 宿主注意：测试脚本自身会把 `PSModulePath` 收敛到系统模块目录并按 UTF-8 解码；
本轮真实运行通过。

## 8. 工具与测试绑定

| 文件 | SHA-256（最终字节） |
| --- | --- |
| `Tools/deploy-mods-safe.ps1` | `80e54204f1a439c0d39696a18364366999481b09522f40861904f4e719177ed4`（版本 1.2.3） |
| `Tools/tests/test_deploy_mods_safe.ps1` | `6a48279caf29a1245bea50936a86f836e9371bb70ed2309e345437cc71996fbe`（50 项） |
| 部署源 `mods-source/` 11 文件 | 见真机报告 §3 与 `comparison-production.json` |
| 标题截图 | `c4994a6c04961cebab357aa548070beac35cf35d8de24bb896c8420c3e2f7f38` |

## 9. CI 只读核验

| 项 | 值 |
| --- | --- |
| 本地 HEAD | `62477fbe3ec8ade4888558ad1256679af219dc3b` |
| 历史 Run | `43546062266`，head_sha `62477fb…`，3/3 jobs success，2026-11-14T02:23:47Z |
| Job 结果 | offline checks 14 s；dotnet build + loader host tests 61 s；powershell gates 68 s |
| 与本轮关系 | **不包含** Phase 1.2.2/1.2.3 改动，也不包含新增 no-root 套件步骤 |
| 本轮 CI | **NOT_RUN**（未 commit/push；用户保留提交控制权） |

历史 Run 只作为“基线提交曾经绿”的记录，不能写成“本轮 CI 通过”。

## 10. 状态矩阵与剩余风险

| 项 | 状态 |
| --- | --- |
| Phase 1.2.2 主机侧修复 | PASS（历史，48/48） |
| Phase 1.2.2 真机验收 | **PASS（本轮，限实际路径）** |
| 工具 ADD / SKIP / verify / rollback | PASS（真机） |
| 工具 REPLACE | **NOT_RUN** |
| Android 11+ | **NOT_RUN / fail-closed** |
| 其它 OEM / 多设备 | **NOT_RUN** |
| 断电故障注入 | **NOT_RUN**（按用户边界未做） |
| 本轮 CI | **NOT_RUN** |
| Phase 2 | **NOT_STARTED** |

剩余风险：

1. REPLACE 的 staging→backup→switch 路径未在真机执行；下次有真实旧版本时可补。
2. 只有一台 V1963A / API 29；Android 11+ 仍无证据，不能放开。
3. 多文件切换非原子；断电/进程被杀仍靠 journal 恢复。
4. `rollback -Apply` 的 JSON `journal.state` 滞后；设备 journal 权威。
5. 事务目录与 backup/rollback 不会自动清理。
6. 大 Mod 树性能未测。
7. 应用非 debuggable；部署后文件可读性由 Loader 报告间接证明，未用第三方文件浏览器直接读取。

## 11. Phase 2 准入结论

**尚未满足“全部完成”的准入条件，但部署工具真机门槛已跨过。**

已具备：

- 无 Root 部署工具在真实 USB 设备上跑通 inspect/plan/dry-run/apply/verify/ADD/rollback；
- Loader 能加载部署后的 Mod，P114 探针 12/12；
- Saves 未被触碰；
- 主机 50 项 fake-adb 回归与离线检查通过。

仍缺：

1. 用户 commit/push 后的**真实 GitHub Actions** 结果（尤其 50 项套件的耗时与 Windows PowerShell 7 环境）；
2. REPLACE 真机覆盖；
3. Android 11+ 与其它设备的可行性证据（否则继续 fail-closed）；
4. Phase 2 自身的可行性研究（最小 Mod API / 事件模型 / Harmony-detour 风险与回滚方案）。

因此：**可以进入 Phase 2 的可行性研究，但不能把“Phase 2 已实现”或“全面兼容”写成事实。**

## 12. 复现与证据

```powershell
cd D:\Download\DSH\StardewValley

# 最终字节哈希
(Get-FileHash Tools\deploy-mods-safe.ps1 -Algorithm SHA256).Hash.ToLower()
(Get-FileHash Tools\tests\test_deploy_mods_safe.ps1 -Algorithm SHA256).Hash.ToLower()

# 主机回归
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_mods_safe.ps1
python Tools\tests\test_ci_offline.py
python Tools\privacy-scan.py
python Tools\tests\test_p114_probe.py
git diff --check
```

证据目录：`.tmp/phase1.2.3/20261114-125621/`（gitignore，不入库）。
备份：`%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-125621\`（项目外）。

本阶段没有修改 `.git`，没有 commit / push / GitHub 写操作；Phase 2 未启动。
