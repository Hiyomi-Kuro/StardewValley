# Phase 1.1.5 报告：Recursive Safe Mod Deployment 与 Android MonoVM Runtime Validation

- 日期：2026-11-06
- 远程基线：`ab1e10cb8a730ce593c463bdcfb48624c570c843`（`main`）
- 上一阶段基线：`345d71280ce5c255c4b9db490732cd9af312c0aa`
- 本阶段性质：**本地实现 + 本地证据 + 只读设备预检**。未 commit、未 push、
  未向设备写入任何文件、未启动游戏、未安装/卸载 APK、未读写 Saves。
- 证据等级定义见 [`phase0.5.1-evidence-index.md`](phase0.5.1-evidence-index.md) §0；
  本报告每条结论都标注了等级（Desktop .NET / AndroidRefsDir / Emulator / ARM64 physical / CI）。

---

## 0. 总状态

**WAITING_FOR_USER_APPROVAL。**

递归部署缺陷已修复并有测试闭环，P114 完整 bundle 已组装并逐文件校验，E0 只读预检通过；
但**没有任何用户批准**，因此设备写入与游戏启动**一步都没有执行**。

| 主题 | 状态 | 说明 |
| --- | --- | --- |
| Git 基线无损核对 | ✅ COMPLETE | 本地 `HEAD` = `origin/main` = `ab1e10cb`；ahead 0 / behind 0；开始时工作区干净 |
| 递归部署缺陷定位 | ✅ COMPLETE | 旧实现只枚举一层并全部复制到 Mods 根目录（源码行号见 §3.1） |
| 递归安全部署实现 | ✅ COMPLETE | 保留完整相对路径、staging 全量校验后复制、最终位置二次校验哈希/属主/权限 |
| 覆盖保护 | ✅ COMPLETE | 同哈希跳过；不同哈希 fail-closed；`-OverwriteModsApproved` 才替换 |
| staging 清理 | ✅ COMPLETE | 成功与失败路径都清理，且有断言 |
| Fake ADB 测试 | ✅ COMPLETE | **95/95 PASS**（原 39 项全部保留 + 56 项新增） |
| P114 完整 bundle | ✅ COMPLETE | 11 个文件 / 172,191 字节，14/14 校验通过（含 APK 无 P114 载荷） |
| Release 重新构建 | ✅ COMPLETE | SDK 8.0.404（装到任务目录）；7 次构建全部 rc=0；旧产物与新构建字节不同，故以新构建为准 |
| 其它本地套件 | ✅ COMPLETE | ci-offline 13/13、p114-static 46/46、device-precheck 10/10、mod-pipeline 27/27、loader 45/45+473/473、AndroidRefsDir 7/7、`run-all` 8/8 |
| E0 只读预检 | ✅ COMPLETE | `E0: PASS (read-only)`；x86_64 + `libnb.so` 翻译层 |
| 设备写入 / 启动游戏 | ⛔ WAITING | **无用户批准**，未执行任何写入或启动 |
| Emulator R-01…R-14 | ⛔ NOT RUN | 依赖上一条批准 |
| ARM64 物理设备 | ⛔ BLOCKED | 无合格物理设备；Emulator 不得计入 |
| 远程 CI | ✅ OBSERVED | `ab1e10cb` 的 3 个 check 全部 success（run `42222905547`） |

一句话：**"部署器不再毁掉 mod 目录树"这件事已经用源码、实现和 95 项 fake-ADB 断言闭环，
并且真正用 SDK 8.0.404 重新构建、逐文件校验了部署包；"在 MonoVM 上真跑一遍"仍停在批准门槛之前，
ARM64 真机仍 BLOCKED。**

---

## 1. Git 基线与远程状态

### 1.1 远程 `main`

| 项 | 值 |
| --- | --- |
| 远程 | `https://github.com/Hiyomi-Kuro/StardewValley.git` |
| 默认分支 | `main` |
| 本阶段核对到的 `main` | `ab1e10cb8a730ce593c463bdcfb48624c570c843` |
| 上一阶段 | `345d71280ce5c255c4b9db490732cd9af312c0aa` |
| 两者关系 | ahead 1 / behind 0（与提示词 §0.1 一致） |

`git fetch origin --prune` 后 `origin/main` 仍是 `ab1e10cb`，**远程没有继续前进**，
因此不存在"需要逐项审查的新差异"。

### 1.2 GitHub Actions（对具体 commit 的真实查询）

查询：`GET /repos/Hiyomi-Kuro/StardewValley/commits/ab1e10cb…/check-runs`（2026-11-06T21:47+08:00 采集）。

| 项 | 值 |
| --- | --- |
| run ID | `42222905547` |
| check 1 | `offline checks (python, powershell, manifests, privacy)` → **success**（13:07:10Z→13:07:26Z） |
| check 2 | `powershell gates (adb resolution, deploy safety, no device)` → **success**（13:07:09Z→13:07:46Z） |
| check 3 | `dotnet build + loader host tests` → **success**（13:07:09Z→13:08:48Z） |
| 合计 | 3 个 check，**全部 completed / success** |

