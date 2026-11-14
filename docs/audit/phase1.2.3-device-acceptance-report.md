# Phase 1.2.3：无 Root Mods 部署工具 · USB 真机验收报告

| 项 | 值 |
| --- | --- |
| 日期 | 2026-11-14（+08:00） |
| 分支 / HEAD | `main` / `62477fbe3ec8ade4888558ad1256679af219dc3b`（`origin/main` 同值） |
| 工作区 | Phase 1.2.2 + 1.2.3 改动**未提交**；本阶段无 commit / push / GitHub 写操作 |
| 设备 | vivo V1963A / PD1963，USB serial `6d72…`，Android 10 / API 29 / arm64-v8a |
| 主机 adb | `C:\Users\<user>\AppData\Local\Android\Sdk\platform-tools\adb.exe`，Android Debug Bridge 1.0.41 / 37.0.1 |
| 工具 | `Tools/deploy-mods-safe.ps1` 1.2.3（本轮修复后） |
| 授权 | 用户明确授权 DSH 直接执行真机验收（识别、只读取证、备份、无 Root 部署、验证、游戏停止/启动、Loader 日志）；不含 commit/push、卸载/重装 APK、`pm clear`、清存档、Root、删除未知文件、改系统安全设置 |
| 关联 | [阶段报告](phase1.2.3-report.md) · [证据索引](phase1.2.3-evidence-index.md) |

---

## 0. 结论（先看这一段）

**本轮在一台真实 USB 物理设备上跑通了 `inspect → plan → deploy dry-run → deploy -Apply → verify`，并额外用项目验收夹具覆盖了 ADD 切换与 `rollback`。生产 Mods 集全部 SKIP（设备本来就是同一份字节），因此“生产文件的 REPLACE/切换”没有被本轮覆盖。**

| 维度 | 判定 | 依据 |
| --- | --- | --- |
| 部署工具真机执行 | **PASS（限本次设备与实际覆盖路径）** | 6 条命令真实退出码 0；journal `committed` / `rolled-back`；无 `su`/Root/APK/`pm clear` |
| 生产部署（11 文件） | **PASS（全 SKIP）** | 计划 11/11 与设备 SHA-256 一致；apply 未切换任何文件 |
| ADD 切换 | **PASS** | 验收夹具 1 个文件真实 ADD、staging/切换/最终哈希通过；随后事务内安全回滚 |
| REPLACE 切换 | **NOT_RUN** | 生产集无冲突；本轮没有为制造差异修改 DLL 或强造 REPLACE |
| rollback | **PASS** | 先 dry-run 后 `-Apply`，退出码 0；journal `rolled-back`；夹具文件移入 `rollback/` |
| Saves 保护 | **PASS** | 部署前、部署后、启动后均为 0 文件，目录 mtime 未变 |
| Loader | **PASS** | `mods-discovered=1`、`mods-initialized=1`、`mods-failed=0`、`result=ok` |
| P114 探针 | **PASS** | `observed=12 pass=12 fail=0`；R-03/R-12 按设计 NOT RUN，由同次 Loader 报告佐证 |
| Android 11+ | **NOT_RUN / 保持 fail-closed** | 本轮设备是 API 29；工具对 API 30+ apply 仍拒绝 |
| 本轮改动的 CI | **NOT_RUN** | 改动未 commit/push；见 §9 |

**结论边界：只覆盖这一台 V1963A / Android 10 / API 29 / arm64-v8a。** 不外推所有 ARM64、其它 OEM、Android 11+ 或 SMAPI 兼容。

## 1. 设备识别（只读）

`adb devices -l` 当时有 3 个 transport：物理机 `6d72…`（V1963A）与两个 MuMu transport
（`127.0.0.1:16384`、`emulator-5554`，均为 V2309A）。本轮只对 `6d72…` 执行命令，**每条 adb 调用都显式 `-s 6d72…`**；
没有连接、唤醒或写入 MuMu。

