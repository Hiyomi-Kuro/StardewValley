# Phase 1.2：ARM64 物理设备验收报告

- 日期：2026-11-14（+08:00）
- 分支：main
- 验收基线：35c515a04e7c610085884fd5c1a7511b3bf77369（本地 HEAD 与 origin/main 一致）
- 设备：vivo V1963A / PD1963，Qualcomm，Android 10 / API 29
- 关联：[Phase 1.1.9 报告](phase1.1.9-device-acceptance-report.md)
- 本地证据索引：[Phase 1.2 证据索引](phase1.2-arm64-evidence-index.md)

---

## 0. 结论

**Phase 1.2 的 ARM64 真机 E1/E2 运行验收通过。**在一台 ARM64 物理设备上，安装 Mod-enabled APK、无 Root 部署 11 个 Mod 文件并启动游戏后：

- Loader：发现 1 个 Mod，初始化 1 个，失败 0，跳过 0，最终 result=ok。
- P114 RuntimeProbe：14 个测试用例中 12 项由探针直接执行，12 PASS / 0 FAIL / 0 BLOCKED；R-03 与 R-12 由 Loader 报告独立佐证，合计 **14 PASS / 0 FAIL**。
- 11/11 个部署文件在设备端 SHA-256 与本地源文件一致。
- 游戏 MainActivity 保持 resumed，进程仍在；进程过滤 logcat 未发现 FATAL EXCEPTION、SIGSEGV、UnsatisfiedLinkError 或 dlopen failed 等匹配项。
- Saves 目录文件数为 0；本阶段未创建存档、未清理数据、未卸载应用。

这是**一台设备、一个 Android 版本**上的真实 ARM64 运行证据，不等于覆盖所有 ARM64 设备或 Android 版本。

## 1. 设备与 APK

| 项 | 结果 |
|---|---|
| 设备型号 | vivo V1963A / PD1963 |
| ABI | arm64-v8a |
| Native Bridge | 0 |
| QEMU 标记 | 未设置 |
| SoC 平台 | Qualcomm |
| Android | 10 / API 29 |
| 安全补丁 | 2022-05-01 |
| Root | 不可用；全程未使用 Root |
| 已安装包 | com.chucklefish.stardewvalley |
| versionCode / versionName | 239 / 1.6.15.0 |
| targetSdk | 34 |
| primaryCpuAbi | arm64-v8a |

APK：final/StardewValley-1.6.15.0-modded-test-debug.apk

- 大小：389,561,500 B
- SHA-256：97c38ed7eb6ac719d7a5ee941abac3766307ad03205569bed19db5a48e7d5d90
- 证书 DER SHA-256：858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff
- 从设备 pm path 对应的 base.apk 拉取副本后，副本 SHA-256 与本地候选包完全一致，apksigner verify --print-certs 的 signer 指纹也一致。
- 安装前目标包未安装，应用外部数据目录不存在；因此本轮是首次安装，不需要卸载旧包，也没有旧 Saves/Mods 需要迁移。

## 2. 无 Root Mod 部署

Loader 实际使用游戏应用专属外部目录下的 files/Mods。现有 deploy-apk-safe.ps1 的 Mod 推送路径依赖 Root，并指向 app-private /data/data/...，不适用于本次无 Root 设备的 Loader 外部 Mods 根目录。因此本轮没有调用该脚本的 Root 推送分支。

本轮采用一次性、受控的无 Root 部署步骤：

1. 校验目标设备 ABI、包版本和已安装 APK 副本哈希。
2. 确认目标 Mods 根目录不存在，拒绝覆盖既有目录。
3. 先写入应用专属外部目录下的唯一 staging 目录。
4. 推送 11 个文件；逐文件执行设备端 sha256sum 与本地 SHA-256 比较。
5. 全部 staging 文件通过后，使用同文件系统目录重命名提升为 Mods，再逐文件复核最终哈希。
6. 目录权限保持应用 UID / sdcard_rw 组可访问；没有使用 su 或修改 Saves。

| 项 | 结果 |
|---|---:|
| 文件数 | 11 |
| 目录数 | 9 |
| 总字节 | 170,655 B |
| staging SHA-256 | 11/11 PASS |
| 最终 Mods SHA-256 | 11/11 PASS |
| Saves 文件数 | 0 |

**后续工具工作项：**将这条经过实机验证的无 Root staging / hash / atomic promotion 路径正式实现为受测试的部署工具，并修正现有安全部署工具的 Mods 目标路径；本轮没有修改部署工具源码。

## 3. E1：游戏启动与 Loader 初始化