> 提示词 §0.1 记录的 "combined status 查询返回空数组" 不是 CI PASS。本次直接查 `check-runs`，
> 拿到的是**该 commit 的真实运行结果**，不是推断。本阶段**没有**触发 `workflow_dispatch`。
>
> 本阶段修改的脚本与新增测试**尚未 push**，因此它们在远程的 CI 状态只能记
> **NOT RUN（未 push）**，不能写成"CI 已通过本阶段改动"。

### 1.3 本地仓库（开始 / 结束）

| 项 | 开始 | 结束 |
| --- | --- | --- |
| branch | `main` | `main` |
| HEAD | `ab1e10cb8a730ce593c463bdcfb48624c570c843` | 同左（本阶段未 commit） |
| `origin/main` | `ab1e10cb…` | `ab1e10cb…` |
| 工作区 | 干净（0 modified / 0 untracked） | 仅本阶段有意修改的文件（见 §11） |

无损协调结论：开始时**没有**用户未提交改动需要保护，因此不存在覆盖风险；
全程未执行 `reset --hard` / `clean` / 强制 `checkout` / `stash`。

---

## 2. Phase 1.1.4 已确认事实的复核

| 事实 | 复核方式 | 结果 |
| --- | --- | --- |
| P114 mod 布局 | 读 `Mod/ModFramework.Runtime/ModDiscovery.cs` | 只枚举 `Directory.GetDirectories(modsRoot)`（一层），每个候选必须有自己的 `manifest.json`；不递归 |
| P114 包结构 | 读 `Mod/P114.RuntimeProbe/README.md` 与 bundle 实际产物 | `Mods/ModFramework.Runtime.dll` + `Mods/P114.RuntimeProbe/{manifest.json,*.dll,probe-fixtures/}` |
| 旧部署行为 | 读 `Tools/deploy-apk-safe.ps1`（修复前） | 见 §3.1，确认只枚举顶层文件 |
| 本地 APK 摘要 | 重新计算 | `65e1e2af…051f9d`，388,967,580 字节 |
| 本地签名摘要 | 重新用 `apksigner verify --print-certs` | `611c8fa4…24c5de`（与历史记录一致） |
| 设备 E0 | 重新只读查询（见 §6） | x86_64 + `libnb.so`，Android 12 / API 32 |

历史值**没有**被当作当前值使用：所有设备事实都在本次重新采集。

---

## 3. 递归部署缺陷：位置、根因、修复

### 3.1 缺陷的源码位置（修复前 `Tools/deploy-apk-safe.ps1`）

```powershell
foreach ($file in Get-ChildItem -LiteralPath $ModsSourceDirectory -File -ErrorAction SilentlyContinue) {
    $remote = "/data/local/tmp/" + $file.Name
    ...
    $staged += $remote
}
$copyCommand = "mkdir -p " + $remoteRoot + " && cp " + ($staged -join " ") + " " + $remoteRoot + "/"
```

### 3.2 根因（三层，缺一不可）

1. **枚举层**：`Get-ChildItem -File` 没有 `-Recurse`，只返回源目录的**第一层普通文件**。
   子目录（`P114.RuntimeProbe/`、`probe-fixtures/**`）根本不进入 `$staged`。
2. **路径层**：远端目标写成 `"/data/local/tmp/" + $file.Name`，只取 `Name`（叶子名），
   **相对路径被丢弃**；随后 `cp ... $remoteRoot/` 把一切平铺到 Mods 根目录。
3. **契约层**：`ModDiscovery.Discover` 只扫 Mods 的**一级子目录**，并要求每个子目录里有
   `manifest.json`。平铺后 `manifest.json` 落在 Mods 根目录，等于把这个 mod 变成"不可发现"。

因此这不是"少拷了几个文件"，而是**部署模型与 Loader 的发现模型不一致**：

```
旧实现（破坏性）                      正确布局（ModDiscovery 期望）
Mods/                                 Mods/
├── ModFramework.Runtime.dll          ├── ModFramework.Runtime.dll
├── manifest.json          ← 错位     └── P114.RuntimeProbe/
├── P114.RuntimeProbe.dll                 ├── manifest.json
└── P114.RuntimeProbe.Dependency.dll      ├── P114.RuntimeProbe.dll
   （probe-fixtures 全部丢失）             ├── P114.RuntimeProbe.Dependency.dll
                                          └── probe-fixtures/…
```

### 3.3 旧测试为什么没抓到

`test_deploy_safe.ps1` 的 fake adb 只做两件事：记录被调用的 argv、`push` 一律打印
"1 file pushed"、`su -c` 一律打印 "(fake) su ok"。它既**没有设备文件系统**，也**不看结果树**，
所以"调用过 push"能 PASS，而"文件到了错误的位置"永远暴露不出来。

### 3.4 修复后的实现（关键点，全部可验证）

