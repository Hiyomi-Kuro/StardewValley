plugins {
    // app 模块既是内容模块，也承载构建逻辑（src/main/kotlin），
    // 所以不引入 com.android.application —— 游戏是 Xamarin/.NET-Android 结构，
    // 需要 APK 根目录下的 assemblies/、预编译 dex 等 AGP 不支持的内容。
    kotlin("jvm")
    base
}

kotlin {
    jvmToolchain(11)
}

dependencies {
    // 构建器把 apktool 当库用；仓库里不放任何二进制工具
    implementation("org.apktool:apktool-cli:2.11.1")
}

// ---------------------------------------------------------------------------
// 内容目录（assets/res/jniLibs/prebuilt/apkroot）就是标准的 app/src/main 布局，
// Kotlin 源码在同一目录下的 kotlin/，两者的路径互不干扰。
// 产物路径与 Android 工程一致：app/build/outputs/apk/debug/app-debug.apk
// ---------------------------------------------------------------------------
val outputApk = layout.buildDirectory.file("outputs/apk/debug/app-debug.apk")
val workDir = rootProject.layout.projectDirectory.dir(".tmp/build")

tasks.register<JavaExec>("assembleDebug") {
    group = "build"
    description = "把 app/src/main 组装成工程树并打包签名"
    dependsOn(tasks.named("classes"))
    classpath = sourceSets["main"].runtimeClasspath
    mainClass.set("com.kaori.svpatch.MainKt")
    args(
        projectDir.absolutePath,
        outputApk.get().asFile.absolutePath,
        workDir.asFile.absolutePath,
    )
}

tasks.named("assemble") {
    dependsOn("assembleDebug")
}

// ---------------------------------------------------------------------------
// Phase 1.1.7: mod-enabled build.
//
// `assembleDebug` above stays a *baseline* build: it packages the untouched
// app/src/main/apkroot/assemblies/assemblies.blob into app-debug.apk.
//
// `assembleModdedDebug` is the explicit opt-in that additionally injects
// ModFramework.Bootstrap.Start() into StardewValley.dll and repacks the blob. It writes a
// different output and a different work directory, so it can never overwrite the baseline APK
// and never writes back into the source tree.
//
//   ./gradlew :app:assembleModdedDebug -Pdotnet=<dotnet> -Pkeystore=<debug.keystore>
//
// Local-only: it needs the real game assemblies, so CI runs the static checks in
// Tools/tests/test_modded_build.py instead.
// ---------------------------------------------------------------------------
val moddedOutputApk = layout.buildDirectory.file("outputs/apk/modded/app-modded-debug.apk")
val moddedWorkDir = rootProject.layout.projectDirectory.dir(".tmp/build-modded")

// Keystore: the baseline build generates <work>/debug.keystore on first use. The mod-enabled
// build must NOT generate one, because a fresh key cannot replace the installed package.
// Override with -Pkeystore=<path> to point at an existing key.
val moddedKeystore = (findProperty("keystore") as String?)
    ?.let { rootProject.file(it) }
    ?: moddedWorkDir.file("debug.keystore").asFile

val pythonExe = (findProperty("python") as String?) ?: "python"
val dotnetExe = (findProperty("dotnet") as String?) ?: "dotnet"

val prepareModdedAssemblies = tasks.register<Exec>("prepareModdedAssemblies") {
    group = "build"
    description = "提取 65 个程序集、注入 Bootstrap.Start() 并重建 assemblies.blob"
    workingDir = rootProject.projectDir
    commandLine(
        pythonExe,
        "Tools/build_modded_apk.py",
        "--dotnet", dotnetExe,
        "--work", moddedWorkDir.asFile.absolutePath,
        "--out-blob", moddedWorkDir.file("modded-assemblies.blob").asFile.absolutePath,
        "--manifest-json", moddedWorkDir.file("modded-build-manifest.json").asFile.absolutePath,
    )
    inputs.file(rootProject.file("Tools/build_modded_apk.py"))
    inputs.file(rootProject.file("Tools/AssemblyStore/store.py"))
    inputs.file(rootProject.file("Tools/ModInjector/Program.cs"))
    inputs.file(rootProject.file("Tools/ModInjector/IlChecks.cs"))
    inputs.file(project.file("src/main/apkroot/assemblies/assemblies.blob"))
    inputs.file(project.file("src/main/apkroot/assemblies/assemblies.manifest"))
    inputs.file(project.file("src/main/apkroot/assemblies/assemblies.arm64_v8a.blob"))
    outputs.file(moddedWorkDir.file("modded-assemblies.blob"))
}

tasks.register<JavaExec>("assembleModdedDebug") {
    group = "build"
    description = "mod-enabled APK：baseline 内容 + 注入 Bootstrap 的 StardewValley 程序集"
    dependsOn(tasks.named("classes"), prepareModdedAssemblies)
    classpath = sourceSets["main"].runtimeClasspath
    mainClass.set("com.kaori.svpatch.MainKt")
    args(
        projectDir.absolutePath,
        moddedOutputApk.get().asFile.absolutePath,
        moddedWorkDir.asFile.absolutePath,
        "--bootstrap-blob", moddedWorkDir.file("modded-assemblies.blob").asFile.absolutePath,
        "--keystore", moddedKeystore.absolutePath,
        "--require-existing-keystore",
    )
}
