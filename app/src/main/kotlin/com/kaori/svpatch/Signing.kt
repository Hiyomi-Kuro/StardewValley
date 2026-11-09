package com.kaori.svpatch

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths

/**
 * 用 apksigner 签名。证书是本机首次构建时生成的 debug keystore，
 * 所以不同机器产出的 APK 签名不同（不能互相覆盖安装）。
 *
 * [requireExistingKeystore] 供 mod-enabled 构建使用：keystore 不存在时直接失败。
 * 自动生成的新 key 与被安装包签名不同，装上去只会得到
 * INSTALL_FAILED_UPDATE_INCOMPATIBLE；把"生成新 key"伪装成"可升级包"是不允许的，
 * 所以那个模式必须显式拒绝，而不是悄悄换一把钥匙。
 */
class ApkSigner(
    private val tools: AndroidBuildTools,
    private val keystore: Path,
    private val requireExistingKeystore: Boolean = false,
) {

    fun sign(input: Path, output: Path) {
        output.parent?.let { Files.createDirectories(it) }
        Files.deleteIfExists(output)
        ensureKeystore()

        Command(
            tools.apksigner.toString(),
            listOf(
                "sign",
                "--ks", keystore.toString(),
                "--ks-pass", "pass:android",
                "--key-pass", "pass:android",
                "--v1-signing-enabled", "true",
                "--v2-signing-enabled", "true",
                "--v3-signing-enabled", "true",
                "--out", output.toString(),
                input.toString(),
            ),
        ).run()

        Log.ok("已签名: $output")
    }

    private fun ensureKeystore() {
        if (Files.isRegularFile(keystore)) return

        // mod-enabled 构建：宁可失败，也不要生成一把与被安装包不同的 key。
        check(!requireExistingKeystore) {
            "keystore 不存在: $keystore。" +
                "这个构建模式不接受自动生成的新 key（新 key 无法覆盖安装已装的包），" +
                "请显式提供 --keystore，或先恢复原来那把 key。"
        }

        keystore.parent?.let { Files.createDirectories(it) }
        Command(
            keytool(),
            listOf(
                "-genkeypair", "-v",
                "-keystore", keystore.toString(),
                "-storepass", "android",
                "-keypass", "android",
                "-alias", "androiddebugkey",
                "-keyalg", "RSA",
                "-keysize", "2048",
                "-validity", "10000",
                "-dname", "CN=Android Debug,O=Android,C=US",
            ),
        ).run()
        Log.ok("已生成 debug keystore: $keystore")
    }

    private fun keytool(): String {
        val home = System.getProperty("java.home")
        val candidate = Paths.get(home, "bin", if (IS_WINDOWS) "keytool.exe" else "keytool")
        return if (Files.isRegularFile(candidate)) candidate.toString() else "keytool"
    }
}