| 关注点 | 实现 |
| --- | --- |
| 递归枚举 | `Get-ChildItem -LiteralPath <src> -Recurse -Force`，计算每个文件相对源根的路径 |
| 路径安全 | 拒绝 reparse point / 符号链接；拒绝 `..`、绝对路径、含 `:` 的路径；必须仍在源根内 |
| 结构前置校验 | Mods 根必须有 `ModFramework.Runtime.dll`；每个一级子目录必须有 `manifest.json`；manifest 的 `entryAssembly`/`privateAssemblies` 必须真实存在 |
| 设备门禁 | 包已安装 且 versionCode=239 / versionName=1.6.15.0；设备端 APK SHA-256 必须等于本地 APK |
| 属主门禁 | 从 app 的 `files` 目录 `stat` 出真实 uid:gid；读不到、或为 root 时停止 |
| 命令门禁 | `command -v sha256sum stat mkdir cp chown chmod rm ls` 必须全部命中，否则在**上传第一个字节前**停止（不假设设备有 GNU `tar`/`rsync`/`unzip`） |
| 冲突策略 | 同路径同 SHA-256 → 跳过；不同 → **fail-closed**；`-OverwriteModsApproved` 才替换 |
| 传输 | 唯一 staging `/data/local/tmp/svmods-<12hex>`，逐文件 `adb push` 到 `staging/<相对路径>` |
| 预校验 | **全部** staged 文件 `sha256sum` 与本地一致后才开始复制 |
| 落地 | `mkdir -p` 目标目录（只 chown 本次新建的目录）→ `cp -p` → `chown <uid>:<gid>` → `chmod 0644` |
| 二次校验 | 最终位置再 `sha256sum`；再 `stat -c "%n %u:%g:%a"` 校验属主与 owner-read |
| 清理 | 成功/失败都由 `finally` 路径 `rm -rf` staging，并重新 `test -e` 证明已消失 |
| 不可控 shell | 所有远端参数经 POSIX 单引号转义（`'` → `'\''`），不拼接未受控字符串 |
| 破坏面 | 不删/不清 Mods 根；不碰其它 Mod；无 `uninstall`/`pm clear`/`force-stop`/`am start` |

新增参数 `-OverwriteModsApproved` 是**独立开关**，默认关闭；它只影响"已存在且内容不同"的路径。

---

## 4. P114 完整部署包

### 4.1 Release 构建状态

**✅ COMPLETE（Release，SDK 8.0.404）。**

本机原本**没有任何** .NET SDK：`dotnet --list-sdks` 为空，`C:\Program Files\dotnet` 只有
host + 8.0 运行时，`shared/Microsoft.NETCore.App` 无 SDK，仓库 `.tmp/*/dotnet/` 也不存在；
`Tools/find-dotnet.ps1` 与 `Tools/tests/toolchain.py` 都会判定"无可用 SDK"。

补齐过程（记录在这里，因为它是可复现的前置条件）：

1. `dotnet-install.ps1` 等待 20 分钟未落盘任何字节；
2. 直接 `curl` 微软 CDN，实测 20–250 KB/s 且中途断开
   （`curl: (18) end of response with 231027281 bytes missing`）；
3. 用 `curl -C -`（断点续传）+ `--retry-all-errors` 重跑，取到完整
   `dotnet-sdk-8.0.404-win-x64.zip`（281,358,929 字节）；
4. 解压到**任务目录**（不写系统 PATH、不动仓库）：
   `tasks/20261106-p115-recursive-deploy-a1/work/dotnet`，`--list-sdks` = `8.0.404`。

随后 `-c Release` 构建 **7 次全部 rc=0**：`ModFramework.Runtime`、`P114.RuntimeProbe`
（含 `P114.RuntimeProbe.Dependency.dll`）、以及 5 个 fixture 变体
（`same` / `diff_a` / `diff_b` / `alpha` / `beta`）。日志 `logs/build-bundle.txt`。

> **重要发现：跨构建目录不保证字节相同，所以"拿旧产物顶替"不可接受。**
> 仓库里 Phase 1.1.4 遗留的 `.tmp/p114/artifacts` 组装出 11 文件 / **170,655** 字节；
> 本阶段从当前干净源码重新 Release 构建得到 11 文件 / **172,191** 字节，`Runtime`、`Probe`、
> `Dependency` 与 `alpha`/`diff-a`/`diff-b` 的 SHA-256 全部不同（旧 fixture 4096 字节，新 4608 字节）。
> 同一份源码在**第三个**目录（`test_loader.py` 的 `--artifacts`）构建出的
> `P114.RuntimeProbe.dll` 又是另一个摘要（`snapshot=sha256:2d3f35d5…`，见 §11 的 loader 日志）。
>
> 也就是说：即使源码相同，`--artifacts-path` 不同就会改变产物字节（嵌入路径 / MVID 派生的
> debug 信息等）。因此**部署源必须来自同一次构建**，不能把历史产物当成"同一份代码"使用。
> 本阶段的部署源固定为 §4.2 这一次构建的 `outputs/bundle-build`。

### 4.2 Bundle 组装与逐文件校验（已完成）

组装来源：**本阶段新鲜 Release 构建**（`work/artifacts` + `outputs/bundle-build/variants`）。
组装脚本沿用 `Tools/tests/test_loader.py::build_p114_package()` 的 fixture 布局，
并额外加入框架 DLL、manifest 审计、逐文件 SHA-256 与 APK 条目检查。
（用旧产物组装的那一份保留在 `outputs/bundle-reuse-p114-artifacts`，仅作"旧产物已过期"的对照，
**不是**部署源。）

结构（`<bundle>/Mods/`，11 个文件 / 172,191 字节）：

