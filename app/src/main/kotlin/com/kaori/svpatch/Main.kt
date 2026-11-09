package com.kaori.svpatch

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.nio.file.StandardCopyOption

/**
 * 构建入口。
 *
 *   app/（标准 Android 模块目录）
 *     ├─ apktool.yml
 *     └─ src/main/
 *          ├─ AndroidManifest.xml     ← 文本清单，补丁作用点
 *          ├─ res/                    ← 资源
 *          ├─ assets/                 ← 游戏资源（Content 等）
 *          ├─ jniLibs/arm64-v8a/      ← 原生库
 *          ├─ prebuilt/classes*.dex   ← Java 层预编译 dex（AGP 没有对应目录）
 *          └─ apkroot/                ← APK 根目录文件（assemblies 下各 .blob 等）
 *        ↓  组装成 apktool 期望的布局
 *   .work/build/decoded
 *        ↓  apktool 打包 → zipalign → apksigner
 *   app/build/outputs/apk/debug/app-debug.apk
 *
 * 仓库里没有 payload/base.apk，也没有 smali：dex 以原始字节参与打包，
 * 唯一的行为改动是清单里的启动入口。
 *
 * Phase 1.1.7 增加了**可选的** mod-enabled 入口（Gradle `assembleModdedDebug`）：
 *
 *   --bootstrap-blob <path>      staging 之后用这份 assemblies.blob 覆盖 APK 根目录里的
 *                                assemblies/assemblies.blob（由 Tools/build_modded_apk.py 生成）
 *   --keystore <path>            显式指定签名用的 keystore（默认 <work>/debug.keystore）
 *   --require-existing-keystore  keystore 不存在时失败，而不是生成新 key
 *
 * 不带这些参数时行为与 Phase 1.1.6 完全一致，`assembleDebug` 仍是 baseline 构建。
 */
fun main(args: Array<String>) {
    val positional = ArrayList<String>()
    var bootstrapBlob: Path? = null
    var keystoreOverride: Path? = null
    var requireExistingKeystore = false

    var i = 0
    while (i < args.size) {
        when (val arg = args[i]) {
            "--bootstrap-blob" -> {
                bootstrapBlob = Paths.get(args.getOrNull(++i) ?: fail("--bootstrap-blob 需要一个路径"))
            }
            "--keystore" -> {
                keystoreOverride = Paths.get(args.getOrNull(++i) ?: fail("--keystore 需要一个路径"))
            }
            "--require-existing-keystore" -> requireExistingKeystore = true
            else -> {
                if (arg.startsWith("--")) fail("未知参数: $arg")
                positional.add(arg)
            }
        }
        i++
    }

    if (positional.size < 3) {
        fail(
            "用法: <app.dir> <output.apk> <work.dir> " +
                "[--bootstrap-blob <blob>] [--keystore <path>] [--require-existing-keystore]"
        )
    }

    val appModule = Paths.get(positional[0]).toAbsolutePath()
    val output = Paths.get(positional[1]).toAbsolutePath()
    val work = Paths.get(positional[2]).toAbsolutePath()

    Log.step("校验 app/ 模块")
    AppSource.verify(appModule)
    LargeAssets.verify(appModule)

    val tools = AndroidBuildTools.detect()
    Log.ok("build-tools: ${tools.dir}")

    Files.createDirectories(work)
    val decoded = work.resolve("decoded")
    Log.step("组装打包用的工程树")
    AppSource.stage(appModule, decoded)
    LargeAssets.assemble(appModule, decoded)
    Log.ok("已组装 $decoded")

    if (bootstrapBlob != null) {
        Log.step("注入 mod-enabled assemblies.blob")
        injectAssembliesBlob(decoded, bootstrapBlob)
    } else {
        Log.ok("baseline 构建：APK 使用 app/src/main/apkroot 里的原始 assemblies.blob")
    }
    Log.step("套用补丁")
    val patches = listOf<ApkPatch>(LaunchEntryPatch())
    for (patch in patches) {
        patch.apply(decoded)
        Log.ok(patch.name)
    }

    val apktool = Apktool.fromRuntimeClasspath()
    val unsigned = work.resolve("unsigned.apk")
    apktool.build(decoded, unsigned, tools)

    val aligned = work.resolve("aligned.apk")
    tools.zipalign(unsigned, aligned)

    val keystore = keystoreOverride ?: work.resolve("debug.keystore")
    Log.ok("keystore: $keystore (require-existing=$requireExistingKeystore)")
    ApkSigner(tools, keystore, requireExistingKeystore).sign(aligned, output)

    Log.step("完成")
    Log.ok("产物  : $output")
    Log.ok("大小  : ${"%.2f".format(Files.size(output) / 1024.0 / 1024.0)} MiB")
    Log.ok("sha256: ${Sha256.of(output)}")
}

/**
 * 用已经注入好的 blob 覆盖 staging 树里的 assemblies.blob。
 *
 * 只动 staging：`app/src/main/apkroot` 永远不被写回，所以 baseline 构建和原始 blob 的
 * SHA-256 都不受影响。容器内容（58 个条目、descriptor index、未改动条目）由
 * `Tools/build_modded_apk.py` 在生成这份 blob 时逐条校验，这里只做形态检查。
 */
