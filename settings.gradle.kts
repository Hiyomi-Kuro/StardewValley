pluginManagement {
    repositories {
        // 国内网络下 Maven Central 更稳；apktool 的 smali 依赖在 Google Maven
        mavenCentral()
        google()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositories {
        mavenCentral()
        google()
    }
}

rootProject.name = "stardew-valley-android"

include(":app")
