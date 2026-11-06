# Phase 1.1.4 证据索引

- 日期：2026-11-06
- 基线：`345d71280ce5c255c4b9db490732cd9af312c0aa`
- 配套报告：[`phase1.1.4-report.md`](phase1.1.4-report.md)
- 本文件的作用：**把"结论"与"可复核的输入"分开**。任何一条结论都能在这里找到它的命令、产物或文件哈希。

---

## 0. 证据等级（本阶段只用这五类）

| 等级 | 含义 | 能证明 | **不能**证明 |
| --- | --- | --- | --- |
| **Desktop .NET** | 在 Windows x64 的 .NET 运行时里真实运行（`Tools/tests/ModFramework.Tests`） | 代码逻辑、Loader 时序、报告内容、异常隔离 | MonoVM / Android 的任何行为 |
| **AndroidRefsDir** | 用游戏自带的 65 个程序集当参考程序集**编译**通过 | 用到的 BCL 成员在目标引用面里**存在** | 成员在设备上行为正确、算法实现未被裁剪 |
| **Emulator** | 真实模拟器进程运行，注明 ABI 与 native bridge | 该模拟器实例上的行为 | 原生 AArch64 行为（MuMu 是翻译层） |
| **ARM64 physical** | 真实物理 ARM64 设备运行 | 目标运行时真实行为 | — |
| **CI** | GitHub Actions 对**具体 commit** 的远程结果 | 该 commit 在该 runner 上通过 | 本地专属套件（缺游戏程序集/设备） |

**本阶段实际拿到的等级**：Desktop .NET ✅、AndroidRefsDir ✅、CI ✅（仅 `345d712`）、
Emulator ❌ NOT RUN（缺安装授权）、ARM64 physical ⛔ BLOCKED（无设备）。

> 铁律：`AndroidRefsDir PASS ≠ SHA256.HashData 在 MonoVM 可用`；
> `Loader host tests PASS ≠ Android Assembly.LoadFrom 语义 PASS`；
> `E0 PASS ≠ Mod 已安装或运行`；`Emulator PASS ≠ ARM64 physical PASS`。

---

## 1. 命令 → 结论 → 日志

运行日志保存在本阶段的 **DSH 任务目录**（`tasks/20261106-p114-probe/logs/`，不在仓库内、不入库）。
仓库里只保留命令、脚本与结论。

| # | 命令（在仓库根执行） | 退出码 | 结论 | 日志名 |
| --- | --- | --- | --- | --- |
| 1 | `python Tools/tests/test_loader.py --refs .tmp/p113/refs` | 0 | 45/45 驱动；宿主 473/473（含 P114 46 项） | `10-test-loader.txt` |
| 2 | `python Tools/tests/test_mod_pipeline.py --refs .tmp/p113/refs` | 0 | 27/27 | `31-mod-pipeline.txt` |
| 3 | `python Tools/tests/test_ci_offline.py` | 0 | 13/13 | `run-all-suites/ci-offline.txt` |
| 4 | `python Tools/tests/test_p114_probe.py` | 0 | 46/46 | `run-all-suites/p114-static-audit.txt` |
| 5 | `powershell -File Tools/tests/test_deploy_safe.ps1` | 0 | 39/39 | `run-all-suites/deploy-safe-fake-adb.txt` |
| 6 | `powershell -File Tools/tests/test_device_precheck.ps1` | 0 | 10/10 | `run-all-suites/device-precheck-selftest.txt` |
| 7 | `python Tools/privacy-scan.py --include-untracked` | 0 | `RESULT: OK` | `run-all-suites/privacy-scan.txt` |
| 8 | `powershell -File Tools/tests/run-all.ps1 -Refs .tmp/p113/refs -Dotnet <sdk>` | 0（修复后） | 8 套件全通过 | `30-run-all.txt` |
| 9 | `git diff --check` | 0 | 通过 | — |
| 10 | `dotnet build <6 个工程> -c Release -p:AndroidRefsDir=<abs .tmp/p113/refs>` | 0 | 6/6 编译通过 | `40-androidrefs-builds.txt` |
| 11 | 负向对照：`-p:AndroidRefsDir=<不存在的目录>` | 1（预期） | `CS0246` → 证明 refs 生效 | `41-androidrefs-negative-control.txt` |
| 12 | `powershell -File Tools/device-precheck.ps1 -OutFile <log>` | 0 | `E0: PASS (read-only)`，`device-class=NOT-ARM64-PHYSICAL` | `20-e0-precheck.txt` |
| 13 | 本地 APK 摘要 + `apksigner verify --print-certs` | 0 | sha256 `65e1e2af…`，signer `611c8fa4…` | `21-local-apk.txt` |
| 14 | `powershell -File Tools/deploy-apk-safe.ps1`（对真实目标跑默认只读路径） | 0 | `PRECHECK-ONLY ok`，0 设备写 | `22-deploy-gate-live-readonly.txt` |
| 15 | 只读扫描已构建 APK 的 zip 条目 | 0 | 3672 条目，无 P114/probe/framework | `23-apk-content-scan.txt` |

