# Phase 0.5.1 报告：审计收尾与证据质量修正

- 报告日期：2026-11-05
- 阶段：**Phase 0.5.1**
- 对应证据索引：`phase0.5.1-evidence-index.md`
- 前置报告：`phase0.5-report.md`、`phase0.5-evidence-index.md`、`phase0.5-device-checklist.md`

### 状态（三个维度分开）

| 维度 | 状态 | 依据 |
| --- | --- | --- |
| **Offline Audit Complete** | **是** | §2–§7 全部完成，每个结论都有本阶段可复核的日志（`.tmp/phase0.5.1/logs/`） |
| **Device Validation** | **BLOCKED: no ARM64 physical device** | 只有 MuMu 12（x86_64 + `libnb.so`），本阶段只做 E0 只读预检（C 级） |
| **Overall Phase 0.5** | **PARTIALLY COMPLETE**（不是 complete） | 真机 E1/E2 仍缺；模拟器历史记录是 D 级 |

> 本阶段**没有**重跑 E1/E2，**没有**安装/卸载/替换设备上的游戏，**没有** commit/push。
> Gate 0 的全部条目已满足（§8），因此 Phase 1 得以开始；Phase 1 的设备验收同样保持 BLOCKED。

---

## 1. 基线（冻结）

| 项 | 值 |
| --- | --- |
| 仓库根 | `D:\Download\DSH\StardewValley`（`git rev-parse --show-toplevel` 确认） |
| remote | `origin https://github.com/Hiyomi-Kuro/StardewValley.git` |
| branch | `main`（`main...origin/main`） |
| HEAD（开始） | `b51af45b14a9574639fa3cefe7250d86cca9ad35` |
| `origin/main`（`git ls-remote`，只读） | `b51af45b14a9574639fa3cefe7250d86cca9ad35` → **与任务书给的快照一致，远程未前进** |
| 工作区（开始） | **干净**：0 modified / 0 untracked（只有 `.gitignore` 覆盖的 `.tmp/ .work/ .gradle/ .kotlin/ app/build/ __pycache__/ AGENTS.md`） |
| HEAD（结束） | 同 `b51af45`（**未提交任何东西**） |
| 工作区（结束） | 16 个已跟踪文件被修改 + 新增文件（清单见 §9） |

结论：**没有需要保护的既有用户改动**——开始时工作区是干净的，结束时全部差异都来自本阶段。
`git diff --stat` = `16 files changed, 1292 insertions(+), 202 deletions(-)`（未计新增文件）。

### 1.1 执行环境

| 项 | 值 |
| --- | --- |
| OS / shell | Windows；Windows PowerShell **5.1**（本机没有 `pwsh` 7） |
| Python | 3.12.10 |
| .NET SDK | 项目私有 `8.0.404`，位于 `.tmp/phase0.5/dotnet/`（**未改 PATH、未装全局 SDK**） |
| 游戏程序集 | `.tmp/phase0.5/refs/` 65 个 dll（已存在，未重新提取） |
| 设备 | MuMu 12 模拟器，Android 12 / API 32，`ro.product.cpu.abi=x86_64`，`ro.dalvik.vm.native.bridge=libnb.so` → `NOT-ARM64-PHYSICAL` |

---

## 2. 审计问题 1：历史证据（D 级）被写成"当前 PASS"

### 2.1 根因

`phase0.5-evidence-index.md` §0 明确规定 D 级 = "Phase 0 记录、原始日志已丢失、不可独立复现"，
但若干文档仍把 Phase 0 的**模拟器**结果直接写成 `PASS`：

- `phase0.5-report.md` §7.2 / §9 状态矩阵：`PASS（Phase 0 记录 …）`；
- `phase0-status.md` §3.13 把模拟器运行写成"注入代码在**真机**运行时执行"；
- `phase0-status.md` §3.22 能力矩阵全是 `✅`，没有等级；
- `adr-0001-mod-injection.md` §6.1/§6.2：写成"通过（模拟器）"，状态列直接写 PASS；
- `runtime-feasibility.md` §10：状态列直接写 PASS；
- `phase0-report.md` §4：`✅ 通过（模拟器）`；
- `evidence-index.md` §5：`8/8 PASS` 没有标注原始文件已丢失；
- `phase0.5-device-checklist.md` §6/§7：`PASS（Phase 0 记录）`；
- `Mod/MonoRuntimeProbe/README.md`：`T00–T07、T11 PASS`。

