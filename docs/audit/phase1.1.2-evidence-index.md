# Phase 1.1.2 证据索引

对应报告：`docs/audit/phase1.1.2-report.md`。
日志根目录：`.tmp/phase1.1.2/logs/`（`.tmp/` 被 `.gitignore` 排除，不随仓库分发）。

## 0. 证据等级定义

| 级 | 含义 | 可复现 |
| --- | --- | --- |
| **A** | 本机本次实际运行得到的测试结果（有 stdout / 退出码 / 日志） | ✅ 同一命令可重复 |
| **B** | 本机本次实际执行的**编译期**证据（编译成功/失败即结论） | ✅ 同一命令可重复 |
| **C** | 对源码 / 报告 / 配置的静态审查结论（不运行任何东西） | ✅ 人工可复核 |
| **D** | 历史阶段的记录（Phase 0 / 1 / 1.1 / 1.1.1 当时留下，本阶段未重跑） | ❌ 不可复现 |
| **BLOCKED** | 需要本机不具备的条件（ARM64 物理设备、装机授权、Push 权限） | ❌ 当前不可执行 |

## 1. 基线与工作区（A）

| ID | 命令 | 退出码 | 结果 | 级 |
| --- | --- | --- | --- | --- |
| B-01 | `git rev-parse --show-toplevel` | 0 | `D:/Download/DSH/StardewValley` | A |
| B-02 | `git remote -v` | 0 | `origin https://github.com/Hiyomi-Kuro/StardewValley.git`（fetch/push，均未使用写权限） | A |
| B-03 | `git branch --show-current` | 0 | `main` | A |
| B-04 | `git rev-parse HEAD` | 0 | `786679bbdf73e2b67689eb166acba967339fcb42` | A |
| B-05 | `git rev-parse origin/main` | 0 | `786679bbdf73e2b67689eb166acba967339fcb42`（未前进） | A |
| B-06 | `git status --porcelain=v1`（开始时） | 0 | 空：无 dirty、无 untracked | A |
| B-07 | `.tmp/phase0.5/dotnet/dotnet.exe --version` | 0 | `8.0.404` | A |
| B-08 | `Get-ChildItem .tmp/phase0.5/refs -Filter *.dll` | 0 | 65 个程序集 | A |
| B-09 | 全仓库测试目录搜索 `OpenForTests` | 0 | 只有 seam 定义，**零调用** → B 组确为覆盖缺口 | C |
| B-10 | `git status --porcelain=v1`（结束时） | 0 | 8 改 + 3 新增 + 2 新文档；无临时文件入库 | A |

## 2. Red 基线：把"决策"改回旧实现，同一套测试失败（A）

方法：`.tmp/phase1.1.2/red/apply_red.py` 对 `Loader.cs` / `AssemblyIdentity.cs` / `AssemblyMetadata.cs`
做**决策级**回退（不改测试），重建宿主后运行，再按备份字节还原。
属于"测试先行、旧实现失败"的等价证据：测试文件在 Red / Green 两轮**完全相同**。

| ID | 轮次 | 退出码 | 结果 | 日志 / 级 |
| --- | --- | --- | --- | --- |
| R-01 | Red-A（按声明路径加载、无快照、无二次校验） | 1 | **11 项失败**：`A112Entry.dll contains assembly "Q112Entry"`（`MF0203`）、替换件入口被执行（`P112.A.Content\|B`）、私有依赖 `BadImageFormatException`、claim 无快照 | `logs/red-a.txt` / A |
| R-02 | Red-D（blob 非空即公钥） | 1 | **5 项失败**：7/9 字节 token、flag+空、flag+8、flag+10 全部被接受 | `logs/red-d.txt` / A |
| R-03 | Red-E（无收敛状态、超限即归类） | 1 | **5 项失败**：`preflight-convergence=converged`、`roundLimit=1` 时 `P112.E.Owner state=Initialized` | `logs/red-e.txt` / A |
| R-04 | 还原后同一套测试 | 0 | `354 checks, 354 passed`（不含 `--hello-world-dir`） | A |

> Red 的失败均来自目标行为断言，没有一条是编译错误、fixture 构建失败或环境缺失。

