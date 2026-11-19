package io.github.rzukic.usagetop.update

import android.app.ActivityManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInfo
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import io.github.rzukic.usagetop.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.TimeUnit

/**
 * Keeps the app current from the GitHub releases.
 *
 * A worker looks every six hours, and Settings has "Check now"; never more
 * often on its own, because GitHub allows an address 60 unauthenticated API
 * calls an hour and each look costs one. A newer usagetop-android.apk is
 * downloaded, checked and then waits. Android itself puts it in: the first
 * time it asks whether usagetop may install apps, and to confirm; from
 * Android 12 on, later updates go in by themselves while the app is not on
 * screen. Nothing here ever pops up unasked: a failed look is a line in
 * Settings.
 *
 * What is checked before a download is handed to Android: its SHA-256 against
 * the one GitHub lists for the file, and that it is this app, in a higher
 * version, signed with the key this copy was signed with. Android checks the
 * signature again and refuses anything else.
 *
 * A development build (no release version) does nothing at all. The Gradle
 * property updateSource, an address that answers like
 * https://api.github.com/repos/rzukic/usagetop, stands in for GitHub, to try
 * an update end to end without publishing.
 */
object Updater {

    enum class Phase { OFF, WAITING, CHECKING, UP_TO_DATE, DOWNLOADING, READY, INSTALLING, FAILED }

    data class State(
        val phase: Phase,
        /** The version being downloaded, or downloaded and waiting. */
        val target: String? = null,
        /** When a look last got an answer, epoch millis. */
        val checkedAt: Long? = null,
        /** What a failure was, in a line short enough for the status. */
        val failure: String? = null,
    )

    private const val TAG = "Updater"
    private const val PREFS = "updates"
    private const val KEY_BETA = "beta"
    private const val KEY_CHECKED = "checkedAt"
    private val PERIOD_MILLIS = TimeUnit.HOURS.toMillis(6)

    /** The running version, or null for a development build, which never updates. */
    val current: Version? = Version.parse(BuildConfig.VERSION_NAME)

    private val states = MutableStateFlow(State(if (current == null) Phase.OFF else Phase.WAITING))
    val state: StateFlow<State> = states

    private val lock = Mutex()
    private var restored = false

