# 交付件说明（Phase 1.1.8 / 1.1.9）

本目录只放**非机密**的交付副本。签名密钥不在仓库内，也不在本目录。

| 文件 | 说明 |
| --- | --- |
| `StardewValley-1.6.15.0-modded-test-debug.apk` | mod-enabled 候选包（`assembleModdedDebug`，Bootstrap 已注入） |
| `SHA256SUMS.txt` | 上表的 SHA-256 |

## 身份

| 项 | 值 |
| --- | --- |
| 包名 / versionCode / versionName | `com.chucklefish.stardewvalley` / 239 / 1.6.15.0 |
| 文件大小 | 389,561,500 B（371.51 MiB） |
| SHA-256 | `97c38ed7eb6ac719d7a5ee941abac3766307ad03205569bed19db5a48e7d5d90` |
| signer（证书 DER SHA-256） | `858ef62d7bb63bfea0cf2868e3548ce92d72669b43728330e34ba6afecfae5ff` |
| 签名方案 | v1 + v2 + v3；`apksigner verify` rc=0；`zipalign -c -p 4`（35.0.0 与 36.0.0）通过 |
| 构建时间 | 2026-11-14 02:39（+08:00） |
| 源 APK（baseline） | `app/build/outputs/apk/debug/app-debug.apk`，`65e1e2af…051f9d`，**未被覆盖** |

## 重要：这个包不能覆盖安装

已安装包的 signer 是 `611c8fa4…c5de`（原始 key，私钥不可恢复）；本包的 signer 是 `858ef62d…e5ff`。
`adb install -r` 会以 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` 失败。

安装本包的唯一合法路径是**卸载重装**，会清空应用数据。执行前必须：

1. 已有通过校验的项目外备份
   （`%USERPROFILE%\Documents\StardewValley-TestBackups\MuMu12\<timestamp>\`，逐文件 SHA-256 清单）；
2. 拿到该设备（仅 `127.0.0.1:16384`，MuMu 12 / x86_64 模拟器）的**明确 B 批准**；
3. 遵循 `docs/audit/phase1.1.9-device-acceptance-report.md` 的门禁与回滚步骤。

## 可复现的校验

```powershell
python Tools/verify_modded_apk.py `
  --candidate final/StardewValley-1.6.15.0-modded-test-debug.apk `
  --baseline app/build/outputs/apk/debug/app-debug.apk `
  --blob-manifest .tmp/build-modded/modded-build-manifest.json `
  --report .tmp/build-modded/modded-apk-verify.json `
  --keystore "$env:USERPROFILE\.android\keystores\StardewValley\stardewvalley-test-signing.keystore" `
  --dotnet .tmp\dotnet-sdk\sdk\dotnet.exe
```

期望：`35 passed / 0 failed / 1 blocked`，BLOCK 项为 `candidate_signer_equals_baseline`（预期，非缺陷）。