> `.tmp/phase0.5/dotnet/dotnet.exe` 在本机**不存在**。本阶段的 SDK 通过
> `$env:STARDEW_DOTNET` 指定（也可用 `--dotnet` 或 `Tools/find-dotnet.ps1` 自动发现）。
> 记录里不写任何用户专属绝对路径。

---

## 2. 本阶段文件清单与 SHA-256

`M` = 修改既有文件，`??` = 新增文件（尚未 `git add`）。基线 commit 是 `345d712`。

| 状态 | 文件 | 字节 | SHA-256 |
| --- | --- | --- | --- |
| `??` | `Mod/MonoRuntimeProbe/NativePageProbe.cs` | 2807 | `c84d328ad3ca4e413b0c079f479b4a06e64e81d8ad590a12965eb6256a61b236` |
| `??` | `Mod/P114.RuntimeProbe.Dependency/DependencyMarker.cs` | 959 | `2cbb95c3e130c3bbdca172bf77972a3fc5a6f4527adac0f3eb709ea0223c1728` |
| `??` | `Mod/P114.RuntimeProbe.Dependency/P114.RuntimeProbe.Dependency.csproj` | 1047 | `28dbcddaa0c9530c56bf75c3f8b84c130403508cdb9875e8ea2c40c4ee90a55c` |
| `??` | `Mod/P114.RuntimeProbe.Fixtures/FixtureMarker.cs` | 952 | `e6a03af8c2632055cd31d5aac1611b1e8d40c94d2b184eaf0a3ba64dd0b73c13` |
| `??` | `Mod/P114.RuntimeProbe.Fixtures/P114Fixture.csproj` | 2062 | `a7f1b03da115c353ba17a8c4bc0a72216287a2734aed18c999582f1af4f2f0f4` |
| `??` | `Mod/P114.RuntimeProbe/P114.RuntimeProbe.csproj` | 2555 | `8d7df8d76fbce55476011864dd4d990feb9fef58c48cf99cbf2f4749de39f38a` |
| `??` | `Mod/P114.RuntimeProbe/P114Entry.cs` | 6804 | `134539e8b3d0e61b72f72e72fbab4fe718a08da2373619285c0656fb3acb08db` |
| `??` | `Mod/P114.RuntimeProbe/README.md` | 8170 | `7c5556439293e6f2934dc6d6fbcf7b3dd2cf58b2ab7ccd759e9360c57578abff` |
| `??` | `Mod/P114.RuntimeProbe/SafeProbes.cs` | 20647 | `8b7e5fbd5ae788e073ac597482dcce1901078aa5754299c0292a3fb8a6293005` |
| `??` | `Mod/P114.RuntimeProbe/manifest.json` | 607 | `05b3bb87bb700e7fa222983c4823294e0ae8ae023e9928b21e82de4204eabc3a` |
| `??` | `Tools/deploy-apk-safe.ps1` | 24275 | `95aa9580f8428593994d2447776d52e0ea056ac2c703dd366cdb719dd99c3b08` |
| `??` | `Tools/find-dotnet.ps1` | 2722 | `0e08c9d5e212a1a8176cac56ebe5689f3ccaea84f4a0dd9b5b716ca6d107a20c` |
| `??` | `Tools/tests/ModFramework.Tests/Phase114Tests.cs` | 14721 | `82cf5326cad9e38fec1935756899e8cc6c8c128fbeb1fd08eeeb22f5503c3546` |
| `??` | `Tools/tests/test_deploy_safe.ps1` | 19122 | `ad60006c10dd59e588ddbe1f46f102e5c781f50090a9ed43c5fda2043cae8119` |
| `??` | `Tools/tests/test_p114_probe.py` | 14087 | `15328284757446d8d9e7401ed6a153351abb49f90fe175cb57d801ecb3ede6bb` |
| `??` | `Tools/tests/toolchain.py` | 3135 | `6e7b389f4e962f5307f4b7406d32b880def5a8054a6b061c4ac93a90cedf0ef7` |
| `??` | `docs/audit/phase1.1.4-report.md` | 34875 | `1bf567263fafcf5b71874e555e799ee9b23e63d233bc57cb0df8ae7e97bc4f08` |
| `M` | `.github/workflows/ci.yml` | 5886 | `1c8d1547b0e30cb7326ecacd0c806ac96b57fb5698ac9b4eff7ea447c2018fea` |
| `M` | `Mod/MonoRuntimeProbe.Host/Program.cs` | 815 | `8f2e7aebc48e6f34ae1eab1518766cb4f568ffa6829e7d93b3e16fa368780cd6` |
| `M` | `Mod/MonoRuntimeProbe/DetourProbe.cs` | 4201 | `d95bd427b7818900dcfe35f66dd5cd10ce173c142a36263f0420dbd0cd0e45ed` |
| `M` | `Mod/MonoRuntimeProbe/MonoRuntimeProbe.csproj` | 2509 | `869d6e651933598e4890d287b8fcac6e89ab2073f7863c6d180e6b15b1467d83` |
| `M` | `Mod/MonoRuntimeProbe/ProbeEntry.cs` | 9945 | `27d06e1daaa185a1e0fde264a76023a2f2996378f5c6095fa97a8f63c754486b` |
| `M` | `Mod/MonoRuntimeProbe/README.md` | 10988 | `f291eae07c23ec0a59b89deb64e107a36a564b216cbee925513e5c04c2de7392` |
| `M` | `README.md` | 17137 | `d5de78cc19e85e0577e0077778f852a75936497be40f6c7bfe4b43c0e86d54c4` |
| `M` | `Tools/README.md` | 12796 | `4ed8ea0d385ad81e23928a861bc5ca442b19f4d8147f9c4ad0052bdd0205d146` |
| `M` | `Tools/deploy-apk.ps1` | 11173 | `72e49099ed7b545b622bf657c87fc4261a6adf21a1ec2862802d528c9e32e89d` |
| `M` | `Tools/device-precheck.ps1` | 12811 | `037d2ea0c695e680560a18070437fc2d44defb5cd8e0737ba6154db0ce9db49b` |
| `M` | `Tools/tests/ModFramework.Tests/Program.cs` | 5032 | `dff3f7aece1d27d9180c3040297f22ad1e65b89d96b848fdaf5fcbbc5c8fd0aa` |
| `M` | `Tools/tests/run-all.ps1` | 4998 | `4e2f4336e9b908f6031037e0a1454ae172e00cb8cb49c052665c24018f475c0a` |
| `M` | `Tools/tests/test_loader.py` | 22434 | `8b036a60f4fdb3a8757a34c88830766fcbe838ef796a949ee68eb32a1571ded4` |
| `M` | `Tools/tests/test_mod_pipeline.py` | 15261 | `e227528464afdf25a30c6d01a1160cf393401bac2b8df104f818cad7e70cf2c5` |
| `M` | `docs/architecture/phase1-loader-design.md` | 23286 | `f4f3cc61b47908e4c06ca302f512a0c5604372f532b2fd947b7dd37b34ed1d0a` |
| `M` | `docs/architecture/phase1-runtime-constraints.md` | 19469 | `974d95340952878e4d29a10fd0a1cde9cfcad505975ed102d9328d2b4679f496` |
| `M` | `docs/audit/phase0.5-device-checklist.md` | 14671 | `f9e557d1ed2f1de83127a248556b97c6a36b6d72a3f9abb062e5829dcd9ac7c6` |
| `M` | `docs/modding/creating-a-mod.md` | 8248 | `8127293677edde9321806c63fecc33737df31fb23a9ec87e52b053c6e7ab4ea6` |

