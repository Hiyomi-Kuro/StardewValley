# Phase 1.2.4：无 Root Mods 部署工具 · REPLACE 真机验收记录

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-14（+08:00） |
| 分支 / 基线 | `main` / `4221283ee73e7a1bc26408fc7709431a9c004d40`（用户已 push） |
| 本地工作区 | 本阶段对 `Tools/` 的修改**未提交**；本阶段无 commit / push / GitHub 写操作 |
| 设备 | vivo V1963A / PD1963，USB serial `6d7200a`，Android 10 / API 29 / arm64-v8a，非 debuggable、无 Root |
| 主机 adb | `C:\Users\<user>\AppData\Local\Android\Sdk\platform-tools\adb.exe`，1.0.41 / 37.0.1-15733141 |
| 工具 | `Tools/deploy-mods-safe.ps1` **1.2.4**，SHA-256 `8d0863f806a5e9ae1542997dc5aa63e584945946194aa583e783421bd602b1dd` |
| 授权 | 用户已授权：USB 真机 ADB、文件推送、游戏停止/启动、APK 安装/卸载重装、`pm clear`、清除本游戏测试存档；本轮只用到**停止/启动游戏**与无 Root 文件推送/回滚 |
| 关联 | [阶段报告](phase1.2.4-report.md) · [证据索引](phase1.2.4-evidence-index.md) · [部署设计](../architecture/phase1.2.1-deployment-design.md) |

---

## 0. 结论

**REPLACE 真机路径首次被完整执行并回滚；回滚后的 stdout JSON、`-JsonOut` 与设备 journal 三者一致。**

| 维度 | 判定 | 依据 |
| --- | --- | --- |
| 设备识别 | PASS | `6d7200a` / V1963A / API 29 / arm64-v8a；APK SHA-256 `97c38ed7…5d90` |
| 部署前备份 | PASS | Mods 17 文件 / 203,328 B；Saves 0；`.mods-deploy` 3 文件 / 6,496 B；逐文件双端 SHA-256 一致 |
| ADD（v1） | PASS | tx `20261114T063751Z-c8bba640`；3 ADD + 1 SKIP；journal `committed` |
| 未批准 REPLACE | PASS | `plan` exit 3 `target-conflict`；设备 Mods 快照逐文件不变 |
| 批准 REPLACE（v1→v2） | **PASS** | tx `20261114T063834Z-aed22504`；`0 added, 1 replaced`；target=v2、backup=v1、journal `committed` |
| verify（v2） | PASS | 4/4 匹配 |
| rollback dry-run | PASS | 4 条 expected；report `state=committed`；设备无写入 |
| rollback apply | **PASS** | exit 0；stdout/JSONout/设备 journal 均为 `rolled-back`；target 恢复 v1 |
| 再次 rollback（幂等） | PASS | exit 0，`rolled-back`，设备树不变 |
| 初始 ADD 回滚 | **PASS** | exit 0；Mods 恢复到本轮起点 17/17；生产 verify 11/11 |
| Saves 保护 | PASS | 前后 0 文件；目录 mtime 不变 |
| Loader | PASS | `mods-discovered=1 / initialized=1 / failed=0 / result=ok` |
| P114 探针 | PASS | `cases=14 observed=12 pass=12 fail=0 not-run=2` |

**结论边界**：只覆盖这一台 V1963A / Android 10 / API 29 / arm64-v8a；不覆盖 Android 11+、其它 OEM、
断电故障注入、大 Mod 树性能。

---

## 1. 设备识别（只读）

`adb devices -l` 列出物理机 `6d7200a`（V1963A）与两个 MuMu transport（`127.0.0.1:16384`、`emulator-5554`，
均为 V2309A）。**每一条 adb 命令都显式 `-s 6d7200a`**，没有连接、唤醒或写入 MuMu。

| 项 | 实际值 |
| --- | --- |
| serial | `6d7200a`（USB 物理机） |
| 型号 / 设备 | V1963A / PD1963；`ro.hardware=qcom`、`ro.kernel.qemu` 为空、`ro.debuggable=0` |
| Android / API | 10 / 29 |
| ABI | arm64-v8a |
| 包 | `com.chucklefish.stardewvalley` |
| versionCode / versionName | 239 / 1.6.15.0 |
| 设备端 APK SHA-256 | `97c38ed7eb6ac719d7a5ee941abac3766307ad03205569bed19db5a48e7d5d90`（389,561,500 B） |
| 开始前游戏进程 | 运行中（pid 15512，Phase 1.2.3 遗留），**force-stop 后**开始；未卸载/重装 APK |

