# Phase 1.1 证据索引

- 阶段：**Phase 1.1**（Loader 加固）
- 报告：[`phase1.1-report.md`](phase1.1-report.md)
- 基线：`f2e86b467203cda77f6f76c3cca6ff629460656e`（`main` = `origin/main`）
- 生成时间：2026-11-05（本地，UTC+08:00）
- 运行环境：Windows，仓库私有 SDK `.tmp/phase0.5/dotnet/dotnet.exe`（8.0.404），Python 3.12.10

> 本索引只登记**本阶段实际执行过**的命令。没有执行的项写 `NOT RUN` / `BLOCKED`，
> 不使用"预计/应该/理论可行"充当证据。

---

## 0. 证据等级定义

| 级 | 含义 | 可复现 |
| --- | --- | --- |
| **A** | 本机本次实际运行得到的测试结果（有 stdout/退出码/日志） | ✅ 同一命令可重复 |
| **B** | 本机本次实际执行的**编译期**证据（编译成功/失败即结论） | ✅ 同一命令可重复 |
| **C** | 对源码/报告/配置的静态审查结论（不运行任何东西） | ✅ 人工可复核 |
| **D** | 历史阶段的记录（Phase 0 / Phase 1 当时留下，本阶段未重跑） | ❌ 不可复现 |
| **BLOCKED** | 需要本机不具备的条件（ARM64 物理设备、装机授权） | ❌ 当前不可执行 |

本阶段所有 P11 门禁只接受 A/B/C 级证据；D 级与 BLOCKED 不得升格为 PASS。

---

## 1. 基线与工作区（C / A）

| ID | 命令 | 时间 | 退出码 | 结果 | 级 |
| --- | --- | --- | --- | --- | --- |
| B-01 | `git rev-parse --abbrev-ref HEAD` | 2026-11-05 19:1x | 0 | `main` | A |
| B-02 | `git rev-parse HEAD` | 同上 | 0 | `f2e86b467203cda77f6f76c3cca6ff629460656e` | A |
| B-03 | `git rev-parse origin/main` | 同上 | 0 | `f2e86b46…`（与 HEAD 相同） | A |
| B-04 | `git remote -v` | 同上 | 0 | `origin https://github.com/Hiyomi-Kuro/StardewValley.git` | A |
| B-05 | `git status --porcelain=v1 -uall` | 同上 | 0 | 空输出（工作区干净，无 dirty/untracked） | A |
| B-06 | `git log --oneline -5` | 同上 | 0 | `f2e86b4` / `b51af45` / `fa4f5c3` / `26efdd2` / `4439b2f` | A |

没有执行 `git fetch` / `pull` / `rebase`：只读本地 `origin/main` 引用。

---

## 2. 关键能力探针：目标运行时的 BCL 表面（B）

这是本阶段最重要的证据：它决定"程序集身份能否在加载前读到"。

| ID | 命令 | 时间 | 退出码 | 结果 | 日志 | 级 |
| --- | --- | --- | --- | --- | --- | --- |
| API-01 | `dotnet build .tmp/phase1.1/work/apiprobe (Present.cs) -p:AndroidRefsDir=<refs>` | 19:20 | 1（预期失败） | **6 个错误**，其中 `AssemblyName` 未包含 `GetAssemblyName`、`SHA256` 未包含 `Create`、`FileInfo` 无 1 参构造 | `.tmp/phase1.1/logs/apiprobe-present.txt` | B |
| API-02 | `dotnet build … (Present2.cs)` | 19:21 | 1（预期失败） | 仅 `SHA256.HashData(Stream)` 与 `HashData(Stream, Span<byte>)` 缺失；**`HashData(byte[])` 可用**；`AppDomain.GetAssemblies`/`Assembly.IsDynamic`/`Assembly.Location`/`FileStream.*` 全部通过 | `.tmp/phase1.1/logs/apiprobe-present2.txt` | B |
| API-03 | `dotnet build … (Present3.cs)` | 19:22 | 1（预期失败） | `HashAlgorithmName.SHA256` 缺失、`SHA256Managed` 不存在 | `.tmp/phase1.1/logs/apiprobe-present3.txt` | B |
| API-04 | `dotnet <surface.dll> <refs> System.Reflection.AssemblyName System.Security.Cryptography.SHA256` | 19:22 | 0 | `AssemblyName` 只有 `internal static Create/FillName`；`SHA256` 只有 `HashData`×2 + `TryHashData` | `.tmp/phase1.1/logs/surface-1.txt` | B |
| API-05 | `dotnet <surface.dll> … IncrementalHash HashAlgorithmName SHA256Managed` | 19:22 | 0 | `IncrementalHash.CreateHash` 存在但 `HashAlgorithmName.SHA256` 不存在 | 同 API-03 会话输出 | B |
| API-06 | `dotnet build .tmp/phase1.1/work/apiprobe (Absent.cs)`（对照） | 19:20 | 1（预期失败） | `Directory.GetFiles`、`Version.TryParse` 均编译失败 → **探针方法本身有分辨力** | 包含在 API-01 日志中 | B |