| 相对路径 | 字节 | SHA-256 |
| --- | ---: | --- |
| `ModFramework.Runtime.dll` | 118272 | `a35a03597e88c38804ad75b91a5df25e77e9a2a19c7934508701833ae3d90fea` |
| `P114.RuntimeProbe/manifest.json` | 607 | `05b3bb87bb700e7fa222983c4823294e0ae8ae023e9928b21e82de4204eabc3a` |
| `P114.RuntimeProbe/P114.RuntimeProbe.dll` | 22016 | `e03e175266d559ec60dc5307789f0f5d69d7ccc78f2c9a9ef8764f5235dfcdf1` |
| `P114.RuntimeProbe/P114.RuntimeProbe.Dependency.dll` | 5120 | `4a878c03511571d4e084824d2d585dc45561c2469558302d762c33aa3c890883` |
| `P114.RuntimeProbe/probe-fixtures/same-a/P114Same.dll` | 4096 | `73154fe8d1be1ef6cfde17ce979910196b6ad522cc9c2192a5ac488edd9b4c22` |
| `P114.RuntimeProbe/probe-fixtures/same-b/P114Same.dll` | 4096 | `73154fe8d1be1ef6cfde17ce979910196b6ad522cc9c2192a5ac488edd9b4c22` |
| `P114.RuntimeProbe/probe-fixtures/diff-a/P114Diff.dll` | 4608 | `2ccdee8fd018f4cb289bf71cd879392a8748bf37621e57f5cd32828cbae967f4` |
| `P114.RuntimeProbe/probe-fixtures/diff-b/P114Diff.dll` | 4608 | `175ae9ef74496478befdd77360ed86ea670d8fbaeca32acbea3bdfef8827ddf2` |
| `P114.RuntimeProbe/probe-fixtures/alpha/P114Alpha.dll` | 4608 | `0d0f54686e9639c3016c1e825bae2df9ea11b373b547c756f6c00528c4b77bdf` |
| `P114.RuntimeProbe/probe-fixtures/beta/P114Beta.dll` | 4096 | `f4097e4632521ee4874e3124a100ddeb2edb57ceda8c39707826630d2399a03d` |
| `P114.RuntimeProbe/probe-fixtures/corrupt/P114Broken.dll` | 64 | `f5a5fd42d16a20302798ef6ed309979b43003d2320d9f0e8ea9831a92759fb4b` |

审计结果 **14/14 PASS**：

| 检查 | 结果 |
| --- | --- |
| `manifest.id == P114.RuntimeProbe` | ✅ |
| `entryAssembly` 文件存在 | ✅（`P114.RuntimeProbe.dll`） |
| `entryType == P114.RuntimeProbe.P114RuntimeProbeEntry` | ✅ |
| `privateAssemblies` 全部存在 | ✅（`P114.RuntimeProbe.Dependency.dll`） |
| `ModFramework.Runtime.dll` 在 Mods 根 | ✅ |
| `manifest.json` 不在 Mods 根 | ✅ |
| `same-a` 与 `same-b` 字节相同 | ✅（同 hash） |
| `diff-a` 与 `diff-b` 字节不同 | ✅（同身份、不同字节） |
| alpha / beta / corrupt fixture 存在 | ✅ |
| corrupt fixture 确实是 64 个零字节 | ✅（R-14 专用） |
| corrupt fixture 未被 manifest 声明 | ✅（不会被自动加载） |
| APK 内无 P114 / MonoRuntimeProbe / ModFramework 条目 | ✅（3672 个条目，0 命中） |

> 注意：`probe-fixtures/` 不在 manifest 里，Loader 不会自动加载它；探针按相对路径显式读取。

---

## 5. Fake ADB 测试：旧 39 项 + 新 56 项 = 95 项

### 5.1 Harness 的改造

旧 fake adb 只会打印"push ok"。新 fake adb **模拟设备文件系统**：

- 每个用例一个独立设备根（`data/data/<pkg>/files`、`data/app/.../base.apk` 都真实存在）；
- `push` 真的把字节写进模拟设备；
- `shell` 解析并执行脚本层：`mkdir -p`、`cp -p`、`chown`、`chmod`、`sha256sum`、`stat -c`、
  `test -e`、`ls -1a`、`rm -rf`、`echo`，支持 `&&` 与 `;`，支持 `su -c '<script>'` 的一层去引用；
- 未识别的远端命令打印 `UNEXPECTED remote command` 并返回非 0（防止"静默放过"）；
- 断言直接读模拟设备树，比较**相对路径集合**与**每个文件的 SHA-256**。

过程中修掉了两个 harness 自身的真 bug（都已记录，避免以后重犯）：

1. `$code = Invoke-Segment …` 会把命令的 stdout 当成退出码吞掉 → 改为 `$script:ExitCode` 传状态；
2. `Split-AndList` 只认 `&&`，不认 `;`，导致 `test -e …; echo status=$?` 永远读不到状态 → 已支持 `;`。

### 5.2 结果

```
95 checks, 95 passed, 0 failed
```

