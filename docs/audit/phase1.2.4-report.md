# Phase 1.2.4：报告与测试可信度修复、REPLACE 真机验收、Phase 2 技术方案

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-14（+08:00） |
| 分支 / 基线 | `main` / `4221283ee73e7a1bc26408fc7709431a9c004d40`（用户已 commit/push，`origin/main` 同值，开工核对时工作区干净） |
| 本阶段工作区 | `Tools/`、`Tools/tests/`、设计文档、审计文档有**未提交**修改；AGENTS.md 为本地文件 |
| 本阶段写操作 | 无 commit、无 push、无 GitHub 写操作（用户未授权） |
| 真机 | USB `6d7200a` = vivo V1963A / PD1963 / Android 10 / API 29 / arm64-v8a |
| 关联 | [REPLACE 真机验收](phase1.2.4-device-acceptance-report.md) · [证据索引](phase1.2.4-evidence-index.md) · [Phase 2 可行性](../architecture/phase2-feasibility.md) · [Phase 1.2.3 报告](phase1.2.3-report.md) |

---

## 0. 结论

**两类已定位问题都按真实根因修复，并用真机 REPLACE 与本地/CI 双重证据验证。**

| 维度 | 判定 | 依据 |
| --- | --- | --- |
| `4221283` 的远端 CI | **SUCCESS** | Run `43563074341`，head_sha 与本地一致，3/3 jobs success；`powershell-gates` 19m31s（45m 上限） |
| 部署工具回滚报告 | **FIXED** | stdout/`-JsonOut`/设备 journal 均为 `rolled-back`；失败分支不再冒充成功 |
| 自动回滚报告偏差 | **FIXED** | 8 个自动回滚分支统一走 `Invoke-AutomaticRollback` + 持久化状态同步 |
| 测试敏感文件断言 | **FIXED** | 每个夹具设备记录初始化哈希，结束后逐文件比较；不再“自己跟自己比” |
| 负向控制 | **PASS** | 独立夹具中改动 Saves sentinel，检查器按预期报 `changed:`，主套件仍通过 |
| REPLACE 真机 | **PASS（首次）** | v1 ADD → v2 REPLACE（未批准拒绝）→ verify → rollback → v1 → 幂等 → ADD 回滚 |
| Saves | **PASS（空目录基线）** | 部署前/后/启动后 0 文件，mtime 未变 |
| Loader / P114 | **PASS** | `mods-discovered=1 / initialized=1 / failed=0 / result=ok`；探针 12/12 |
| 本阶段新代码的远端 CI | **NOT_RUN** | 修改未提交；不能拿 `4221283` 的 Run 冒充 |
| Phase 2 | **可行性研究完成，未实现** | 推荐路线 B；`ModHooks` 在本版本不存在 |

**三件必须说清楚的事：**

1. **`4221283` 的 CI 是真实的、已完成的 success**，但它只覆盖该提交，**不覆盖本阶段未提交的修改**。
2. **REPLACE 是本阶段第一次在真机上真正执行**（前两个阶段生产集全 SKIP）；它使用纯数据夹具，
   没有为了制造冲突修改生产 DLL。
3. **Phase 2 仍未实现**；本阶段只给出路线、最小范围与验收标准，且 `Harmony` 继续判为不可行。

---

## 1. 基线核对与 `4221283` 的真实 CI

### 1.1 开工核对

```text
branch            : main
HEAD              : 4221283ee73e7a1bc26408fc7709431a9c004d40
origin/main       : 4221283ee73e7a1bc26408fc7709431a9c004d40
tracked changes   : (none)
untracked         : (none)
```

- 用户已手动 commit/push；本地与远端一致，工作区干净。
- 全程没有 `git reset`、`git clean`、`git commit`、`git push`；没有 GitHub 写操作。
- AGENTS.md 的“62477fb / 未提交 / CI NOT_RUN”是 Phase 1.2.3 的历史状态，已在本阶段“当前状态”中更新。

### 1.2 Actions Run `43563074341`