## 3. Green：修复后的全量结果（A）

| ID | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| G-01 | `python Tools/tests/test_loader.py --dotnet .tmp/phase0.5/dotnet/dotnet.exe --artifacts .tmp/p112/artifacts --workdir .tmp/p112/work --refs .tmp/phase0.5/refs` | 0 | 宿主 `360 checks, 360 passed, 0 failed`；驱动 `35 checks, 35 passed, 0 failed, 0 skipped` | `logs/final/test-loader.txt` |
| G-02 | 驱动内含 Mod Pipeline | 0 | `27 checks, 27 passed, 0 failed` | `logs/final/test-mod-pipeline.txt` |
| G-03 | `python Tools/tests/test_ci_offline.py` | 0 | `13 checks, 13 passed, 0 failed, 0 skipped` | `logs/final/ci-offline.txt` |
| G-04 | `python Tools/privacy-scan.py --include-untracked` | 0 | CRITICAL/HIGH 0、MEDIUM 0、`RESULT: OK`（INFO 均为历史文档中的旧仓库绝对路径） | `logs/final/privacy-scan.txt` |
| G-05 | `powershell -File Tools/tests/test_device_precheck.ps1` | 0 | `10 checks, 10 passed` | `logs/final/device-precheck-selftest.txt` |
| G-06 | `powershell -File Tools/tests/run-all.ps1 -Refs .tmp/phase0.5/refs -LogDir .tmp/phase1.1.2/logs/final` | 0 | `6 suites, 6 passed, 0 failed, 0 skipped` | `logs/final-run-all-console.txt` |
| G-07 | `git diff --check` | 0 | 无输出（无空白错误） | `logs/final/git-diff-check.txt` |
| G-08 | 65 程序集解析差分 `--diff-refs` | 0 | `compared: 65 (strong-name signed: 58)`、`RESULT: OK` | `logs/final/test-loader.txt` |
| G-09 | `p112_*` 断言计数（`PASS p112_` 行） | 0 | **77** 条全部 PASS | `logs/final/test-loader.txt` |

## 4. 目标引用编译与 API 探针（B）

| ID | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| C-01 | `dotnet build Mod/ModFramework.Runtime -c Release -p:AndroidRefsDir=<abs .tmp/phase0.5/refs>` | 0 | 0 warning / 0 error（新代码用到的 `File.OpenRead`、`Guid.NewGuid`、`Directory.Delete(string,bool)`、`FileStream`、`SHA256.HashData`、`File.ReadAllBytes` 都在裁剪后的引用集内） | `logs/build-runtime-androidrefs.txt` |
| C-02 | `dotnet build Mod/ExampleMod.HelloWorld -c Release -p:AndroidRefsDir=<refs>`（由驱动执行） | 0 | `hello_world_build` PASS、`hello_world_host_run` PASS | `logs/final/test-loader.txt` |
| C-03 | 7 行探针：`Assembly.Load(byte[])` + `Assembly.LoadFrom(string)` | 1（预期） | `error CS1503: 参数 1: 无法从“byte[]”转换为“string”`；同一编译单元里的 `LoadFrom(string)` 正常 → **目标运行时只能从路径加载** | `logs/probe-load-api.txt` |
| C-04 | `dotnet build Mod/ModFramework.Runtime -c Release`（桌面） | 0 | 0 warning / 0 error | `logs/final/test-loader.txt` |

## 5. 主题 A：快照绑定（A）

