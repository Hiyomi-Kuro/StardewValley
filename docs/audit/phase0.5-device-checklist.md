# Phase 0.5 设备检查清单（ARM64 真机 E0 / E1 / E2）

- 日期：2026-11-05
- 适用：`com.chucklefish.stardewvalley` 1.6.15.0（versionCode 239），.NET for Android / MonoVM 8.0.1124
- 当前状态：**E0 可在模拟器执行（已完成）；E1/E2 = BLOCKED（无 ARM64 物理设备）**
- 关联：`phase0.5-report.md` §7、`phase0.5-evidence-index.md` §2.6

> 铁律：
> 1. **模拟器结果永远填在 Emulator 列**，不能计入 Device 列；
> 2. 任何"卸载 / 安装 / 替换 / 授权 / 改游戏目录"的动作，**必须先拿到用户明确批准**；
> 3. 不得清应用数据、不得卸载用户原游戏、不得触碰 `Saves/`；
> 4. 任何崩溃实验只能用**可恢复的测试副本**，并写清回滚路径。

---

## 0. 状态取值（只能用这四个）

| 值 | 含义 |
| --- | --- |
| **PASS** | 真实 ARM64 设备上有直接证据（日志/文件哈希/截图/进程状态） |
| **FAIL** | 真实设备上复现失败，且有完整日志 |
| **BLOCKED** | 因设备、授权、签名、权限或环境条件无法执行，并写明是哪一条 |
| **NOT RUN** | 本阶段未尝试，并写明原因 |

模拟器单独一列，值同样用这四个，但**不计入** Device 列。

---

## 1. 前置条件（逐项打勾后才允许进入 E1）

| # | 条件 | 检查方法 | 当前 |
| --- | --- | --- | --- |
| 1 | 一台**物理 ARM64** Android 设备（非模拟器、非云手机、非 x86_64） | `adb shell getprop ro.product.cpu.abi` = `arm64-v8a`；`ro.dalvik.vm.native.bridge` 为空/`0`；`ro.kernel.qemu` 为空 | ❌ 无 |
| 2 | 设备系统 ≥ Android 7（本项目 APK minSdk 21，targetSdk 34） | `getprop ro.build.version.sdk` | — |
| 3 | 设备上**已备份存档** | 复制 `/sdcard/Android/data/com.chucklefish.stardewvalley/files/Saves` 到电脑 | 未做 |
| 4 | 用户明确批准"卸载原游戏 + 安装自签名测试包"（**会丢存档**，签名与原版不同） | 书面确认 | 未获得 |
| 5 | 本地已构建基线 APK（回滚用） | `app/build/outputs/apk/debug/app-debug.apk`，SHA `65e1e2af…` | ✅（388,967,580 字节） |
| 6 | `.tmp/phase0.5/refs/` 有 65 个程序集 | `(Get-ChildItem .tmp\phase0.5\refs -Filter *.dll).Count` = 65 | ✅ |
| 7 | 私有 SDK 可用 | `.tmp/phase0.5/dotnet/dotnet.exe --list-sdks` | ✅（8.0.404） |
| 8 | 测试包已生成 | `.tmp/phase0.5/out/sv.full.dll` + `assemblies.full.blob` + `mods/*.dll` | ✅（未安装） |
| 9 | `adb` 可连该设备且 `su` 状态已知 | `adb devices -l`；`adb shell su -c id` | **不得假定 root** |

> 如果 1–4 任一不满足 → E1/E2 记 **BLOCKED**，只做 E0，并停止所有写操作。

---

## 2. E0：只读预检（任何设备都可以先做）

### 2.1 命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\device-precheck.ps1 `
    -Serial <serial> -OutFile .tmp\phase0.5\logs\e0\device-precheck-<serial>.txt
```

脚本只做 `getprop / pm path / dumpsys / ls / sha256sum`，**不安装、不卸载、不清数据、不 force-stop**。
它会自己给出 `device-class`（`ARM64-PHYSICAL` / `NOT-ARM64-PHYSICAL`）和 E1/E2 的初步判定。

### 2.2 记录表（抄进结果）

| 项 | 值 |
| --- | --- |
| 采集时间 / 操作者 | |
| serial / transport | |
| `ro.product.cpu.abi` | |
| `ro.product.cpu.abilist` | |
| `ro.dalvik.vm.native.bridge` | |
| `ro.kernel.qemu` | |
| Android 版本 / API / 安全补丁 | |
| 厂商 / 型号 / device / fingerprint | |
| `device-class`（脚本判定） | |
| root 状态（`su -c id`） | |
| 游戏包是否安装 / versionCode / versionName | |
| 安装时间（first/lastUpdate） | |
| 设备上 APK 的 SHA-256 | |
| 本地基线 APK 的 SHA-256（对比） | |
| `Mods/` 是否存在 / 是否有 `loader.log` | |
| E0 判定 | PASS / FAIL / BLOCKED / NOT RUN |

**判定规则**

- `abi=arm64-v8a` 且无 native bridge、非 qemu → `ARM64-PHYSICAL`，E1 可以准备；
- `abi=x86_64` 或存在 native bridge（`libnb.so`/`libhoudini.so`/`libndk_translation.so`）→ **不是真机**，E1/E2 记 `BLOCKED`；
- 云手机即使上报 `arm64-v8a` 也不算物理设备（任务书明确排除），记 `BLOCKED` 并在备注写"cloud device"。

---

## 3. E1：真实游戏运行时加载并执行 C# DLL

> 前置：§1 全部满足 + 用户批准 + E0 = PASS + `device-class = ARM64-PHYSICAL`。

### 3.1 准备（不改仓库）

```powershell
# a) 确认原始 blob 备份存在（deploy 脚本的 finally 会用它还原仓库）
Get-FileHash .tmp\phase0.5\out\assemblies.original.blob -Algorithm SHA256   # 期望 c8ee504e…