### 2.2 修正规则（已落地）

1. 历史模拟器结果一律改写为
   **`RECORDED PASS (Evidence D; original logs unavailable; not independently reproducible)`**；
2. 只有本阶段直接读取的 adb 输出才记 C 级；
3. 模拟器列与真机列严格分列，模拟器值不得填进 Device 列；
4. 无法找到原始日志支持的数字（`Game1 576/532`、T12 崩溃栈）标注
   `Historical Record / Not independently verified`；
5. 只有"编译期"可复核的结论保留 ✅ 并标注 **A（编译期）**。

### 2.3 修改文件

| 文件 | 修改 |
| --- | --- |
| `docs/audit/phase0.5-report.md` | 顶部加"三维度状态"+ 证据等级口径；§7.2 全表改为 RECORDED PASS (D)；§9 状态矩阵同上；§1 结论区分"源码/编译期成立"与"运行时只有 D 级记录"；§11 标注 27/27 的等级为 B 且日志可删 |
| `docs/audit/phase0-status.md` | §3.13 删掉"真机"错误表述并加更正块；§3.22 能力矩阵加"证据等级"列（编译期 A / 记录 D）；§3.22 门控判定改为 D 级；§3 阻塞表改为 RECORDED PASS (D)；§5 门控判定补充 Phase 1 的准入解释 |
| `docs/audit/phase0.5-device-checklist.md` | §6 总表、§7 结论表改为 RECORDED PASS (D)；§6 增加取值规则说明 |
| `docs/audit/phase0.5-evidence-index.md` | §0 增加 D 级必须写成 RECORDED PASS 的硬规则；结论映射表改写 |
| `docs/audit/phase0-report.md` | §4 门控表改为 RECORDED PASS (D)，并说明"Phase 0 结束时未启动 Phase 1" |
| `docs/audit/evidence-index.md` | §5 探针证据标注"已丢失 + RECORDED 8/8 PASS (Evidence D) + 模拟器非真机" |
| `docs/audit/adr-0001-mod-injection.md` | §6.1/§6.2 改为 RECORDED PASS (D)；补充约束逐条标注 A（编译期）；ADR 仍 **Proposed** |
| `docs/audit/runtime-feasibility.md` | 报告状态、§10 状态列、变更记录改为 RECORDED PASS (D) |
| `Mod/MonoRuntimeProbe/README.md` | 状态块与用例表改为 RECORDED (D) |

### 2.4 证据

- CI 里加了一条**机器检查**：`docs/audit/*.md` 中不得出现"模拟器 PASS"而不带 `RECORDED`
  （`Tools/tests/test_ci_offline.py` → `no_current_run_pass_for_historical_evidence`）。
- 本阶段重新采集了 E0（C 级）：`.tmp/phase0.5.1/logs/e0/device-precheck.txt`
  → `abi=x86_64`、`native-bridge=libnb.so`、`device-class = NOT-ARM64-PHYSICAL`、`E1/E2: BLOCKED`。

---

## 3. 审计问题 2：`device-precheck.ps1` 硬编码个人路径

### 3.1 根因

第 43 行把 `C:\Users\<用户名>\AppData\Local\Android\Sdk\platform-tools\adb.exe` 写死为最后的候选，
既泄露个人目录，又让"找不到 adb"的情况永远不会触发可操作错误。

### 3.2 修复

- **删除**该硬编码；新增 `Resolve-Adb` / `Get-AdbCandidates` 解析链，优先级：
  1. `-Adb <path>`（显式参数，最高）
  2. `PATH`（`Get-Command adb`）
  3. `ANDROID_HOME`
  4. `ANDROID_SDK_ROOT`
  5. `%LOCALAPPDATA%\Android\Sdk`
  6. `ADB_PATH`（非常规布局的显式覆盖）
- 多个候选同时存在时：用最高优先级的那个，并打印 `adb-conflict` 与全部候选（不静默）。
- 找不到 adb：`verdict=BLOCKED reason=no-adb` + 可操作提示（PATH / ANDROID_HOME / -Adb），退出码 2。
- **输出脱敏**：`Redact-Path` 把 `%USERPROFILE%` / `%HOME%` / `C:\Users\<name>` 替换为 `<home>`。
- 新增 `-ResolveAdbOnly`：只做解析、不连设备，使该逻辑可被自动测试。
- 顺带修了一个真实缺陷：冷启动的 adb server 会让前几次 `getprop` 返回空
  （本阶段第一次跑 E0 时 `ro.product.cpu.abi` 等 6 个属性全空）。现在 `Adb-Prop` 最多重试 3 次。

