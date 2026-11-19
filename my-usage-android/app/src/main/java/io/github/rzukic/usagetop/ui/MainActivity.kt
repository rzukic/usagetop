package io.github.rzukic.usagetop.ui

import android.content.Intent
import android.content.SharedPreferences
import android.graphics.Typeface
import android.net.Uri
import android.os.Bundle
import android.text.method.PasswordTransformationMethod
import android.view.MotionEvent
import android.view.View
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.browser.customtabs.CustomTabsIntent
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.google.android.material.button.MaterialButton
import com.google.android.material.chip.Chip
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.auth.OAuthCallbackService
import io.github.rzukic.usagetop.auth.OAuthConfig
import io.github.rzukic.usagetop.auth.OAuthSession
import io.github.rzukic.usagetop.data.Credentials
import io.github.rzukic.usagetop.data.CombinedUsage
import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.BuildConfig
import io.github.rzukic.usagetop.data.ServerClient
import io.github.rzukic.usagetop.update.Updater
import io.github.rzukic.usagetop.data.Snapshot
import io.github.rzukic.usagetop.data.UsageRepository
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.data.UsageWindow
import io.github.rzukic.usagetop.databinding.ActivityMainBinding
import io.github.rzukic.usagetop.widget.UsageWidgetProvider
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.roundToInt

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private val credentials by lazy { Credentials(this) }

    /**
     * The server's accounts, shown in place of this app's own sign-ins while an
     * address is set. Built after the layout is inflated, so it is lazy.
     */
    private val serverAccounts by lazy {
        ServerAccounts(
            activity = this,
            credentials = credentials,
            rows = binding.serverAccountRows,
            status = binding.serverAccountsStatus,
            addRow = binding.serverAccountAdd,
            openUrl = ::openUrl,
            onChanged = {
                // The server polls the account the moment its credentials change.
                lifecycleScope.launch {
                    delay(4_000)
                    refresh()
                }
            },
        )
    }
    private val repository by lazy { UsageRepository(this) }
    private var cacheWatcher: SharedPreferences.OnSharedPreferenceChangeListener? = null
    private var refreshing = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        setupSecretFields()
        loadSavedValues()
        applyMode()
        showTab(TAB_USAGE)
        applySnapshot(repository.cached())

        binding.tabUsage.setOnClickListener { showTab(TAB_USAGE) }
        binding.tabSettings.setOnClickListener { showTab(TAB_SETTINGS) }
        binding.btnRefresh.setOnClickListener { refresh(force = true) }
        binding.allUsageCard.setOnClickListener {
            startActivity(Intent(this, CombinedActivity::class.java))
        }
        binding.btnSave.setOnClickListener {
            val wasServer = repository.usesServer()
            saveValues()
            showLocalAccounts()
            if (repository.usesServer() != wasServer) applyMode()
            refresh(force = true)
        }
        binding.serverWebsite.setOnClickListener {
            credentials.serverUrl()?.let(::openUrl)
        }

        binding.claudeSignIn.setOnClickListener { signIn(OAuthConfig.CLAUDE.providerId) }
        binding.chatgptSignIn.setOnClickListener { signIn(OAuthConfig.CHATGPT.providerId) }
        binding.museSignIn.setOnClickListener { signIn(ProviderId.MUSE) }
        binding.antigravitySignIn.setOnClickListener { signIn(OAuthConfig.ANTIGRAVITY.providerId) }
        binding.mistralGetKey.setOnClickListener { openUrl(MISTRAL_KEYS) }
        binding.kimiGetKey.setOnClickListener { openUrl(KIMI_KEYS) }

        setupUpdates()
        setupLocalAccounts()

        lifecycleScope.launch {
            OAuthSession.results.collect { result ->
                setBusy(false)
                when (result) {
                    is OAuthSession.Result.Success -> {
                        binding.overallStatus.text = getString(R.string.signed_in)
                        loadSavedValues()
                        showLocalAccounts()
                        refresh()
                    }
                    is OAuthSession.Result.Failure -> {
                        binding.overallStatus.text =
                            "${getString(R.string.sign_in_failed)}: ${result.message}"
                    }
                }
            }
        }
    }

    /**
     * The cache is shared with the widget and the other screens, so whatever landed
     * while this one was in the background is what it shows on the way back in.
     * Opening the app then replaces that reading with a fresh one, unless the one
     * on screen is only seconds old.
     */
    override fun onResume() {
        super.onResume()
        if (!refreshing) applySnapshot(repository.cached())
        cacheWatcher = repository.watch {
            if (!refreshing) applySnapshot(repository.cached())
        }
        if (!repository.isFresh()) refresh()
        // Back from Android's own screens, what Install will do may have changed.
        showUpdate(Updater.state.value)
        if (Updater.isDue(this)) lifecycleScope.launch { Updater.check(this@MainActivity) }
    }

    override fun onPause() {
        super.onPause()
        cacheWatcher?.let(repository::unwatch)
        cacheWatcher = null
    }

    // ---- Updates ----------------------------------------------------------

    private fun setupUpdates() {
        binding.updateVersion.text = getString(R.string.updates_version, BuildConfig.VERSION_NAME)
        binding.updateBeta.isChecked = Updater.beta(this)
        // Turning beta versions on or off is a different question, so it is asked again.
        binding.updateBeta.setOnCheckedChangeListener { _, wanted ->
            Updater.setBeta(this, wanted)
            lifecycleScope.launch { Updater.check(this@MainActivity) }
        }
        binding.updateCheck.setOnClickListener {
            lifecycleScope.launch { Updater.check(this@MainActivity) }
        }
        binding.updateInstall.setOnClickListener { Updater.install(this) }
        lifecycleScope.launch { Updater.state.collect(::showUpdate) }
    }

    private fun showUpdate(state: Updater.State) {
        val phase = state.phase
        binding.updateStatus.text = when (phase) {
            Updater.Phase.OFF -> getString(R.string.updates_off)
            Updater.Phase.WAITING -> getString(R.string.updates_waiting)
            Updater.Phase.CHECKING -> getString(R.string.updates_checking)
            Updater.Phase.UP_TO_DATE -> state.checkedAt
                ?.let { getString(R.string.updates_up_to_date_checked, Format.ago(java.time.Instant.ofEpochMilli(it))) }
                ?: getString(R.string.updates_up_to_date)
            Updater.Phase.DOWNLOADING -> getString(R.string.updates_downloading, state.target.orEmpty())
            Updater.Phase.READY -> getString(R.string.updates_ready, state.target.orEmpty())
            Updater.Phase.INSTALLING -> getString(R.string.updates_installing, state.target.orEmpty())
            Updater.Phase.FAILED -> state.failure ?: getString(R.string.updates_failed)
        }
        val ready = phase == Updater.Phase.READY
        val off = phase == Updater.Phase.OFF
        val busy = phase == Updater.Phase.CHECKING || phase == Updater.Phase.DOWNLOADING || phase == Updater.Phase.INSTALLING
        binding.updateActions.visibility = if (off) View.GONE else View.VISIBLE
        binding.updateBeta.visibility = if (off) View.GONE else View.VISIBLE
        binding.updateInstall.visibility = if (ready) View.VISIBLE else View.GONE
        binding.updateCheck.isEnabled = !busy
        binding.updateHint.visibility = if (ready) View.VISIBLE else View.GONE
        binding.updateHint.text = when {
            !packageManager.canRequestPackageInstalls() -> getString(R.string.updates_hint_first)
            Updater.installsUnattended(this) -> getString(R.string.updates_hint_unattended)
            else -> getString(R.string.updates_hint_confirm)
        }
        // A dot on the tab, so a waiting update is seen without opening Settings.
        binding.tabSettings.text = getString(if (ready) R.string.tab_settings_update else R.string.tab_settings)
    }

    // ---- Tabs -------------------------------------------------------------

    private fun showTab(tab: Int) {
        val usage = tab == TAB_USAGE
        binding.pageTitle.setText(if (usage) R.string.tab_usage else R.string.tab_settings)
        binding.usageContainer.visibility = if (usage) View.VISIBLE else View.GONE
        binding.allUsageCard.visibility = if (usage) View.VISIBLE else View.GONE
        binding.settingsContainer.visibility = if (usage) View.GONE else View.VISIBLE
        styleSegment(binding.tabUsage, usage)
        styleSegment(binding.tabSettings, !usage)

        // The server's accounts may have changed on the website since this was built.
        if (!usage && repository.usesServer()) serverAccounts.load()
    }

    /**
     * With an address set the server holds the accounts, so its own card replaces
     * the five below: a key typed here belongs to the server, not to this app.
     */
    /** Each provider's card in the local settings, to show or hide as it is added or removed. */
    private val localCards by lazy {
        mapOf(
            ProviderId.CLAUDE to binding.claudeCard,
            ProviderId.CHATGPT to binding.chatgptCard,
            ProviderId.MISTRAL to binding.mistralCard,
            ProviderId.KIMI to binding.kimiCard,
            ProviderId.MUSE to binding.museCard,
            ProviderId.ANTIGRAVITY to binding.antigravityCard,
        )
    }

    /** Gives every card its Remove button, once. */
    private fun setupLocalAccounts() {
        localCards.forEach { (id, card) ->
            card.addView(MaterialButton(this, null, com.google.android.material.R.attr.borderlessButtonStyle).apply {
                text = getString(R.string.remove_account)
                isAllCaps = false
                textSize = 12f
                minHeight = 0
                minimumHeight = 0
                setPadding(0, dp(6), 0, 0)
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.text_secondary))
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.WRAP_CONTENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT,
                ).apply { topMargin = dp(4) }
                setOnClickListener { removeProvider(id) }
            })
        }
        showLocalAccounts()
    }

    /** Only the added providers' cards, and a button for each of the rest. */
    private fun showLocalAccounts() {
        val added = credentials.added()
        localCards.forEach { (id, card) -> card.visibility = if (id in added) View.VISIBLE else View.GONE }

        val missing = ProviderId.values().filter { it !in added }
        binding.addAccountCard.visibility = if (missing.isEmpty()) View.GONE else View.VISIBLE
        binding.addAccountCaption.text = getString(
            if (added.isEmpty()) R.string.add_account_caption_empty else R.string.add_account_caption
        )
        binding.addAccountChoices.removeAllViews()
        missing.forEach { id ->
            binding.addAccountChoices.addView(Chip(this).apply {
                text = id.displayName
                chipIcon = ContextCompat.getDrawable(this@MainActivity, ProviderArt.logo(id))
                chipIconTint = android.content.res.ColorStateList.valueOf(
                    ContextCompat.getColor(this@MainActivity, R.color.text_primary)
                )
                setOnClickListener {
                    credentials.add(id)
                    showLocalAccounts()
                    renderUsage(repository.cached())
                }
            })
        }
    }

    private fun removeProvider(id: ProviderId) {
        android.app.AlertDialog.Builder(this)
            .setMessage(getString(R.string.remove_account_question, id.displayName))
            .setPositiveButton(R.string.remove_account) { _, _ ->
                credentials.remove(id)
                loadSavedValues()
                showLocalAccounts()
                refresh(force = true)
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun applyMode() {
        val server = repository.usesServer()
        binding.localAccounts.visibility = if (server) View.GONE else View.VISIBLE
        binding.serverAccounts.visibility = if (server) View.VISIBLE else View.GONE
        binding.serverWebsite.visibility =
            if (credentials.serverUrl() != null) View.VISIBLE else View.GONE

        if (server) {
            serverAccounts.setup()
            serverAccounts.load()
        }
    }

    private fun styleSegment(view: TextView, selected: Boolean) {
        view.setBackgroundResource(if (selected) R.drawable.segment_thumb else 0)
        view.setTextColor(
            ContextCompat.getColor(this, if (selected) R.color.text_primary else R.color.text_secondary),
        )
        view.typeface = if (selected) {
            Typeface.create("sans-serif-medium", Typeface.NORMAL)
        } else {
            Typeface.create("sans-serif", Typeface.NORMAL)
        }
    }

    // ---- Usage dashboard --------------------------------------------------

    private fun applySnapshot(snapshot: Snapshot?) {
        renderUsage(snapshot)
        renderCombined(snapshot)
        applyStatus(snapshot)
        binding.overallStatus.text = overallStatus(snapshot)
        binding.serverStatus.text = when {
            !repository.usesServer() -> getString(R.string.server_off)
            else -> repository.serverProblem() ?: getString(R.string.server_connected)
        }
    }

    /**
     * With a server that could not be reached, the reading on screen is an old
     * one and has to say so.
     */
    private fun overallStatus(snapshot: Snapshot?): String {
        val read = snapshot?.takeIf { it.usages.isNotEmpty() }?.let { Format.ago(it.fetchedAt) }
        val problem = repository.serverProblem()
        return when {
            problem != null && read != null -> getString(R.string.server_problem_reading, problem, read)
            problem != null -> problem
            snapshot == null -> getString(R.string.never_updated)
            repository.usesServer() -> getString(R.string.updated_ago_server, Format.ago(snapshot.fetchedAt))
            else -> getString(R.string.updated_ago, Format.ago(snapshot.fetchedAt))
        }
    }

    /** The one-line summary of every long window, and the way into its chart. */
    private fun renderCombined(snapshot: Snapshot?) {
        val combined = snapshot?.let { CombinedUsage.of(it) }
        val percent = combined?.percent

        binding.allUsagePercent.text = percent?.let { getString(R.string.percent_left, it.roundToInt()) }
            ?: "—"
        binding.allUsagePercent.setTextColor(
            percent?.let { Severity.textColor(this, it) }
                ?: ContextCompat.getColor(this, R.color.text_tertiary),
        )
        binding.allUsageCaption.text = when {
            combined == null || combined.providers == 0 -> getString(R.string.combined_none)
            else -> getString(R.string.combined_caption, combined.providers)
        }
    }

    private fun renderUsage(snapshot: Snapshot?) {
        binding.usageContainer.removeAllViews()

        // Only what has been added: on a server, the accounts it follows; here,
        // the providers added in Settings, each with a section even before it
        // reports. Accounts added on a server follow their provider's.
        val usages = snapshot?.usages.orEmpty()
        val added = if (repository.usesServer()) emptySet() else credentials.added()
        val sections = ProviderId.values().flatMap { id ->
            val first = usages.firstOrNull { it.id == id && it.slot.isEmpty() }
            val firstSection = if (first != null || id in added) listOf(id to first) else emptyList()
            firstSection + usages.filter { it.id == id && it.slot.isNotEmpty() }.map { id to it }
        }

        if (sections.isEmpty() && snapshot != null) {
            binding.usageContainer.addView(TextView(this).apply {
                text = getString(R.string.no_accounts)
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.text_secondary))
                textSize = 13f
                setPadding(dp(4), dp(8), dp(4), dp(8))
            })
            return
        }

        sections.forEachIndexed { index, (id, usage) ->
            val section = layoutInflater.inflate(R.layout.usage_section, binding.usageContainer, false)
            if (index > 0) {
                (section.layoutParams as LinearLayout.LayoutParams).topMargin = dp(18)
            }
            bindSection(section, id, usage)
            binding.usageContainer.addView(section)
        }
    }

    private fun bindSection(section: View, id: ProviderId, usage: ProviderUsage?) {
        section.findViewById<TextView>(R.id.section_name).text = usage?.displayName ?: id.displayName
        section.findViewById<ImageView>(R.id.section_logo).setImageResource(ProviderArt.logo(id))

        // A reading kept from an earlier fetch is marked so it never reads as live.
        val stale = section.findViewById<TextView>(R.id.section_stale)
        if (usage?.stale == true) {
            stale.text = "· ${Format.ago(usage.fetchedAt)}"
            stale.visibility = View.VISIBLE
        } else {
            stale.visibility = View.GONE
        }

        val plan = section.findViewById<TextView>(R.id.section_plan)
        val planName = usage?.plan
        if (planName.isNullOrBlank()) {
            plan.visibility = View.GONE
        } else {
            plan.text = planName
            plan.visibility = View.VISIBLE
        }

        val card = section.findViewById<LinearLayout>(R.id.section_card)
        card.setOnClickListener {
            startActivity(ProviderDetailActivity.intent(this, usage?.accountKey ?: id.key))
        }

        val windows = usage?.windows.orEmpty()
        if (windows.isEmpty()) {
            card.addView(note(usage))
        } else {
            windows.forEach { card.addView(metricRow(it)) }
        }
    }

    /** A dim line standing in for the rows a provider has not reported yet. */
    private fun note(usage: ProviderUsage?): TextView = TextView(this).apply {
        text = when {
            usage == null -> getString(R.string.not_configured)
            usage.state == UsageState.NOT_CONFIGURED -> usage.message ?: getString(R.string.not_configured)
            else -> usage.message ?: getString(R.string.refresh_failed)
        }
        setTextColor(ContextCompat.getColor(context, R.color.text_tertiary))
        textSize = 12f
        setPadding(0, dp(4), 0, dp(4))
    }

    private fun metricRow(window: UsageWindow): View {
        val row = layoutInflater.inflate(R.layout.usage_metric_row, binding.usageContainer, false)
        row.findViewById<TextView>(R.id.metric_label).text = window.label

        val meter = row.findViewById<MeterView>(R.id.metric_meter)
        val value = row.findViewById<TextView>(R.id.metric_value)
        val reset = row.findViewById<TextView>(R.id.metric_reset)

        val percent = window.remainingPercent
        if (percent != null) {
            meter.fill = percent / 100f
            meter.fillColor = Severity.color(this, percent)
            meter.visibility = View.VISIBLE
            value.text = getString(R.string.percent_left, percent.roundToInt())
            value.setTextColor(Severity.textColor(this, percent))
        } else {
            meter.visibility = View.INVISIBLE
            value.text = window.detail ?: "—"
            value.setTextColor(ContextCompat.getColor(this, R.color.text_secondary))
        }

        reset.text = Format.reset(window.resetAt).orEmpty()
        return row
    }

    // ---- Settings ---------------------------------------------------------

    /**
     * Keys are masked by default: a settings screen showing four live tokens in
     * plain text is one screenshot away from leaking them. The eye at the end of
     * each field reveals that one field only.
     */
    private fun setupSecretFields() {
        listOf(
            binding.claudeToken,
            binding.chatgptToken,
            binding.mistralToken,
            binding.kimiToken,
            binding.museToken,
            binding.serverToken,
        ).forEach { field ->
            field.transformationMethod = PasswordTransformationMethod.getInstance()
            field.compoundDrawablePadding = dp(8)
            field.setCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, R.drawable.ic_eye_off, 0)
            field.setOnTouchListener { view, event -> handleEyeTap(field, view, event) }
        }
    }

    private fun handleEyeTap(field: EditText, view: View, event: MotionEvent): Boolean {
        if (event.action != MotionEvent.ACTION_UP) return false
        if (field.compoundDrawablesRelative[2] == null) return false
        val iconStart = field.width - field.totalPaddingEnd + field.compoundDrawablePadding
        if (event.x < iconStart - dp(6)) return false

        val hidden = field.transformationMethod is PasswordTransformationMethod
        field.transformationMethod =
            if (hidden) null else PasswordTransformationMethod.getInstance()
        field.setCompoundDrawablesRelativeWithIntrinsicBounds(
            0,
            0,
            if (hidden) R.drawable.ic_eye else R.drawable.ic_eye_off,
            0,
        )
        field.setSelection(field.text?.length ?: 0)
        view.performClick()
        return true
    }

    private fun signIn(config: OAuthConfig) {
        binding.overallStatus.text = getString(R.string.signing_in)
        setBusy(true)

        OAuthSession.prepare()
        startForegroundService(OAuthCallbackService.intent(this, config.providerId))

        lifecycleScope.launch {
            val url = OAuthSession.awaitAuthorizeUrl(5_000)
            if (url == null) {
                setBusy(false)
                binding.overallStatus.text =
                    "${getString(R.string.sign_in_failed)}: no callback port"
                return@launch
            }
            openUrl(url)
        }
    }

    /**
     * Every sign-in runs in [OAuthCallbackService], which outlives this screen:
     * the browser round trip is longer than a backgrounded process survives.
     * The service publishes the URL to open and, later, the result.
     */
    private fun signIn(provider: ProviderId) {
        binding.overallStatus.text = getString(R.string.signing_in)
        setBusy(true)

        OAuthSession.prepare()
        startForegroundService(OAuthCallbackService.intent(this, provider))

        lifecycleScope.launch {
            val url = OAuthSession.awaitAuthorizeUrl(5_000)
            if (url == null) {
                setBusy(false)
                binding.overallStatus.text =
                    "${getString(R.string.sign_in_failed)}: no sign-in URL"
                return@launch
            }
            openUrl(url)
        }
    }

    private fun openUrl(url: String) {
        val uri = Uri.parse(url)
        try {
            CustomTabsIntent.Builder().build().launchUrl(this, uri)
        } catch (t: Throwable) {
            startActivity(Intent(Intent.ACTION_VIEW, uri))
        }
    }

    private fun setBusy(busy: Boolean) {
        binding.claudeSignIn.isEnabled = !busy
        binding.chatgptSignIn.isEnabled = !busy
        binding.museSignIn.isEnabled = !busy
        binding.antigravitySignIn.isEnabled = !busy
        binding.btnRefresh.isEnabled = !busy
        binding.btnSave.isEnabled = !busy
    }

    /** Each provider's token field, so loading and saving walk the same list. */
    private fun tokenFields(): Map<ProviderId, android.widget.EditText> = mapOf(
        ProviderId.CLAUDE to binding.claudeToken,
        ProviderId.CHATGPT to binding.chatgptToken,
        ProviderId.MISTRAL to binding.mistralToken,
        ProviderId.KIMI to binding.kimiToken,
        ProviderId.MUSE to binding.museToken,
    )

    /** What each field held when it was filled in, to tell an edit from a leftover. */
    private val loadedTokens = mutableMapOf<ProviderId, String>()

    private fun loadSavedValues() {
        tokenFields().forEach { (id, field) ->
            val token = credentials.token(id).orEmpty()
            loadedTokens[id] = token
            field.setText(token)
        }
        binding.serverUrl.setText(credentials.serverUrl().orEmpty())
        binding.serverToken.setText(credentials.serverToken().orEmpty())
        binding.kimiRegion.isChecked = credentials.kimiChina()
        binding.mistralBudget.setText(credentials.mistralBudget()?.let(::plainNumber).orEmpty())
        binding.mistralSpent.setText(credentials.mistralSpent()?.let(::plainNumber).orEmpty())
    }

    private fun saveValues() {
        // Only a field the user actually edited is written. The screen may have
        // been open for hours, and writing back what it loaded would put an access
        // token that has since been renewed over its replacement.
        tokenFields().forEach { (id, field) ->
            val value = field.text?.toString().orEmpty().trim()
            if (value == loadedTokens[id].orEmpty()) return@forEach
            credentials.setManualToken(id, value)
            loadedTokens[id] = value
        }
        // Something typed that is not an address is left unsaved rather than read
        // as "no server", which would quietly switch the app back.
        val typed = binding.serverUrl.text?.toString().orEmpty().trim()
        val url = ServerClient.normalizeUrl(typed)
        if (typed.isEmpty() || url != null) {
            credentials.setServer(url, binding.serverToken.text?.toString())
            binding.serverUrl.setText(url.orEmpty())
            binding.serverUrl.error = null
        } else {
            binding.serverUrl.error = getString(R.string.server_bad_address)
        }

        credentials.setKimiChina(binding.kimiRegion.isChecked)
        credentials.setMistralBudget(binding.mistralBudget.text?.toString()?.toDoubleOrNull())
        credentials.setMistralSpent(binding.mistralSpent.text?.toString()?.toDoubleOrNull())
    }

    private fun refresh(force: Boolean = false) {
        if (refreshing) return
        refreshing = true
        binding.overallStatus.text = getString(R.string.refreshing)
        binding.btnRefresh.isEnabled = false
        RefreshSpinner.start(binding.btnRefresh)

        lifecycleScope.launch {
            val snapshot = try {
                repository.refresh(force)
            } catch (t: Throwable) {
                null
            }

            refreshing = false
            binding.btnRefresh.isEnabled = true
            RefreshSpinner.stop(binding.btnRefresh)
            if (snapshot == null) {
                binding.overallStatus.text = getString(R.string.refresh_failed)
            } else {
                applySnapshot(snapshot)
            }
            UsageWidgetProvider.render(this@MainActivity)
        }
    }

    private fun applyStatus(snapshot: Snapshot?) {
        binding.claudeStatus.text = describe(snapshot, ProviderId.CLAUDE)
        binding.chatgptStatus.text = describe(snapshot, ProviderId.CHATGPT)
        binding.mistralStatus.text = describe(snapshot, ProviderId.MISTRAL)
        binding.kimiStatus.text = describe(snapshot, ProviderId.KIMI)
        binding.museStatus.text = describe(snapshot, ProviderId.MUSE)
        binding.antigravityStatus.text = describe(snapshot, ProviderId.ANTIGRAVITY)
    }

    private fun describe(snapshot: Snapshot?, id: ProviderId): String {
        val usage = snapshot?.usages?.firstOrNull { it.id == id && it.slot.isEmpty() }
        if (usage == null) {
            return if (credentials.isSignedIn(id)) getString(R.string.signed_in) else "Not configured"
        }

        return when (usage.state) {
            UsageState.NOT_CONFIGURED -> usage.message ?: "Not configured"
            UsageState.ERROR -> usage.message ?: "Error"
            UsageState.OK -> {
                val windows = usage.windows.mapNotNull { window ->
                    when {
                        window.remainingPercent != null ->
                            "${window.label} ${window.remainingPercent.roundToInt()}% left"
                        window.detail != null -> window.detail
                        else -> null
                    }
                }
                listOfNotNull(
                    windows.joinToString(" · ").takeIf { it.isNotBlank() },
                    usage.plan,
                    "as of ${Format.ago(usage.fetchedAt)}".takeIf { usage.stale },
                )
                    .joinToString(" · ")
                    .ifBlank { "OK" }
            }
        }
    }

    // ---- Formatting -------------------------------------------------------

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).roundToInt()

    private fun plainNumber(value: Double): String =
        if (value % 1.0 == 0.0) value.toLong().toString() else value.toString()

    private companion object {
        const val TAB_USAGE = 0
        const val TAB_SETTINGS = 1

        const val MISTRAL_KEYS = "https://console.mistral.ai/api-keys"
        const val KIMI_KEYS = "https://www.kimi.ai/code/console"
    }
}