| 项 | 值 |
| --- | --- |
| 结果 | `completed` / **`success`** |
| head_sha | `4221283ee73e7a1bc26408fc7709431a9c004d40` |
| run 编号 / 触发 | #14 / push（`main`） |
| 开始 / 结束（UTC） | 2026-11-14T05:45:42Z / 06:05:16Z |
| 证据 | `.tmp/phase1.2.4/ci-run.json`、`ci-jobs.json`（REST API 原样保存） |

| Job | 结果 | 耗时 |
| --- | --- | ---: |
| offline checks | success | 18 s |
| dotnet build + loader host tests | success | 73 s |
| powershell gates | success | **19 m 31 s** |

`powershell-gates` 三个步骤：

| 步骤 | 结果 | 开始 → 结束（UTC） | 耗时 |
| --- | --- | --- | ---: |
| `adb resolution self-test` | success | 05:45:59 → 05:46:05 | 6 s |
| `deploy gate fake-adb suite` | success | 05:46:05 → 05:46:47 | 42 s |
| `no-root mods deploy fake-adb suite` | success | 05:46:47 → 06:05:12 | **18 m 25 s** |

**结论与余量**：

- 新增的 no-root 套件在 **PowerShell 7（windows-latest `pwsh`）** 上真实通过；
- 该步骤是本 job 的主要耗时（1105 s）；job 总耗时 1171 s，距 `timeout-minutes: 45`（2700 s）约有
  **25 分 29 秒**余量，但余量主要被这一套件吃掉；
- 本机单次 PS 5.1 运行时间明显更长（见 §4），说明套件耗时对宿主/负载敏感，不能把“CI 18 分钟”当作固定值。

**日志可得性**：公开仓库的 Actions 日志下载需要认证，本机未配置 token，
`GET /actions/runs/<id>/logs` 返回 **403**；因此本报告不使用远端日志中的测试计数。
测试计数以本机真实运行（§4）为准，不与 CI 数字混用。

**CI 不能证明的事**：`4221283` 的 success 早于本阶段所有修改。本阶段新代码的远端 CI 状态是
**NOT_RUN**，见 §7。

---

## 2. 修复 A：回滚后的 JSON 状态以设备持久化状态为准

### 2.1 根因

不是“JSON 序列化晚了”，而是**报告值在回滚前就被复制**：

```text
Invoke-RollbackCommand
  ├─ $journal = 读取设备 journal              (state=committed)
  ├─ $Result.journal.state = committed        ← 先写进报告
  ├─ Invoke-Rollback $journal
  │    ├─ 写 rolling-back
  │    ├─ 恢复文件
  │    └─ 写 rolled-back
  └─ Exit-Tool 0                              ← 直接输出，报告仍是 committed
```

`Invoke-Deploy` 的 8 个自动回滚分支同样在 `applying` 时把状态写进报告，之后不再更新。

### 2.2 修复

- `Invoke-Rollback` 返回 `JournalState`：本进程**实际证明已持久化**的最后一个状态；
  - 连 `rolling-back` 都写不进 → `JournalState=""`（没有新证据）；
  - 最终写失败 → `JournalState="rolling-back"`（文件可能已恢复，但事务未完成）；
  - 成功 → `rolled-back`。
- 新增 `Sync-JournalResultState`：**优先重新读取设备 journal**，用它的 `state` 覆盖报告；
  读取失败才回退到 `JournalState`；两者都没有时保持原有报告值（不冒充成功）。
- 新增 `Invoke-AutomaticRollback`：8 个自动回滚分支统一走它，先回滚再同步状态。
- staging 失败分支（`staging-failed` / `staging-verified`）写 journal 后也同步报告，避免同类“报告旧值”。
- 退出码、`category`、`reason`、`detail` 语义不变；设备 journal 仍是唯一权威。

### 2.3 行为矩阵（真机 + fake adb 双覆盖）

