package com.kaori.svpatch

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardOpenOption
import java.security.MessageDigest

/**
 * 超过 GitHub 单文件 100 MB 限制的资源，以**原位置分片**保存：
 *
 *   app/src/main/assets/Content/XACT/music.xwb.part000
 *   app/src/main/assets/Content/XACT/music.xwb.part001
 *   app/src/main/assets/Content/XACT/music.xwb.part002
 *
 * 构建时按编号顺序拼接回 `music.xwb`（校验 SHA-256），分片本身**不会**进 APK。
 * 这样既不引入 LFS，也不需要在 src 与 build 之间多一层目录。
 */
object LargeAssets {

    private val PART = Regex("""^(.+)\.part(\d{3})$""")

    /** 已知大资源的原始 SHA-256；版本升级后需要同步更新。 */
    private val EXPECTED = mapOf(
        "assets/Content/XACT/music.xwb" to
            "cdbc4937bb48ec1d05f87bf686c564c9176ddb0a234931b9c832f578c812d3d5",
    )

    fun isPart(path: Path): Boolean = PART.matches(path.fileName.toString())

    /** 校验：分片存在，且同名的「完整文件」没有被误留。 */
    fun verify(app: Path) {
        val groups = collect(app)
        if (groups.isEmpty()) {
            Log.ok("无分片资源")
            return
        }
        val srcMain = app.resolve("src/main")
        for ((relative, parts) in groups) {
            check(!Files.exists(srcMain.resolve(relative))) {
                "$relative 仍然存在，应只保留 ${parts.size} 个 .partNNN 分片"
            }
            val total = parts.sumOf { Files.size(it) }
            Log.ok("$relative: ${parts.size} 个分片, ${total / 1024 / 1024} MiB")
        }
    }

    /** 把分片拼接成原始文件，放进 apktool 布局的对应位置。 */
    fun assemble(app: Path, decoded: Path) {
        for ((relative, parts) in collect(app)) {
            val target = decodedPath(decoded, relative)
            target.parent?.let { Files.createDirectories(it) }
            Files.deleteIfExists(target)

            val digest = MessageDigest.getInstance("SHA-256")
            Files.newOutputStream(target, StandardOpenOption.CREATE_NEW).use { output ->
                for (part in parts) {
                    Files.newInputStream(part).use { input ->
                        val buffer = ByteArray(1 shl 20)
                        while (true) {
                            val read = input.read(buffer)
                            if (read < 0) break
                            output.write(buffer, 0, read)
                            digest.update(buffer, 0, read)
                        }
                    }
                }
            }

            val actual = digest.digest().joinToString("") { "%02x".format(it) }
            val expected = EXPECTED[relative]
            if (expected != null) {
                check(actual == expected) {
                    "$relative 拼接后 SHA-256 不匹配\n  期望: $expected\n  实际: $actual"
                }
            }
            Log.ok("已还原 $relative (${Files.size(target) / 1024 / 1024} MiB)")
        }
    }

    /** 扫描 src/main，返回「目标相对路径 → 按编号排序的分片」。 */
    private fun collect(app: Path): Map<String, List<Path>> {
        val srcMain = app.resolve("src/main")
        if (!Files.isDirectory(srcMain)) return emptyMap()

        val groups = mutableMapOf<String, MutableList<Path>>()
        val stream = Files.walk(srcMain)
        try {
            stream.filter { Files.isRegularFile(it) }.forEach { path ->
                val match = PART.matchEntire(path.fileName.toString()) ?: return@forEach
                val relative = srcMain.relativize(path).toString().replace('\\', '/')
                val target = relative.removeSuffix(".part" + match.groupValues[2])
                groups.getOrPut(target) { mutableListOf() }.add(path)
            }
        } finally {
            stream.close()
        }
        return groups.mapValues { (_, parts) -> parts.sortedBy { it.fileName.toString() } }
    }

    /** src/main 相对路径 → apktool 解码目录路径。 */
    private fun decodedPath(decoded: Path, relativePath: String): Path = when {
        relativePath.startsWith("assets/") -> decoded.resolve(relativePath)
        relativePath.startsWith("res/") -> decoded.resolve(relativePath)
        relativePath.startsWith("jniLibs/") -> decoded.resolve("lib/" + relativePath.removePrefix("jniLibs/"))
        relativePath.startsWith("apkroot/") -> decoded.resolve("unknown/" + relativePath.removePrefix("apkroot/"))
        else -> error("分片不支持这个目录：$relativePath")
    }
}
