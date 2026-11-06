# Phase 1.1.6 报告：Approval-Gated Emulator P114 Deployment 与 MonoVM Runtime Characterization

- 日期：2026-11-06 / 07（跨零点）
- 本阶段基线：`6e90ef2ac0e1d52832ad3564d4234640cc535d14`（`main`）
- 本阶段性质：本地核对 + 只读 E0 + **经用户明确批准的真实设备推送（A）** + **经用户另行明确批准的启动与只读取证（C）** + 文档纠错。
- 未安装/覆盖/卸载 APK、未执行 `pm clear`/`force-stop`、未读写 Saves、未运行 T11/T12、未做 detour。
- 证据等级沿用 [`phase0.5.1-evidence-index.md`](phase0.5.1-evidence-index.md) §0：
  Desktop .NET / AndroidRefsDir / Emulator / ARM64 physical / CI。每条结论都标注来源等级。
- **时钟说明**：模拟器系统时钟比宿主慢约 42–43 分钟（宿主 `00:11` 时设备 `23:28`）。
  下文时间一律为**设备时间**（UTC+8），与 logcat 可直接对齐。

---

## 0. 总状态

**PARTIALLY COMPLETE。**

- **部署与启动都成功**：11 个文件真实写入模拟器并逐文件校验；游戏冷启动到标题界面，未加载存档。
- **MonoVM 运行时表征 BLOCKED**：装机 APK 是**未注入 Bootstrap 的基线包**（§5），
  因此没有 `loader-report.txt`，也没有 `probe-output/phase1.1.4.txt`；R-01…R-14 只能如实记为 BLOCKED。
- 部署后还发现一条新的设备行为：游戏冷启动时把**内部** `files/Mods/` **迁移到外部**
  `/sdcard/Android/data/<pkg>/files/Mods/`（§6.6）。这是真实观察，不是推断。

| 主题 | 状态 | 说明 |
| --- | --- | --- |
| Git 基线无损核对 | ✅ COMPLETE | `HEAD` = `origin/main` = `6e90ef2`；ahead 0 / behind 0 |
| Phase 1.1.5 文档纠错 | ✅ COMPLETE | 95/95 与 39+56、分组 39/9/14/9/2/8/10/4、本机 SKIP 与 CI PASS、SDK 8.0.404、手动 push 后记 |
| Bundle 复核 | ✅ COMPLETE | 11 文件 / 172,191 字节；逐文件 SHA-256 与 manifest 一致 |
| E0 只读预检 | ✅ COMPLETE | `E0: PASS (read-only)`；`EMULATOR-TRANSLATED`；双端 APK SHA 相同 |
| A 推送批准 | ✅ GRANTED | 用户在本轮明确批准 **A + C** |
| 推送 | ✅ PUSHED / VERIFIED | 11/11 文件、hash、属主 `10152:10152`、文件 `0644`、目录 `0755`、staging 已清理 |
| B 覆盖批准 | ➖ NOT APPLICABLE | 目标 `Mods` 原本不存在，无 hash 冲突 |
| C 启动批准 | ✅ GRANTED | 同上 |
| 启动 | ✅ 到标题界面 | `am start -W` Status ok / COLD；截图见 §6.4；**未点击任何按钮** |
| Loader / P114 运行时证据 | ⛔ BLOCKED | 两处 Mods 都没有 `loader-report.txt` / `probe-output`；原因为未注入 Bootstrap |
| Emulator R-01…R-14 | ⛔ BLOCKED | 见 §7；逐项写明缺失的前置条件 |
| ARM64 physical | ⛔ BLOCKED | 无合格物理设备；Emulator 不得计入 |
| 本地回归 | ✅ COMPLETE | 8/8 套件 PASS |
| 远程 CI | ✅ OBSERVED | `6e90ef2` 的 run `42238545103`（#7）completed / success，3/3 jobs |

一句话：**"能推上去、能校验、能启动到标题界面"这三件事已经在真实模拟器上闭环；
但"让 MonoVM 真跑 P114"仍被一个装机前置条件挡住——当前 APK 里没有 Bootstrap，
所以这次拿到的是"部署与启动成功 + Loader 缺失"的真实负向证据，而不是 R-01…R-14 的运行值。**

---

## 1. Git 基线与远程状态

### 1.1 本地/远程核对（Gate 0）