| 场景 | 退出码 | JSON `journal.state` | 设备 journal | 说明 |
| --- | ---: | --- | --- | --- |
| dry-run | 0 | 回滚前状态（`committed`） | 未变 | 没写设备 |
| apply 成功 | 0 | `rolled-back` | `rolled-back` | 三者一致 |
| 已回滚再 apply | 0 | `rolled-back` | `rolled-back` | 幂等，无写入 |
| 最终 journal 写失败 | 5 | `rolling-back` | `rolling-back` | 文件已恢复但状态未完成 |
| `rolling-back` 写失败 | 5 | 回滚前状态（`committed`） | `committed` | 没有新持久化证据 |
| 归属无法证明 | 5 | `rolling-back` | `rolling-back` | 拒绝继续，保留现场 |
| 自动回滚（deploy 失败） | 4/5 | 设备实际状态 | 同左 | 不再固定 `applying` |

### 2.4 回归

fake-adb 新增/加强：

- `approved REPLACE commits`、`rollback -Apply restores the pre-state`；
- `rollback -JsonOut agrees with stdout and the device journal`；
- `failed rolling-back write still reports the committed state`；
- `failed final journal write does not claim rolled-back`；
- `refused rollback reports the persisted journal state, not a rolled-back claim`；
- 旧 `rollback refuses bytes not owned by transaction` 保留。

真机证据见 §5。

---

## 3. 修复 B：测试自身的无效断言

### 3.1 根因

`test_deploy_mods_safe.ps1` 末尾的敏感文件检查：

```powershell
if ((Get-FileHash $saves …).Hash -ne (Get-FileHash $saves …).Hash) { $unchanged = $false }  # 自己跟自己比
if (-not (Test-Path $key) -or -not (Test-Path $apk)) { $unchanged = $false }                # 只看存在
```

- Saves 哈希比较恒为 false（永远“未变”）；
- keystore / APK 只检查存在，内容被改也发现不了；
- 夹具目录用名字前缀过滤，**只覆盖了一部分**设备目录，其余设备从未被检查。

### 3.2 修复

- `New-Device` 在每个虚拟设备创建后**立即**记录 3 个 sentinel 的 SHA-256 到 `$script:sentinels`；
- 运行结束对 `$script:devices`（本 run 创建的全部设备，不再靠名字前缀）逐设备比较；
- 新增 `Test-SentinelSnapshot`，返回 `changed:` / `missing:` 明细，不再有“自己跟自己比”的写法；
- 详情里报告 `devices=N`，能看出实际检查了多少个夹具。

### 3.3 负向控制

在**独立**虚拟设备 `sentinel-negative-control` 中：

1. 创建后先自检 → 必须 `Ok=true`；
2. 改写 `Saves/save1.dat`；
3. 再检查 → 必须 `Ok=false` 且 `bad` 恰为
   `changed:sdcard/Android/data/com.chucklefish.stardewvalley/files/Saves/save1.dat`。

该设备在主断言**之后**创建，因此不会把主套件变成“故意失败”；负向控制本身以 PASS 记录。

---

## 4. 主机回归（本阶段真实执行）

| 命令 | 结果 | 日志 |
| --- | --- | --- |
| `powershell -File Tools\tests\test_deploy_mods_safe.ps1`（5.1） | **76/76，0 failed / 0 skipped，退出码 0**；14:39:44→15:14:53（35m09s） | `.tmp/phase1.2.4/suite-final-ps51.log`、`logs/93-suite-ps51.txt` |
| `pwsh -File Tools\tests\test_deploy_mods_safe.ps1`（7.4.6 便携版） | **76/76，0 failed / 0 skipped，退出码 0**；15:15:42→16:06:01（50m19s） | `.tmp/phase1.2.4/suite-final-ps7.log`、`logs/94-suite-ps7.txt` |
| `python Tools/tests/test_ci_offline.py` | **13/13，退出码 0** | `logs/90-ci-offline.txt` |
| `python Tools/privacy-scan.py` | **RESULT: OK，退出码 0** | `logs/91-privacy.txt` |
| `python Tools/tests/test_p114_probe.py` | **46/46，退出码 0** | `logs/92-p114.txt` |
| `git diff --check` | 退出码 0（仅 LF→CRLF 提示） | — |

