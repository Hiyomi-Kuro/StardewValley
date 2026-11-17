# 构建与安装验证（Phase 2.0.1）

本文只描述“怎么构建、怎么判断能不能装、装完怎么证明”，不重复事件契约。
事件契约见 [phase2-events.md](phase2-events.md)；阶段结论见
[Phase 2.0.1 报告](../audit/phase2.0.1-report.md)。

---

## 1. 三个 APK 不是同一个东西

| 角色 | 来源 | 用途 | 签名是否参与准入 |
| --- | --- | --- | --- |
| **内容 baseline** | `:app:assembleDebug` → `app/build/outputs/apk/debug/app-debug.apk` | 证明候选 APK 除 `assemblies.blob` 外的条目逐字节未变；记录固定哈希门禁 | **否**。它是构建产物，不是设备状态。 |
| **candidate** | `:app:assembleModdedDebug` → `app/build/outputs/apk/modded/app-modded-debug.apk` | 本轮要安装的产物 | 是（作为比较的一方） |
| **installed** | 设备 `/data/app/…/base.apk`（`pm path` 给出；安装前从设备拉回） | 当前设备实际运行/可升级的包 | 是（作为比较的另一方） |

旧逻辑（Phase 1.1.7）用 `candidate signer == baseline signer` 当准入，等于拿“构建参考”冒充“设备状态”：
baseline 与本机 key 不同就会误报不可升级；baseline 恰好相同又会放行一个设备根本装不上的包。
Phase 2.0.1 起，准入只由 candidate 与 installed 决定。

---

## 2. 构建

### 2.1 前置

- 私有 .NET SDK：`.tmp/dotnet-sdk/dotnet/dotnet.exe`（8.0.404；不改系统 PATH）；
- Android SDK：`ANDROID_HOME` / `%LOCALAPPDATA%\Android\Sdk`（build-tools、platform-tools）；
- JDK 11（`JAVA_HOME` 或默认路径）；
- 既有签名 key：**`.work/debug.keystore`**。mod 构建拒绝自动生成新 key：
  新 key 与被安装包签名不同，装上去只会 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`，把“新 key”伪装成“可升级包”是不允许的。

### 2.2 C（Loader + 事件）

```powershell
.\gradlew.bat --no-daemon :app:assembleModdedDebug `
  -Pdotnet=.tmp\dotnet-sdk\dotnet\dotnet.exe `
  -Ppython=python `
  -Pkeystore=.work/debug.keystore
```

任务链：`build_modded_apk.py`（提取 65 程序集 → `ModInjector patch/verify` → 只替换
`StardewValley` 条目 → 校验其余 57 条与容器结构）→ apktool 重建 → zipalign → apksigner（v1+v2+v3）。
输出与工作目录是 `.tmp/build-modded/`，与 A/B 隔离；事件开关是 Gradle input。

### 2.3 A（内容基线）与 B（Loader-only，回退）

```powershell
.\gradlew.bat --no-daemon :app:assembleDebug
.\gradlew.bat --no-daemon :app:assembleLoaderOnlyDebug -Pdotnet=... -Ppython=python -Pkeystore=.work/debug.keystore
```

B 只用于回退，不产生事件钩子。Phase 2.0.1 只改注入校验/诊断，不改变注入的 IL 形状，
因此本轮没有重做 B 的真机安装（避免无目的反复安装）。

---

## 3. 验证器：内容完整性 vs 安装准入

### 3.1 内容完整性（与设备无关）

`Tools/verify_modded_apk.py` 对 candidate 做：

- 文件事实：baseline 哈希固定门禁、candidate 与 baseline 必须不同；
- 包名/版本：`com.chucklefish.stardewvalley` / 239 / 1.6.15.0；
- 逐条目 CRC/大小：只允许 `assemblies/assemblies.blob` 与 `META-INF/` 变化；
- 最终 APK 解出的 `StardewValley.dll` 必须等于 `build_modded_apk.py` 的 patched 哈希，
  再从**最终 APK** 重新 `ModInjector verify`（事件钩子“应有/应无”）；
- zipalign、apksigner v1/v2/v3、源 blob/manifest 未被改。

### 3.2 安装准入（必须给 installed 证据）

```powershell
python Tools/verify_modded_apk.py `
  --candidate <candidate.apk> `
  --baseline  <baseline.apk> `
  --blob-manifest .tmp/build-modded/modded-build-manifest.json `
  --installed-apk <pulled base.apk> `
  --report <report.json> `
  --dotnet .tmp/dotnet-sdk/dotnet/dotnet.exe
```

可选的 installed 证据：

| 参数 | 含义 |
| --- | --- |
| `--installed-apk <path>` | 从设备拉回的 `base.apk`；读它的 SHA-256、包名/版本、签名 |
| `--installed-signer-sha256 <hex[,hex]>` | 已用其他方式测得设备签名时使用 |
| `--installed-apk-sha256 <hex>` | 设备的 `base.apk` 哈希（普通 shell `sha256sum`） |
| `--installed-absent` | 明确“设备没有安装该包”（首装） |
| `--installed-unreadable <原因>` | 签名/哈希读不到；分类为 UNKNOWN，不是 PASS |

判定表（`Tools/apk_admission.py`）：

