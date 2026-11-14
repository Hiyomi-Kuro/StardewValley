# Phase 1.2 ARM64 证据索引

所有原始设备证据均保存在本机 .tmp/phase1.2-arm64/，不纳入 Git：

| 本地证据 | 内容 |
|---|---|
| .tmp/phase1.2-arm64/e0/device-precheck-arm64.txt | ARM64 设备只读预检（设备序列号已脱敏） |
| .tmp/phase1.2-arm64/install-result.txt | 首次安装结果与包身份（设备序列号已脱敏） |
| .tmp/phase1.2-arm64/deploy-precheck.txt | Mod 部署前置计划（设备序列号已脱敏） |
| .tmp/phase1.2-arm64/arm64-device-metadata.txt | 已脱敏的设备与包 ABI 元数据 |
| .tmp/phase1.2-arm64/arm64-installed-apk-identity.txt | 从设备拉取的 base.apk 的 SHA-256 与 signer 指纹 |
| .tmp/phase1.2-arm64/arm64-mods-hashes.txt | 11 个 Mod 文件本地 / 设备 SHA-256 对照 |
| .tmp/phase1.2-arm64/evidence/arm64-loader-report.txt | 设备端 Loader 最终报告 |
| .tmp/phase1.2-arm64/evidence/arm64-p114-report.txt | P114 RuntimeProbe 原始输出 |
| .tmp/phase1.2-arm64/evidence/arm64-p114-log.txt | P114 mod 日志 |
| .tmp/phase1.2-arm64/evidence/arm64-mod-run-logcat.txt | 按游戏 PID 过滤的启动日志 |
| .tmp/phase1.2-arm64/evidence/first-launch-logcat.txt | 首次无 Mods 启动日志 |

屏幕截图采集件已在检查后删除，未纳入正式证据；设备序列号不写入仓库报告。