- 本机没有预装 PowerShell 7；本轮使用 GitHub Releases 的便携版
  `PowerShell-7.4.6-win-x64.zip` 解压到 `.tmp/phase1.2.4/pwsh-7.4.6`（不写系统 PATH、不装 MSI）。
- 5.1 与 7 的测试数都应以真实输出为准，**不预设仍为 50**。
- 未重跑 `test_device_precheck.ps1` / `test_deploy_safe.ps1`：本阶段没有改它们；历史 10/10、95/95 仍是历史数字。

---

## 5. REPLACE 真机验收（摘要）

完整记录见 [REPLACE 真机验收](phase1.2.4-device-acceptance-report.md)。

| 步骤 | 退出码 | 关键结果 |
| --- | ---: | --- |
| inspect | 0 | 只读；API 29；V1963A |
| deploy v1（ADD） | 0 | tx `20261114T063751Z-c8bba640`；3 ADD + 1 SKIP；`committed` |
| plan v2（未批准） | 3 | `target-conflict`；设备树不变 |
| plan v2（`-Overwrite` 精确路径） | 0 | 1 REPLACE + 3 SKIP |
| deploy v2（REPLACE） | 0 | tx `20261114T063834Z-aed22504`；`0 added, 1 replaced`；target=v2、backup=v1；`committed` |
| verify v2 | 0 | 4/4 |
| rollback dry-run | 0 | 报告 `committed`；无写入 |
| rollback apply | 0 | stdout/JsonOut/设备 journal 均 `rolled-back`；target=v1 |
| rollback again | 0 | 幂等；`rolled-back`；无写入 |
| rollback 初始 ADD | 0 | Mods 恢复 17/17；生产 verify 11/11 |