    private val client: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .build()
    }

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    private fun folder(context: Context) = File(context.filesDir, "updates")

    /** Early builds as well as releases, whichever is newer. */
    fun beta(context: Context): Boolean = prefs(context).getBoolean(KEY_BETA, false)

    fun setBeta(context: Context, value: Boolean) {
        prefs(context).edit().putBoolean(KEY_BETA, value).apply()
    }

    /**
     * Picks up where an earlier process left off: a download that is still
     * newer than what is running is offered again, anything else is cleared.
     */
    fun restore(context: Context) {
        if (current == null || restored) return
        restored = true
        val checked = prefs(context).getLong(KEY_CHECKED, 0L).takeIf { it > 0 }
        val waiting = waiting(context)
        states.value = when {
            waiting != null -> State(Phase.READY, waiting.first.text, checked)
            checked != null -> State(Phase.UP_TO_DATE, checkedAt = checked)
            else -> State(Phase.WAITING)
        }
    }

    /** True when the last look is old enough that opening the app is worth another. */
    fun isDue(context: Context): Boolean {
        if (current == null) return false
        return System.currentTimeMillis() - prefs(context).getLong(KEY_CHECKED, 0L) >= PERIOD_MILLIS
    }

    /** One look, and a download if it finds something. A second caller waits for the first. */
    suspend fun check(context: Context) {
        val app = context.applicationContext
        val running = current ?: return
        restore(app)
        lock.withLock {
            val before = states.value
            if (before.phase == Phase.INSTALLING) return
            // Keep "ready" on screen while looking for something newer still.
            if (before.phase != Phase.READY) states.value = before.copy(phase = Phase.CHECKING, failure = null)
            try {
                val offer = withContext(Dispatchers.IO) { look(app) }
                val now = System.currentTimeMillis()
                prefs(app).edit().putLong(KEY_CHECKED, now).apply()
                val waiting = waiting(app)
                when {
                    offer == null || offer.version <= running ->
                        states.value = if (waiting != null) State(Phase.READY, waiting.first.text, now) else State(Phase.UP_TO_DATE, checkedAt = now)
                    waiting != null && waiting.first == offer.version ->
                        states.value = State(Phase.READY, offer.version.text, now)
                    else -> {
                        states.value = State(Phase.DOWNLOADING, offer.version.text, now)
                        withContext(Dispatchers.IO) { download(app, offer) }
                        states.value = State(Phase.READY, offer.version.text, now)
                        Log.i(TAG, "Version ${offer.version} is ready")
                    }
                }
            } catch (problem: Refused) {
                fail(app, problem.message ?: "Couldn't check for updates")
            } catch (problem: Exception) {
                Log.w(TAG, "Update check failed", problem)
                fail(app, "Couldn't check for updates")
            }
        }
    }

    private fun fail(context: Context, line: String) {
        Log.w(TAG, line)
        val checked = prefs(context).getLong(KEY_CHECKED, 0L).takeIf { it > 0 }
        // An older download that is still waiting stays offered.
        val waiting = waiting(context)
        states.value = if (waiting != null) State(Phase.READY, waiting.first.text, checked)
        else State(Phase.FAILED, checkedAt = checked, failure = line)
    }

    private class Refused(message: String) : Exception(message)

    private fun look(context: Context): Offer? {
        val path = if (beta(context)) "/releases?per_page=30" else "/releases/latest"
        val request = Request.Builder()
            .url(BuildConfig.UPDATE_SOURCE.trimEnd('/') + path)
            .header("Accept", "application/vnd.github+json")
            .header("User-Agent", "usagetop-android/${BuildConfig.VERSION_NAME}")
            .build()
        client.newCall(request).execute().use { response ->
            return when (response.code) {
                200 -> ReleaseFeed.newest(response.body?.string().orEmpty())
                // No release at all yet, or a repository that cannot be seen.
                404 -> null
                403, 429 -> throw Refused("GitHub is limiting requests; it will be tried again later")
                else -> throw Refused("Couldn't check for updates (GitHub answered ${response.code})")
            }
        }
    }

    private fun download(context: Context, offer: Offer) {
        val folder = folder(context)
        folder.deleteRecursively()
        folder.mkdirs()
        val partial = File(folder, "download.part")
        val digest = MessageDigest.getInstance("SHA-256")
        val request = Request.Builder()
            .url(offer.url)
            .header("User-Agent", "usagetop-android/${BuildConfig.VERSION_NAME}")
            .build()
        try {
            client.newCall(request).execute().use { response ->
                if (response.code != 200) throw Refused("Couldn't download ${offer.version} (the download answered ${response.code})")
                val body = response.body ?: throw Refused("Couldn't download ${offer.version}")
                partial.outputStream().use { out ->
                    body.byteStream().use { input ->
                        val buffer = ByteArray(64 * 1024)
                        while (true) {
                            val read = input.read(buffer)
                            if (read < 0) break
                            digest.update(buffer, 0, read)
                            out.write(buffer, 0, read)
                        }
                    }
                }
            }
            // GitHub lists each file's SHA-256; the testing source may leave it out.
            val actual = digest.digest().joinToString("") { "%02x".format(it) }
            if (offer.sha256 != null && offer.sha256 != actual) {
                throw Refused("The download of ${offer.version} was damaged")
            }
            whyNot(context, partial)?.let { throw Refused("The download of ${offer.version} was not usable: $it") }
            if (!partial.renameTo(apk(context, offer.version))) throw Refused("Couldn't keep the download of ${offer.version}")
        } catch (problem: Exception) {
            folder.deleteRecursively()
            throw problem
        }
    }

    private fun apk(context: Context, version: Version) = File(folder(context), "usagetop-${version.text}.apk")

    /** The checked download that is waiting to go in, if it is still newer than what runs. */
    private fun waiting(context: Context): Pair<Version, File>? {
        val running = current ?: return null
        val files = folder(context).listFiles().orEmpty()
        val found = files
            .mapNotNull { file ->
                Regex("""^usagetop-(.+)\.apk$""").matchEntire(file.name)?.groupValues?.get(1)?.let(Version::parse)?.let { it to file }
            }
            .filter { it.first > running }
            .maxByOrNull { it.first }
        if (found == null && files.isNotEmpty()) folder(context).deleteRecursively()
        return found
    }

    /**
     * Why a file must not be handed to Android as an update, or null when it
     * may: it has to be this app, in a higher version, signed like this copy.
     */
    private fun whyNot(context: Context, file: File): String? {
        val packages = context.packageManager
        val flags = if (Build.VERSION.SDK_INT >= 28) PackageManager.GET_SIGNING_CERTIFICATES else @Suppress("DEPRECATION") PackageManager.GET_SIGNATURES
        val theirs = packages.getPackageArchiveInfo(file.path, flags) ?: return "it is not an app"
        val ours = packages.getPackageInfo(context.packageName, flags)
        if (theirs.packageName != context.packageName) return "it is another app"
        if (versionCode(theirs) <= versionCode(ours)) return "it is not newer"
        val theirSigners = signers(theirs)
        // Some Android versions do not read an archive's signers; Android's own
        // check at install time still stands.
        if (theirSigners.isNotEmpty() && theirSigners != signers(ours)) return "it is signed with another key"
        return null
    }

    @Suppress("DEPRECATION")
    private fun versionCode(info: PackageInfo): Long =
        if (Build.VERSION.SDK_INT >= 28) info.longVersionCode else info.versionCode.toLong()

    @Suppress("DEPRECATION")
    private fun signers(info: PackageInfo): Set<String> {
        val signatures = if (Build.VERSION.SDK_INT >= 28) {
            info.signingInfo?.let { if (it.hasMultipleSigners()) it.apkContentsSigners else it.signingCertificateHistory }
        } else {
            info.signatures
        }
        return signatures.orEmpty().map { signature ->
            MessageDigest.getInstance("SHA-256").digest(signature.toByteArray()).joinToString("") { "%02x".format(it) }
        }.toSet()
    }

    /**
     * True when Android would put an update in without asking: from Android
     * 12, for an app that updates itself and has been allowed to install apps.
     */
    fun installsUnattended(context: Context): Boolean =
        Build.VERSION.SDK_INT >= 31 && context.packageManager.canRequestPackageInstalls()

    /** The app has nothing on screen, so replacing it interrupts nobody. */
    fun inBackground(): Boolean {
        val process = ActivityManager.RunningAppProcessInfo()
        ActivityManager.getMyMemoryState(process)
        return process.importance > ActivityManager.RunningAppProcessInfo.IMPORTANCE_VISIBLE
    }

    /**
     * Hands the waiting download to Android. What happens next arrives at
     * [UpdateReceiver]: a request to confirm, a failure, or nothing at all,
     * because a successful update replaces this process.
     */
    fun install(context: Context): Boolean {
        val app = context.applicationContext
        val (version, file) = waiting(app) ?: return false
        if (states.value.phase == Phase.INSTALLING) return false
        whyNot(app, file)?.let {
            folder(app).deleteRecursively()
            states.value = State(Phase.FAILED, failure = "Couldn't install ${version.text}: $it")
            return false
        }
        return try {
            val installer = app.packageManager.packageInstaller
            val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
            params.setAppPackageName(app.packageName)
            params.setSize(file.length())
            if (Build.VERSION.SDK_INT >= 31) params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
            val id = installer.createSession(params)
            installer.openSession(id).use { session ->
                session.openWrite("usagetop.apk", 0, file.length()).use { out ->
                    file.inputStream().use { it.copyTo(out) }
                    session.fsync(out)
                }
                val mutable = if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
                val result = PendingIntent.getBroadcast(
                    app, id, Intent(app, UpdateReceiver::class.java), PendingIntent.FLAG_UPDATE_CURRENT or mutable,
                )
                states.value = states.value.copy(phase = Phase.INSTALLING, target = version.text, failure = null)
                session.commit(result.intentSender)
            }
            true
        } catch (problem: Exception) {
            Log.w(TAG, "Install failed", problem)
            states.value = State(Phase.FAILED, failure = "Couldn't install ${version.text}")
            false
        }
    }

    /** Android answered an install: it wants confirming, or it failed. */
    internal fun installAnswered(context: Context, needsUser: Boolean, failure: String?) {
        val waiting = waiting(context.applicationContext)
        val checked = prefs(context).getLong(KEY_CHECKED, 0L).takeIf { it > 0 }
        states.value = when {
            needsUser && waiting != null -> State(Phase.READY, waiting.first.text, checked)
            failure != null -> State(Phase.FAILED, checkedAt = checked, failure = failure)
            waiting != null -> State(Phase.READY, waiting.first.text, checked)
            else -> State(Phase.UP_TO_DATE, checkedAt = checked)
        }
    }
}
