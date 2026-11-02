package com.kaori.svpatch

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths

/**
 * 用 apksigner 签名。证书是本机首次构建时生成的 debug keystore，
 * 所以不同机器产出的 APK 签名不同（不能互相覆盖安装）。
 */
class ApkSigner(
    private val tools: AndroidBuildTools,
    private val keystore: Path,
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