| 分组 | 覆盖内容 | 结果 |
| --- | --- | --- |
| 原 39 项（Phase 1.1.4 回归） | 默认只读、设备选择、签名门禁、安装门禁、脱敏、静态无卸载路径 | 39/39 PASS |
| 计划与门禁（新） | 预检打印 12 文件计划且不写设备；推送必须显式 `-Serial`；源目录缺失/空/缺 Runtime DLL/缺 manifest；reparse point（2 项） | 9/9 PASS（其中 2 项 reparse-point 用例在本机记 SKIP，见下方说明） |
| 递归目录树（新） | 12 个相对路径全部存在、数量一致、逐文件本地/远端 hash 相同；`manifest.json` 在 `Mods/P114.RuntimeProbe/`；Runtime DLL 在 Mods 根；`probe-fixtures` 与三级嵌套完整；same/diff fixture 字节关系；staging 已清理；staged/remote/owner 三段校验输出 | 14/14 PASS |
| 覆盖保护（新） | 首次部署 12 次 push；二次部署 `ALREADY-PRESENT` 且 0 次 push；不同内容 fail-closed、目标未被改、无 push、staging 清理；`-OverwriteModsApproved` 才替换且无破坏性命令 | 9/9 PASS |
| 特殊路径（新） | 空格 / 方括号 / 圆括号 / 中文的目录与文件名，hash 仍一致 | 2/2 PASS |
| 失败与清理（新） | push 失败、copy 失败、mkdir 失败、属主校验失败；每次失败都停止并清理 staging | 8/8 PASS |
| 设备门禁（新） | 包未安装、版本不符、设备 APK hash 不符、uid 不可读、必需命令缺失、Mods 根属主错误；其它 Mod 未被触碰 | 10/10 PASS |
| 无破坏性（新） | 每个推送场景均无 uninstall / pm clear / force-stop / am start / Saves / install | 4/4 PASS |
| **合计** | 39 + 9 + 14 + 9 + 2 + 8 + 10 + 4 | **95/95**（56 项新增） |

> **计数口径（Phase 1.1.6 校正）**：上表按 `Tools/tests/test_deploy_safe.ps1` 的真实执行顺序与
> `Check` 调用逐项清点，**不重不漏**；只有这一个合计等于 `95 checks, 95 passed, 0 failed`。
> 旧版本的 `39 + 9 + 10 + 3 + 6 + 2 + 8 + 10 + 4 = 91` 是分组重复/漏计：把
> `verify-staged` / `verify-remote` / `verify-owner` 误算成 3 条独立断言（实际只有 2 条 `Check`，
> 其中一条同时断言 staged 与 remote），同时递归树组漏算了 manifest 位置、fixture 关系、staging 清理等项。
> 该旧表已废弃。

> **symlink / reparse-point 的口径必须分开（Phase 1.1.6 校正）**：
> - **DSH 本机（Windows，非管理员/未开启开发者模式）**：`New-Item -ItemType SymbolicLink` 抛异常，
>   `$linkCreated = $false`，脚本走 `else` 分支，两条断言以 **`PASS` + detail `SKIPPED: symlink creation
>   not permitted on this host`** 记入结果。因此本机的 `95/95` 中**有 2 条是 SKIP**，真实执行的断言是
>   **93 条**；不能把这两条读成"本机验证过 reparse-point 拒绝"。
> - **GitHub Windows runner（CI）**：runner 以管理员身份运行，能创建符号链接，`$linkCreated = $true`，
>   两条断言**真实执行并 PASS**（`exit=3` + `reparse-point`，且无 push）。该结论来自 CI run
>   `42238545103` 的 powershell-gates 作业；本阶段复核时 Actions 日志下载需要认证（未登录 API 返回 403），
>   因此这里引用的是 CI 运行结果记录，而不是本次重新下载的日志文本。
> - 结论：**这是测试宿主权限差异，不是产品缺陷**；本机 SKIP 与 CI PASS 必须分别陈述。

关键回归点：**"旧实现能过、新实现也能过"的用例（如 `approved_push_pushes`）现在还会额外断言
远端目录树与 hash**，所以"平铺复制"这种退化会在测试里立刻失败。

---

## 6. E0 只读设备预检（本次重新采集）

工具：`Tools/device-precheck.ps1 -Serial emulator-5554` 与 `Tools/deploy-apk-safe.ps1` 默认模式。
两者都是只读。日志见证据索引 `E0-01` / `E0-02`。

