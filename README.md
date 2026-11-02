# 星露谷物语 

## 目录

```
settings.gradle.kts / build.gradle.kts / gradle.properties
gradle/wrapper/…、gradlew、gradlew.bat        Gradle 8.10.2 wrapper
app/                                          唯一的模块：内容 + 构建逻辑
  build.gradle.kts                             assembleDebug（JavaExec）
  apktool.yml                                  apktool 元数据（versionCode/Name、doNotCompress）
  src/main/
    AndroidManifest.xml                        文本清单 ← 补丁作用点
    res/                                       资源
    assets/Content/…                           343.8 MB 游戏资源（>100 MB 的 music.xwb 以 .partNNN 分片存放）
    jniLibs/arm64-v8a/…                        41.8 MB 原生库（Mono + AOT）
    prebuilt/classes{,2}.dex                   Java 层预编译 dex（原样，不反编译）
    apkroot/assemblies/…                       要放到 APK 根目录的文件（.NET 程序集等）
    kotlin/com/kaori/svpatch/                  构建逻辑（Kotlin，约 400 行，不进 APK）
      Main.kt                                  校验 → 组装 → 打补丁 → 打包 → 对齐 → 签名
      ManifestPatch.kt                         ApkPatch 接口 + 启动入口补丁
      Sdk.kt / Tools.kt / Signing.kt           SDK 定位、命令、签名
```

| 目录 | 内容 | 原因 |
| --- | --- | --- |
| `prebuilt/` | `classes*.dex` | 这些是原版 Java 层字节码，没有源码可编译；AGP 不接受预编译 dex 输入 |
| `apkroot/` | `assemblies/*.blob`、`rc.bin` | .NET Android 运行时要求它们位于 APK **根目录**，AGP 不支持向根目录放任意文件 |

超过 GitHub 单文件 100 MB 限制的资源按原位置切分（如 `assets/Content/XACT/music.xwb.part000`），
构建时自动拼接回原文件并校验 SHA-256，分片不会进 APK；下面 `assets/` 那条映射对还原后的文件生效。

构建时由 `app/src/main/kotlin` 里的构建逻辑把 `app/src/main` 的内容映射成 apktool 的布局，再打包：

```
AndroidManifest.xml → decoded/AndroidManifest.xml
res/                → decoded/res/
assets/             → decoded/assets/
jniLibs/            → decoded/lib/
prebuilt/*.dex      → decoded/classes*.dex
apkroot/            → decoded/unknown/    （apktool 会把 unknown/ 的内容放到 APK 根）
apktool.yml         → decoded/apktool.yml
```



## 前置条件

| 依赖 | 说明 |
| --- | --- |
| JDK 11+ | `java`、`keytool` 可用 |
| Android SDK `build-tools` | 需要 `aapt2`、`zipalign`、`apksigner`；优先 35.0.0，否则取最高版本 |
| `ANDROID_HOME` | 可不设（依次找 `ANDROID_HOME` → `ANDROID_SDK_ROOT` → `%LOCALAPPDATA%\Android\Sdk`） |
| 网络 | 首次构建下载 Gradle 发行包与 Kotlin/apktool 依赖（之后走缓存） |

wrapper 的 `distributionUrl` 默认指向腾讯镜像（国内 services.gradle.org 常连不上），
网络通畅时可换回官方地址。

## 构建

```powershell
.\gradlew.bat assembleDebug
```



## 二次开发

补丁是一个接口，加新改动只需实现并注册：

```kotlin
interface ApkPatch {
    val name: String                 // 构建日志里显示
    fun apply(decodedDir: Path)      // 改 .tmp/build/decoded 里的工程树
}
```

常见改法：改 `app/src/main/AndroidManifest.xml`（权限/组件）、替换
`app/src/main/assets/Content` 下的贴图或数据、往 `app/src/main/res` 加资源。
改 dex 级行为需要额外工具，本工程刻意不碰。