- 备份（项目外）：`%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-141147\`；
  Mods 17 文件 / 203,328 B，逐文件双端一致；Saves 0；`.mods-deploy` 3 文件 / 6,496 B。
- 夹具：`Phase124.ReplaceFixture`，纯数据、非 PE；v1/v2 只差 `Fixture.Data.dll`。
- 没有修改生产 DLL、没有删除未知文件、没有 `pm clear`、没有卸载/重装 APK、没有 Root。
- 最终启动：Loader `mods-discovered=1 / initialized=1 / failed=0 / result=ok`；
  探针 `observed=12 pass=12 fail=0`；标题界面截图已取得；未进入存档。

---

## 6. 状态矩阵与边界

| 项 | 状态 |
| --- | --- |
| `4221283` 远端 CI | **SUCCESS（3/3）** |
| 本阶段本地修改的远端 CI | **NOT_RUN**（未提交） |
| 回滚报告一致性（fake adb） | PASS |
| 回滚报告一致性（真机） | PASS |
| 敏感文件断言 + 负向控制 | PASS |
| REPLACE 真机 | PASS（首次） |
| ADD / SKIP / verify / rollback 真机 | PASS |
| Saves（空目录） | PASS |
| Android 11+ | NOT_RUN / fail-closed |
| 非空 Saves | NOT_RUN |
| 断电/进程被杀故障注入 | NOT_RUN |
| 其它 OEM / 多设备 | NOT_RUN |
| 大 Mod 树性能 | NOT_RUN |
| Phase 2 实现 | NOT_STARTED |

**结论边界**：只覆盖这一台 V1963A / Android 10 / API 29 / arm64-v8a 与本阶段实际执行的路径。
Android 11+、其它 OEM、非空存档、断电恢复尚未研究，这不阻止本阶段的有界任务完成，但也不得外推。

---

## 7. Phase 2 技术方案（摘要）

完整研究见 [Phase 2 可行性](../architecture/phase2-feasibility.md)。

| 路线 | 判定 | 理由 |
| --- | --- | --- |
| A 游戏已有扩展点 | **只能覆盖局部事件** | `StardewValley.ModHooks` 在 1.6.15.24354 **不存在**；全程序集仅 34 个事件，均为 netcode/UI/本地化/地点等实例事件 |
| B 构建期 Cecil 注入 | **推荐** | 已有可用的注入链与 IL 校验；可把事件钩子放在 `GameRunner` 上 |
| C Harmony/运行时 detour | **不可行（继续）** | `Reflection.Emit` 缺失；原生 detour 的 icache 刷新未解决；本阶段不重开 T11/T12、不引入 NDK |

首批事件（1–2 个，标题界面可验收）：

1. `GameLaunched` ← 注入 `StardewValley.GameRunner.Initialize()` 末尾；
2. `Tick` ← 注入 `StardewValley.GameRunner.Update(GameTime)` 开头。

关键事实：Loader 初始化发生在 `new GameRunner()` **之前**，此时没有任何 `Game` 实例，只能登记静态订阅；
`Game1` 实例要读档/新建存档后才存在，因此 `Game1.Update` / `Game1.OnDayStarted` 不适合作为首批事件。

**本阶段没有实现事件框架，也没有引入 Harmony/NDK 依赖。**

---

## 8. 交付物与版本

| 文件 | 变化 |
| --- | --- |
| `Tools/deploy-mods-safe.ps1` | 1.2.3 → **1.2.4**：回滚/自动回滚状态同步 |
| `Tools/tests/test_deploy_mods_safe.ps1` | 敏感文件基线、负向控制、REPLACE/回滚报告回归、mv 计数故障注入 |
| `docs/architecture/phase1.2.1-deployment-design.md` | §9.6 记录 1.2.4 修复 |
| `docs/architecture/phase2-feasibility.md` | 新增 |
| `docs/audit/phase1.2.4-report.md` | 本文件 |
| `docs/audit/phase1.2.4-evidence-index.md` | 新增 |
| `docs/audit/phase1.2.4-device-acceptance-report.md` | 新增 |
| `AGENTS.md` | 更新“当前状态”（本地文件，不入库） |

最终字节哈希：

| 文件 | SHA-256 |
| --- | --- |
| `Tools/deploy-mods-safe.ps1` | `8d0863f806a5e9ae1542997dc5aa63e584945946194aa583e783421bd602b1dd` |
| `Tools/tests/test_deploy_mods_safe.ps1` | `a05d2630844ae34a1d6869bf8ba157fa697aa34095d7705d491f833706cc39f2` |

---

## 9. 剩余风险

1. **本阶段修改未提交**：没有对应的远端 CI；用户提交后必须重新核验 `powershell-gates` 三步。
2. **套件耗时**：CI 上 no-root 步骤 18m25s（45m 上限）；本机更慢。新增用例后需要观察是否逼近上限。
3. **REPLACE 只覆盖数据夹具**：生产 mod 的 REPLACE 走同一代码路径，但没有单独执行。
4. **Saves 只验证空目录**：非空存档未测。
5. **Android 11+ 仍 fail-closed**：没有新证据，不放开。
6. **事务目录增长**：设备上已有 10 个事务（历史 + 本阶段），工具不自动清理。
7. **Phase 2 的线程/顺序/异常隔离仍是设计**：需要实现后真机验收（E-1…E-12）。

---

## 10. 复现

```powershell
cd D:\Download\DSH\StardewValley

# 工具/测试最终哈希
(Get-FileHash Tools\deploy-mods-safe.ps1 -Algorithm SHA256).Hash.ToLower()
(Get-FileHash Tools\tests\test_deploy_mods_safe.ps1 -Algorithm SHA256).Hash.ToLower()

# 主机回归（5.1 与便携 7.4.6）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_mods_safe.ps1
.\.tmp\phase1.2.4\pwsh-7.4.6\pwsh.exe -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_mods_safe.ps1
python Tools\tests\test_ci_offline.py
python Tools\privacy-scan.py
python Tools\tests\test_p114_probe.py
git diff --check

# 真机 REPLACE 验收（会停止游戏并写 Mods；需要 -Serial）
powershell -NoProfile -ExecutionPolicy Bypass -File .tmp\phase1.2.4\device-replace.ps1
```

证据目录：`.tmp/phase1.2.4/20261114-142000/`（gitignore）。
备份：`%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-141147\`（项目外）。

本阶段没有修改 `.git`，没有 commit / push / GitHub 写操作。