### 3.3 测试

`Tools/tests/test_device_precheck.ps1`（10 项，全 PASS；每个用例在独立 pwsh 进程里用受控环境跑）：

| 用例 | 覆盖 |
| --- | --- |
| `adb_from_path` | PATH |
| `adb_from_android_home` | ANDROID_HOME |
| `adb_from_android_sdk_root` | ANDROID_SDK_ROOT |
| `adb_from_localappdata` | LOCALAPPDATA 回退 |
| `home_path_redacted` | 输出里是 `<home>`，不是真实用户目录 |
| `adb_missing_is_blocked` | 缺 adb → 退出码 2 + 可操作提示 |
| `no_guessed_user_directory` | 输出里不含任何"猜测出来的"用户目录 |
| `adb_conflict_reported` | 多候选冲突被报告，且优先级正确 |
| `explicit_adb_wins` | `-Adb` 覆盖 PATH |
| `no_hardcoded_user_path_in_script` | 脚本源码里没有 `C:\Users\<name>` |

证据：`.tmp/phase0.5.1/logs/device-precheck-selftest.txt`。

### 3.4 顺带修的另一个工具

`Tools/deploy-apk.ps1` 也有机器相关默认值，已改为：
`-Serial` 默认空（自动选中"唯一一台"设备，0 台或多台都报错并要求显式指定）、
adb 走同一套解析链、`JAVA_HOME` 可用 `-JavaHome` 覆盖。
**该脚本本阶段未执行**（会卸载+重装游戏），只做了语法检查。

---

## 4. 审计问题 3：ModInjector 未知命令输出乱码

### 4.1 根因

`Tools/ModInjector/Program.cs` 第 55 行：

```text
修复前该行是：Console.Error.WriteLine("[error] <6 个乱码码点>: " + command);
```

原始中文是 `未知命令`，其 **GBK 字节**被当成 UTF-8 解码，产生
`U+93C8 U+E046 U+7161 U+935B U+6212 U+62A4`——其中 `U+E046` 是**私用区字符**，
所以普通编辑器里既看不见也删不掉，用文本方式替换会失败。

### 4.2 修复

- 用字节级 Python 脚本（`.tmp/phase0.5/work/fix_mojibake.py`）按**码点**精确替换为
  ASCII 的 `unknown command`；同时断言"替换后文件里没有私用区字符"且 CRLF 未变。
- 全仓库扫描（`*.cs/*.csproj/*.props/*.ps1/*.py/*.md/*.json/*.yml`）：只有这 1 处。

### 4.3 验证

```
$ dotnet ModInjector.dll bogus-command
[error] unknown command: bogus-command        (stderr)
exit code 2
```

- `dotnet build Tools/ModInjector` → 0 warning / 0 error。
- 该文件 diff 只有 1 行。
- **防回归**：CI 新增 `no_mojibake_in_tracked_text`（检查私用区字符 + 24 个已知 mojibake 记号）
  与 `tracked_text_is_utf8`；`Tools/tests/fixtures/manifests/invalid.not-utf8.json` 是唯一豁免项
  （它的用途就是"非法 UTF-8 必须被拒绝"）。
- 运行时乱码 vs 仅注释乱码：本阶段只修了**运行时输出**这 1 处；`Mod/Directory.Build.props`
  与两个 csproj 的注释乱码（Phase 0.5 未决项 #9）仍保留，理由见 §10。

---

## 5. 审计问题 4：隐私扫描

新增 `Tools/privacy-scan.py`（只扫"会被发布"的文件：`git ls-files` + 未跟踪但未被 ignore 的），
分级规则：

| 级 | 规则 |
| --- | --- |
| CRITICAL | 私钥块、AWS/GitHub/OpenAI/Slack token、硬编码 password |
| HIGH | `C:\Users\<name>`、`/home/<name>`、`/Users/<name>` |
| MEDIUM | 邮箱、`SV_DEVICE_SERIAL` 环境变量给出的设备序列号 |
| INFO | 机器相关的绝对路径（仓库位置），只报告不失败 |

**结果**（`.tmp/phase0.5.1/logs/privacy-scan.txt`）：112 个文本文件，
**CRITICAL/HIGH = 0**，MEDIUM = 0，INFO = 5（都是 `docs/audit` 里的仓库绝对路径），
allow-list = 2。

