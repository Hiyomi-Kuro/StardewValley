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
// Phase 1.1.7 / Phase 2.0: mod-enabled builds.
//
// `assembleDebug` above stays a *baseline* build: it packages the untouched
// app/src/main/apkroot/assemblies/assemblies.blob into app-debug.apk.
//
// Phase 2.0 has three distinct artifacts and three distinct work directories:
//
//   A. assembleDebug             baseline game APK            .tmp/build
//   B. assembleLoaderOnlyDebug   loader only, no event hooks  .tmp/build-loader-only
//   C. assembleModdedDebug       loader + GameLaunched/Tick   .tmp/build-modded
//
// B and C never share a work directory or an output path, so switching the event switch
// cannot reuse a stale blob, build manifest or APK. The event switch is an explicit
// `--event-hooks enabled|disabled` argument to Tools/build_modded_apk.py, recorded in
// modded-build-manifest.json and re-verified from the final APK by verify_modded_apk.py.
//
//   ./gradlew :app:assembleModdedDebug -Pdotnet=<dotnet> -Ppython=<python>
//   ./gradlew :app:assembleLoaderOnlyDebug -Pdotnet=<dotnet> -Ppython=<python>
//
// Local-only: they need the real game assemblies, so CI runs the static checks in
// Tools/tests/test_modded_build.py instead.
// ---------------------------------------------------------------------------
val pythonExe = (findProperty("python") as String?) ?: "python"
val dotnetExe = (findProperty("dotnet") as String?) ?: "dotnet"

// Both mod builds must be signed with the *baseline* key: a fresh key cannot replace the
// installed package. Override with -Pkeystore=<path>.
val sharedModKeystore = (findProperty("keystore") as String?)
    ?.let { rootProject.file(it) }
    ?: rootProject.layout.projectDirectory.file(".tmp/build/debug.keystore").asFile

fun prepareAssembliesTask(
    taskName: String,
    work: Directory,
    eventHooks: Boolean,
): TaskProvider<Exec> = tasks.register<Exec>(taskName) {
    group = "build"
    description = if (eventHooks) {
        "提取 65 个程序集、注入 Bootstrap + 事件钩子并重建 assemblies.blob"
    } else {
        "提取 65 个程序集、仅注入 Bootstrap.Start() 并重建 assemblies.blob（事件回退构建）"
    }
    workingDir = rootProject.projectDir
    commandLine(
        pythonExe,
        "Tools/build_modded_apk.py",
        "--dotnet", dotnetExe,
        "--work", work.asFile.absolutePath,
        "--out-blob", work.file("modded-assemblies.blob").asFile.absolutePath,
        "--manifest-json", work.file("modded-build-manifest.json").asFile.absolutePath,
        "--event-hooks", if (eventHooks) "enabled" else "disabled",
    )
    inputs.property("eventHooks", if (eventHooks) "enabled" else "disabled")
    inputs.file(rootProject.file("Tools/build_modded_apk.py"))
    inputs.file(rootProject.file("Tools/AssemblyStore/store.py"))
    inputs.file(rootProject.file("Tools/ModInjector/Program.cs"))
    inputs.file(rootProject.file("Tools/ModInjector/IlChecks.cs"))
    inputs.file(rootProject.file("Tools/ModInjector/Injection.cs"))
    inputs.file(project.file("src/main/apkroot/assemblies/assemblies.blob"))
    inputs.file(project.file("src/main/apkroot/assemblies/assemblies.manifest"))
    inputs.file(project.file("src/main/apkroot/assemblies/assemblies.arm64_v8a.blob"))
    outputs.file(work.file("modded-assemblies.blob"))
    outputs.file(work.file("modded-build-manifest.json"))
}

val moddedOutputApk = layout.buildDirectory.file("outputs/apk/modded/app-modded-debug.apk")
val moddedWorkDir = rootProject.layout.projectDirectory.dir(".tmp/build-modded")
val loaderOnlyOutputApk = layout.buildDirectory.file("outputs/apk/loader-only/app-loader-only-debug.apk")
val loaderOnlyWorkDir = rootProject.layout.projectDirectory.dir(".tmp/build-loader-only")

val prepareModdedAssemblies = prepareAssembliesTask(
    "prepareModdedAssemblies", moddedWorkDir, eventHooks = true)
val prepareLoaderOnlyAssemblies = prepareAssembliesTask(
    "prepareLoaderOnlyAssemblies", loaderOnlyWorkDir, eventHooks = false)

fun modApkTask(
    taskName: String,
    output: Provider<RegularFile>,
    work: Directory,
    prepare: TaskProvider<Exec>,
    descriptionText: String,
): TaskProvider<JavaExec> = tasks.register<JavaExec>(taskName) {
    group = "build"
    description = descriptionText
    dependsOn(tasks.named("classes"), prepare)
    classpath = sourceSets["main"].runtimeClasspath
    mainClass.set("com.kaori.svpatch.MainKt")
    args(
        projectDir.absolutePath,
        output.get().asFile.absolutePath,
        work.asFile.absolutePath,
        "--bootstrap-blob", work.file("modded-assemblies.blob").asFile.absolutePath,
        "--keystore", sharedModKeystore.absolutePath,
        "--require-existing-keystore",
    )
}

modApkTask(
    "assembleModdedDebug",
    moddedOutputApk,
    moddedWorkDir,
    prepareModdedAssemblies,
    "C：mod-enabled APK（Loader + GameLaunched/Tick 事件钩子）",
)

modApkTask(
    "assembleLoaderOnlyDebug",
    loaderOnlyOutputApk,
    loaderOnlyWorkDir,
    prepareLoaderOnlyAssemblies,
    "B：Loader-only 回退 APK（无事件钩子，用于 Phase 2.0 回退验收）",
)
