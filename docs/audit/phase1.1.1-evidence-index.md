# Phase 1.1.1 证据索引

- 阶段：**Phase 1.1.1**（Assembly Preflight Correctness & Security Hardening）
- 报告：[`phase1.1.1-report.md`](phase1.1.1-report.md)
- 上一阶段：[`phase1.1-report.md`](phase1.1-report.md) / [`phase1.1-evidence-index.md`](phase1.1-evidence-index.md)
- 基线：`675b05807454ad7be9125672e71e83423a7e64dc`（`main` = `origin/main`）
- 生成时间：2026-11-05（本地，UTC+08:00）
- 运行环境：Windows，仓库私有 SDK `.tmp/phase0.5/dotnet/dotnet.exe`（8.0.404），Python 3.12.10，65 个参考程序集
- 原始日志目录（**仓库外**，DSH 任务目录）：`<task>\logs\`

> 本索引只登记**本阶段实际执行过**的命令。没有执行的项写 `NOT RUN` / `BLOCKED`，
> 不用"预计/应该/理论可行"充当证据。

---

## 0. 证据等级定义

| 级 | 含义 | 可复现 |
| --- | --- | --- |
| **A** | 本机本次实际运行得到的测试结果（有 stdout/退出码/日志） | ✅ 同一命令可重复 |
| **B** | 本机本次实际执行的**编译期**证据（编译成功/失败即结论） | ✅ 同一命令可重复 |
| **C** | 对源码/报告/配置的静态审查结论（不运行任何东西） | ✅ 人工可复核 |
| **D** | 历史阶段的记录（Phase 0 / 1 / 1.1 当时留下，本阶段未重跑） | ❌ 不可复现 |
| **BLOCKED** | 需要本机不具备的条件（ARM64 物理设备、装机授权） | ❌ 当前不可执行 |

本阶段所有 P111 门禁只接受 A/B/C 级证据；D 级与 BLOCKED 不得升格为 PASS。

---

## 1. 基线与工作区（A / C）

| ID | 命令 | 退出码 | 结果 | 级 |
| --- | --- | --- | --- | --- |
| B-01 | `git rev-parse --show-toplevel` | 0 | `D:/Download/DSH/StardewValley` | A |
| B-02 | `git remote -v` | 0 | `origin https://github.com/Hiyomi-Kuro/StardewValley.git` | A |
| B-03 | `git rev-parse --abbrev-ref HEAD` | 0 | `main` | A |
| B-04 | `git rev-parse HEAD` | 0 | `675b05807454ad7be9125672e71e83423a7e64dc` | A |
| B-05 | `git rev-parse refs/remotes/origin/main` | 0 | `675b05807454ad7be9125672e71e83423a7e64dc`（未前进） | A |
| B-06 | `git status --short --branch` | 0 | `## main...origin/main`（无 dirty / 无 untracked） | A |
| B-07 | `git ls-files --others --exclude-standard` | 0 | 空（0 个 untracked） | A |
| B-08 | 私有 SDK `dotnet --version` | 0 | `8.0.404` | A |
| B-09 | `Get-ChildItem .tmp/phase0.5/refs -Filter *.dll` | 0 | 65 个程序集 | A |

只读核对了本地 `origin/main` 引用；没有执行 `git fetch` / `pull` / `rebase` / `reset` / `clean`。

---

## 2. Red 基线：新增测试在 Phase 1.1 源码上失败（A）

命令（源码 = Phase 1.1 基线 + 仅新增内部测试 seam + 新测试文件）：

```
python Tools/tests/test_loader.py --dotnet .tmp/phase0.5/dotnet/dotnet.exe --refs .tmp/phase0.5/refs
```

