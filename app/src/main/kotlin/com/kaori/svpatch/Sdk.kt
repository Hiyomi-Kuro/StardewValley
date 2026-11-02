package com.kaori.svpatch

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths

/**
 * 定位 Android SDK 的 build-tools（aapt2 / zipalign / apksigner）。
 * 查找顺序：ANDROID_HOME → ANDROID_SDK_ROOT → %LOCALAPPDATA%\Android\Sdk → ~/Android/Sdk
 */
object Sdk {

    fun roots(): List<Path> {
        val candidates = mutableListOf<String>()
        System.getenv("ANDROID_HOME")?.let { candidates += it }
        System.getenv("ANDROID_SDK_ROOT")?.let { candidates += it }
        System.getenv("LOCALAPPDATA")?.let { candidates += "$it\\Android\\Sdk" }
        candidates += System.getProperty("user.home") + "/Android/Sdk"
        return candidates.map { Paths.get(it) }.filter { Files.isDirectory(it) }
    }
}

/**
 * 定位 build-tools。版本优先 35.0.0，否则取已安装的最高版本。
 */
class AndroidBuildTools(val dir: Path) {
    val aapt2: Path = dir.resolve(if (IS_WINDOWS) "aapt2.exe" else "aapt2")
    val zipalign: Path = dir.resolve(if (IS_WINDOWS) "zipalign.exe" else "zipalign")
    val apksigner: Path = dir.resolve(if (IS_WINDOWS) "apksigner.bat" else "apksigner")

    fun zipalign(input: Path, output: Path) {
        Files.deleteIfExists(output)
        Command(zipalign.toString(), listOf("-f", "-p", "4", input.toString(), output.toString())).run()
        Log.ok("zipalign: ${Files.size(output) / 1024 / 1024} MiB")
    }

    companion object {
        fun detect(): AndroidBuildTools {
            for (root in Sdk.roots()) {
                val toolsRoot = root.resolve("build-tools")
                if (!Files.isDirectory(toolsRoot)) continue

                val stream = Files.list(toolsRoot)
                val versions: List<Path>
                try {
                    versions = stream.filter { Files.isDirectory(it) }
                        .collect(java.util.stream.Collectors.toList())
                } finally {
                    stream.close()
                }

                val chosen = versions.firstOrNull { it.fileName.toString() == "35.0.0" }
                    ?: versions.maxByOrNull { versionKey(it.fileName.toString()) }
                    ?: continue

                val tools = AndroidBuildTools(chosen)
                if (Files.isRegularFile(tools.aapt2) &&
                    Files.isRegularFile(tools.zipalign) &&
                    Files.isRegularFile(tools.apksigner)
                ) {
                    return tools
                }
            }

            error(
                """
                找不到可用的 Android SDK build-tools（需要 aapt2 / zipalign / apksigner）。
                请安装 Android SDK 并设置 ANDROID_HOME，例如：
                    setx ANDROID_HOME "%LOCALAPPDATA%\Android\Sdk"
                """.trimIndent()
            )
        }

        private fun versionKey(name: String): Int {
            val parts = name.split('.', '-').mapNotNull { it.toIntOrNull() }
            return parts.getOrElse(0) { 0 } * 10000 +
                parts.getOrElse(1) { 0 } * 100 +
                parts.getOrElse(2) { 0 }
        }
    }
}