| 状态 | 含义 | `adb_install_allowed` |
| --- | --- | --- |
| `INSTALL_CANDIDATE` | installed 与 candidate 签名一致 | 是 |
| `FIRST_INSTALL` | 明确未安装 | 是 |
| `UPDATE_INCOMPATIBLE_RISK` | 签名不一致，`-r` 会失败 | 否 |
| `DOWNGRADE_RISK` | installed versionCode 更高 | 否 |
| `PACKAGE_MISMATCH` | 包名/版本不符 | 否 |
| `UNSUPPORTED_MULTI_SIGNER` | 候选或设备有多个签名摘要；轮换 lineage 未实现 | 否 |
| `SIGNATURE_UNREADABLE` | 签名/哈希读不到 | 否 |
| `NOT_VERIFIED` | 没提供 installed 证据 | 否 |

- baseline 签名与 candidate 不同只记录在 `baseline_matches_candidate`，**不参与**准入；
- 没有 installed 证据 → 检查项状态 `UNKNOWN`、报告 `NOT_VERIFIED`，不是 PASS，也不是 FAIL；
- `post_install_hash_matches_candidate` 是独立的后置证明：安装后设备 APK 哈希是否等于 candidate；
  安装前它应当是 UNKNOWN。

### 3.3 多 signer / 轮换

本阶段**没有实现**多 signer 与 v3 轮换 lineage。出现多于一个签名摘要时一律
`UNSUPPORTED_MULTI_SIGNER` 并拒绝准入，不会“只比较第一个摘要”。这是明确的 unsupported，
不是“已验证通过”。

---

## 4. 安装：默认只读，写入需显式参数

### 4.1 无 Root 原位更新（推荐）

```powershell
# 默认：只读预检，打印将执行的命令，不写设备
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk-safe.ps1 -Serial 6d7200a

# 显式批准后才安装（同签名 -r）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk-safe.ps1 `
  -Serial 6d7200a -ApkPath app\build\outputs\apk\modded\app-modded-debug.apk `
  -InstallApproved -OutFile .tmp/phase2.0.1/.../install.txt
```

行为：

1. 解析 adb/apksigner（不写死用户目录）；
2. 设备必须显式 `-Serial`，不会猜；
3. 读 installed 的包/版本/签名（必要时 `adb pull` 当前 `base.apk`）；签名不可读 → 停在写入前；
4. 签名相同才 `adb install -r --no-incremental`；
5. 安装成功后：`pm path` **恰好一条** `package:`（split → `UNSUPPORTED_SPLIT_APK`），
   对完整路径执行**普通 shell** `sha256sum`；退出码必须 0、输出必须是完整 64-hex 行、
   必须等于本地 APK 哈希；否则 `INSTALLED_BUT_DEVICE_APK_UNVERIFIED`（exit 4）；
6. 没有任何 uninstall / `pm clear` / force-stop / 启动路径。

### 4.2 历史破坏式脚本

`Tools/deploy-apk.ps1` 仍保留用于审计，但：

- 默认（不带 `-LegacyDestructiveInstall`）在写设备前退出；
- 即使带了该开关，还必须给 `-SavesBackupRoot <存在且非空的备份目录>` 和 `-SavesBackupConfirmed`，
  否则拒绝；
- 安装后哈希证明改为普通 shell（不再 `su`），并拒绝 split 布局。

用户对“本轮安装”的授权不等于“脚本可以永久默认卸载”。卸载仍然是一次性的、显式的、
带备份确认的动作。

### 4.3 Root/私有目录 Mods

`Tools/deploy-apk-safe.ps1` 的 `-PushModsApproved` 是另一条历史路径（写应用私有目录，需要 Root）。
本项目 Loader 使用**外部** `files/Mods`，由 `Tools/deploy-mods-safe.ps1` 负责，无需 Root，
与本节的 APK 哈希验证互不混用。

---

## 5. 安装后验证清单

对一次真实安装，至少保留：

1. `verify_modded_apk.py` 的安装前报告：准入 `INSTALL_CANDIDATE`，且明确写出来源是 installed；
2. `deploy-apk-safe.ps1` 的输出：`signature-gate: SAME`、`Success`、`post-install: MATCH`、
   完整 64-hex 哈希；
3. 安装后从设备拉回 `base.apk`，再跑一次验证器：`post_install_hash_matches_candidate=true`；
4. `adb shell pm path` 的原始输出（一条还是多条）、`sha256sum` 的完整行；
5. 设备包版本/签名/更新时间的只读采集；
6. 若更新了 Runtime：重启应用进程后再验收事件（不能依赖 `Assembly.LoadFrom` 在同身份下替换字节）。

Phase 2.0.1 的真实结果见 [真机验收报告](../audit/phase2.0.1-device-acceptance-report.md)：
candidate `c571a25b…`、installed 前后 `d4f60d57… → c571a25b…`、签名均为 `858ef62d…`。

---

## 6. 已知限制

- 只支持一台 V1963A / Android 10 / API 29 的实测；不外推到 Android 11+。
- 多 signer / v3 轮换 lineage 未实现，遇到即拒绝。
- `pm path` 的多 APK（split）布局未支持，明确拒绝而不是猜 `base.apk`。
- 验证器的 UNKNOWN/BLOCKED 不是构建失败；它们表示“不能据此安装”，退出码仍可为 0。
- 构建产物不是字节可复现（注入会生成新 MVID），每次构建的 APK 哈希都不同；报告必须绑定“这一次”的哈希。