| ID | 检查 | 结果 | 日志 |
| --- | --- | --- | --- |
| A-01 | `p112_a_claim_has_a_snapshot` | PASS（`StagedPath` 非空且文件存在） | `final/test-loader.txt` |
| A-02 | `p112_a_snapshot_length_matches_the_claim` / `_is_the_exact_preflighted_bytes` | PASS（`sha256(快照) == claim.Digest`） | 同上 |
| A-03 | `p112_a_snapshot_is_not_the_declared_path` | PASS | 同上 |
| A-04 | `p112_a_snapshot_fingerprint_is_recorded` | PASS（`sha256:` 或 `sha1:`） | 同上 |
| A-05 | `p112_a_replaced_entry_with_same_length_identity_is_not_loaded` | PASS（仍 `Initialized`，无 `MF0203`） | 同上 |
| A-06 | `p112_a_replaced_entry_content_is_not_executed` | PASS（标记 `\|A`） | 同上 |
| A-07 | `p112_a_replaced_private_assembly_is_not_loaded` | PASS | 同上 |
| A-08 | `p112_a_modified_snapshot_is_refused` + `_reason_names_the_snapshot` | PASS（`MF0202` + "could not be snapshotted …"；无入口执行） | 同上 |
| A-09 | `p112_a_preflight_short_read_is_rejected` | PASS（`MF0205`） | 同上 |
| A-10 | `p112_a_growth_during_read_loads_only_the_declared_length` | PASS | 同上 |
| A-11 | `p112_a_shared_artifact_is_loaded_once` / `_is_reported_as_shared` | PASS（1 条 load 行；`preflight-shared=`） | 同上 |

## 6. 主题 B：ContentIdentity 分支（A）

| ID | 检查 | 结果 |
| --- | --- | --- |
| B1-01 | `p112_b_left_open_failure_is_unverifiable` / `_right_…` | PASS |
| B1-02 | `p112_b_read_failure_after_bytes_is_unverifiable` | PASS |
| B1-03 | `p112_b_legal_partial_reads_are_same` | PASS |
| B1-04 | `p112_b_early_eof_is_unverifiable` | PASS |
| B1-05 | `p112_b_length_mismatch_is_unverifiable` | PASS |
| B1-06 | `p112_b_extra_bytes_is_unverifiable` | PASS |
| B1-07 | `p112_b_different_bytes_are_different` / `_identical_bytes_are_same` | PASS |
| B1-08 | `p112_b_trygetlength_open_failure_is_false` / `_length_failure_is_false` / `_reason_is_path_free` | PASS |
| B1-09 | `p112_b_dispose_failure_keeps_same` / `_keeps_different` | PASS |
| B1-10 | `p112_b_mod_caller_rejects_unverifiable_content` / `_loads_nothing` | PASS（双方 `MF0206`） |
| B1-11 | `p112_b_host_caller_rejects_unverifiable_content` / `_not_reported_as_shared` | PASS（`MF0207`） |

## 7. 主题 C：Host 正向共享（A）

| ID | 检查 | 结果 |
| --- | --- | --- |
| C1-01 | `p112_c_host_claim_share_from_host` | PASS |
| C1-02 | `p112_c_host_claim_same_instance`（`ReferenceEquals`） | PASS = **true** |
| C1-03 | `p112_c_host_claim_points_at_the_host_image` | PASS |
| C1-04 | `p112_c_host_claim_identity_matches_loaded_instance` | PASS |
| C1-05 | `p112_c_host_share_initializes` / `_reported` | PASS |
| C1-06 | `p112_c_host_share_loads_no_second_copy`（AppDomain 计数） | PASS = 1 |
| C1-07 | `p112_c_host_share_issues_no_load_from` | PASS（0 条对应 load 行） |
| C1-08 | `p112_c_host_without_instance_is_rejected` | PASS（`MF0207`） |

## 8. 主题 D：ECMA-335 Flags/Blob 矩阵（A）

| ID | 检查 | 结果 |
| --- | --- | --- |
| D-01 | `p112_d_unsigned_is_accepted_and_not_strong_named` | PASS |
| D-02 | `p112_d_eight_byte_token_is_accepted` / `_token_describe_is_consistent` | PASS |
| D-03 | `p112_d_seven_byte_token_is_rejected` / `_nine_byte_…` | PASS（拒绝） |
| D-04 | `p112_d_public_key_flag_without_blob_is_rejected` | PASS（拒绝） |
| D-05 | `p112_d_public_key_flag_over_a_token_is_rejected` | PASS（拒绝） |
| D-06 | `p112_d_public_key_flag_over_a_truncated_header_is_rejected` | PASS（拒绝） |
| D-07 | `p112_d_full_public_key_is_accepted` / `_token_matches_ecma_335` | PASS（真实 RSA 公钥；token 与 BCL SHA-1 一致） |
| D-08 | `p112_d_full_public_key_agrees_with_assembly_name_api` | PASS（`AssemblyName.SetPublicKey/GetPublicKeyToken` oracle 一致） |
| D-09 | `p112_d_zero_filled_key_blob_is_accepted_as_representation_only` | PASS（显式记录"只校验表示"） |
| D-10 | `p112_d_retargetable_flag_is_accepted` / `_windows_runtime_content_type_…` / `_jit_flags_…` | PASS（无误杀） |
| D-11 | `p112_d_unsigned_and_token_signed_are_different_identities` | PASS |