结论（写入报告 §3）：`AssemblyName.GetAssemblyName(string)` **不存在**，
因此采用项目内只读 PE/CLI 解析器；`SHA256.HashData(byte[])` 存在，流式 API 不存在。

> 说明：探针工程与 `surface` 工具都在 `.tmp/phase1.1/work/`（gitignored），是一次性诊断工具，
> 不随仓库发布。它们的命令与输出在上表日志里，可重放。

---

## 3. 构建（B）

| ID | 命令 | 时间 | 退出码 | 结果 | 级 |
| --- | --- | --- | --- | --- | --- |
| BUILD-01 | `dotnet build Mod/ModFramework.Runtime/…csproj -c Release` | 19:33 | 0 | 0 warning / 0 error | B |
| BUILD-02 | `dotnet build Mod/ModFramework.Runtime/…csproj -c Release -p:AndroidRefsDir=<refs>` | 19:33 | 0 | 0 warning / 0 error（**P11-07 核心证据**） | B |
| BUILD-03 | `dotnet build Tools/tests/ModFramework.Tests/…csproj -c Release` | 19:33 | 0 | 0/0 | B |
| BUILD-04 | `dotnet build Mod/ExampleMod.HelloWorld/…csproj -p:AndroidRefsDir=<refs>` | 19:32 | 0 | 0/0（由 `test_loader.py --refs` 触发，`hello_world_build` PASS） | B |
| BUILD-05 | 夹具工程构建（`ConflictLib` × 8 变体 / `PlainLib` / `FakeMonoRuntimeProbe`） | 19:32 | 0 | `fixture_variant_*` 10 项全部 PASS | A |

---

## 4. 测试运行（A）

| ID | 命令 | 时间 | 退出码 | 通过/失败/跳过 | 日志 |
| --- | --- | --- | --- | --- | --- |
| T-01 | `python Tools/tests/test_ci_offline.py` | 19:32 | 0 | 13 / 0 / 0 | `.tmp/phase1.1/logs/run-all/ci-offline.txt` |
| T-02 | `python Tools/privacy-scan.py --include-untracked` | 19:32 | 0 | `RESULT: OK`（0 CRITICAL/HIGH） | `.tmp/phase1.1/logs/run-all/privacy-scan.txt` |
| T-03 | `powershell -File Tools/tests/test_device_precheck.ps1` | 19:32 | 0 | 10 / 0 / 0 | `.tmp/phase1.1/logs/run-all/device-precheck-selftest.txt` |
| T-04 | `python Tools/tests/test_mod_pipeline.py --refs …` | 19:32 | 0 | 27 / 0 / 0 | `.tmp/phase1.1/logs/run-all/test-mod-pipeline.txt` |
| T-05 | `python Tools/tests/test_loader.py --refs …` | 19:32 | 0 | 驱动 19 / 0 / 0；宿主 **206 / 0 / 0** | `.tmp/phase1.1/logs/run-all/test-loader.txt` |
| T-06 | `powershell -File Tools/tests/run-all.ps1 -Refs … -LogDir …` | 19:32 | 0 | 6 suites / 6 PASS | `.tmp/phase1.1/logs/run-all-console.txt` |
| T-07 | `git diff --check` | 19:32 | 0 | 无输出（无空白错误） | 无输出，故 `git-diff-check.txt` 未生成 |
| T-08 | 红基线：旧 Runtime + 场景驱动 | 19:29 | 0 | **4 / 4 缺陷复现** | `.tmp/phase1.1/logs/red-baseline.txt` |

### 4.1 红基线明细（T-08）