| 项 | 值 |
| --- | --- |
| 仓库 | `D:\Download\DSH\StardewValley` |
| remote | `https://github.com/Hiyomi-Kuro/StardewValley.git` |
| branch | `main`（跟踪 `origin/main`） |
| HEAD | `6e90ef2ac0e1d52832ad3564d4234640cc535d14` |
| `origin/main`（fetch 后） | 同左 |
| ahead / behind | **0 / 0** |
| 开始时工作区 | 干净（0 modified / 0 untracked） |
| `6e90ef2` 相对父提交 | 9 文件（7 M + 2 A），+1946 / −68 |

无损协调结论：不需要 merge/rebase/reset/checkout/stash/clean；全程未执行破坏性 git 操作。

### 1.2 GitHub Actions（对 `6e90ef2` 的只读查询）

| 项 | 值 |
| --- | --- |
| run ID | `42238545103`（#7） |
| event / head_sha | `push` / `6e90ef2` |
| 状态 | `completed` / `success` |
| jobs | 3/3 success：offline checks、dotnet build + loader host tests、powershell gates |

> 证据边界：job 状态来自只读 REST API；**job 日志未重新下载**（未认证访问 logs 返回 403，本机无 `gh`）。
> "CI 上 95/95、两条 reparse-point 真实 PASS"引用该 run 的记录；本地复跑是 93 条断言 + 2 条 SKIP（§2）。

---

## 2. Phase 1.1.5 文档纠错（最小差异，不改写历史时间线）

| # | 位置 | 修正前 | 修正后 |
| --- | --- | --- | --- |
| 1 | `phase1.1.5-report.md` §5 标题 | 旧 39 + 新 54 = 93 | 旧 39 + 新 **56** = **95** |
| 2 | 同上 §5.2 分组表 | `39+9+10+3+6+2+8+10+4 = 91` | `39+9+14+9+2+8+10+4 = 95` |
| 3 | 同上 §5.2 symlink 说明 | "本机 SKIP……仍按 PASS 计" | 本机 2 条 SKIP（真实断言 93）；CI runner 上 2 条真实 PASS；两者分开陈述 |
| 4 | 同上 §13 | 只写 DSH 结束时 `ab1e10cb`、未 commit | 保留原文，追加 §13.1：`6e90ef2` + run `42238545103`，注明用户手动 push |
| 5 | 同上 §14 | 工具链说明 | 追加：SDK 路径仍存在，`--list-sdks` = 8.0.404，无需"先装 SDK 再重跑" |
| 6 | `phase1.1.5-evidence-index.md` §1 #1 | 95/95（39+56） | 追加本机 2 条 SKIP / CI 真实 PASS 口径 |
| 7 | 同上 §0 CI 行 | "本阶段改动 NOT RUN（未 push）" | 保留原表，追加 1.1.6 后记（用户手动 push + CI 3/3） |
| 8 | `Tools/README.md` 离线回归表 | 部署门禁 38 | **95** |
| 9 | `Mod/P114.RuntimeProbe/README.md` | 仅部署/启动步骤 | 新增 §4.1：装机 APK 未注入 Bootstrap 的前置条件与影响 |

### 2.1 重建后的分组统计

| 分组 | 项数 | 结果 |
| --- | ---: | --- |
| Phase 1.1.4 回归 | 39 | 39/39 |
| 计划与门禁（含 2 条 reparse-point） | 9 | 9/9（本机 2 条 SKIP） |
| 递归目录树（含 manifest 位置、fixture、staging 清理、verify-*） | 14 | 14/14 |
| 覆盖保护 | 9 | 9/9 |
| 特殊路径 | 2 | 2/2 |
| 失败与清理 | 8 | 8/8 |
| 设备门禁 | 10 | 10/10 |
| 无破坏性 | 4 | 4/4 |
| **合计** | **95** | **95/95（本机 93 真实断言 + 2 SKIP）** |

> 旧表的 `10 + 3 + 6` 切错了组：`push_reports_hash_verification` 一条同时断言
> `verify-staged 12/12` 与 `verify-remote 12/12`，`push_reports_owner_verification` 单独一条，
> 它们属于递归树组；覆盖保护实际 9 条。

### 2.2 本机 SKIP 与 CI PASS