## 9. 主题 E：不动点（A）

| ID | 检查 | 结果 |
| --- | --- | --- |
| E-01 | `p112_e_stable_graph_converges` / `_converges_in_one_round` | PASS（`preflight-rounds=1`） |
| E-02 | `p112_e_stable_graph_loads_every_mod` | PASS（3/3 `Initialized`） |
| E-03 | `p112_e_stable_graph_preflight_covers_the_pool` | PASS（`preflight=assemblies=6 conflicts=0`） |
| E-04 | `p112_e_conflict_dependency_cycle_is_reported_as_exhausted` | PASS |
| E-05 | `p112_e_cycle_loads_nothing` / `_reports_a_stable_reason` | PASS（无入口执行；`MF0208`；`preflight-exhausted-reason=`） |
| E-06 | `p112_e_round_bound_exhaustion_loads_nothing` / `_is_reported` / `_rejects_every_candidate` | PASS（`roundLimit=1` → `exhausted` → 全员 `MF0208`） |
| E-07 | `p112_e_direct_conflict_converges_and_rejects_both` | PASS（`converged` + `MF0206`） |
| E-08 | `p112_e_multi_conflict_group_rejects_every_owner` | PASS（3/3 `MF0206`） |
| E-09 | `p112_e_no_load_before_the_preflight_completes` | PASS（`preflight-stage=complete` 行号 < 首条 `assembly-load=`） |
| E-10 | `p112_e_classification_is_independent_of_discovery_order` | PASS（正反目录序签名一致） |

## 10. 静态审查结论（C）

| ID | 结论 |
| --- | --- |
| S-01 | `Assembly.LoadFrom` 只在 `Loader.TryResolveClaim` 一处调用，且必在 `preflight-stage=complete` 之后 |
| S-02 | 所有 `TryRead` 的 claim 只有在快照成功后才可能进入 `TryResolveClaim`；无快照即拒绝（无"按路径兜底"分支） |
| S-03 | 新增 seam 全部 `internal`：`Loader.BeforeLoadForTests`、`Loader.RoundLimitOverrideForTests`、`AssemblyClaimReader.OpenForTests`；生产代码没有 mod 可配置的"跳过校验"开关 |
| S-04 | `ManagedAssemblyReader` 的 Flags/Blob 解释集中在 `TryClassifyKeyBlob`，`IsStrongNamed` / `Describe` / `AssemblyIdentityComparison` 消费同一结果，无第二套解释 |
| S-05 | 不动点的三集合是下一轮输入的全部，因此状态签名重复是收敛性的充分判据 |
| S-06 | 未修改 Phase 1.1.1 的历史报告与索引；未修改与五个主题无关的功能 |

## 11. 不可执行项（BLOCKED / NOT RUN）

| ID | 项 | 状态 | 原因 |
| --- | --- | --- | --- |
| X-01 | 快照文件的"校验→加载"原子性 | **BLOCKED** | 需要 `Assembly.Load(byte[])`（C-03 证明不存在）或运行时句柄加载 API |
| X-02 | AArch64 Mono 运行期语义 | **BLOCKED** | 无 ARM64 设备；模拟器结论不可外推 |
| X-03 | 远程 GitHub Actions | **NOT RUN** | 任务禁止 push；用户自行 Push 后由 CI 产出 |
| X-04 | adb 装机 / 真实 Mod 运行 | **NOT RUN** | 任务禁止安装与真实游戏目录改动 |