| 项 | 观察值 |
| --- | --- |
| serial | `emulator-5554`（`device` 状态在线，`transport_id:1`） |
| product / model / device | `PD2309` / `V2309A` / `PD2309`（manufacturer `vivo`，fingerprint `vivo/PD2309/PD2309:12/V417IR/2721:user/release-keys`） |
| Android / API | 12 / 32（security patch 2022-04-05） |
| ABI | `ro.product.cpu.abi = x86_64`；abilist `x86_64,arm64-v8a,x86,armeabi-v7a,armeabi` |
| native bridge | `libnb.so` |
| `ro.kernel.qemu` | 空（**没有** qemu 标记，因此不能靠这一项判模拟器） |
| device-class | **NOT-ARM64-PHYSICAL**（`EMULATOR-TRANSLATED`：x86_64 + `libnb.so`） |
| 游戏包 | `com.chucklefish.stardewvalley` 已安装 |
| versionCode / versionName | **239 / 1.6.15.0** |
| primaryCpuAbi | `arm64-v8a`（翻译层报告值） |
| 设备端 APK SHA-256 | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d` |
| 本地 APK SHA-256 | 同左（388,967,580 字节） |
| 本地签名摘要 | `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de` |
| Mods 目录 | **不存在**（`/data/data/<pkg>/files/Mods` 与 `/sdcard/Android/data/<pkg>/files/Mods` 都没有） |
| app files 目录 | `drwxrwx--x u0_a152 u0_a152`，uid:gid `10152:10152`，mode `771` |
| root | `adb shell` 是 `uid=2000(shell)`；`su -c id` = `uid=0(root)` **可用** |
| 需要的命令 | `sha256sum` / `stat` / `mkdir` / `cp` / `chown` / `chmod` / `rm` / `ls` / `id` / `tar` / `unzip` 全部存在 |
| SELinux | `Permissive`；`restorecon` **不存在** |
| 结论 | `E0: PASS (read-only)`；`E1/E2: BLOCKED reason=no-arm64-physical-device` |

对部署的影响：属主/权限必须显式设置并复核（`10152:10152`、`0644`/`0755`），
不能指望 `restorecon`；也不能假设设备有 GNU `tar`/`rsync` —— 脚本只用逐文件 `push` + 小命令集。

---

## 7. 批准门槛：本阶段**没有**执行任何设备操作

提示词 §6 明确规定"提示词本身不构成批准"。本轮**没有**收到用户对推送或启动的批准，因此：

| 未执行的动作 | 状态 |
| --- | --- |
| `adb push`（任何文件） | ❌ 未执行 |
| 在 `/data/data/<pkg>/` 下创建/修改文件 | ❌ 未执行 |
| 创建 `/data/data/<pkg>/files/Mods` | ❌ 未创建 |
| 启动游戏 Activity（`am start`/`monkey`） | ❌ 未执行 |
| `adb pull` 运行时日志/探针输出 | ❌ 未执行（只 `su -c sha256sum` 读 APK 摘要） |
| 安装 / 覆盖 APK | ❌ 未执行 |
| 卸载 / `pm clear` / `force-stop` | ❌ 未执行 |
| 读取或修改 Saves | ❌ 未执行（只看到目录存在，未列出内容） |

所有设备交互都在"只读命令"白名单内：`getprop`、`pm path`、`dumpsys package`、`ls`、
`stat`、`sha256sum`、`test -e`、`command -v`、`getenforce`。

当前状态：**WAITING_FOR_USER_APPROVAL**。批准摘要见 §10。

---

## 8. Emulator 运行验证：NOT RUN

R-01 … R-14 在 **Emulator** 列全部为 **NOT RUN**：没有推送、没有启动、没有 `loader-report.txt`、
没有 `probe-output/phase1.1.4.txt`。按提示词要求，缺失的行不得补写推测值。

Desktop .NET 列是**本阶段重跑**的（`test_loader.py` 473/473、`test_mod_pipeline.py` 27/27），
但它仍然只是桌面宿主：**Desktop ≠ MonoVM**，不能外推到设备。

---

## 9. ARM64 物理设备：BLOCKED（独立状态）

- 当前唯一在线目标是 x86_64 + `libnb.so` 翻译层，属 **Emulator**；
- 没有任何 ARM64 物理设备通过 E0；
- 因此 `E1/E2 = BLOCKED reason=no-arm64-physical-device`，Phase 1.1.5 的"ARM64 验证"**未完成**；
- 未修改任何设备属性、未改脚本绕过分类、未启动 Phase 1.2。

---

## 10. 批准摘要（等待用户决定）

### 10.1 目标与结果预期

1. **serial / device-class**：`emulator-5554` / `EMULATOR-TRANSLATED`（NOT-ARM64-PHYSICAL）。
2. **系统**：Android 12（API 32），ABI `x86_64`，native bridge `libnb.so`。
3. **游戏包**：`com.chucklefish.stardewvalley`，239 / 1.6.15.0；
   设备端 APK SHA-256 = 本地 = `65e1e2af…051f9d`。
4. **Mods 当前状态**：不存在（首次创建）。
5. **将写入的文件**：见 §4.2 表格，共 **11 个文件 / 172,191 字节**，每个都有 SHA-256。
6. **远端最终目录**：`/data/data/com.chucklefish.stardewvalley/files/Mods`
   （属主 `10152:10152`，目录 `0755`，文件 `0644`）。
7. **将执行的命令**（仅这些，逐条有作用）：

| 命令 | 作用 |
| --- | --- |
| `adb -s emulator-5554 shell getprop …` / `pm path` / `dumpsys package` | 推送前再次核实设备与包版本 |
| `adb -s emulator-5554 shell su -c 'sha256sum <设备 base.apk>'` | 推送前再次核对 APK 摘要 |
| `adb -s emulator-5554 shell su -c 'stat -c %u:%g:%a <files 目录>'` | 取得 app uid:gid |
| `adb -s emulator-5554 shell su -c 'mkdir -p /data/local/tmp/svmods-<id>/…'` | 建唯一 staging 目录 |
| `adb -s emulator-5554 push <本地文件> /data/local/tmp/svmods-<id>/<相对路径>` ×11 | 逐文件上传，保留相对路径 |
| `adb -s emulator-5554 shell su -c 'sha256sum <staging 文件…>'` | **全量**校验后才落地 |
| `adb -s emulator-5554 shell su -c 'mkdir -p …/files/Mods/P114.RuntimeProbe/probe-fixtures/…'` | 建最终目录 |
| `adb -s emulator-5554 shell su -c 'chown 10152:10152 …' && chmod 0755 …` | 只对本次新建目录设属主 |
| `adb -s emulator-5554 shell su -c 'cp -p <staging> <最终> && chown 10152:10152 <最终> && chmod 0644 <最终>'` ×11 | 落地并设属主/权限 |
| `adb -s emulator-5554 shell su -c 'sha256sum <最终文件…>'` | 最终位置二次校验 |
| `adb -s emulator-5554 shell su -c 'stat -c '%n %u:%g:%a' …'` | 校验属主与可读位 |
| `adb -s emulator-5554 shell su -c 'rm -rf /data/local/tmp/svmods-<id>'` | 清理 staging |

8. **写入范围/空间**：仅在 `files/Mods` 下新增 `P114.RuntimeProbe/**` 与 `ModFramework.Runtime.dll`，
   约 **168 KiB**；staging 在 `/data/local/tmp` 下临时占用同等大小。
9. **Saves 风险**：不卸载、不 `pm clear`、不读取/修改 Saves；但**启动游戏本身**会产生
   普通运行数据（日志/缓存），且**绝不会进入或加载任何存档**。
10. **回滚**：只删除本阶段新建的 `files/Mods/P114.RuntimeProbe/` 与 `files/Mods/ModFramework.Runtime.dll`；
    仅当确认这些文件由本阶段创建、且没有被其它 Mod 使用时才删；**绝不删除整个 Mods 目录**。
11. **是否批准推送？** —— 需要用户**明确**回答。
12. **是否批准启动游戏？** —— 与推送**分开**确认；只批准推送时不会启动。

### 10.2 若获批准，接下来的唯一命令序列

```powershell
# 1) 推送（不安装 APK，不启动游戏）
pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -PushModsApproved -Serial emulator-5554 -ModsSourceDirectory <bundle>\Mods

# 2) 仅在**另外**批准启动后
adb -s emulator-5554 shell am start -W -n com.chucklefish.stardewvalley/com.chucklefish.stardewvalley.MainActivity

# 3) 仅只读取回证据（不 force-stop、不 pm clear）
adb -s emulator-5554 shell su -c 'cat /data/data/com.chucklefish.stardewvalley/files/Mods/loader-report.txt'
adb -s emulator-5554 shell su -c 'cat /data/data/com.chucklefish.stardewvalley/files/Mods/P114.RuntimeProbe/probe-output/phase1.1.4.txt'
```

`<bundle>\Mods` = 本任务目录 `outputs/bundle-build/Mods`（见证据索引 `B-01`）。

---

## 11. 测试与静态检查汇总

| # | 命令 | 结果 | 日志 |
| --- | --- | --- | --- |
| 1 | `test_deploy_safe.ps1` | **95/95 PASS**（本机：93 条真实断言 + 2 条 symlink SKIP，见 §5.2） | `logs/test_deploy_safe.txt` |
| 2 | `test_loader.py --refs .tmp/p113/refs` | **45/45 驱动 + 473/473 宿主 PASS**（含 P114 套件与 65 refs 解析差分） | `logs/test_loader.txt` |
| 3 | `test_mod_pipeline.py --refs .tmp/p113/refs` | **27/27 PASS**（见下方环境说明） | `logs/test_mod_pipeline.txt` |
| 4 | `test_ci_offline.py` | **13/13 PASS** | `logs/ci-offline.txt` |
| 5 | `test_p114_probe.py` | **46/46 PASS** | `logs/p114-static.txt` |
| 6 | `test_device_precheck.ps1` | **10/10 PASS** | `logs/device-precheck.txt` |
| 7 | `run-all.ps1 -Dotnet <sdk> -Refs .tmp/p113/refs` | **8/8 套件 PASS** | `logs/run-all.txt` |
| 8 | `privacy-scan.py --include-untracked` | **RESULT: OK**（命中均为既有 docs 里的历史路径，属 allowlist） | `logs/privacy-scan.txt` |
| 9 | Release build (Runtime/P114/Dependency/Fixtures) | **7 次构建全部 rc=0**（SDK 8.0.404） | `logs/build-bundle.txt` |
| 10 | AndroidRefsDir 编译（65 refs） | **7/7 rc=0**（Runtime / MonoRuntimeProbe / 其 Dependency / P114 / 其 Dependency / P114Fixture / ExampleMod） | `logs/androidrefs-build.txt` |
| 11 | `git diff --check` | ✅ exit 0 | `logs/git-diff-check.txt` |
| 12 | CI YAML / JSON manifest / PowerShell / Python 静态检查 | ✅ 由 `test_ci_offline.py` 覆盖（13 项） | 同上 |
| 13 | APK 条目检查（无 P114 载荷） | ✅ 3672 条目，0 命中 | `logs/build-bundle.txt` |

工具版本：Windows PowerShell 5.1.26100.9711、Python 3.12.10、
adb（`platform-tools`，由 `LOCALAPPDATA` 解析）、apksigner `build-tools/36.0.0`、
.NET SDK **8.0.404**（解压到任务目录，未改系统 PATH）。

---

## 12. 修改文件与文档

| 文件 | 变更 |
| --- | --- |
| `Tools/deploy-apk-safe.ps1` | **重写** `-PushModsApproved` 分支：递归、路径保留、staging 全量校验、属主校验、冲突 fail-closed、清理；新增 `-OverwriteModsApproved`；新增 `Get-ModsPlan` / `Quote-Posix` / `Invoke-RootShell` / `Get-RemoteSha256` / `Test-RemotePath` / `Get-RemoteOwnerMode`；预检模式输出 mods-plan |
| `Tools/tests/test_deploy_safe.ps1` | fake adb 改为**设备文件系统模拟器**；39 项保留 + 56 项新增（递归树、hash、覆盖、特殊路径、失败清理、设备门禁、命令门禁） |
| `Tools/README.md` | 更新部署入口说明、递归语义、测试计数 |
| `Mod/P114.RuntimeProbe/README.md` | 设备运行步骤改为使用修复后的递归部署脚本 |
| `docs/architecture/phase1-loader-design.md` | 新增 §9.1 部署契约；变更记录追加 Phase 1.1.5 |
| `docs/architecture/phase1-runtime-constraints.md` | 新增 §9.6 设备侧部署事实（E0 实测） |
| `docs/audit/phase1.1.5-report.md` | 本文件 |
| `docs/audit/phase1.1.5-evidence-index.md` | 证据索引 |

未修改：`Mod/**` 源码、`app/**`、`.github/**`、`README.md`（根目录四节结构未变，本阶段无必要改动）。

---

## 13. Git 工作区最终状态

- branch `main`，HEAD `ab1e10cb8a730ce593c463bdcfb48624c570c843`（**未 commit**）；
- 工作区仅有本阶段**有意修改**的文件（§12），无未跟踪的构建产物散落到仓库根；
- **DSH 没有执行 `git push`**，也没有通过任何 API/Connector 创建远程 commit 或更新 ref；
- 远程 `main` 仍是 `ab1e10cb`，这是**预期**的，不代表本阶段失败。

### 13.1 后记（Phase 1.1.6 追加，2026-11-06）

上面 §13 是 **Phase 1.1.5 DSH 结束时的历史快照**，保留原样（当时确实未 commit、未 push）。
用户随后按自己的工作流**手动**提交并 push，形成新的远程 `main`：

| 项 | 值 |
| --- | --- |
| commit | `6e90ef2ac0e1d52832ad3564d4234640cc535d14` |
| 父提交 | `ab1e10cb8a730ce593c463bdcfb48624c570c843`（即 §13 的 HEAD） |
| 变更 | 9 个文件（7 modified + 2 added），+1946 / −68；包含新增的 `docs/audit/phase1.1.5-report.md` 与 `phase1.1.5-evidence-index.md` |
| 关系 | relative to 父提交：1 commit ahead / 0 behind |
| Actions | run `42238545103`（run #7，event=push，head_sha=`6e90ef2`）completed / **success**，3/3 jobs |
| DSH 行为 | 该 commit 与 push **不是** DSH 执行的；DSH 没有调用 GitHub API/Connector 写 commit/branch/tag/ref |

> 因此"本阶段未 push"仍是准确的历史事实；远程 `main` 现在前进到 `6e90ef2` 是**用户手动 push 的结果**。

---

## 14. 下一阶段建议（不自动启动）

1. **等待批准**：先决定是否推送 P114 bundle；推送与启动必须分别批准。
2. **工具链留在本机**：本阶段的 SDK 8.0.404 解压在任务目录
   `tasks/20261106-p115-recursive-deploy-a1/work/dotnet`（未改系统 PATH）。
   Phase 1.1.6 复核：该目录仍存在，`dotnet.exe --list-sdks` 输出 `8.0.404 [...]`，
   本阶段回归直接用这个 SDK，不需要"先安装 .NET 8 SDK 再重跑"。
   若要长期复现，把它移到固定的私有 SDK 目录或安装到系统，
   然后用 `Tools/find-dotnet.ps1` / `$env:STARDEW_DOTNET` 指向它；
   否则下一次会话又会退化成"无 SDK"。另外 `.tmp/` 里的旧构建状态会引起
   "时好时坏"的假失败（见证据索引 §7.1），必要时整体清掉重建。
3. **ARM64 物理设备**：仍然 BLOCKED，需要一台真正 arm64-v8a 且无 native bridge 的设备过 E0。
4. **获得 Emulator 数据后**：先只读比对 `loader-report.txt` 的 `preflight-stage=complete` 与
   首条 `assembly-load=` 顺序，再逐条记录 R-01…R-14，缺失项标 NOT RUN，不得补写。
5. **仍然禁止**：Harmony/detour、SMAPI 兼容层、事件 Hook、Content/Data API、热重载、存档读写、
   游戏玩法功能——这些都属于 Phase 1.2 及以后。

最终原则：先修复递归部署，再谈设备推送；先只读预检，再请求批准；
不卸载、不清数据、不覆盖其它 Mod、不碰 Saves；Emulator 不冒充 ARM64；由用户手动 `git push`。