两个 allow-list 条目都带理由（`Tools/privacy-allowlist.json`）：

1. `test_device_precheck.ps1` 里的合成 home 路径（`<tmp>` 下的 `home/AppData/...`）—— 自测构造，不是真实目录；
2. `test_device_precheck.ps1` 里的 Windows 用户目录前缀 —— 它是**断言字符串**，用来证明脚本不会打印猜测的用户目录。

真实泄露 vs 普通示例的判定：

| 命中 | 判定 |
| --- | --- |
| `com.kaori.svpatch`（Kotlin 包名） | **不是泄露**：作者自选命名空间，不是路径 |
| `Hiyomi-Kuro`（remote URL） | **不是泄露**：公开仓库属主 |
| `127.0.0.1:16384` | **不是泄露**：回环端口（MuMu 默认），不是设备序列号；已从 `deploy-apk.ps1` 的默认值里移除 |
| `product:PD2309 model:V2309A` | **不是泄露**：设备型号，不是序列号；作为 E0 证据保留 |
| `D:\Download\DSH\StardewValley` | INFO：仓库位置，已在历史文档里公开，不改写 |

---

## 6. 审计问题 5：CI

### 6.1 先分析依赖（不是先写 yaml）

| 测试 | 依赖 | 能否在公开 runner 跑 |
| --- | --- | --- |
| `Tools/tests/test_ci_offline.py` | 只要 Python + git | ✅ |
| `Tools/privacy-scan.py` | 只要 Python + git | ✅ |
| `Tools/tests/test_device_precheck.ps1` | 只要 PowerShell | ✅（ubuntu 自带 `pwsh`） |
| `Tools/tests/test_loader.py`（无 `--refs`） | .NET SDK + 源码 | ✅（示例 mod 部分 SKIP） |
| `Tools/ModInjector` 构建 | .NET SDK + NuGet（Mono.Cecil） | ✅ |
| `Tools/tests/test_mod_pipeline.py` | **游戏自带的 `StardewValley.dll` + 容器 blob** | ❌ local-only |
| `Mod/ExampleMod.HelloWorld` 构建 | `AndroidRefsDir`（65 个游戏程序集） | ❌ local-only |
| 任何 E1/E2 | ARM64 真机 | ❌ BLOCKED |

关键判断：`test_mod_pipeline.py` 需要的 `StardewValley.dll` 虽然可以从仓库里的 blob 解出来，
但**不在 CI 里解**——那等于在公开 runner 上展开完整游戏程序集。所以它被显式标为 local-only。

### 6.2 工作流

`.github/workflows/ci.yml`：

| job | 内容 |
| --- | --- |
| `offline-checks` | checkout → setup-python 3.12 → `test_ci_offline.py`（13 项）→ `privacy-scan.py` |
| `dotnet-tools` | checkout → setup-dotnet 8.0.x → 构建 ModInjector / Runtime / FixtureMod → `test_loader.py --dotnet dotnet`（无游戏程序集）→ 打印 local-only 的 SKIPPED 声明 |

- 触发：`push`（main）、`pull_request`、`workflow_dispatch`。
- `permissions: contents: read`；`concurrency` 取消同 ref 的旧运行；两个 job 都有 `timeout-minutes`。
- 只使用官方 action，且**固定到具体版本**（`checkout@v4.2.2`、`setup-python@v5.3.0`、`setup-dotnet@v4.1.0`）。
- **不上传任何 artifact**、不读 `secrets`（CI 检查里有断言）。
- 不依赖 `.tmp/`：CI 里没有任何 `.tmp/phase0.5` 路径。

### 6.3 本地校验

- `test_ci_offline.py` 的 `ci_workflow_static`：用 PyYAML 解析并断言
  "三个触发器齐全 / `permissions.contents=read` / 每个 job 有 `runs-on` 与 `timeout-minutes` /
  所有 `uses:` 都带 `@` / 不含 `upload-artifact` 与 `secrets.`"。
  没有 PyYAML 时退化为行级检查并在输出里说明覆盖度。
- 两个 job 的**每一条命令**都在本机跑过（见 §7 的测试矩阵）。
- **远程 Actions 未运行**（本阶段没有 commit/push）→ 状态是
  **"配置完成 + 本地可执行，等待远程触发"**，**不是 CI PASS**。

---

## 7. 测试矩阵（本阶段实际执行）