共 35 个文件（18 个修改 / 17 个新增）。

> 本文件（`docs/audit/phase1.1.4-evidence-index.md`）本身不计入上表：自引用哈希在写入后立刻失效。
> 需要核验它时用 `git status` / `git diff --stat`，或用 `Get-FileHash` 现算。
> 上表由 `git status --porcelain -uall` + `Get-FileHash -Algorithm SHA256` 生成，
> 状态列中 `M` 表示相对 `345d712` 的既有文件，`??` 表示尚未 `git add` 的新文件（本阶段不 commit）。

> 说明：`Tools/deploy-apk-safe.ps1`、`Tools/find-dotnet.ps1`、`Tools/tests/test_p114_probe.py`、
> `Tools/tests/test_deploy_safe.ps1`、`Tools/tests/toolchain.py`、`Tools/tests/ModFramework.Tests/Phase114Tests.cs`、
> `Mod/P114.RuntimeProbe*`、`docs/audit/phase1.1.4-*.md` 都是本阶段新增；
> 其余 `M` 文件只做与本阶段主题直接相关的定向修改。

---

## 3. 关键产物摘要（可复现）

P114 探针包（由 `Tools/tests/test_loader.py::build_p114_package()` 生成）：

| 文件 | 字节 | SHA-256 |
| --- | --- | --- |
| `manifest.json` | 607 | `05b3bb87bb700e7fa222983c4823294e0ae8ae023e9928b21e82de4204eabc3a` |
| `P114.RuntimeProbe.dll` | 22016 | `616f542f300f2a0e1922d2b442fd005c2301df465311b0182f4b46a066a22ca3` |
| `P114.RuntimeProbe.Dependency.dll` | 5120 | `8b7015adca482f9de9b973c817bcec3ade0835a1a4eb70421a0e3cdb64b37aad` |
| `probe-fixtures/same-a/P114Same.dll` | 4096 | `44f30a9ded86cfd6be4aab6f51b4a2fbc5ec72226290cdb163a6a2e99f9a9973` |
| `probe-fixtures/same-b/P114Same.dll` | 4096 | `44f30a9ded86cfd6be4aab6f51b4a2fbc5ec72226290cdb163a6a2e99f9a9973`（与 same-a **逐字节相同**） |
| `probe-fixtures/diff-a/P114Diff.dll` | 4096 | `f18fc85051bf16aa050db9a5ca4f3ad1c3bc5abc89cf2d4c625064b72d982704` |
| `probe-fixtures/diff-b/P114Diff.dll` | 4096 | `33de42ac1afbfd0dbd810fe44486d91a811681d458cd878dd2b37476d0a067ec`（与 diff-a **不同字节、同身份**） |
| `probe-fixtures/alpha/P114Alpha.dll` | 4096 | `89260acbe8e66d8854779432dfbf720b265070c59cde1c136fc855822a1d8ad4` |
| `probe-fixtures/beta/P114Beta.dll` | 4096 | `0c553a3bcaa9f6fe6445de52595124550d75f14949c6ff9072a7675b6c8a225d` |
| `probe-fixtures/corrupt/P114Broken.dll` | 64 | `f5a5fd42d16a20302798ef6ed309979b43003d2320d9f0e8ea9831a92759fb4b`（全 `0x00`，只给 R-14） |