APK 哈希与 `final/StardewValley-1.6.15.0-modded-test-debug.apk` 相同；本轮没有安装、卸载或替换 APK。

---

## 2. 备份（项目外，写入前）

| 项 | 值 |
| --- | --- |
| 位置 | `%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-141147\` |
| Mods | 17 文件 / 203,328 B；设备 `find … -exec sha256sum` 与本机对照 **17/17 一致** |
| Saves | 目录存在、**0 文件**（只验证了空目录基线） |
| `.mods-deploy` | 存在，3 文件 / 6,496 B（Phase 1.2.3 的 2 个事务）；逐文件一致 |
| 目标目录 | 创建前不存在，未覆盖任何旧备份 |

备份脚本：`.tmp/phase1.2.4/backup-device.ps1`（会话临时，gitignore）。
备份 manifest：`.tmp/phase1.2.4/20261114-142000/backup-*.sha256`、`compare-*.txt`。

---

## 3. 验收夹具与部署源

夹具是**纯数据、非可执行 PE**，命名明确，仅用于覆盖 staging→backup→switch→rollback 路径：

```text
fixtures/phase124-v1/
├── ModFramework.Runtime.dll                      (设备同字节，8c444bbc…ffe)
└── Phase124.ReplaceFixture/
    ├── manifest.json                             (id=Phase124.ReplaceFixture, entryAssembly=Fixture.Data.dll)
    ├── Fixture.Data.dll                          ("fixture-v1 (data only, not a PE)")
    └── data.txt                                  (不变)
