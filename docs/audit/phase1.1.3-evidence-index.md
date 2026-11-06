# Phase 1.1.3 证据索引

对应报告：[`phase1.1.3-report.md`](phase1.1.3-report.md)。
日志根目录：`.tmp/p113/logs/`（`.tmp/` 被 `.gitignore` 排除，不随仓库分发）。

## 0. 证据等级

| 级 | 含义 | 可复现 |
| --- | --- | --- |
| **A** | 本机本次真实运行得到的测试结果（有 stdout / 退出码 / 日志） | ✅ 同一命令可重复 |
| **B** | 本机本次真实执行的**编译期**证据（编译成功/失败即结论） | ✅ 同一命令可重复 |
| **C** | 对源码 / 报告 / 配置的静态审查结论（不运行任何东西） | ✅ 人工可复核 |
| **RED** | 决策级回退后在**同一套测试**上得到的失败 | ✅ 脚本可重放 |
| **BLOCKED** | 需要本机不具备的条件（ARM64 设备、装机授权、Push 权限） | ❌ 当前不可执行 |
| **NOT RUN** | 本阶段不允许执行（远程 CI 需要 push） | ❌ |

## 1. 基线与工作区（A）

| ID | 命令 | 退出码 | 结果 | 级 |
| --- | --- | --- | --- | --- |
| B-01 | `git rev-parse --show-toplevel` | 0 | `D:/Download/DSH/StardewValley` | A |
| B-02 | `git remote -v` | 0 | `origin https://github.com/Hiyomi-Kuro/StardewValley.git`（未使用写权限） | A |
| B-03 | `git branch --show-current` | 0 | `main` | A |
| B-04 | `git rev-parse HEAD`（开始） | 0 | `76aa836ea935d03aa64fcdd7ad164f9cf88626e6` | A |
| B-05 | `git rev-parse origin/main` | 0 | 同上（**未前进**） | A |
| B-06 | `git status --porcelain=v1`（开始） | 0 | 空：无 dirty、无 untracked | A |
| B-07 | `git rev-parse HEAD`（结束） | 0 | 同上（未提交） | A |
| B-08 | `git status --porcelain=v1`（结束） | 0 | 14 改 + 3 新增（无 in-tree `bin/`、`obj/`） | A |
| B-09 | `dotnet --list-sdks`（本机可用 SDK） | 0 | `10.0.401`（复用本机已有 SDK；上一阶段的 `.tmp/phase0.5/dotnet` 已不存在） | A |
| B-10 | `python --version` | 0 | `Python 3.12.10` | A |

## 2. 引用程序集（A，从仓库内 APK 解出，未下载）

| ID | 命令 | 退出码 | 结果 |
| --- | --- | --- | --- |
| R-00a | 从 `app/build/outputs/apk/debug/app-debug.apk` 解出 `assemblies/assemblies.blob` 与 `assemblies/assemblies.arm64_v8a.blob` | 0 | 2 个 blob + manifest |
| R-00b | `python Tools/AssemblyStore/store.py extract …assemblies.blob .tmp/p113/refs` | 0 | 58 个 dll |
| R-00c | `python Tools/AssemblyStore/store.py extract …assemblies.arm64_v8a.blob .tmp/p113/refs --blob-id 001` | 0 | 7 个 dll（含 `System.Private.CoreLib`） |
| R-00d | `Get-ChildItem .tmp/p113/refs -Filter *.dll` | 0 | **65** 个 |

## 3. Red：同一套测试在旧决策下失败（RED）

方法：`.tmp/p113/red/apply_red.py apply` 对 `AssemblyStaging.cs` 做 3 处**决策级**替换
（命中即共享 / 完整性值=去重键 / 校验重算去重键），保留全部 seam 与类型，同一份测试文件两轮通用。
`restore` 用备份字节写回并打印 SHA-256。

| ID | 轮次 | 退出码 | 结果 | 日志 / 级 |
| --- | --- | --- | --- | --- |
| D-01 | Red（决策回退） | 1 | `421 checks, 385 passed, 36 failed` | `.tmp/p113/logs/red-collision.txt` / RED |
| D-02 | 回退文件 SHA-256 | 0 | `69a250119991cd68848bf9f867142c4c8c5e707cc953de57b21512f2cf1b232e` | 脚本输出 / RED |
| D-03 | 还原文件 SHA-256 | 0 | `d94d9d3c8552beda9a3451cc5ab366bf3ab73701871da6a405389277e90a76b3`（与 Green 备份一致） | 脚本输出 / A |