| 缺陷 | 旧代码表现 | 对应新测试 |
| --- | --- | --- |
| 裸 `MonoRuntimeProbe.dll` 被执行 | `probe=<root>/probe-output/probe.txt`，marker 存在 | `legacy_probe_marker_absent` 等 8 项 |
| 同名不同版本无预检 | `Red.VersionA` 成功、`Red.VersionB` 失败于 `FileLoadException: Assembly with same name is already loaded`（`MF0202`，不提示真实原因） | `conflict_version_both_rejected` 等 |
| 显式 gameVersion 放行 | `>=99.0.0` + unknown → `Initialized` | `gate_game_version_unknown_rejected` |
| 报告无日志状态 | 报告里没有 `log-status=` | `log_degraded_status_recorded` |

---

## 5. 本次运行日志的 SHA-256

命令（`sha256sum` 等价物，PowerShell）：

```powershell
Get-FileHash -Algorithm SHA256 .tmp\phase1.1\logs\<file>
```

| 文件 | 字节 | SHA-256 |
| --- | --- | --- |
| `.tmp/phase1.1/logs/run-all/ci-offline.txt` | 2090 | `53f3056e9e86aa27cce6a6350b452261f84f6b2e969270fb74b2c67cb638b570` |
| `.tmp/phase1.1/logs/run-all/privacy-scan.txt` | 2200 | `49517729a70bf7a9fdb1c8e0f594dabdd77f9a7e1dae8defc3d3615c726ed772` |
| `.tmp/phase1.1/logs/run-all/device-precheck-selftest.txt` | 1416 | `d1b63c1300b6da143e5a5bd908bda8f67817cb3861e0f11c031e756123354f6d` |
| `.tmp/phase1.1/logs/run-all/test-mod-pipeline.txt` | 4986 | `ce13cbd7b5afefc7e3db2c12975b7bcc9278f8247358314f6b3b5a3f1a66cba1` |
| `.tmp/phase1.1/logs/run-all/test-loader.txt` | 50996 | `664948eeab1dfd47c331a636ffb18fbf2b613a2138234423ae8c5b6ff39f6144` |
| `.tmp/phase1.1/logs/run-all-console.txt` | 63252 | `79bc53155705d1fc01b1a8e54b555c378ba466e02f7ea8a0a5a75724fc9bbd15` |
| `.tmp/phase1.1/logs/red-baseline.txt` | 1280 | `46edc673d1813b09771d5786bbdef5be6c5c274ac4ae5c971cd3044dd2e0d296` |
| `.tmp/phase1.1/logs/apiprobe-present.txt` | 5092 | `0317a51f8c6ed5cd813506d784d4d87bafe5e0720707819e8f73b34825309ada` |
| `.tmp/phase1.1/logs/apiprobe-present2.txt` | 1924 | `e4b052a8ecd2177a33b07470eb4390379c053c0dbc1ab7425afef6b2980a8ec5` |
| `.tmp/phase1.1/logs/apiprobe-present3.txt` | 1796 | `1b78d39dbfb97f79baa6a5e0d3104e8e5e145874956864c39c34b6509037cb58` |
| `.tmp/phase1.1/logs/surface-1.txt` | 1412 | `90bb9caf624dfc02674e21e2cfc57d96b613e58f3dd5c3daf0733dd0040411ce` |

说明：

- 上表对应 §4 的那次运行（日志时间戳 2026-11-05 19:39–19:40）。`.tmp/` 被 gitignore，
  这些日志**不会进仓库**，只在本机可复核；`run-all.ps1` 每次运行会覆盖它们。
- `git-diff-check.txt` 不存在，因为 `git diff --check` 无输出时 `Tee-Object` 不创建文件；
  "无输出 + 退出码 0"本身就是该检查通过的证据（T-07）。
- 红基线（`red-baseline.txt`）与会话时间较早的探针日志没有在最后一次运行中被覆盖，
  因此其哈希与 §4 的时间戳一致。
- 本文件定稿之后又跑了一次 `test_ci_offline.py` + `privacy-scan.py`（输出在
  `.tmp/phase1.1/logs/post-docs-*.txt`），用来证明"加入本索引与哈希表之后"仓库仍然通过
  离线检查、隐私扫描与 `git diff --check`（13/13、`RESULT: OK`、退出码 0）。
  那次复核不覆盖 §5 列出的日志，所以上表哈希仍然有效。

---

## 6. 交付物 SHA-256（变更的 tracked 文件）

