package io.github.rzukic.usagetop.update

import org.json.JSONArray
import org.json.JSONObject

/**
 * A version as releases name them: "1.2.1", "v1.2.1", "1.3.0-beta.2". Ordered
 * as SemVer orders them: a pre-release comes before its release.
 */
class Version private constructor(
    private val numbers: List<Int>,
    private val pre: List<String>,
) : Comparable<Version> {

    val text: String
        get() = numbers.joinToString(".") + if (pre.isEmpty()) "" else "-" + pre.joinToString(".")

    override fun compareTo(other: Version): Int {
        for (index in 0 until 3) {
            if (numbers[index] != other.numbers[index]) return numbers[index].compareTo(other.numbers[index])
        }
        if (pre.isEmpty() || other.pre.isEmpty()) return other.pre.size.coerceAtMost(1) - pre.size.coerceAtMost(1)
        for (index in 0 until minOf(pre.size, other.pre.size)) {
            val left = pre[index]
            val right = other.pre[index]
            if (left == right) continue
            val leftNumber = left.toIntOrNull()
            val rightNumber = right.toIntOrNull()
            return when {
                leftNumber != null && rightNumber != null -> leftNumber.compareTo(rightNumber)
                leftNumber != null -> -1
                rightNumber != null -> 1
                else -> left.compareTo(right)
            }
        }
        return pre.size.compareTo(other.pre.size)
    }

    override fun equals(other: Any?): Boolean = other is Version && compareTo(other) == 0
    override fun hashCode(): Int = text.hashCode()
    override fun toString(): String = text

    companion object {
        private val pattern = Regex("""^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$""")

        fun parse(text: String): Version? {
            val match = pattern.matchEntire(text.trim()) ?: return null
            val numbers = match.groupValues.subList(1, 4).map { it.toIntOrNull() ?: return null }
            val pre = match.groupValues[4].split('.').filter { it.isNotEmpty() }
            return Version(numbers, pre)
        }
    }
}

/** A release's Android app: which version, where from, and the SHA-256 GitHub lists for it. */
data class Offer(val version: Version, val url: String, val sha256: String?)

/**
 * Reads GitHub's answers about releases: `/releases/latest` (one object) or
 * `/releases` (a list, for beta versions).
 */
object ReleaseFeed {

    const val ASSET = "usagetop-android.apk"

    /**
     * The newest release that carries the Android app, or null when there is
     * none: drafts never count, and a release whose APK has not been attached
     * yet is passed over.
     */
    fun newest(json: String): Offer? {
        val text = json.trim()
        val releases = if (text.startsWith("[")) {
            JSONArray(text).let { list -> (0 until list.length()).map(list::getJSONObject) }
        } else {
            listOf(JSONObject(text))
        }
        return releases.mapNotNull(::offer).maxByOrNull { it.version }
    }

    private fun offer(release: JSONObject): Offer? {
        if (release.optBoolean("draft")) return null
        val version = Version.parse(release.optString("tag_name")) ?: return null
        val assets = release.optJSONArray("assets") ?: return null
        for (index in 0 until assets.length()) {
            val asset = assets.getJSONObject(index)
            if (asset.optString("name") != ASSET) continue
            val url = asset.optString("browser_download_url").takeIf { it.isNotEmpty() } ?: return null
            val digest = asset.optString("digest").takeIf { it.startsWith("sha256:") }?.removePrefix("sha256:")?.lowercase()
            return Offer(version, url, digest)
        }
        return null
    }
}
