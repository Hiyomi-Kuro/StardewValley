package com.kaori.svpatch

import java.io.InputStream
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.Comparator

object Log {
    fun step(msg: String) = println("\n==> $msg")
    fun ok(msg: String) = println("    $msg")
}

object Sha256 {
    fun of(path: Path): String {
        val digest = MessageDigest.getInstance("SHA-256")
        Files.newInputStream(path).use { input: InputStream ->
            val buffer = ByteArray(1 shl 20)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
}

val IS_WINDOWS: Boolean = System.getProperty("os.name").lowercase().contains("win")

/** 一条外部命令；pathPrefix 会插到子进程 PATH 的最前面（apktool 需要找到 aapt2）。 */
class Command(
    private val exe: String,
    private val args: List<String>,
    private val pathPrefix: Path? = null,
) {
    fun run(): Int {
        val command = listOf(exe) + args
        println("    $ " + command.joinToString(" "))
        val builder = ProcessBuilder(command)
        if (pathPrefix != null) {
            val env = builder.environment()
            val key = env.keys.firstOrNull { it.equals("PATH", ignoreCase = true) } ?: "PATH"
            env[key] = pathPrefix.toString() + java.io.File.pathSeparator + (env[key] ?: "")
        }
        builder.inheritIO()
        val code = builder.start().waitFor()
        check(code == 0) { "命令失败（exit $code）：${command.joinToString(" ")}" }
        return code
    }
}

/**
 * apktool 通过 Maven 依赖引入（org.apktool:apktool-cli），仓库里不放任何二进制工具。
 * 工程树里的 dex 是原始字节（解码时用 -s），所以整个流程不产生 smali。
 *
 * 用独立进程调用 brut.apktool.Main，避免它的 System.exit 影响构建器本身。
 */
class Apktool(private val classpath: String) {

    fun build(decodedDir: Path, outputApk: Path, tools: AndroidBuildTools) {
        Files.deleteIfExists(outputApk)
        Command(
            javaExe(),
            listOf("-Xmx4g", "-cp", classpath, "brut.apktool.Main",
                "b", "-f", decodedDir.toString(), "-o", outputApk.toString()),
            pathPrefix = tools.dir,
        ).run()
        Log.ok("打包: ${Files.size(outputApk) / 1024 / 1024} MiB")
    }

    private fun javaExe(): String {
        val home = System.getProperty("java.home")
        val candidate = Paths.get(home, "bin", if (IS_WINDOWS) "java.exe" else "java")
        return if (Files.isRegularFile(candidate)) candidate.toString() else "java"
    }

    companion object {
        fun fromRuntimeClasspath(): Apktool = Apktool(System.getProperty("java.class.path"))
    }
}

fun deleteRecursively(dir: Path) {
    if (!Files.exists(dir)) return
    val stream = Files.walk(dir)
    try {
        stream.sorted(Comparator { a: Path, b: Path -> b.compareTo(a) })
            .forEach { Files.deleteIfExists(it) }
    } finally {
        stream.close()
    }
}

/** 把工程树整体复制到工作目录（构建时不改动仓库里的原件）。skip 命中的文件不会被复制。 */
fun copyTree(source: Path, target: Path, skip: (Path) -> Boolean = { false }) {
    deleteRecursively(target)
    Files.createDirectories(target)
    val stream = Files.walk(source)
    try {
        stream.forEach { path ->
            if (skip(path)) return@forEach
            val destination = target.resolve(source.relativize(path).toString())
            if (Files.isDirectory(path)) {
                Files.createDirectories(destination)
            } else {
                Files.copy(path, destination, StandardCopyOption.REPLACE_EXISTING)
            }
        }
    } finally {
        stream.close()
    }
}