| 环境 | 符号链接 | 两条断言的含义 |
| --- | --- | --- |
| DSH 本机（非管理员/无开发者模式） | 创建失败 | **SKIP**：只证明测试宿主限制 |
| GitHub Windows runner | 创建成功 | **PASS**：`exit=3` + `reparse-point`，且无 push |

本阶段没有为了把 SKIP 变 PASS 去改脚本或改宿主权限，只把口径写清楚。

> 提示词提到的 §12 "39 项 + 54 项新增"：当前仓库实际是 "39 项 + **56** 项新增"，与 95/95 自洽，
> 因此**未改**；没有为了对齐提示词而把 56 改回 54。

---

## 3. Bundle 复核（P116-03）

部署源：`tasks/20261106-p115-recursive-deploy-a1/outputs/bundle-build/Mods`
（`bundle-manifest.json` SHA-256 `d1e82c04…f63dc`，`fileCount=11`，`totalBytes=172191`）。

本阶段重新逐文件哈希，与 manifest 及 Phase 1.1.5 报告 §4.2 逐字符一致：

| 相对路径 | 字节 | SHA-256 |
| --- | ---: | --- |
| `ModFramework.Runtime.dll` | 118272 | `a35a03597e88c38804ad75b91a5df25e77e9a2a19c7934508701833ae3d90fea` |
| `P114.RuntimeProbe/manifest.json` | 607 | `05b3bb87bb700e7fa222983c4823294e0ae8ae023e9928b21e82de4204eabc3a` |
| `P114.RuntimeProbe/P114.RuntimeProbe.dll` | 22016 | `e03e175266d559ec60dc5307789f0f5d69d7ccc78f2c9a9ef8764f5235dfcdf1` |
| `P114.RuntimeProbe/P114.RuntimeProbe.Dependency.dll` | 5120 | `4a878c03511571d4e084824d2d585dc45561c2469558302d762c33aa3c890883` |
| `.../probe-fixtures/same-a/P114Same.dll` | 4096 | `73154fe8d1be1ef6cfde17ce979910196b6ad522cc9c2192a5ac488edd9b4c22` |
| `.../probe-fixtures/same-b/P114Same.dll` | 4096 | `73154fe8d1be1ef6cfde17ce979910196b6ad522cc9c2192a5ac488edd9b4c22` |
| `.../probe-fixtures/diff-a/P114Diff.dll` | 4608 | `2ccdee8fd018f4cb289bf71cd879392a8748bf37621e57f5cd32828cbae967f4` |
| `.../probe-fixtures/diff-b/P114Diff.dll` | 4608 | `175ae9ef74496478befdd77360ed86ea670d8fbaeca32acbea3bdfef8827ddf2` |
| `.../probe-fixtures/alpha/P114Alpha.dll` | 4608 | `0d0f54686e9639c3016c1e825bae2df9ea11b373b547c756f6c00528c4b77bdf` |
| `.../probe-fixtures/beta/P114Beta.dll` | 4096 | `f4097e4632521ee4874e3124a100ddeb2edb57ceda8c39707826630d2399a03d` |
| `.../probe-fixtures/corrupt/P114Broken.dll` | 64 | `f5a5fd42d16a20302798ef6ed309979b43003d2320d9f0e8ea9831a92759fb4b` |

- 同目录的 `bundle-reuse-p114-artifacts`（11 / 170,655）是旧产物对照，**未使用**。
- Fake ADB 测试树 12 路径（多 `probe-fixtures/nested/deep/one.txt`），不是生产 bundle。
- SDK：`work/dotnet/dotnet.exe`，`--list-sdks` = `8.0.404`。

---

## 4. Gate 1：只读设备 E0（P116-04）