全部日志在 `.tmp/phase0.5.1/logs/`（`run-all.ps1` 生成）。

| # | 命令 | 结果 | 退出码 | 日志 |
| --- | --- | --- | --- | --- |
| 1 | `python Tools/tests/test_ci_offline.py` | **13/13 PASS** | 0 | `ci-offline.txt` |
| 2 | `python Tools/privacy-scan.py --include-untracked` | 0 CRITICAL/HIGH；2 allow-listed | 0 | `privacy-scan.txt` |
| 3 | `powershell -File Tools/tests/test_device_precheck.ps1` | **10/10 PASS** | 0 | `device-precheck-selftest.txt` |
| 4 | `python Tools/tests/test_mod_pipeline.py` | **27/27 PASS** | 0 | `test-mod-pipeline.txt` |
| 5 | `python Tools/tests/test_loader.py --refs …` | 9/9 驱动检查 + 内部 **125/125 PASS** | 0 | `test-loader.txt` |
| 6 | `git diff --check` | 无空白错误 | 0 | `git-diff-check.txt` |
| 7 | `dotnet build Tools/ModInjector` | 0 warning / 0 error | 0 | 见 #4 日志 |
| 8 | `dotnet build Mod/ModFramework.Runtime`（桌面） | 0 / 0 | 0 | 见 #5 日志 |
| 9 | `dotnet build Mod/ModFramework.Runtime -p:AndroidRefsDir=…` | 0 / 0 | 0 | 见 #4 日志 |
| 10 | `dotnet build Mod/ExampleMod.HelloWorld -p:AndroidRefsDir=…` | 0 / 0 | 0 | 见 #5 日志 |
| 11 | `dotnet build Mod/MonoRuntimeProbe -p:AndroidRefsDir=…` | 0 error / 1 warning（`CS0169 _staticCounter`，反射用，刻意保留） | 0 | `build-probe.txt` |
| 12 | `powershell -File Tools/device-precheck.ps1`（E0 只读） | E0 PASS（C 级）；E1/E2 BLOCKED | 0 | `e0/device-precheck.txt` |
| 13 | `powershell -File Tools/tests/run-all.ps1 -Refs …` | 6 suites / 6 PASS | 0 | 汇总输出 |

### 7.1 关于"27 项测试"的核实

Phase 0.5 报告写的 "27/27 PASS" 确实来自该阶段的日志
（`.tmp/phase0.5/logs/test-mod-pipeline.final.txt`，仍在），**等级 B**。
本阶段**重新执行**了同一个脚本，仍然是 27/27（`test-mod-pipeline.txt`），
所以这条从"当时通过"变成"当前可复核"。

### 7.2 NOT RUN（如实标注）

| 项 | 状态 | 原因 |
| --- | --- | --- |
| 远程 GitHub Actions 运行 | **NOT RUN** | 未 commit/push（本阶段不允许远程写） |
| `adb install` / 卸载 / 覆盖安装 | **NOT RUN** | 未获设备所有者授权；卸载会删存档 |
| E1/E2（模拟器重跑） | **NOT RUN** | 需要装机授权 |
| E1/E2（ARM64 真机） | **BLOCKED** | 无物理设备 |
| `Tools/deploy-apk.ps1` 实跑 | **NOT RUN** | 会卸载+重装游戏 |

---

## 8. Gate 0 检查表

| # | 门禁项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 1 | 证据 D 与当前直接证据严格区分 | ✅ | §2；CI 机器检查 `no_current_run_pass_for_historical_evidence` |
| 2 | Phase 0.5 总状态 / 模拟器 / 真机三者一致且不夸大 | ✅ | §2、`phase0.5-report.md` 顶部三维度表 |
| 3 | `device-precheck` 无个人路径且有自动定位测试 | ✅ | §3；10/10 PASS |
| 4 | ModInjector 未知命令输出无乱码、编码可复现 | ✅ | §4；`[error] unknown command` + 退出码 2 |
| 5 | 隐私扫描无未解释的真实秘密/个人路径 | ✅ | §5；0 CRITICAL/HIGH，2 条有理由的 allow-list |
| 6 | CI 工作流已建立、不依赖未提交的 `.tmp`、本地校验通过 | ✅ | §6；`ci_workflow_static` PASS |
| 7 | 相关离线测试、构建、diff 检查通过 | ✅ | §7（13 + 10 + 27 + 125 项） |
| 8 | 本阶段改动与原有用户改动可区分 | ✅ | §1：开始时工作区干净；§9 列出全部改动 |