Red 的关键失败（摘录）：

| 断言 | Red 实际 | 说明 |
| --- | --- | --- |
| `p113_b_entries_only_never_load_a_foreign_assembly` | `MF0210 … loaded assembly is "E113EntE1" but this claim was preflighted as "F113EntF2"` | 错误程序集**真的进入了进程** |
| `p113_a_second_snapshot_is_its_own_bytes` | 快照摘要 = 第一个 claim 的字节 | 别名 |
| `p113_b_both_mods_initialize` | 两 mod 都 `MF0202`（`4608 bytes, expected 4096`） | private claim 被别名到 entry 快照 |
| `p113_a_bucket_holds_two_artifacts` | `candidates=1 files=1` | 一键一文件 |
| `p113_a_max_files_is_reachable` / `_max_total_bytes_is_reachable` | 第三份内容被"共享"而不是拒绝 | 预算在碰撞下失效 |
| `p113_c_the_recorded_integrity_value_is_sha256` | `forced:collision` | 完整性值被去重键污染 |
| `p113_c_no_sha256_fails_closed` | `staged=True` | 无强摘要仍继续 |

## 4. Green：修复后的全量结果（A）

| ID | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| G-01 | `python Tools/tests/test_loader.py --dotnet <sdk> --refs .tmp/p113/refs` | 0 | 宿主 `427 checks, 427 passed, 0 failed`；驱动 `35 checks, 35 passed, 0 failed, 0 skipped` | `.tmp/p113/logs/final/test-loader.txt` |
| G-02 | `python Tools/tests/test_mod_pipeline.py --dotnet <sdk> --refs .tmp/p113/refs` | 0 | `27 checks, 27 passed, 0 failed` | `.tmp/p113/logs/final/test-mod-pipeline.txt` |
| G-03 | `python Tools/tests/test_ci_offline.py` | 0 | `13 checks, 13 passed, 0 failed, 0 skipped` | `.tmp/p113/logs/final/ci-offline.txt` |
| G-04 | `python Tools/privacy-scan.py --include-untracked` | 0 | `RESULT: OK`（CRITICAL/HIGH 0、MEDIUM 0） | `.tmp/p113/logs/final/privacy-scan.txt` |
| G-05 | `powershell -File Tools/tests/test_device_precheck.ps1` | 0 | `10 checks, 10 passed` | `.tmp/p113/logs/final/device-precheck-selftest.txt` |
| G-06 | `powershell -File Tools/tests/run-all.ps1 -Dotnet <sdk> -Refs .tmp/p113/refs -LogDir .tmp/p113/logs/final` | 0 | `6 suites, 6 passed, 0 failed, 0 skipped` | `.tmp/p113/logs/final-run-all-console.txt` |
| G-07 | `git diff --check` | 0 | 无输出 | `.tmp/p113/logs/final/git-diff-check.txt` |
| G-08 | 驱动内 `--diff-refs .tmp/p113/refs` | 0 | `compared: 65 (strong-name signed: 58)`、`RESULT: OK` | `.tmp/p113/logs/final/test-loader.txt` |
| G-09 | 还原后再次全量（无 `--refs`） | 0 | `421 checks, 421 passed, 0 failed` | `.tmp/p113/logs/green-after-restore.txt` |

**回归等价性**：`427 − 67（新增 p113 检查）= 360`，与 Phase 1.1.2 记录的宿主 360 项一致；
驱动 35/35、Mod Pipeline 27/27、Offline 13/13 也与历史记录一致 → 旧用例没有被削弱或删除。

## 5. 主题 A：去重与碰撞（A）