设备上启动 com.chucklefish.stardewvalley/.MainActivity 成功。加载 Mod 后，MainActivity 仍为 resumed activity，应用进程持续存在。

Loader 报告关键结果：

| 指标 | 结果 |
|---|---:|
| game-version | 1.6.15（known） |
| framework-version | 1.0.0（known） |
| mods-discovered | 1 |
| mods-initialized | 1 |
| mods-failed / skipped | 0 / 0 |
| preflight assemblies | 2 |
| conflicts / rejected | 0 / 0 |
| preflight-stage | complete |
| convergence | converged，1 round |
| assembly snapshot | 2 files / 26,624 B |
| logging | healthy |
| final result | ok |

Mod 状态：P114.RuntimeProbe = Initialized；生命周期为 ManifestValidated > DependenciesResolved > AssemblyLoaded > EntryValidated > Instantiated > Initialized。

## 4. E2：P114 RuntimeProbe 结果

探针模式为 safe-only，不执行 P/Invoke、mprotect、RWX、detour 或游戏调用。

| 编号 | 结果 | 证据口径 |
|---|---|---|
| R-01 | PASS | SHA-256 空输入向量匹配 |
| R-02 | PASS | SHA-256 标准向量匹配 |
| R-03 | PASS | Loader 报告：预检完成先于入口程序集加载，Mod Initialized |
| R-04 | PASS | 同路径重复 LoadFrom 返回同一引用 |
| R-05 | PASS | 相同身份、相同字节、不同路径返回同一引用，AppDomain delta=0 |
| R-06 | PASS | 同身份不同字节时 MonoVM 返回已加载实例，marker 保持 variant A，delta=1 |
| R-07 | PASS | 不同 SimpleName 独立加载 |
| R-08 | PASS | AssemblyName Name/Version/Culture/PublicKeyToken 元数据读取正确 |
| R-09 | PASS | Assembly.Location 可读，输出仅保留文件名并脱敏绝对路径 |
| R-10 | PASS | 单次枚举读取 19 个程序集；枚举顺序不作为跨平台契约 |
| R-11 | PASS | 私有依赖在入口初始化前预加载 |
| R-12 | PASS | Loader 报告：snapshot 路径位于 .staging，预检先于首个 assembly-load |
| R-13 | PASS | 21,504 B 探针 DLL 实际加载，SHA-256 = ed0671a784251f1031c3c058220e1aa3afb5d25a4fa834e2cc127508a186fe3c，与预期一致 |
| R-14 | PASS | 损坏 DLL 以 BadImageFormatException 被隔离，后续测试继续执行 |

探针原始汇总：cases=14 observed=12 pass=12 fail=0 blocked=0 not-run=2。R-03 / R-12 的 NOT RUN 是探针设计，二者由同次启动的 Loader 报告独立佐证；因此合并验收口径为 **14 PASS / 0 FAIL**，不是将 NOT RUN 伪报成探针 PASS。

## 5. 稳定性与数据保护

- Mod 启动后 MainActivity 仍处于 resumed 状态，PID 存在。
- PID 过滤 logcat 共 317 B；未匹配到 FATAL EXCEPTION、SIGSEGV、UnsatisfiedLinkError、dlopen failed 等关键错误模式。
- 设备 Saves 目录文件数仍为 0；没有启动新游戏、创建存档或修改 Saves。
- 没有执行 pm clear、uninstall、清除应用数据或 Root 操作。
- 测试 APK 与 P114 诊断 Mod 目前仍安装在该测试设备上，应用保持运行；本轮没有执行回滚。

## 6. 限制与后续

1. 本轮只验证 vivo V1963A / Android 10 / API 29；尚不能推断 Android 11–15、其他 OEM、其他 SoC 或所有 ARM64 设备均兼容。
2. 本轮验证的是启动链、Loader、程序集加载与安全探针；没有验证实际游戏存档写入、长时间游戏、性能、耗电或大量第三方 Mod。
3. Harmony、方法补丁、事件钩子、SMAPI API 兼容、游戏内 Mod 管理器仍未实现。
4. 现有 deploy-apk-safe.ps1 的 Root/private-path Mod 部署逻辑需要升级为无 Root、外部 app-specific files 路径，并配套自动化测试。
5. 下一阶段应先完成部署工具无 Root 化和 README/AGENTS 状态收敛，再设计 Phase 2 的最小 Mod API、事件模型与 Harmony 可行性验证；不要直接宣称兼容 SMAPI。

---

本轮没有修改 Mod Framework / APK 业务源码，没有执行 Git commit/push。验收原始输出保存在 .tmp/phase1.2-arm64/，该目录受 .gitignore 排除。