| 项 | 本次观察值 |
| --- | --- |
| serial | `emulator-5554`（`device`，`transport_id:1`） |
| 型号 | `PD2309` / `V2309A` / `PD2309`（`vivo`） |
| Android / API | 12 / 32（patch 2022-04-05） |
| ABI | `x86_64`；abilist `x86_64,arm64-v8a,x86,armeabi-v7a,armeabi` |
| native bridge | `libnb.so`（启动日志里实际加载的是 houdini 翻译层） |
| `ro.kernel.qemu` | 空（不能靠它判模拟器） |
| device-class | **EMULATOR-TRANSLATED / NOT-ARM64-PHYSICAL** |
| 包 / 版本 | `com.chucklefish.stardewvalley`，**239 / 1.6.15.0**，`userId=10152` |
| 设备端 APK SHA-256 | `65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d`（388,967,580 B） |
| 本地 APK SHA-256 | **同左** |
| 本地签名摘要 | `611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de` |
| app files 目录 | `10152:10152`，mode `771`；仅 `.config`、`.local` |
| `files/Mods`（预检时） | **不存在** |
| 现存 Mod | 无 |
| `/data` 可用空间 | `avail=27664818 × 4096` ≈ **105.5 GiB** |
| root / SELinux | `su` = uid 0 / `Permissive` |
| 必需命令 | `sha256sum stat mkdir cp chown chmod rm ls id test du tar unzip` 全在 |
| Saves | 只 `test -d`，未列出/读取 |
| 结论 | `E0: PASS (read-only)`；`E1/E2: BLOCKED reason=no-arm64-physical-device` |

推送前部署器二次门禁（`-PushModsApproved` 内部）：

```
version-gate    : OK 239 / 1.6.15.0
apk-gate        : OK 65e1e2af…051f9d (device == local)
owner-gate      : OK app uid:gid=10152:10152 files-dir-mode=771
tool-gate       : OK 8/8 device commands present
collision-check : 0 identical (skipped), 11 to write, 0 conflicting
```

---

## 5. 关键前置条件：装机 APK 未注入 Bootstrap

| 检查 | 结果 |
| --- | --- |
| APK 条目总数 | 3672 |
| `ModFramework` / `P114` / `MonoRuntimeProbe` 条目 | **0** |
| APK 内 `assemblies/assemblies.blob` | 5,588,326 B，SHA-256 `c8ee504eea163d2c82d89d0004c8924314b3e6b41adb649ea5f136151eecabd9` |
| 仓库 `app/src/main/apkroot/assemblies/assemblies.blob` | **同一 SHA-256**（原始、未注入） |

设计契约（`docs/architecture/phase1-loader-design.md` §9）：Bootstrap 的 IL 注入在
`StardewValley.dll` 内，由它在 `OnCreatePartTwo` 首条指令前
`File.Exists(<Mods>/ModFramework.Runtime.dll)` → `Assembly.LoadFrom` → `Loader.Run`。

既然容器还是原始基线，就**没有随 APK 分发的代码**会去读 Mods；因此本次启动后
**没有** `loader-report.txt`、`loader.log` 或 `probe-output/phase1.1.4.txt`（§6.5 实测）。
这不是 P114 mod 或部署脚本的缺陷，而是"运行时装不装 Bootstrap"的前置条件缺失。

---

## 6. 执行记录（A + C）

### 6.1 批准

用户在本轮明确选择 **「批准 A + C」**：
A = 把 §3 的 11 个文件递归写入 `emulator-5554` 的目标目录并校验；
C = 启动到标题界面并按批准范围只读取证。B（覆盖）不适用。

### 6.2 推送（A）

命令（唯一入口，未手工 `adb push`/`cp`）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy-apk-safe.ps1 `
  -PushModsApproved -Serial emulator-5554 `
  -ModsSourceDirectory D:\Download\DSH\tasks\20261106-p115-recursive-deploy-a1\outputs\bundle-build\Mods