| 项 | 实际值 |
| --- | --- |
| serial | `6d72…`（`ro.boot.serialno` / `ro.serialno`） |
| 型号 / 设备 | V1963A / PD1963；vivo Z6；Qualcomm lito |
| Android / API | 10 / 29（`ro.product.first_api_level=29`） |
| ABI | `arm64-v8a`（abilist `arm64-v8a,armeabi-v7a,armeabi`，`ro.dalvik.vm.native.bridge=0`） |
| 物理机特征 | `ro.kernel.qemu` 未设置、`ro.hardware=qcom`、`ro.boot.verifiedbootstate=green`、`ro.debuggable=0` |
| 包 | `com.chucklefish.stardewvalley` |
| versionCode / versionName | `239` / `1.6.15.0`（targetSdk 34，primaryCpuAbi arm64-v8a） |
| 安装路径 | `/data/app/com.chucklefish.stardewvalley-8Ctsci-ZDtiJ2v3FDOotxg==/base.apk` |
| 设备端 APK SHA-256 | `97c38ed7eb6ac719d7a5ee941abac3766307ad03205569bed19db5a48e7d5d90`（389,561,500 B） |
| firstInstallTime / lastUpdateTime | 2026-11-14 09:14:24 / 同值 |
| 游戏进程（开始前） | 未运行（`pidof` 退出 1、无输出），**本轮没有执行 force-stop** |

APK 哈希与 `final/StardewValley-1.6.15.0-modded-test-debug.apk` 相同，说明设备上是 Phase 1.1.9/1.2 的 mod-enabled 测试包；
本轮没有安装、卸载或替换 APK。

### 1.1 外部 files 目录现状（部署前）

| 路径 | 现状 |
| --- | --- |
| `files/Mods` | 存在；11 个部署文件 + 运行期生成物 |
| `files/Saves` | 存在；**0 个文件**，mtime 2026-11-14 09:15 |
| `files/.mods-deploy` | **不存在**（没有历史事务） |
| `files/Screenshots` | 存在，未触碰 |

`Mods` 内 17 个文件（11 个部署文件 + `loader.log` + `loader-report.txt` + `logs/P114.RuntimeProbe.log` +
`P114.RuntimeProbe/probe-output/phase1.1.4.txt` + `.staging/...` 两个快照文件）。逐文件哈希见 §4。

## 2. 备份（项目外，部署写入前）