| ID | 结果 | 日志 |
| --- | --- | --- |
| R-01 | 退出码 **1**；`283 checks, 251 passed, 32 failed`（驱动 `27 checks, 25 passed, 2 failed`） | `RED-baseline.txt` |
| R-02 | 32 项失败**全部为目标行为断言失败**，无编译错误、无 fixture 构建失败、无环境缺失 | 同上 |
| R-03 | seam 先行验证：只加 3 个 inert seam（digest override / framework-version unavailable / host snapshot）后，基线套件仍 `206 checks, 206 passed` + `19 checks, 19 passed` | `gate1-seams-inert.txt` |

失败分布（按缺陷）：A 4 项、B 3 项、C 5 项、D 10 项、E 4 项、F 2 项，另加 `test_host_exit_code` / `test_host_summary` 两条聚合断言。

---

## 3. Green：修复后的全量结果（A）

| ID | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| G-01 | `python Tools/tests/test_loader.py --dotnet … --refs …` | 0 | `283 checks, 283 passed, 0 failed`；驱动 `28/28` | `GREEN-after-fix.txt` / `gate4-final-verify.txt` |
| G-02 | `python Tools/tests/test_mod_pipeline.py --dotnet … --refs …` | 0 | `27 checks, 27 passed, 0 failed` | `gate3-mod-pipeline.txt` |
| G-03 | `python Tools/tests/test_ci_offline.py` | 0 | `13 checks, 13 passed` | `gate3-ci-offline.txt` |
| G-04 | `python Tools/privacy-scan.py --include-untracked` | 0 | `CRITICAL/HIGH 0`、`MEDIUM 0`、`INFO 11`、`RESULT: OK` | `gate3-privacy.txt` |
| G-05 | `powershell -File Tools/tests/test_device_precheck.ps1` | 0 | `10 checks, 10 passed` | `gate3-device-precheck.txt` |
| G-06 | `powershell -File Tools/tests/run-all.ps1 -Refs …` | 0 | `6 suites, 6 passed, 0 failed, 0 skipped` | `gate4-run-all-final.txt` |
| G-07 | 风格检查（`git diff --check`、`py_compile`×5、PowerShell 解析×4、schema JSON） | 0 | 全部 OK | `gate3-style-checks.txt` |

`run-all` 各套件耗时（源码冻结后的最终一次运行）：ci-offline 1.0s、privacy 0.4s、device-precheck 10.2s、mod-pipeline 42.0s、loader 80.6s、git-diff-check 0.1s。loader 套件内部：宿主 `283/283`、驱动 `28/28`、差分 `65（签名 58）`。

---

## 4. 目标引用编译（B）

| ID | 命令 | 退出码 | 结果 |
| --- | --- | --- | --- |
| B-10 | `dotnet build Mod/ModFramework.Runtime -c Release -p:AndroidRefsDir=.tmp/phase0.5/refs` | 0 | 0 warning / 0 error（`runtime_compiles_against_android_refs`） |
| B-11 | `dotnet build Mod/ExampleMod.HelloWorld -c Release -p:AndroidRefsDir=…` | 0 | 0 warning / 0 error，并在宿主里 `Initialized`（`hello_world_build` / `hello_world_host_run`） |
| B-12 | 同上，**反向**证据：`AssemblyName.GetPublicKey()` 在 AndroidRefsDir 下 `CS1061` | 1 | 这就是 `PublicKeyToken.cs` 存在的原因（报告 §3 C） |

**这两条只证明"API 在裁剪后的引用集里存在/不存在"，不等于 Mono 设备运行通过。**

---

## 5. 65 程序集解析差分（A）

```
dotnet .tmp/phase0.5/artifacts-…/ModFramework.Tests.dll --diff-refs .tmp/phase0.5/refs
refs-differential: 65 files present
  compared        : 65 (strong-name signed: 58)
  identity mismatch: 0
  token mismatch  : 0
  rejected by us  : 0
  not managed (BCL): 0
RESULT: OK
```

| ID | 结论 | 级 |
| --- | --- | --- |
| D-01 | 65/65 的 Name / Version / Culture 与 `AssemblyName.GetAssemblyName` 一致 | A |
| D-02 | 65/65 的 `PublicKeyToken` 与 BCL 一致，其中 **58 个为强名称签名**（验证 `Assembly.Flags` 解释与 SHA-1 Token 派生） | A |
| D-03 | 隐私：只输出计数、文件名、错误类型；未复制/未哈希入库/未打印目录 | C（脚本审查） |

