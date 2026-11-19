package io.github.rzukic.usagetop.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ReleaseFeedTest {

    private fun version(text: String) = Version.parse(text)!!

    @Test
    fun versionsOrderAsSemVerDoes() {
        val ordered = listOf("1.2.0", "1.2.1-beta.1", "1.2.1-beta.2", "1.2.1-beta.10", "1.2.1-rc.1", "1.2.1", "1.10.0", "2.0.0")
        for (index in 0 until ordered.size - 1) {
            assertTrue("${ordered[index]} < ${ordered[index + 1]}", version(ordered[index]) < version(ordered[index + 1]))
        }
        assertEquals(version("v1.2.1"), version("1.2.1"))
        assertEquals("1.3.0-beta.2", version("v1.3.0-beta.2").text)
    }

    @Test
    fun whatIsNotAVersionIsRefused() {
        for (text in listOf("dev", "", "1.2", "1.2.x", "latest")) assertNull(text, Version.parse(text))
    }

    private fun release(tag: String, draft: Boolean = false, apk: Boolean = true, digest: String? = "sha256:AB12") = """
        {"tag_name":"$tag","draft":$draft,"assets":[
          {"name":"usagetop-win-Setup.exe","browser_download_url":"https://example.com/$tag/setup.exe"}
          ${if (apk) """,{"name":"usagetop-android.apk","browser_download_url":"https://example.com/$tag/app.apk"${digest?.let { ""","digest":"$it"""" } ?: ""}}""" else ""}
        ]}"""

    @Test
    fun theLatestReleaseIsRead() {
        val offer = ReleaseFeed.newest(release("v1.2.2"))!!
        assertEquals("1.2.2", offer.version.text)
        assertEquals("https://example.com/v1.2.2/app.apk", offer.url)
        assertEquals("ab12", offer.sha256)
    }

    @Test
    fun aReleaseWithoutTheAppIsPassedOver() {
        assertNull(ReleaseFeed.newest(release("v1.2.2", apk = false)))
        assertNull(ReleaseFeed.newest(release("v1.2.2", draft = true)))
        assertNull(ReleaseFeed.newest("[]"))
    }

    @Test
    fun ofAListTheHighestVersionWins() {
        val list = "[" + listOf(
            release("v1.2.1"),
            release("v1.3.0-beta.2", digest = null),
            release("v1.3.0-beta.1"),
            release("v1.4.0", draft = true),
            release("v1.3.0", apk = false),
        ).joinToString(",") + "]"
        val offer = ReleaseFeed.newest(list)!!
        assertEquals("1.3.0-beta.2", offer.version.text)
        assertNull(offer.sha256)
    }
}