**Gate 0 = PASS**（无 BLOCKED 项）→ 允许进入 Phase 1。Phase 1 的设备部分仍记 BLOCKED。

---

## 9. 本阶段改动清单

### 9.1 修改（已跟踪文件，16 个）

| 文件 | 改动 |
| --- | --- |
| `Tools/ModInjector/Program.cs` | 乱码 → `unknown command`（1 行） |
| `Tools/device-precheck.ps1` | adb 解析链、脱敏、`-ResolveAdbOnly`、属性重试 |
| `Tools/deploy-apk.ps1` | 去掉硬编码 serial / adb / JAVA_HOME 默认值 |
| `docs/audit/phase0.5-report.md` | 证据等级修正 + 三维度状态 |
| `docs/audit/phase0.5-evidence-index.md` | D 级硬规则 |
| `docs/audit/phase0.5-device-checklist.md` | 总表/结论表 RECORDED |
| `docs/audit/phase0-status.md` | §3.13 更正、§3.22 等级列、§3/§5 门控 |
| `docs/audit/phase0-report.md` | §4 门控表 |
| `docs/audit/evidence-index.md` | §5 探针证据 |
| `docs/audit/adr-0001-mod-injection.md` | §6.1/§6.2 RECORDED + 等级标注 |
| `docs/audit/runtime-feasibility.md` | 状态/§10/变更记录 |
| `Mod/MonoRuntimeProbe/README.md` | 状态块 + 用例表 |
| `Mod/ModFramework.Runtime/Loader.cs` | Phase 1 重写（见 `phase1-report.md`） |
| `Mod/ModFramework.Runtime/ModFramework.Runtime.csproj` | 版本号 + `InternalsVisibleTo` |
| `Mod/ExampleMod.HelloWorld/ModEntry.cs` | 改成 `IModEntry` 实现（见 `phase1-report.md`） |
| `Mod/ExampleMod.HelloWorld/ExampleMod.HelloWorld.csproj` | 引用契约 + 复制 manifest |

### 9.2 新增

| 文件 | 用途 |
| --- | --- |
| `.github/workflows/ci.yml` | 公开 CI（离线部分） |
| `Tools/privacy-scan.py` / `Tools/privacy-allowlist.json` | 隐私/秘密扫描 + 有理由的豁免 |
| `Tools/tests/test_ci_offline.py` | CI 可跑的 13 项检查 |
| `Tools/tests/test_device_precheck.ps1` | adb 解析自测（10 项） |
| `Tools/tests/test_loader.py` | Loader 测试驱动 |
| `Tools/tests/run-all.ps1` | 一键跑全部本地套件 |
| `docs/audit/phase0.5.1-report.md` / `phase0.5.1-evidence-index.md` | 本阶段报告与证据索引 |

（Phase 1 的新增文件见 `phase1-report.md` §7。）

---

## 10. 未解决 / 保留项

| # | 项 | 状态 | 说明 |
| --- | --- | --- | --- |
| 1 | ARM64 真机 E1/E2 | **BLOCKED** | 唯一硬阻塞；需要物理设备 + 装机授权 |
| 2 | 远程 CI 结果 | NOT RUN | 未 push；工作流已本地校验 |
| 3 | `Mod/Directory.Build.props` 与两个 csproj 的**注释**乱码 | 保留 | 纯注释、无功能影响；改它会产生大 diff 且再次踩 PowerShell 往返坑。CI 现在会拦住**新的**乱码 |
| 4 | `docs/audit` 里的仓库绝对路径（INFO ×9） | 保留 | 历史文档已公开该路径，改写会让"当时的命令"不可复现 |
| 5 | 补丁 DLL 大小 9,261,568 vs 记录 9,261,056 | 仍未解释 | 不影响容器校验；文档已声明"注入产物大小不是身份" |
| 6 | `Game1` 576/532 口径差 | 仍未复核 | 需设备或元数据脚本；已标 D 级 |

---

## 11. 本阶段没有做的事

- 没有 `git commit` / `push` / Release / 改远程分支。
- 没有 `git reset` / `git clean` / 强制 checkout。
- 没有卸载、安装、替换设备上的游戏；没有碰 `Saves/`。
- 没有安装 NDK / 全局 .NET SDK / 任何新的大型依赖（只用了已有的私有 SDK 与 Python 标准库 + 已有的 PyYAML）。
- 没有把模拟器结果写成真机结果。