---

## 6. 回归保护：Phase 1.1 原测试（A）

Phase 1.1 的 206 项宿主检查与 19 项驱动检查**全部包含**在本次 283/28 中，且全部 PASS：

| ID | 内容 | 结果 |
| --- | --- | --- |
| R-10 | LegacyProbe 不被加载（8 项） | PASS |
| R-11 | 版本门禁 Fail-Closed（19 项） | PASS |
| R-12 | 程序集预检冲突矩阵（22 项，含共享/宿主冲突/损坏/缺失/确定性） | PASS |
| R-13 | 日志降级（12 项） | PASS |
| R-14 | 单元层（SemVer/JSON/manifest/依赖图/发现/状态机/错误码/路径规则） | PASS |
| R-15 | 示例 mod 端到端（HelloWorld，需 `--refs`） | PASS |

唯一被**有意改写**的旧断言是 Phase 1.1 的 `metadata_digest_fallback_*` / `metadata_key_fingerprint_*`
（它们断言的正是"FNV-1a 64 回退"与"公钥指纹"，即本阶段要移除的行为），已改为断言
`metadata_digest_unavailable_is_empty` / `metadata_digest_has_no_weak_fallback` /
`metadata_identity_text_marks_unsigned`。

---

## 7. 映射到 Definition of Done（§4 测试矩阵）

| ID | 主题 | 证据 | 结果 |
| --- | --- | --- | --- |
| P111-01 | 依赖失败预传播 | `p111_a1_*`（A1：健康 C 不被拖累） | PASS |
| P111-02 | 预检后依赖传播 | `p111_a2_downstream_classified_as_dependency_failure`、`p111_a3_multi_level_chain_blocked` | PASS |
| P111-03 | 预检重新计算 | `p111_a2_unrelated_mod_survives_after_repreflight`、`_removed_downstream_no_longer_forms_a_conflict` | PASS |
| P111-04 | 确定性 | `p111_a5_classification_independent_of_discovery_order`、`conflict_deterministic_across_enumeration_order` | PASS |
| P111-05 | 内容身份（无 SHA 时逐字节） | `p111_b_no_sha256_identical_content_still_shares`、`p111_b_no_sha256_different_content_rejected` | PASS |
| P111-06 | 摘要碰撞 | `p111_b_digest_collision_does_not_permit_sharing`、`p111_b_identical_bytes_share_even_with_weak_digest` | PASS |
| P111-07 | 公钥身份 | `p111_c_host_public_key_mismatch_is_a_conflict`、D-02（58 签名程序集 Token） | PASS |
| P111-08 | 宿主匹配（四项） | `p111_c_host_culture_mismatch_is_a_conflict`、`p111_c_snapshot_keeps_*`、`AssemblyIdentityComparison` 单元 | PASS |
| P111-09 | 宿主不可验证 Fail-Closed | `p111_c_unverifiable_host_content_rejected`、`_not_silently_shared`、`_reported_as_rejected` | PASS |
| P111-10 | 已加载程序集选择 | `p111_c_ambiguous_host_candidate_rejected`、`p111_c_snapshot_keeps_the_assembly_instance` | PASS |
| P111-11 | PE Optional Header | `p111_d_cli_directory_absent_*`、`p111_d_optional_header_too_short_*`、`p111_d_cli_header_size_truncated_*` | PASS |
| P111-12 | RVA / Section | `p111_d_rva_partially_backed_by_raw_data_*`、`p111_d_section_raw_pointer_outside_file_*`、`p111_d_section_table_truncated_*` | PASS |
| P111-13 | Metadata Stream | `p111_d_metadata_stream_outside_metadata_root_*`、`p111_d_unterminated_stream_name_*` | PASS |
| P111-14 | Heap / Table | `p111_d_string_terminator_outside_heap_*`、`p111_d_blob_length_outside_heap_*`、`p111_d_table_rows_outside_stream_*`、`p111_d_assembly_row_count_overflow_*` | PASS |
| P111-15 | Assembly Flags | D-02（58 个签名程序集的 Token 全部正确 = Flags 位解释正确） | PASS |
| P111-16 | Parser fuzz | `p111_d_fuzz_never_throws`、`p111_d_fuzz_is_deterministic`（1500 变异） | PASS |
| P111-17 | FrameworkVersion | `p111_e_unknown_framework_version_is_not_a_placeholder`、`_fails_closed`（`MF0506`）、`_report_states_framework_version_state`、`_mod_context_reports_unknown_*` | PASS |
| P111-18 | 隐私 / 脱敏 | `p111_f_report_contains_no_absolute_path`、`p111_f_mod_log_contains_no_absolute_path`、`p111_f_missing_root_diagnostic_contains_no_absolute_path`、G-04 | PASS |
| P111-19 | 既有回归 | §6（206 → 283 全通过） | PASS |
| P111-20 | 目标引用编译 | B-10 / B-11 | PASS |
| P111-21 | 文档/证据 | 本索引 + `phase1.1.1-report.md` + manifest-spec/loader-design/runtime-constraints/README 更新 | PASS |
| P111-22 | 远程 CI | **NOT RUN**（本地交付；等待用户自行 Push，本阶段不 push） | NOT RUN |

