package com.kaori.svpatch

import org.w3c.dom.Document
import org.w3c.dom.Element
import org.w3c.dom.Node
import java.nio.file.Files
import java.nio.file.Path
import javax.xml.parsers.DocumentBuilderFactory
import javax.xml.transform.OutputKeys
import javax.xml.transform.TransformerFactory
import javax.xml.transform.dom.DOMSource
import javax.xml.transform.stream.StreamResult

/**
 * 一个针对「已解码工程目录」的修改。
 * 需要新增改动时，实现这个接口并在 Main.kt 的 patches 列表里注册即可。
 */
interface ApkPatch {
    /** 打印给人看的说明 */
    val name: String

    /** decodedDir 是 apktool 解码出来的目录（含 AndroidManifest.xml / res / assets / dex） */
    fun apply(decodedDir: Path)
}

object AndroidNs {
    const val URI = "http://schemas.android.com/apk/res/android"
}

/**
 * 保证游戏本体 Activity 挂着 LAUNCHER（幂等）。
 *
 * 原版清单的 LAUNCHER 曾挂在第三方分发壳上，壳启动游戏后会投递
 * 「更新 → 隐私协议 → 实名认证」三层插页。现在清单里壳的组件已删除、
 * 游戏 Activity 已是唯一入口，这里只做校验与兜底，不依赖壳是否还存在。
 */
class LaunchEntryPatch : ApkPatch {

    override val name: String = "启动入口：确保游戏 Activity 是 LAUNCHER"

    override fun apply(decodedDir: Path) {
        val manifestFile = decodedDir.resolve("AndroidManifest.xml")
        val document = Manifest.load(manifestFile)

        val game = document.activity(GAME_ACTIVITY)
            ?: error("清单里找不到 $GAME_ACTIVITY")

        val filter = game.children("intent-filter").firstOrNull()
            ?: game.appendChild(document.createElement("intent-filter")) as Element

        val hasMain = filter.children("action").any { it.androidAttr("name") == MAIN_ACTION }
        if (!hasMain) {
            val action = document.createElement("action")
            action.setAttributeNS(AndroidNs.URI, "android:name", MAIN_ACTION)
            filter.appendChild(action)
        }

        val hasLauncher = filter.children("category")
            .any { it.androidAttr("name") == LAUNCHER_CATEGORY }
        if (!hasLauncher) {
            val category = document.createElement("category")
            category.setAttributeNS(AndroidNs.URI, "android:name", LAUNCHER_CATEGORY)
            filter.appendChild(category)
        }

        Manifest.save(document, manifestFile)
    }

    private companion object {
        const val GAME_ACTIVITY = "com.chucklefish.stardewvalley.MainActivity"
        const val MAIN_ACTION = "android.intent.action.MAIN"
        const val LAUNCHER_CATEGORY = "android.intent.category.LAUNCHER"
    }
}

/** 清单 XML 的读写。 */
object Manifest {

    fun load(file: Path): Document {
        val factory = DocumentBuilderFactory.newInstance().apply {
            isNamespaceAware = true
            // 清单是本地可信文件，但仍关掉外部实体，避免解析器去读网络/文件
            setFeature("http://apache.org/xml/features/disallow-doctype-decl", true)
        }
        Files.newInputStream(file).use { input ->
            return factory.newDocumentBuilder().parse(input)
        }
    }

    fun save(document: Document, file: Path) {
        val transformer = TransformerFactory.newInstance().newTransformer().apply {
            setOutputProperty(OutputKeys.INDENT, "yes")
            setOutputProperty(OutputKeys.ENCODING, "utf-8")
        }
        Files.newOutputStream(file).use { output ->
            transformer.transform(DOMSource(document), StreamResult(output))
        }
    }
}

// ---- DOM 小工具（顶层扩展函数，补丁里可以直接用）----

fun Document.activity(name: String): Element? {
    val activities = getElementsByTagName("activity")
    for (i in 0 until activities.length) {
        val element = activities.item(i) as? Element ?: continue
        if (element.androidAttr("name") == name) return element
    }
    return null
}

fun Element.children(tag: String): List<Element> {
    val result = mutableListOf<Element>()
    val nodes = childNodes
    for (i in 0 until nodes.length) {
        val node: Node = nodes.item(i)
        if (node is Element && node.tagName == tag) result += node
    }
    return result
}

fun Element.androidAttr(name: String): String? {
    if (!hasAttributeNS(AndroidNs.URI, name)) return null
    return getAttributeNS(AndroidNs.URI, name)
}
