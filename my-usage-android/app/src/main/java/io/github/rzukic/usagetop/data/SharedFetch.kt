package io.github.rzukic.usagetop.data

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * Lets callers that ask at the same time share one fetch.
 *
 * The background worker, the widget and an open screen can all ask for a refresh
 * within the same second. Run side by side they would each renew the same OAuth
 * token -- which is single-use, so all but one would fail -- and each rewrite the
 * history under the others. Here the first caller does the work and anyone who
 * was already waiting when it finished takes its result.
 */
internal class SharedFetch<T : Any>(private val clock: () -> Long = System::nanoTime) {

    private val lock = Mutex()
    private var last: T? = null
    private var finishedAt = 0L

    suspend fun share(fetch: suspend () -> T): T {
        val asked = clock()
        return lock.withLock {
            // A result that landed after this caller asked is as fresh as anything
            // it could fetch itself. One from before is not: the caller asked
            // because it wanted something newer.
            val ready = last
            if (ready != null && finishedAt - asked >= 0L) return@withLock ready

            fetch().also {
                last = it
                finishedAt = clock()
            }
        }
    }
}