fixtures/phase124-v2/                             仅 Fixture.Data.dll 变为 "fixture-v2 …"
```

| 文件 | SHA-256 |
| --- | --- |
| `phase124-v1/Phase124.ReplaceFixture/Fixture.Data.dll` | `fb35235d2cff83aef20944c68248a4120b5e2f8f3944bdcc64b09b7dd689eb4c` |
| `phase124-v2/Phase124.ReplaceFixture/Fixture.Data.dll` | `f743324b060ed0b402d9a8dc27a9ccc1307a9d09beeb0fe8b3d025077c5654d7` |
| `Phase124.ReplaceFixture/manifest.json`（两版相同） | `ed4cdd5f0e1ed80f9c84861de9981295bcdaa94da99c122f08061acc485a89a3` |
| `Phase124.ReplaceFixture/data.txt`（两版相同） | `c06ff77e82f63db5bb89cb2729f98f9e12d0d5c91dc18d158e2412bfedff838b` |
| `ModFramework.Runtime.dll` | `8c444bbc598458377efca7c85613158f2bf637e7b72b5899f352415d56af5ffe` |

没有修改任何生产 DLL 来制造冲突；REPLACE 冲突来自同一路径的 v1→v2 数据夹具。

---

## 4. 执行链（真实退出码与耗时）

所有命令在 `D:\Download\DSH\StardewValley` 下执行，`-Adb` 显式传入 SDK adb，`-Serial 6d7200a` 显式指定，
`-JsonOut` / `-ReportOut` 保存主机证据。完整 JSON/日志在 `.tmp/phase1.2.4/20261114-142000/`。

| 步骤 | 退出码 | 耗时 | 关键值 |
| --- | ---: | ---: | --- |
| `10-inspect` | 0 | 5.0 s | `deviceTouched=false`；API 29；V1963A；`transactions` 6 条（均非 active） |
| `20-deploy-v1-add` | 0 | 28.1 s | tx `20261114T063751Z-c8bba640`；3 ADD + 1 SKIP；journal `committed` |
| `30-plan-v2-unapproved` | **3** | 6.2 s | `target-conflict`；conflicts 恰好 1 条（`Phase124.ReplaceFixture/Fixture.Data.dll`）；设备 Mods 快照不变 |
| `31-plan-v2-approved` | 0 | 6.5 s | 4 文件：1 REPLACE + 3 SKIP；无多余动作 |
| `40-deploy-v2-replace` | 0 | 31.1 s | tx `20261114T063834Z-aed22504`；`0 added, 1 replaced`；journal `committed` |
| `50-verify-v2` | 0 | 3.3 s | 4/4 matched |
| `60-rollback-dry-run` | 0 | 2.1 s | report `journal.state=committed`（回滚前状态）；设备快照不变 |
| `70-rollback-apply` | 0 | 11.7 s | stdout `rolled-back`、`-JsonOut` `rolled-back`、设备 journal `rolled-back`；target 恢复 v1 |
| `80-rollback-again` | 0 | 1.4 s | 幂等；仍 `rolled-back`；设备快照不变 |
| `90-rollback-add` | 0 | 15.6 s | 初始 ADD 回滚；Mods 恢复 17/17（与运行起点逐文件一致） |
| `91-verify-production` | 0 | 2.0 s | 生产源 11/11 matched |

> 耗时为端到端主机计时（含 PowerShell 启动与 adb 往返），不是纯 staging 时间。

### 4.1 为什么 v1 计划是 3 ADD + 1 SKIP

`ModFramework.Runtime.dll` 设备上已存在且字节相同 → SKIP；夹具的 3 个文件（manifest、DLL、data.txt）不存在 → ADD。
因此计划里只有 1 个待写目录与 3 个 ADD。

---

## 5. REPLACE 实物证据

| 检查 | 值 |
| --- | --- |
| 计划动作 | 只有 `Phase124.ReplaceFixture/Fixture.Data.dll` 是 REPLACE |
| 未批准 | exit 3，`target-conflict`，设备树逐文件哈希不变 |
| 批准后 | exit 0，`0 added, 1 replaced; old versions were kept` |
| target（设备） | `fixture-v2 (data only, not a PE)` |
| backup（设备） | `.mods-deploy/<tx2>/backup/Phase124.ReplaceFixture/Fixture.Data.dll` = `fixture-v1 (data only, not a PE)` |
| journal | `<tx2>/journal.json` `state=committed`，`expected` 4 条，REPLACE 条目 `existingSha256=fb3523…` |
| verify | 4/4 |

REPLACE 的 `staging → mv target→backup → mv staging→target → 哈希复核` 全部真实执行；
旧字节保留在 backup，未删除。

---

## 6. 回滚报告一致性（本阶段修复的核心）

| 输出 | 值 |
| --- | --- |
| stdout JSON `journal.state` | `rolled-back` |
| `-JsonOut` 文件 `journal.state` | `rolled-back` |
| 设备 `<tx2>/journal.json` `state` | `rolled-back` |
| 退出码 | 0 |
| `detail` | `rollback completed; transaction-owned bytes were retained` |
| target 内容 | `fixture-v1 (data only, not a PE)` |
| 第二次 `rollback -Apply` | exit 0，仍 `rolled-back`，设备快照不变 |

dry-run 仍然报告**回滚前**状态（`committed`），因为它没有写设备；这与“apply 后报告持久化结果”不矛盾。

初始 ADD 回滚后：stdout/device 均为 `rolled-back`，Mods 与本轮起点 17/17 一致，`Phase124.ReplaceFixture`
不在文件清单中（空目录在最终启动前用 `rmdir` 清理，见 §9）。

---

## 7. Saves 对照

| 时点 | 文件数 | 目录 mtime |
| --- | ---: | --- |
| 部署前（备份时） | 0 | 2026-11-14 09:15:06.607092550 +0800 |
| ADD / REPLACE / rollback 后 | 0 | 未变 |
| 最终启动后 | 0 | 未变 |

本轮没有进入或创建存档、没有 `pm clear`、没有修改 Saves。
**只验证了“空 Saves 基线不变”，不是“非空存档不受影响”的证明。**

---

## 8. 事务实物

本轮新增 2 个事务（此前 Phase 1.2.3 的 2 个、本阶段前 3 次尝试的 6 个均保留）：

| txid | 用途 | journal 最终状态 | 设备目录 |
| --- | --- | --- | --- |
| `20261114T063751Z-c8bba640` | 夹具 ADD（v1） | `rolled-back` | `rollback/Phase124.ReplaceFixture/*` 保留 v1 字节；staging/backup 保留 |
| `20261114T063834Z-aed22504` | v1→v2 REPLACE + 回滚 | `rolled-back` | `backup/…/Fixture.Data.dll` = v1；`rollback/…/Fixture.Data.dll` = v2；均保留 |

工具不自动清理事务目录；未手工修改 journal、未删除任何事务证据。
设备 `.mods-deploy` 当前共 10 个事务目录（全部 `committed` 或 `rolled-back`，无 active）。

---

## 9. 启动、Loader 与探针（最终状态）

| 项 | 值 |
| --- | --- |
| 启动 | `am start -W -n com.chucklefish.stardewvalley/.MainActivity` → `Status: ok`，`WaitTime: 882` |
| 前台 | `mResumedActivity: com.chucklefish.stardewvalley/.MainActivity` |
| 进程 | pid 25626 |
| 旧报告 | 1368 B / `b75b883d…25e4` / mtime 14:28:24 |
| 新报告 | 1368 B / `21e1fad8a8811e2c042d502b445a1314ad2e4ae6a882a7c7137bdde7f811f1ba` / mtime **14:39:53** |
| 截图 | `device/72-title-screen.png`，2400×1080 PNG，397,158 B，sha256 `af12b55899685d99257bf84eff71b2c755b71f50f89e7e7acc69e428b3ac5035`；NEW/LOAD，无更新/隐私/实名弹窗 |
| logcat | 4 行，无 `FATAL EXCEPTION` / `SIGSEGV` / `UnsatisfiedLinkError` / `dlopen failed` |

最终 `loader-report.txt` 关键行：

```text
loader-report 2026-11-14T14:39:53.7253160+08:00
mods-discovered=1
mods-initialized=1
mods-failed=0
mods-skipped=0
preflight=assemblies=2 conflicts=0 shared=0 host-shared=0 host-unverified=0 rejected=0
assembly-snapshot-files=2 bytes=26624
assembly-load=P114.RuntimeProbe file=P114.RuntimeProbe.dll snapshot=sha256:ed0671a784…
initialized=P114.RuntimeProbe
result=ok
warnings:
  - ignored (hidden or reserved name): .staging
  - ignored (no manifest.json): logs
```

探针：`summary: cases=14 observed=12 pass=12 fail=0 blocked=0 not-run=2`
（R-03/R-12 由同次 Loader 报告佐证，合并 14 PASS）。

**夹具未引入额外失败**：最终启动前夹具的 3 个文件已随初始 ADD 回滚移出 Mods，空目录已 `rmdir`；
报告里不再出现 `Phase124.ReplaceFixture` 警告。验收结束时游戏停在标题界面（pid 25626），未进入存档。

---

## 10. 覆盖不足与风险

1. **单设备、单 API**：V1963A / Android 10 / API 29；Android 11+ 仍 fail-closed。
2. **Saves 只验证空目录**：没有非空存档的对照。
3. **断电/进程被杀**：本轮未做故障注入；回滚幂等与 journal 状态已覆盖，但不等于断电恢复已证明。
4. **应用可读性**：非 debuggable、无 `run-as`；部署后的字节由 verify/Loader 间接证明，未用第三方文件浏览器复核。
5. **事务不自动清理**：10 个事务目录保留，属于测试资产；生产使用需要清理策略。
6. **夹具是数据 DLL**：只覆盖工具路径，不覆盖“可加载 mod 的 REPLACE”；生产 mod 的 REPLACE 仍是同一代码路径但未单独执行。
7. **耗时**：单次 apply 28–31 s（端到端），大 Mod 树未测。

---

## 11. 证据路径

```text
D:\Download\DSH\StardewValley\.tmp\phase1.2.4\20261114-142000\
├── json\        10-inspect … 91-verify-production（stdout + JsonOut）
├── logs\        每步 -ReportOut 文本 + launch-final2.txt
├── device\      设备哈希清单、journal 副本、meta/IL dump、loader-report、截图、logcat
├── fixtures\    phase124-v1 / phase124-v2
├── attempt1\ … attempt3\   前三次尝试（harness 断言口径问题，非工具失败）
├── device-replace.log / device-replace-summary.json
└── fixtures.sha256.json
```

备份：`%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-141147\`（项目外）。

---

**最终判定：REPLACE + 回滚报告一致性 + Saves 保护，在本设备与本轮执行路径上 PASS；
Android 11+、非空 Saves、断电恢复为 NOT_RUN。**