| ID | 检查 | 结果 |
| --- | --- | --- |
| A-01 | `p113_a_collision_does_not_share_a_snapshot` | PASS（两条不同的 staged 路径） |
| A-02 | `p113_a_first_snapshot_is_its_own_bytes` / `_second_…` | PASS（各自 SHA-256 == 各自 fixture） |
| A-03 | `p113_a_snapshot_names_follow_the_declarations` | PASS（`Q113EntA1.dll` / `R113EntB2.dll`） |
| A-04 | `p113_a_bucket_holds_two_artifacts` | PASS（`candidates=2 files=2 bytes=9216`） |
| A-05 | `p113_a_third_claim_finds_the_matching_candidate` / `_did_not_reuse_the_first_candidate` | PASS（找到第 2 个候选） |
| A-06 | `p113_a_a_different_length_is_never_shared` / `_staged_verbatim` | PASS（不同长度不共享） |
| A-07 | `p113_a_identical_bytes_share_one_snapshot` / `_counted_once` / `_exact_bytes` | PASS（`files=1 bytes=4608`） |
| A-08 | `p113_a_unreadable_candidate_fails_closed` / `_reports_staging_failure` / `_created_no_extra_artifact` | PASS（`MF0209: … could not be compared`） |
| A-09 | `p113_a_max_files_is_reachable` / `_rejects_with_a_budget_message` / `_sharing_still_works_at_the_file_bound` | PASS（`already holds 2 files`） |
| A-10 | `p113_a_max_total_bytes_is_reachable` / `_rejects_with_a_budget_message` | PASS（`is full`） |
| A-11 | `p113_a_paths_do_not_overwrite` / `_same_mod_different_names_…` / `_same_declared_name_in_two_mods_…` | PASS（`files=4 distinct=True`） |

## 6. 主题 B：Preflight + Loader 端到端（A）

| ID | 检查 | 结果 |
| --- | --- | --- |
| B1-01 | `p113_b_preflight_rejects_nothing` / `_both_entries_are_in_different_identity_groups` | PASS（`declared=4`） |
| B1-02 | `p113_b_cross_name_entries_do_not_share_a_snapshot` / `_entries_are_staged_verbatim` | PASS |
| B1-03 | `p113_b_cross_name_privates_do_not_share_a_snapshot` / `_privates_are_staged_verbatim` | PASS |
| B1-04 | `p113_b_private_snapshot_identity_matches_its_claim`（`Q113PriA`/`R113PriB`） | PASS |
| B1-05 | `p113_b_entry_snapshot_identity_matches_its_claim`（`Q113EntA1`/`R113EntB2`） | PASS |
| B1-06 | `p113_b_both_mods_initialize` / `_each_mod_runs_its_own_entry` / `_the_two_entries_are_loaded_separately` | PASS（标记 `Alpha\|A`、`Beta\|A`；各 1 条 load） |
| B1-07 | `p113_b_no_identity_mismatch_is_reported` | PASS（无 `MF0210`） |
| B1-08 | `p113_b_order_independent_classification` / `_loading` | PASS（排序后签名一致） |
| B1-09 | `p113_b_same_name_different_content_is_still_rejected` / `_a_conflict_still_loads_nothing` | PASS（双方 `MF0206`） |
| B1-10 | `p113_b_entries_only_both_initialize` / `_never_load_a_foreign_assembly` / `_run_their_own_markers` / `_load_twice` | PASS（Red 中为 `MF0210`） |

## 7. 主题 C：快照完整性（A）

| ID | 检查 | 结果 |
| --- | --- | --- |
| C1-01 | `p113_c_a_fresh_snapshot_verifies` | PASS |
| C1-02 | `p113_c_same_length_different_identity_is_refused` | PASS（`no longer matches`） |
| C1-03 | `p113_c_same_identity_different_content_is_refused` | PASS |
| C1-04 | `p113_c_truncated_snapshot_is_refused` / `_grown_` / `_deleted_` | PASS（长度/存在性分别拒绝） |
| C1-05 | `p113_c_a_forced_collision_does_not_fool_the_verifier` | PASS |
| C1-06 | `p113_c_the_recorded_integrity_value_is_sha256` | PASS（`sha256:5c72c7c9…`） |
| C1-07 | `p113_c_dedup_key_falls_back_to_sha1_without_sha256` | PASS（`sha1:fa6725a8…`） |
| C1-08 | `p113_c_no_sha256_fails_closed` / `_reports_the_missing_strong_digest` | PASS（`MF0209`，消息点名 SHA-256） |
| C1-09 | `p113_c_verifier_fails_closed_without_sha256` | PASS |
| C1-10 | `p113_c_a_deleted_snapshot_fails_the_mod` / `_the_declared_file_is_not_loaded_instead` | PASS（`MF0202`，无 `assembly-load=`，无 marker） |
| C1-11 | `p113_c_a_tampered_snapshot_fails_the_mod` / `_never_initializes` / `_runs_no_entry` | PASS |