private fun injectAssembliesBlob(decoded: Path, blob: Path) {
    require(Files.isRegularFile(blob)) { "缺少 --bootstrap-blob 文件: $blob" }
    val target = decoded.resolve("unknown").resolve("assemblies").resolve("assemblies.blob")
    require(Files.isRegularFile(target)) { "staging 树里没有 assemblies.blob: $target" }

    val head = ByteArray(4)
    Files.newInputStream(blob).use { stream ->
        require(stream.read(head) == 4) { "blob 太小: $blob" }
    }
    require(String(head, Charsets.US_ASCII) == "XABA") { "不是 XABA 容器: $blob" }

    Files.copy(blob, target, StandardCopyOption.REPLACE_EXISTING)
    Log.ok("blob  : ${blob.toAbsolutePath()}")
    Log.ok("sha256: ${Sha256.of(blob)}")
    Log.ok("覆盖  : ${target.toAbsolutePath()}")
}

private fun fail(message: String): Nothing {
    System.err.println("错误: $message")
    kotlin.system.exitProcess(2)
}

/** 校验 app/ 模块结构，并把标准目录映射成 apktool 的布局。 */
object AppSource {
    private const val PACKAGE = "com.chucklefish.stardewvalley"
    private const val VERSION_NAME = "1.6.15.0"
    private const val MIN_ASSET_FILES = 3000L

    fun verify(app: Path) {
        val main = app.resolve("src/main")
        requireFile(main.resolve("AndroidManifest.xml"))
        requireFile(main.resolve("prebuilt/classes.dex"))
        requireDir(main.resolve("assets"))
        requireDir(main.resolve("res"))
        requireDir(main.resolve("jniLibs"))
        requireDir(main.resolve("apkroot"))
        requireFile(app.resolve("apktool.yml"))

        val manifest = Manifest.load(main.resolve("AndroidManifest.xml"))
        val packageName = manifest.documentElement.getAttribute("package")
        check(packageName == PACKAGE) { "清单包名是 $packageName，预期 $PACKAGE" }

        // apktool 把 versionCode/versionName 存在 apktool.yml，打包时写回清单
        val versionName = readVersionName(app.resolve("apktool.yml"))
        check(versionName == VERSION_NAME) { "工程版本是 $versionName，预期 $VERSION_NAME" }

        val assetCount = countFiles(main.resolve("assets"))
        check(assetCount >= MIN_ASSET_FILES) { "assets 只有 $assetCount 个文件，预期 >= $MIN_ASSET_FILES" }

        Log.ok("package=$packageName versionName=$versionName")
        Log.ok("assets=$assetCount 个文件，共 ${"%.1f".format(sizeOf(main) / 1024.0 / 1024.0)} MiB")
    }

    /** 把标准 Android 目录映射到 apktool 的目录布局。 */
    fun stage(app: Path, decoded: Path) {
        val main = app.resolve("src/main")
        deleteRecursively(decoded)
        Files.createDirectories(decoded)

        Files.copy(app.resolve("apktool.yml"), decoded.resolve("apktool.yml"))
        Files.copy(main.resolve("AndroidManifest.xml"), decoded.resolve("AndroidManifest.xml"))
        copyTree(main.resolve("res"), decoded.resolve("res"), LargeAssets::isPart)
        copyTree(main.resolve("assets"), decoded.resolve("assets"), LargeAssets::isPart)
        // apktool 的 lib/ 就是 APK 的 lib/，对应 AGP 的 jniLibs/
        copyTree(main.resolve("jniLibs"), decoded.resolve("lib"), LargeAssets::isPart)
        // apktool 的 unknown/ 会原样放到 APK 根目录
        copyTree(main.resolve("apkroot"), decoded.resolve("unknown"), LargeAssets::isPart)

        val dexDir = main.resolve("prebuilt")
        val dexStream = Files.list(dexDir)
        try {
            dexStream.filter { it.fileName.toString().endsWith(".dex") }.forEach { dex ->
                Files.copy(dex, decoded.resolve(dex.fileName.toString()))
            }
        } finally {
            dexStream.close()
        }
    }

    private fun readVersionName(ymlFile: Path): String? {
        val lines = Files.readAllLines(ymlFile)
        val start = lines.indexOfFirst { it.trim() == "versionInfo:" }
        if (start < 0) return null
        for (i in start + 1 until lines.size) {
            val line = lines[i]
            if (line.isNotEmpty() && !line.startsWith(" ")) break
            val trimmed = line.trim()
            if (trimmed.startsWith("versionName:")) {
                return trimmed.removePrefix("versionName:").trim()
            }
        }
        return null
    }

    private fun requireFile(path: Path) = require(Files.isRegularFile(path)) { "缺少 $path" }
    private fun requireDir(path: Path) = require(Files.isDirectory(path)) { "缺少目录 $path" }

    private fun countFiles(dir: Path): Long {
        val stream = Files.walk(dir)
        try {
            return stream.filter { Files.isRegularFile(it) }.count()
        } finally {
            stream.close()
        }
    }

    private fun sizeOf(dir: Path): Long {
        val stream = Files.walk(dir)
        try {
            return stream.filter { Files.isRegularFile(it) }.mapToLong { Files.size(it) }.sum()
        } finally {
            stream.close()
        }
    }
}