# b) 构建并安装（唯一允许的装机入口；内部会：patch → replace → gradlew → 还原 blob → install → 哈希证明）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy-apk.ps1 `
    -Mode full -HookMethod OnCreatePartTwo -Serial <serial> -PushMods

# 若只想先验证"能构建"，不装：
#   ... -SkipInstall
```

`deploy-apk.ps1` 必须打印：

```
local apk  : <sha> (<bytes>)
device apk : <同一个 sha>
verdict    : installed APK == freshly built APK (proven by sha256)
```

**没有这三行就等于没证明装的是哪个包，E1 不成立。**

### 3.2 启动与抓日志

```powershell
adb -s <serial> logcat -c
adb -s <serial> logcat -b all -s monodroid:V MonoDroid:V DOTNET:V mono:V houdini:V AndroidRuntime:E > .tmp\phase0.5\logs\e1\logcat.txt
adb -s <serial> shell "am start -W -n com.chucklefish.stardewvalley/com.chucklefish.stardewvalley.MainActivity"
# 让游戏进入标题界面后再停 logcat（Ctrl+C）
```

### 3.3 取回文件证据

```powershell
# 应用私有目录需要 root；没有 root 就改放应用可写的外部目录（见 AGENTS.md 10.3 #8 的坑）
adb -s <serial> shell "su -c 'cat /data/data/com.chucklefish.stardewvalley/files/Mods/loader.log'"
adb -s <serial> shell "su -c 'cat /data/data/com.chucklefish.stardewvalley/files/Mods/loader-report.txt'"
adb -s <serial> shell "su -c 'cat /data/data/com.chucklefish.stardewvalley/files/Mods/probe-output/probe.txt'"
```

同时记录：

| 记录项 | 值 |
| --- | --- |
| 设备 / 系统 / 游戏版本 | |
| APK SHA-256（本地 + 设备端，必须相同） | |
| `sv.full.dll` SHA-256 | 期望 `5521edc9…`（若重跑注入会变，记录实际值） |
| `ModFramework.Runtime.dll` SHA-256 | 期望 `8b77214e…` |
| 注入时刻 / 启动时刻 | |
| `loader.log` 首行 | 期望 `loader-entered <ISO8601>` |
| `loader-report.txt` 的 `locate=` / `probe-run=` | |
| `probe.txt` 各用例结果 | |
| 是否进入标题界面 / 是否崩溃 | |
| `JavaProxyThrowable` / `monodroid-assembly` 关键字 | 期望**没有** |

### 3.4 通过标准（全部满足才算 PASS）

1. 进程运行在 **ARM64 物理设备**上（§2 的 E0 记录支撑）；
2. `Bootstrap` 被触发（证据：`loader.log` 出现 / 或 `--no-catch` 构建下 logcat 出现 `bootstrap-ran` 阶段的异常）；
3. `Assembly.LoadFrom` 路径可用（`loader-report.txt` 里 `locate=` 为真实路径、无 `!MissingMethodException`）；
4. `Loader.Start()` 执行且探针入口执行（`probe.txt` 存在且含 T00–T07 行）；
5. 游戏进入标题界面，无 `FATAL` / `monodroid-assembly` 错误；
6. §3.3 所有记录项齐全、APK 哈希双端一致。

任一条不满足 → **FAIL**（有完整日志）或 **BLOCKED**（条件缺失），不得写"部分通过"。

### 3.5 失败时怎么定位（不新增工具）

| 现象 | 先看 | 说明 |
| --- | --- | --- |
| 游戏正常但无 `loader.log` | `Mods/` 是否真实存在、`Loader` 是否被调用 | 用 `--no-catch` 重建一次，异常会进 logcat |
| `stage-1:file-missing` | `Mods/ModFramework.Runtime.dll` 在**应用进程内**是否可读 | root 创建的外部目录应用可能读不到（Phase 0 的坑） |
| `stage-2:assembly-null` | DLL 是否完整（哈希）、ABI 是否匹配 | |
| `TargetInvocationException` | `loader-report.txt`（新 Loader 会落盘） | 旧 Loader 会丢报告，这也是本阶段修它的原因 |
| 原生崩溃、无托管栈 | `logcat -b crash` + `houdini` 行 | T12 类问题；不要在这条路上盲试 |

---

## 4. E2：游戏类型反射只读访问

> 前提：E1 = PASS。

1. 只读枚举 `StardewValley.Game1` 的**类型/成员**：
   - 允许：`GetType` / `GetMethods` / `GetFields` / `GetProperties`（只枚举）；
   - 允许：读**不会改变状态**的静态字段/属性（案例：`Game1.IsMultiplayer`，Phase 0 已用 `allowGameInvocation=false` 跑过）；
   - **禁止**：`new Game1()`、调用 `Update/Draw/DayStarted` 之类会推进世界的成员、写字段、改存档。
2. 记录：

| 记录项 | 值 |
| --- | --- |
| 程序集名 / 版本 | 期望 `StardewValley 1.6.15.24354` |
| `Game1` 是否能解析 | |
| 方法数 / 字段数（反射口径） | 模拟器记录为 576 / 532，**真机需重新记录，不得复用** |
| 实际读取的成员名与值（若有） | |
| 失败项与异常类型 | |
| E2 判定 | PASS / FAIL / BLOCKED / NOT RUN |

> 模拟器的 576/532 **不能**填进真机列；口径也不同（反射 vs 声明，见 `decompile-report.md` §4）。

---

## 5. 回滚路径（预先确认，出事照做）

| 场景 | 动作 |
| --- | --- |
| 只是验证失败，想回基线 | `adb -s <serial> install -r app\build\outputs\apk\debug\app-debug.apk`（同签名才能覆盖；本工程一直用同一个 debug 证书，通常可以） |
| 覆盖失败（签名不符） | 先备份 `Saves/`，再 `adb uninstall com.chucklefish.stardewvalley`，然后装基线 APK，最后把 `Saves/` 还原 |
| 仓库 blob 被临时替换 | `powershell -File Tools\deploy-apk.ps1 -Restore`（从 `.tmp/phase0.5/out/assemblies.original.blob` 还原），并核对 `c8ee504e…` |
| 设备起不来 | `adb shell am force-stop` + 重新 `install` 基线；不要在设备上删 `/data/data/...` |

**禁止**：`adb shell pm clear`、`adb uninstall` 后不还原存档、手动改 `/sdcard/Android/data/.../files/Saves`。

---

## 6. 结果记录总表（复制到报告里填）

| 项目 | 源码存在 | 离线测试 | Emulator（MuMu 12, x86_64） | Device（ARM64 物理） | 最终状态 |
| --- | --- | --- | --- | --- | --- |
| Bootstrap 触发 | 是 | PASS | PASS（Phase 0 记录） | **BLOCKED** | PARTIAL |
| Loader 初始化 | 是 | PASS | PASS（记录） | **BLOCKED** | PARTIAL |
| Assembly.LoadFrom | 是 | PASS | PASS（记录） | **BLOCKED** | PARTIAL |
| C# Mod 入口执行 | 探针入口有 / Mod 入口无 | PASS（探针路径） | PASS（探针） | **BLOCKED** | NOT IMPLEMENTED |
| Game1 反射只读访问 | 是 | Unknown | PASS（576/532） | **BLOCKED** | PARTIAL |
| ModInjector | 是 | PASS | N/A | N/A | DONE（离线） |
| AssemblyStore 重建/校验 | 是 | PASS | N/A | N/A | DONE（离线） |
| Harmony / native detour | 是 | N/A | FAIL（T12） | **BLOCKED** | FAIL（当前工具链） |

签名：

- 执行人 / 日期：
- 设备型号 / serial：
- 附：`logcat.txt`、`loader.log`、`loader-report.txt`、`probe.txt`、APK 哈希证明

---

## 7. 本阶段的 E0/E1/E2 结论（已填）

| 项 | Emulator | Device | 备注 |
| --- | --- | --- | --- |
| E0 只读预检 | **PASS**（`.tmp/phase0.5/logs/e0/device-precheck-127.0.0.1_16384.txt`） | **NOT RUN** | 无物理设备 |
| E1 加载并执行 C# DLL | **PASS**（Phase 0 记录，D 级证据） | **BLOCKED**：`no ARM64 physical device` | 未重跑（未获装机授权） |
| E2 反射访问 `Game1` | **PASS**（同上） | **BLOCKED**：同上 | 576/532 不可外推到真机 |

> 依据：当前唯一可用设备 `127.0.0.1:16384` 的 `ro.product.cpu.abi = x86_64`、
> `ro.dalvik.vm.native.bridge = libnb.so`（ARM 翻译层），`device-class = NOT-ARM64-PHYSICAL`。