---

## 8. NOT RUN / BLOCKED（不得升格）

| 项 | 状态 | 原因 |
| --- | --- | --- |
| GitHub Actions（push 后） | **NOT RUN** | DSH 不 commit / 不 push；需用户 Push 后另起验收 |
| ARM64 物理设备 E1/E2 | **BLOCKED** | 无设备；不装机、不 `adb install`、不碰真实存档 |
| Mono 设备上的元数据读取 / SHA-256 / Token | **BLOCKED** | 只有编译期（B-10/B-11/B-12）与桌面（D-01/D-02）证据 |
| 签名程序集的端到端宿主复用 | 部分 | 仓库不放私钥，无签名夹具；由 D-02 + 单元规则覆盖 |
| 第三方 Mod 生态兼容性 | 未验证 | 不声称 |

---

## 9. 静态审查清单（C）

| ID | 检查 | 结论 |
| --- | --- | --- |
| C-01 | `Assembly.LoadFrom` 出现次数 | 生产代码**仅 1 处**（`Loader.cs` `TryResolveClaim`），只在不动点结束后可达 |
| C-02 | 生产代码是否仍用 FNV/CRC 做判定 | 否；`fnv`/`crc` 只出现在解释"已移除"的注释里 |
| C-03 | 是否仍有"仅凭 `HasPublicKey` 布尔值"判定 | 否；该字段已删除，比较只用公钥/Token 字节 |
| C-04 | 是否仍有"仅凭摘要相等"共享 | 否；摘要只做否定过滤，相等必须逐字节确认 |
| C-05 | 宿主内容不可验证时是否仍共享 | 否；`MF0207` + `preflight-host-unverifiable-rejected=` |
| C-06 | 报告/日志是否写绝对路径 | 否；`PathScrubber` + 末段/相对名策略，测试断言扫描 |
| C-07 | 测试是否会因 AppDomain 残留同名程序集假通过 | 已处理：冲突场景用独立程序集名；内容比较相关断言允许 `MF0206|MF0207` 两种等价拒绝 |
| C-08 | 生产代码是否引入测试专用开关 | 3 个 `*ForTests` seam 均为 `internal` + `InternalsVisibleTo("ModFramework.Tests")`，默认值等于生产行为（R-03 证明 inert） |
| C-09 | 是否新增 Mod 作者可见 API | 否（`IModEntry`/`IModContext`/`IModLogger` 未改） |
| C-10 | 是否新增第三方依赖 | 否；无 NuGet，SHA-1 为自实现（仅编码用途） |