| 项 | 值 |
| --- | --- |
| 位置 | `%USERPROFILE%\Documents\StardewValley-TestBackups\V1963A\20261114-125621\` |
| 内容 | `Mods/` 17 文件 / 202,891 B；`Saves/` 0 文件；`meta/` 设备标识与 `backup-manifest.json` |
| 既有事务 | `.mods-deploy` 不存在，无事务记录可备份 |
| 校验 | 设备 `find … -exec sha256sum` 与本机逐文件对照：**17/17 一致** |
| 覆盖策略 | 目标目录必须不存在，未覆盖任何旧备份 |

备份目录只在本机；本阶段没有把备份、存档或设备标识提交到仓库。

## 3. 部署源（本轮未重编译）

| 项 | 值 |
| --- | --- |
| 来源 | `.tmp/phase1.1.9/mods-bundle-fixed`（Phase 1.1.9 `artifacts-p114fix` 构建、Phase 1.2 已在该设备验证的 11 文件组合） |
| 本轮副本 | `.tmp/phase1.2.3/20261114-125621/mods-source` |
| 文件数 / 字节 | 11 / 170,655 B |
| 结构 | 根 `ModFramework.Runtime.dll`；`P114.RuntimeProbe/manifest.json` + entry/dependency + `probe-fixtures/` |
| 与设备关系 | 11/11 SHA-256 与设备现状完全相同（因此计划全 SKIP） |

关键哈希：

```text
ModFramework.Runtime.dll                              8c444bbc598458377efca7c85613158f2bf637e7b72b5899f352415d56af5ffe  118784
P114.RuntimeProbe/manifest.json                       05b3bb87bb700e7fa222983c4823294e0ae8ae023e9928b21e82de4204eabc3a     607
P114.RuntimeProbe/P114.RuntimeProbe.dll               ed0671a784251f1031c3c058220e1aa3afb5d25a4fa834e2cc127508a186fe3c   21504
P114.RuntimeProbe/P114.RuntimeProbe.Dependency.dll    b6c451406906be09ba166b0c5cf9c26c1629b2f7042da1239f01007c6bbca4c9    5120
P114.RuntimeProbe/probe-fixtures/alpha/P114Alpha.dll  f3a8a6f81dd963aefb265bdb27f503a7cd325515e04b02e46518a57aef7ac963    4096
P114.RuntimeProbe/probe-fixtures/beta/P114Beta.dll    a99295c4d6ef14f48664232f2f63830013893620a9ccb621695cafef5a7cbe18    4096
P114.RuntimeProbe/probe-fixtures/corrupt/P114Broken.dll f5a5fd42d16a20302798ef6ed309979b43003d2320d9f0e8ea9831a92759fb4b     64
P114.RuntimeProbe/probe-fixtures/diff-a/P114Diff.dll  b44faa478239e9126e25442a7aa70a1053f5bd488860fab99d71d1edfaec1333    4096
P114.RuntimeProbe/probe-fixtures/diff-b/P114Diff.dll  5db96bea580b147f84a3c911e883118ffa87c4375d2403a02df65b399becf2fa    4096
P114.RuntimeProbe/probe-fixtures/same-a/P114Same.dll  7bedd33772e7f6a99c3b16bce175b3d4e946ffd87a7a44f50c2727ff3787620c    4096
P114.RuntimeProbe/probe-fixtures/same-b/P114Same.dll  7bedd33772e7f6a99c3b16bce175b3d4e946ffd87a7a44f50c2727ff3787620c    4096
```

**AndroidRefsDir 口径**：本轮**没有重新编译任何程序集**，因此没有触发 `-p:AndroidRefsDir` 编译步骤；
复用的是 Phase 1.1.9 已用该参数构建、且 Phase 1.2 在本设备通过 12/12 探针验证的字节。若后续需要重编译，
必须通过 `Tools/find-dotnet.ps1` 找 SDK 并传 `-p:AndroidRefsDir=<65 refs>`，不得写死历史路径。

## 4. 执行链与真实退出码

所有命令都在 `D:\Download\DSH\StardewValley` 下执行，`-Adb` 显式传入本机 SDK adb，`-Serial 6d72…` 显式指定；
`-JsonOut` / `-ReportOut` 保存主机侧证据。完整 JSON/文本在 `.tmp/phase1.2.3/20261114-125621/`。

| 步骤 | 结果 | 关键字段 |
| --- | --- | --- |
| `inspect` | exit **0** | `status=ok`、`deviceTouched=false`、API 29、包 239/1.6.15.0、`running=false`、`transactions=[]` |
| `plan` | exit **0** | 11 文件 / 170,655 B；**11 SKIP**；`conflicts=[]`；`deviceTouched=false` |
| `deploy`（dry-run） | exit **0** | `mode=dry-run`；`deviceTouched=false`；dry-run 后 `.mods-deploy` 仍不存在 |
| `deploy -Apply` | exit **0** | `mode=apply`；`deviceTouched=true`；txid `20261114T050317Z-b29f6e23`；`journal.state=committed`；耗时 **30.2 s**；`0 added, 0 replaced` |
| `verify`（生产源） | exit **0** | `expected=11 matched=11 missing=0 modified=0 extra=6` |
| `plan`（夹具源） | exit **0** | 12 文件；11 SKIP + **1 ADD**（`P114.RuntimeProbe/phase1.2.3-acceptance-marker.txt`，221 B） |
| `deploy -Apply`（夹具） | exit **0** | txid `20261114T050505Z-9971fd24`；`journal.state=committed`；耗时 **36.2 s**；`1 added, 0 replaced`；日志有 `switched : ADD …` 与 `verify-remote : 12/12` |
| `verify`（夹具） | exit **0** | `expected=12 matched=12 missing=0 modified=0` |
| `rollback` dry-run | exit **0** | `deviceTouched=false`；打印 12 条 expected；未写设备 |
| `rollback -Apply` | exit **0** | 耗时 **7.2 s**；设备 journal `state=rolled-back`；`detail=rollback completed; transaction-owned bytes were retained` |
| `verify`（回滚后） | exit **0** | `expected=11 matched=11 missing=0 modified=0 extra=6` |

> 耗时为主机 `Stopwatch` 的**端到端**计时（含 PowerShell 启动、adb 往返与工具内所有检查），不是纯 staging 时间；
> 同一台设备、同一 adb 下三次 apply/rollback 分别为 30.2 s、36.2 s、7.2 s。

### 4.1 生产集为何全 SKIP

设备上已经是同一份 11 文件字节。工具按设计把“目标字节完全相同”判为 `SKIP`，apply 只创建事务目录、
写 journal 并做最终哈希复核，**没有发生任何 Mods 文件切换**。因此本报告不把这次 apply 描述成“已验证文件切换”。

### 4.2 ADD 覆盖与回滚（不制造 REPLACE）

为覆盖真实写入路径，本轮在**完整部署源副本**的既有 Mod 目录内加入 1 个可识别、无副作用的验收标记文件
（`P114.RuntimeProbe/phase1.2.3-acceptance-marker.txt`，221 B，sha256 `bf258fabfe8d7ee056df7f967c7c4335b2e2f4689c6a40cc312113d2f89d24ff`）。
它不在 `manifest.json` 声明中，Loader/P114 探针不会读取它；apply 后设备字节与源一致，verify 12/12；
随后用同一事务的 `rollback -Apply` 把它移入 `.mods-deploy/<txid>/rollback/P114.RuntimeProbe/`，不删除、不覆盖事务外文件。

没有为了制造差异修改 DLL、删除旧文件或强造 REPLACE。

## 5. 设备端 SHA-256 对照

| 对照 | 结果 |
| --- | --- |
| 生产计划 11 文件 vs 设备 17 文件中的同名项 | **11/11 一致** |
| 夹具 apply 后 12 文件 | **12/12 一致**（含 ADD 标记） |
| 回滚后生产 11 文件 | **11/11 一致**；ADD 标记已不存在 |
| 设备多出的 6 个文件 | `.staging/.../P114.RuntimeProbe.dll`、`.staging/.../P114.RuntimeProbe.Dependency.dll`、`loader.log`、`loader-report.txt`、`logs/P114.RuntimeProbe.log`、`P114.RuntimeProbe/probe-output/phase1.1.4.txt`——全部是 Loader/探针运行期生成物，不是本次部署写入 |
| 既有未知文件 | 部署前后 17 文件哈希逐项一致；`.staging` 未被改动 |

## 6. 启动、Loader 与探针

| 项 | 实际值 |
| --- | --- |
| 启动时间 | 2026-11-14 13:06:57（主机）/ 13:06:58（设备） |
| 命令 | `am start -W -n com.chucklefish.stardewvalley/.MainActivity` → `Status: ok`，`WaitTime: 1291` |
| 前台 Activity | `mResumedActivity: com.chucklefish.stardewvalley/.MainActivity` |
| 进程 | pid `15512`，启动后保持 |
| 截图 | `device/70-title-screen.png`，2400×1080 PNG，398,992 B，sha256 `c4994a6c04961cebab357aa548070beac35cf35d8de24bb896c8420c3e2f7f38`；标题界面 NEW/LOAD，无更新/隐私/实名弹窗 |
| 旧报告 | 部署前 `loader-report.txt` 1273 B / `0e8cd074…3bc9`，mtime 09:20:16；本轮启动后变为 1368 B，mtime 13:06:58 |

本次 `loader-report.txt` 关键行：

```text
loader-report 2026-11-14T13:06:58.9174121+08:00
mods-discovered=1
mods-initialized=1
mods-failed=0
mods-skipped=0
preflight=assemblies=2 conflicts=0 shared=0 host-shared=0 host-unverified=0 rejected=0
preflight-stage=complete
preflight-convergence=converged
assembly-snapshot-files=2 bytes=26624
assembly-load=P114.RuntimeProbe file=P114.RuntimeProbe.dll snapshot=sha256:ed0671a784…
initialized=P114.RuntimeProbe
result=ok
```

警告只有两条，均为既有的运行期目录：`ignored (hidden or reserved name): .staging`、`ignored (no manifest.json): logs`；
不是失败。

本次 `probe-output/phase1.1.4.txt`：

```text
summary: cases=14 observed=12 pass=12 fail=0 blocked=0 not-run=2
R-01 PASS … SHA-256 空输入向量匹配
R-02 PASS … SHA-256 标准向量匹配
R-04/R-05/R-06/R-07/R-08/R-09/R-10/R-11/R-13/R-14 PASS
R-03 NOT RUN : loader-observed (preflight-stage=complete / assembly-load=entry / state=Initialized)
R-12 NOT RUN : loader-observed (preflight-stage=complete precedes the first assembly-load, path is .staging)
```

判定口径：探针直接执行 12/12 PASS；R-03、R-12 不是探针失败，而是由同一次启动的 Loader 报告独立佐证。
合并口径 **14 PASS / 0 FAIL**。P114 的 `R-13` 设备端加载哈希 `ed0671a7…` 与部署源一致。

`logcat --pid=15512` 只有 4 行（含 vivo `VPerformance: Fail to get file list` 两条），没有
`FATAL EXCEPTION` / `SIGSEGV` / `UnsatisfiedLinkError` / `dlopen failed`。
**验收结束时游戏仍停在标题界面运行**（pid 15512）；本轮没有 force-stop。需要释放设备时由操作者手动关闭。

## 7. Saves 对照

| 时点 | 文件数 | 目录 mtime |
| --- | --- | ---: |
| 部署前（备份时） | 0 | 2026-11-14 09:15 |
| 生产 apply 后 | 0 | 未变 |
| 夹具 apply + rollback 后 | 0 | 未变 |
| 游戏启动后 | 0 | 未变 |

本轮没有进入存档、创建存档、修改 Saves 或执行 `pm clear`。

## 8. 事务与回滚实物

| txid | 用途 | journal 最终状态 | 设备目录 |
| --- | --- | --- | --- |
| `20261114T050317Z-b29f6e23` | 生产集 apply（全 SKIP） | `committed` | `staging/`、`backup/`、`rollback/` 均为空目录；`operations=[]`、`createdDirectories=[]` |
| `20261114T050505Z-9971fd24` | 夹具 ADD + rollback | `rolled-back` | `rollback/P114.RuntimeProbe/phase1.2.3-acceptance-marker.txt`（221 B）保留；staging 保留空子目录；backup 空 |

工具不自动清理事务目录；本轮保留两个事务供追溯，未删除、未手工修改 journal。

## 9. 本轮改动的测试与 CI

主机侧回归见 [阶段报告](phase1.2.3-report.md) §7。CI 结论：

- 本地 `62477fb` 对应已有 GitHub Actions Run `43546062266`（3/3 jobs success，2026-11-14T02:23:47Z），
  但它是**本轮改动之前**的提交，且不含新增的 no-root 套件步骤；
- 本轮 Phase 1.2.2/1.2.3 改动**未 commit/push**，所以**没有对应 head_sha 的 CI run**。CI 状态为 **NOT_RUN**，
  不能用历史 Run 冒充本轮通过。

## 10. 覆盖不足与风险

1. **REPLACE 未覆盖**：生产集全 SKIP；夹具只覆盖 ADD。REPLACE 的 staging→backup→switch 路径在真机仍未被本轮执行。
2. **单设备、单 API**：只有 V1963A / Android 10 / API 29；Android 11+ apply 继续 fail-closed。
3. **应用可读性**：应用非 debuggable、无 `run-as`；本轮用 Loader 报告证明部署后的文件被加载，未用第三方文件浏览器复核。
4. **多文件切换非原子**：逐文件 `mv` + journal；本轮没有做断电/拔线故障注入。
5. **大 Mod 树性能**：只测了 11+1 文件；数千文件的耗时不在此结论内。
6. **事务目录增长**：两个事务目录保留，工具不自动清理。
7. **JSON 报告小偏差**：`rollback -Apply` 的 JSON `journal.state` 字段仍是回滚前读取的 `committed`；
   设备端 journal 才是权威值（本次为 `rolled-back`）。这是报告字段问题，不是回滚失败；见阶段报告 §6.3。
8. **测试宿主**：主机是 Windows PowerShell 5.1；CI 的 PowerShell 7 未在本轮真实运行。

## 11. 证据路径

```text
D:\Download\DSH\StardewValley\.tmp\phase1.2.3\20261114-125621\
├── json\       10-inspect … 20-verify-after-rollback
├── logs\       对应文本日志 + 30-test_deploy_mods_safe.txt + 离线检查
├── device\     设备只读输出、journal、哈希、截图
├── mods-source\          本轮生产部署源（11 文件）
├── mods-source-fixture\  验收夹具源（12 文件，仅用于 ADD/rollback）
├── backup-manifest.json
├── comparison-production.json
└── comparison-fixture-post-rollback.json
```

备份：`C:\Users\<user>\Documents\StardewValley-TestBackups\V1963A\20261114-125621\`（不在仓库内）。

---

**最终判定：本次工具真机验收在“实际执行的 inspect/plan/dry-run/apply/verify/ADD/rollback + Saves 保护”范围内 PASS；
REPLACE、Android 11+、其它设备为 NOT_RUN，不得据此宣称全面通过。**