```

未传 `-InstallApproved`、未传 `-OverwriteModsApproved`。设备时间 `23:26:05–23:26:17`。

关键输出：

```
staging         : /data/local/tmp/svmods-9d4ce611681a
verify-staged   : 11/11 staged files match the local SHA-256
dirs-created    : 10 (owner 10152:10152, mode 0755)
verify-remote   : 11/11 deployed files match the local SHA-256
verify-owner    : 11/11 files owned by 10152:10152, mode 0644
verify-dirs     : 10/10 created directories owned by 10152:10152
staging-cleanup : removed /data/local/tmp/svmods-9d4ce611681a
verdict         : PUSHED
```

### 6.3 推送后独立复核（不依赖脚本自述）

`23:26:35–23:26:44`，只用 root 只读命令重新枚举：

- 内部 `files/Mods` 下 **11 个文件**，相对路径与 §3 完全一致；
- 11 个 SHA-256 与 bundle **逐字符相同**；
- 文件 `10152:10152` / `0644`；目录 `10152:10152` / `0755`；
- `/data/local/tmp/svmods-*` 数量 = **0**（staging 已清理）。

### 6.4 启动（C）

```powershell
adb -s emulator-5554 shell am start -W -n com.chucklefish.stardewvalley/com.chucklefish.stardewvalley.MainActivity
```

设备时间 `23:26:54`，输出：

```
Status: ok
LaunchState: COLD
Activity: com.chucklefish.stardewvalley/.MainActivity
TotalTime: 1769
WaitTime: 1773
```

随后观测：

- 进程 `28116` 存活；`topResumedActivity` / `mCurrentFocus` = `com.chucklefish.stardewvalley/.MainActivity`；
- MonoGame 已初始化（日志有 `GraphicsDeviceManager.ResetClientBounds` 多次调用）；
- **没有** `FATAL` / `AndroidRuntime` / `am_crash` / native crash / ANR 行；
- 标题界面已由 `screencap` 记录（设备时间约 `23:30`，未点击任何按钮）。

> 截图证据（仓库外，DSH 任务目录）：
> `D:\Download\DSH\tasks\20261106-p116-emulator-p114-a1\evidence\title-screen-1.1.6.png`
> （SHA-256 `bf89e1c2f161402cdfdc09c1e21dc9d9935ce22780561a448aeb5999c7f5fcf7`）。
> 画面显示 `NEW` / `LOAD`，**没有**进入、创建或保存任何存档。

### 6.5 运行时文件读取结果（批准范围内）

| 路径 | 内部 `files/Mods` | 外部 `/sdcard/Android/data/<pkg>/files/Mods` |
| --- | --- | --- |
| `loader-report.txt` | ABSENT | ABSENT |
| `loader.log` | ABSENT | ABSENT |
| `P114.RuntimeProbe/probe-output/` | （内部目录已不存在） | ABSENT |
| `probe-output/phase1.1.4.txt` | ABSENT | ABSENT |

### 6.6 新发现：游戏把内部 `Mods/` 迁移到外部存储

启动后第一次复查时发现内部 `files/Mods` **整目录消失**；继续只读排查后确认不是丢失，而是**迁移**：

- 游戏进程日志（`23:26:56.048`，pid 28116）：
  `I/DOTNET: [SDV_INIT] GetExternalFilesDir passed! There are files/folders in GetFilesDir that need to be copied.`
- 迁移结果：`/sdcard/Android/data/<pkg>/files/Mods/` 下出现**同样 11 个文件、同样 SHA-256**，
  属主 `10152:1078`（`u0_a152:ext_data_rw`，应用自己的外部数据目录），文件 mode `644`、目录 `2770`；
- 内部 `files/` 仍保留 `.config`、`.local`（**不是**清数据），只有 `Mods/` 被搬走。

含义（对后续阶段重要）：

1. 这款游戏的运行期数据根看起来是 **外部** `files/`，内部 `files/` 被当作待迁移来源；
2. 因此"部署到内部 `files/Mods`"在第一次冷启动后会被搬到外部，**部署后的校验要在启动前做**；
3. 若未来要做"运行时读取 Mods"，必须先确定 Bootstrap 用的是哪个根（`FilesDir` 还是
   `GetExternalFilesDir`），并让部署路径与它一致；否则即使注入了 Bootstrap 也可能读不到；
4. 外部目录由游戏自己创建时属主/SELinux 上下文正确（与 AGENTS.md 10.3 第 8 条
   "root 手工创建的外部目录应用读不到"并不矛盾——那是手工创建的情况）。

### 6.7 当前设备最终状态

- `Mods` 内容：外部路径下 11 个文件（哈希同 §3）；内部路径不存在；
- `files/Mods` 与 `/sdcard/.../Mods` 均**没有** loader/probe 产物；
- 游戏进程仍在运行、前台为 `MainActivity`；未进行任何 force-stop / 清数据 / 卸载；
- Saves 未被读取、修改或创建。

### 6.8 回滚方案（若用户要求）

只删除**经证实由本阶段创建**的：

```
/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods/P114.RuntimeProbe/
/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods/ModFramework.Runtime.dll
```

保留空的 `Mods/` 根、保留 `Saves/`、保留其它任何文件；绝不卸载、绝不清数据。

---

## 7. R-01 … R-14（Emulator 全部 BLOCKED）

原因统一为：**装机 APK 无 Bootstrap → Loader 从未启动 → 没有 P114 探针输出**。
没有一项是"跑过但失败"，也没有一项被补写推测值。

| ID | Emulator | 缺失的前置条件 | Desktop .NET（参照，非外推） |
| --- | --- | --- | --- |
| R-01 | BLOCKED | 无 probe-output | PASS `len=32 hex=e3b0c442…b855` |
| R-02 | BLOCKED | 无 probe-output | PASS `hex=ba7816bf…0015ad` |
| R-03 | BLOCKED | 无 `loader-report.txt` | PASS（loader 侧）`preflight 17 < load 23`，`state=Initialized` |
| R-04 | BLOCKED | 无 probe-output | PASS `first=ok second=ok same-reference=True` |
| R-05 | BLOCKED | 无 probe-output | PASS `same-reference=True delta=0` |
| R-06 | BLOCKED | 无 probe-output | PASS `FileLoadException`（桌面行为，不预设 MonoVM 相同） |
| R-07 | BLOCKED | 无 probe-output | PASS `P114Alpha` / `P114Beta` |
| R-08 | BLOCKED | 无 probe-output | PASS `name=P114.RuntimeProbe version=1.1.4.0 token=null` |
| R-09 | BLOCKED | 无 probe-output | PASS `empty=False leaf=P114.RuntimeProbe.dll` |
| R-10 | BLOCKED | 无 probe-output | PASS `count=54 entry-index=49` |
| R-11 | BLOCKED | 无 probe-output | PASS `preloaded-before-entry=True` |
| R-12 | BLOCKED | 无 `loader-report.txt` | PASS（loader 侧）入口 `Location` 在 `.staging/…` |
| R-13 | BLOCKED | 无 probe-output | PASS `sha256=30cbe2…`（本次宿主构建，不等于设备 bundle 的 `e03e17…`） |
| R-14 | BLOCKED | 无 probe-output | PASS `BadImageFormatException` + `InvalidOperationException` 均被隔离 |

---

## 8. 安全边界与未执行动作

已执行（均在批准范围内）：`adb push` 11 个文件、`su` 下的 `mkdir/cp/chown/chmod/sha256sum/stat/rm -rf staging`、
`am start -W`、只读 `ls/stat/sha256sum/test -e/cat 日志/logcat 过滤/screencap`。

未执行：

| 动作 | 状态 |
| --- | --- |
| 安装/覆盖/卸载 APK | ❌ |
| `pm clear` / `force-stop` / 重启设备 | ❌ |
| 读取或改写 Saves | ❌（只做 `test -d`） |
| 进入/创建/保存存档、点击 NEW 或 LOAD | ❌（截图可见，未点击） |
| T11/T12、P/Invoke、mprotect、RWX、机器码、Harmony/detour | ❌ |
| `-OverwriteModsApproved` | ❌（无冲突，未传） |
| `git push` / GitHub 写 API | ❌（只读 GET） |

---

## 9. 测试与静态检查（全部真实执行）

| # | 命令 | 退出码 | 结果 | 日志 |
| ---: | --- | ---: | --- | --- |
| 1 | `test_deploy_safe.ps1` | 0 | 95/95（93 断言 + 2 SKIP） | `logs/test_deploy_safe-1.1.6.txt` |
| 2 | `test_device_precheck.ps1` | 0 | 10/10 | `logs/test_device_precheck-1.1.6.txt` |
| 3 | `test_ci_offline.py` | 0 | 13/13 | `logs/test_ci_offline-1.1.6.txt` |
| 4 | `test_p114_probe.py` | 0 | 46/46 | `logs/test_p114_probe-1.1.6.txt` |
| 5 | `test_loader.py`（SDK 8.0.404 + 65 refs） | 0 | 45/45 驱动 + 473/473 宿主 | `logs/test_loader-1.1.6.txt` |
| 6 | `test_mod_pipeline.py`（SDK 8.0.404 + 65 refs） | 0 | 27/27 | `logs/test_mod_pipeline-1.1.6.txt` |
| 7 | `run-all.ps1` | 0 | **8/8 套件 PASS** | `logs/run-all-1.1.6.txt`、`logs/run-all/` |
| 8 | `privacy-scan.py --include-untracked` | 0 | `RESULT: OK` | `logs/privacy-scan-1.1.6.txt` |
| 9 | `git diff --check` | 0 | 通过 | `logs/git-diff-check-1.1.6.txt` |

工具：PowerShell 5.1.26100.9711、Python 3.12.10、adb 37.0.1、apksigner build-tools 36.0.0、.NET SDK 8.0.404。

---

## 10. Git 工作区最终状态

- branch `main`，HEAD `6e90ef2`（**未 commit**）；
- `origin/main` = `6e90ef2`；ahead 0 / behind 0；
- 工作区仅有本阶段有意修改/新增的文档（§11）；
- **DSH 没有执行 `git push`**，也没有写任何远程 ref/commit/branch/tag。

---

## 11. 修改文件清单

| 文件 | 变更 |
| --- | --- |
| `docs/audit/phase1.1.5-report.md` | §5 标题 93→95；§5.2 分组表 39/9/14/9/2/8/10/4；SKIP 与 CI PASS 口径；§11 #1 补注；§13.1 后记；§14 SDK 复核 |
| `docs/audit/phase1.1.5-evidence-index.md` | §1 #1 口径；§0 CI 行后记 |
| `Tools/README.md` | 部署门禁计数 38→95 |
| `Mod/P114.RuntimeProbe/README.md` | 新增 §4.1 前置条件（未注入 Bootstrap） |
| `docs/audit/phase1.1.6-report.md` | 本文件（新增） |
| `docs/audit/phase1.1.6-evidence-index.md` | 证据索引（新增） |

未修改：`Tools/deploy-apk-safe.ps1`、`Tools/tests/*`、`Mod/**/*.cs`、`app/**`、`.github/**`、根 `README.md`。

---

## 12. Definition of Done 对照

| ID | 状态 | 证据 |
| --- | --- | --- |
| P116-01 | ✅ | §1 |
| P116-02 | ✅ | §2 |
| P116-03 | ✅ | §3 |
| P116-04 | ✅ | §4 |
| P116-05 | ✅ | 批准前零写入；批准后仅执行 A/C 范围内动作 |
| P116-06 | ✅ | §6.2/6.3：11/11 路径、hash、owner、permission 全部验证 |
| P116-07 | ✅ | 无冲突，未传 `-OverwriteModsApproved` |
| P116-08 | ✅ | §6.4：到标题界面，未加载存档，截图证据 |
| P116-09 | ⛔ BLOCKED | §7：每项都有明确原因；但阶段目标（真实 MonoVM 值）未达成 |
| P116-10 | ✅ | §8：无 uninstall/pm clear/force-stop/Saves/T11/T12/detour |
| P116-11 | ✅ | Emulator 与 ARM64 physical 分开；ARM64 仍 BLOCKED |
| P116-12 | ✅ | §9：8/8 套件 PASS，SKIP 单独说明 |
| P116-13 | ✅ | 本报告 + evidence index |
| P116-14 | ✅ | §10：DSH 未 commit/push |

---

## 13. 下一阶段建议（不自动启动 Phase 1.2）

1. **先修前置条件**：要拿到 R-01…R-14，必须有一个**注入 Bootstrap 的 APK** 并装机
   （`ModInjector` + 重建 assemblies 容器 + 重打包；装机属另一类批准，且卸载会丢存档）。
2. **同时确认 Mods 根**：本阶段证明游戏会把内部 `files/Mods` 搬到外部 `files/Mods`；
   Bootstrap 必须明确使用哪个根，部署路径要与之对齐，否则运行时仍然找不到。
   建议先在桌面/宿主用同一份 Loader 代码验证"外部根"路径语义，再上设备。
3. **保留本次负向证据**：`loader-report.txt` 缺失、`probe-output` 缺失、
   无 crash、标题界面可达——这些都说明"部署链路本身工作正常，缺的是运行时注入"。
4. **ARM64 physical**：仍 BLOCKED，需要真 arm64-v8a 且无 native bridge 的设备过 E0。
5. 仍然禁止：T11/T12 排障、Harmony/detour、SMAPI 兼容层、事件 Hook、Content/Data API、
   热重载、存档读写、玩法功能。

最终原则：先无损核对，再只读 E0；推送、覆盖、启动和运行时证据读取都受明确批准约束；
不碰 Saves；Emulator 不冒充 ARM64；DSH 永不自动 push。