交叉核对：`P114.RuntimeProbe.dll` 的 SHA-256 与运行时报告 R-13 的 `sha256=` **相同**，
也与 `loader-report.txt` 的 `assembly-load=… snapshot=sha256:` **相同** —— 这就是"加载的字节 =
预检批准的字节"的端到端证据。

---

## 4. 设备与 APK 事实（E0，只读）

| 项 | 值 |
| --- | --- |
| serial | `emulator-5554` |
| ABI / abilist | `x86_64` / `x86_64,arm64-v8a,x86,armeabi-v7a,armeabi` |
| native bridge | `libnb.so`（翻译层） |
| Android / API | 12 / 32 |
| device-class | `NOT-ARM64-PHYSICAL` |
| 包 / versionCode / versionName | `com.chucklefish.stardewvalley` / 239 / 1.6.15.0 |
| 设备端 APK SHA-256 | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| 本地 APK SHA-256（同值） | `65e1e2af…f9d`，388,967,580 字节 |
| 本地 APK 签名摘要 | `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de` |
| `Mods/` | 不存在 |
| E1/E2 | `BLOCKED reason=no-arm64-physical-device` |

---

## 5. 明确**不是**证据的东西

1. 本阶段**没有** Emulator 或 ARM64 设备上的探针运行日志——R-03/R-12 之外的设备观察一个都没有。
2. `Tools/privacy-scan.py` 的 `INFO` 项（机器相关路径）不是密钥，也不是泄露；`RESULT: OK` 才是结论。
3. `AndroidRefsDir` 编译通过**不是**运行时可用性证明；负向对照（#11）只证明"这个开关真的在替换引用"。
4. 桌面 R-06 的 `FileLoadException` **不是** Mono 的行为，不能写进设备列。
5. `345d712` 的 CI success **不包含**本阶段新增的测试（本次没有 push）。
6. 上一阶段 README 里"模拟器 T00–T07/T11 PASS"是 **RECORDED (Evidence D)**：原始日志已丢失，
   本阶段未重跑，不可独立复核，也不能与本阶段的任何 PASS 混用。
