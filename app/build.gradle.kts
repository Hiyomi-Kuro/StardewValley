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