| 文件 | 字节 | SHA-256 |
| --- | --- | --- |
| `.github/workflows/ci.yml` | 3461 | `2887bf2ded1878fab2b0b9d8ce89de7ad91f97ee6bc10b09cd64d05aa6d38898` |
| `README.md` | 11578 | `24142397ad549d39910aeb69e07d1aae78b355e5213c24242e876573466567fb` |
| `Mod/ModFramework.Runtime/ErrorCodes.cs` | 9203 | `b5b0258c72b0ee1ff6b26ade289b67bddc1c6ab3346b2021bf78e5307520476a` |
| `Mod/ModFramework.Runtime/Loader.cs` | 38873 | `acc3e7f03775ed0b451aecd47d0e76d3604fe72489dedd5565cbcada913abf1d` |
| `Mod/ModFramework.Runtime/ModLogger.cs` | 4031 | `08b214adf5b32ab76ad54fb71e7609a395dcb7915fb4d5a3b93d4bcae8a88c43` |
| `Mod/ModFramework.Runtime/ModState.cs` | 3605 | `a92e471d05b01e55412eafc6c6187a67f29d6a8633b9529c32a7a83966af1f16` |
| `Mod/ModFramework.Runtime/AssemblyIdentity.cs` | 10615 | `a35db0c1e35f08668f7783a2be1f2ca6deb70d47cb4b59d1b556dc18e7144b2f` |
| `Mod/ModFramework.Runtime/AssemblyMetadata.cs` | 25096 | `bb9b757c59b0b3e4cdde2b20df85c48dd56a8550c1d9d1fc6c30f9b03ea42195` |
| `Mod/ModFramework.Runtime/AssemblyPreflight.cs` | 18930 | `f8e860d927e42118d3e6d450c373788c5cbc061263b0583ed3c60d899abef163` |
| `Mod/ModFramework.Runtime/PathRules.cs` | 954 | `60ec9dbc94faa87e6219e1cc291fecd38f7fe28b8a745df012348d599fa04ad8` |
| `Mod/MonoRuntimeProbe/README.md` | 8260 | `7afcb5e0044654d600dc8743a0d10c6c7efc5956c8d265870c3c6d67960f7eb8` |
| `Tools/tests/ModFramework.Tests/HostTests.cs` | 47459 | `5173ea4797f79bdb0c741548833c73e925f559bea10713573dca154b3ba1ad35` |
| `Tools/tests/ModFramework.Tests/UnitTests.cs` | 26762 | `fcf2646b023fd7f2da70e675ee013f55b6060f3e19a6daffd1f0babaf8bc2cfc` |
| `Tools/tests/test_loader.py` | 12145 | `b854fbc80488a6bac080aa5051479af04fed3fc8c9888f4267a186caadcbf12d` |
| `Tools/tests/FixtureMods/ConflictLib/ConflictLib.csproj` | 2177 | `3aa413ff2bb8abe1bee823448c97493e726313fc432c31bcbd364635a99aaa3f` |
| `Tools/tests/FixtureMods/ConflictLib/FixtureEntry.cs` | 1340 | `f5f69a5b79fd8d7064aa7b5c3d31a120c17e86675b4e9bedde4ec7dadb30251d` |
| `Tools/tests/FixtureMods/PlainLib/PlainLib.csproj` | 1439 | `ca8df626bd26792dfb489a28225e52dba2e615b0229c0788d3ceb63c67506d0c` |
| `Tools/tests/FixtureMods/PlainLib/Marker.cs` | 375 | `2c84c37253711d9ef45fa19c470974cf6104bdc4a6f98f79dd56d59bcbc731c5` |
| `Tools/tests/FixtureMods/FakeMonoRuntimeProbe/FakeMonoRuntimeProbe.csproj` | 906 | `873c6eb7f72467a3922e60fcf1f9bba6b1c2fc9f94ec76c039c744ea9b3ce55e` |
| `Tools/tests/FixtureMods/FakeMonoRuntimeProbe/Probe.cs` | 949 | `ac0709aae047bf5161c490e51b1ce07e75333f57e03832d961e5ed49af6d33b4` |
| `docs/architecture/phase1-loader-design.md` | 10132 | `02fc5f67db84dfee559704a2ba57e22ca81176c666ef6635f88704477372a2e2` |
| `docs/audit/phase1-report.md` | 19795 | `0e05c2809b80ff045cc5e33f699fe978cdf23606583f045566f01c29fd7f816c` |
| `docs/audit/phase1.1-report.md` | 36889 | `d2e0c0130ef17f0667356ab37cd4a0b2706d62db3b779d7c4a50ecb3fc1e8dbe` |
| `docs/modding/manifest-spec.md` | 13887 | `1919d74c2b2c324989be744c582337f7928bac0e61337aa1e9980e10070687ea` |

