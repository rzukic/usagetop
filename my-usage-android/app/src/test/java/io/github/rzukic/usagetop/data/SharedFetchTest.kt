package io.github.rzukic.usagetop.data

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Test

class SharedFetchTest {

    @Test
    fun `callers that ask during a fetch share its result`() = runBlocking {
        val shared = SharedFetch<String>()
        val release = CompletableDeferred<Unit>()
        var fetches = 0

        suspend fun fetch(): String {
            fetches++
            release.await()
            return "reading $fetches"
        }

        // The worker starts a fetch; the widget and a screen ask while it runs.
        val worker = async { shared.share { fetch() } }
        yield()
        val widget = async { shared.share { fetch() } }
        val screen = async { shared.share { fetch() } }
        yield()

        release.complete(Unit)

        assertEquals("reading 1", worker.await())
        assertEquals("reading 1", widget.await())
        assertEquals("reading 1", screen.await())
        assertEquals(1, fetches)
    }

    @Test
    fun `a caller that asks after a fetch finished gets a new one`() = runBlocking {
        val shared = SharedFetch<String>()
        var fetches = 0

        val first = shared.share { "reading ${++fetches}" }
        val second = shared.share { "reading ${++fetches}" }

        // The second caller asked because it wanted something newer than what
        // was already there, so an old result is not handed back to it.
        assertEquals("reading 1", first)
        assertEquals("reading 2", second)
    }

    @Test
    fun `a failed fetch is not remembered`() = runBlocking {
        val shared = SharedFetch<String>()

        val failed = runCatching { shared.share { error("no network") } }
        val next = shared.share { "reading" }

        assertEquals(true, failed.isFailure)
        assertEquals("reading", next)
    }
}