## 8. 构建（B）

| ID | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| C-01 | `dotnet build Mod/ModFramework.Runtime -c Release`（桌面） | 0 | 0 warning / 0 error | `.tmp/p113/logs/final/test-loader.txt`（`build_runtime`） |
| C-02 | `dotnet build Mod/ModFramework.Runtime -c Release -p:AndroidRefsDir=<.tmp/p113/refs>` | 0 | 0 warning / 0 error（新代码用到的 `SHA256.HashData`、`File.ReadAllBytes`、`File.OpenRead`、`Path.GetDirectoryName`、`Directory.CreateDirectory`、`Guid.NewGuid` 均在裁剪引用集内） | `.tmp/p113/logs/build-runtime-androidrefs.txt` |
| C-03 | `dotnet build Mod/ExampleMod.HelloWorld -c Release -p:AndroidRefsDir=<refs>`（驱动执行） | 0 | `hello_world_build` PASS、`hello_world_host_run` PASS | `.tmp/p113/logs/final/test-loader.txt` |

## 9. 静态审查（C）

| ID | 结论 |
| --- | --- |
| S-01 | `Assembly.LoadFrom` 仍只有一处调用（`Loader.TryResolveClaim`），且必在 `preflight-stage=complete` 之后 |
| S-02 | 去重键与完整性摘要由**两个不同函数**产生：`ComputeDedupKey`（允许 SHA-1）/ `TryComputeSnapshotDigest`（只允许 SHA-256）；后者失败即 fail-closed |
| S-03 | 候选比较的右手边是当前 claim 的 `bytes`；`CompareFileToBuffer` 不接受另一条 staged path 作为"内容" |
| S-04 | `AllocatePath` 不再删除既有文件；同名占用时追加 `~N`，仍逐条经过 `PathRules.IsInside` |
| S-05 | 资源预算按 artifact 计数；共享既有 artifact 不消耗预算 |
| S-06 | 新增 seam 全部 `internal` 且只在测试中使用：`DedupKeyForTests`、`SnapshotDigestUnavailableForTests`、`LimitsForTests`；生产代码无任何"跳过校验"开关 |
| S-07 | `MF0210` 分支不写 `s_loadedByPath` 缓存，因此不会污染后续 claim |
| S-08 | 未修改 Phase 1.1.2 的历史报告/索引，也未改动与四个主题无关的功能 |

## 10. 不可执行项（BLOCKED / NOT RUN / SKIP）

| ID | 项 | 状态 | 原因 |
| --- | --- | --- | --- |
| X-01 | `校验 → LoadFrom` 原子性 | **BLOCKED** | 需要 `Assembly.Load(byte[])`（目标引用集无此重载） |
| X-02 | AArch64 Mono 的 `SHA256.HashData` 运行时语义 | **BLOCKED** | 无 ARM64 设备；禁止装机 |
| X-03 | AArch64 Mono 的 `LoadFrom` 去重/实例语义 | **BLOCKED** | 同上；本阶段不依赖该假设 |
| X-04 | 远程 GitHub Actions | **NOT RUN** | 任务禁止 push；用户自行 push 后由 CI 产出 |
| X-05 | adb 装机 / 真实游戏 Mod 运行 | **NOT RUN** | 任务禁止安装与真实游戏目录改动 |
| X-06 | 单文件 64 MiB 上限的碰撞断言 | **SKIP** | 检查先于写入且与碰撞无关；未构造 64 MiB fixture |
| X-07 | 真实 4096 / 256 MiB 临界值 | **SKIP** | 用 `LimitsForTests` 缩小后验证逻辑，生产常量未改 |
| X-08 | 快照"拒绝读取"（权限拒绝）的确定性用例 | **PARTIAL（C 级）** | 进程内无法跨平台确定性制造"存在但不可读"；实现为 `try/catch → false`，相邻用例（删除/截断/增长/无 SHA-256）证明不会返回 true |