复核命令（在仓库根执行，任一文件不符即说明被改动过）：

```powershell
Get-ChildItem <上表中的路径> | Get-FileHash -Algorithm SHA256
```

说明：本文件（`phase1.1-evidence-index.md`）不列入上表——把自身哈希写进自身会立即失效。
需要时请单独 `Get-FileHash`。

---

## 7. 未覆盖 / BLOCKED

| ID | 项 | 状态 | 原因 |
| --- | --- | --- | --- |
| X-01 | Android 设备 E1：mod 入口在真机真实执行并被观测 | **BLOCKED** | 无 ARM64 物理设备；未获装机授权 |
| X-02 | Android 设备 E2：真机只读反射 `Game1` | **BLOCKED** | 同上 |
| X-03 | 设备上 PE/CLI 解析器对游戏真实程序集的行为 | **BLOCKED** | 同上（只有桌面实测 + 编译期证据） |
| X-04 | 设备上 `SHA256.HashData` 的实际可用性 | **BLOCKED** | 同上；失败时按设计退化并在报告标注 |
| X-05 | 远程 GitHub Actions | **NOT RUN** | 本任务不 commit / 不 push |
| X-06 | 真实第三方 mod 生态兼容性 | **NOT RUN** | 只用夹具与示例 mod |
| X-07 | `Tools/deploy-apk.ps1` 实跑 / APK 重新构建 | **NOT RUN** | 需装机授权，且本阶段未改 APK 侧 |

---

## 8. 一键复现

```powershell
# 仓库根：D:\Download\DSH\StardewValley
$dotnet = ".tmp\phase0.5\dotnet\dotnet.exe"
$refs   = (Resolve-Path .tmp\phase0.5\refs).Path

# 1) 目标引用集构建（P11-07）
& $dotnet build Mod\ModFramework.Runtime\ModFramework.Runtime.csproj -c Release -p:AndroidRefsDir=$refs
& $dotnet build Mod\ExampleMod.HelloWorld\ExampleMod.HelloWorld.csproj -c Release -p:AndroidRefsDir=$refs

# 2) 全量本地套件（CI 离线 + 隐私 + 设备自测 + 注入流水线 + Loader 宿主 + diff 检查）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\tests\run-all.ps1 -Refs .tmp\phase0.5\refs

# 3) 单独只看 Loader（含 206 项宿主检查）
python Tools\tests\test_loader.py --dotnet $dotnet --refs $refs
```

```powershell
# 4) 红基线（需要一次性工程，见报告 §7.1；工程在 .tmp 下，不随仓库发布）
& $dotnet build .tmp\phase1.1\work\red\red-driver\red-driver.csproj -c Release -o .tmp\phase1.1\work\red\out
& $dotnet .tmp\phase1.1\work\red\out\RedDriver.dll <fixtures目录> <临时目录>
```

> `.tmp/` 被 `.gitignore` 排除：这些日志与探针工程**不会**进仓库，
> 因此本索引用仓库相对路径登记，不用绝对路径（隐私扫描要求）。

---

## 9. 证据与结论的对应关系

| 门禁 | 主要证据 ID |
| --- | --- |
| P11-01 | T-05（`legacy_probe_*` 8 项）+ T-08（旧代码复现） |
| P11-02 | T-05（`gate_game_version_*`） |
| P11-03 | T-05（`gate_framework_version_*`） |
| P11-04 | T-05（`conflict_preflight_runs_before_first_load`） |
| P11-05 | T-05（`conflict_version_*` / `conflict_content_*` / `conflict_shared_*` / `conflict_host_*` / `conflict_deterministic_*`） |
| P11-06 | T-05（`conflict_downstream_blocked` / `conflict_unrelated_still_initialized`） |
| P11-07 | API-01…06 + BUILD-02 |
| P11-08 | T-05（`log_degraded_*` 共 10 项） |
| P11-09 | T-04 + T-05（206/206，无回归） |
| P11-10 | C：README / manifest-spec / phase1 交叉引用 / phase1.1 报告 |
| P11-11 | T-01…T-07（本地）；远程 NOT RUN |
| P11-12 | X-01 / X-02（BLOCKED） |
