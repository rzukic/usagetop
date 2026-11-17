package io.github.rzukic.usagetop.ui

import android.app.AlertDialog
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.text.InputType
import android.text.method.PasswordTransformationMethod
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.Switch
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.google.android.material.button.MaterialButton
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.Credentials
import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ServerAccount
import io.github.rzukic.usagetop.data.ServerClient
import io.github.rzukic.usagetop.data.ServerException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.roundToInt

/**
 * The server's accounts, managed from here. In server mode the app's own
 * sign-ins are not used, so the settings show this instead: add an account,
 * set or replace its key, sign in again, rename it, sign it out, remove it.
 *
 * The server never hands a token back, so nothing here can read a key or a
 * session — only replace one.
 */
class ServerAccounts(
    private val activity: AppCompatActivity,
    private val credentials: Credentials,
    private val rows: LinearLayout,
    private val status: TextView,
    private val addRow: LinearLayout,
    private val openUrl: (String) -> Unit,
    private val onChanged: () -> Unit,
) {
    private val rowsByKey = mutableMapOf<String, Row>()
    private var loading = false

    /** One button per provider: a second Claude subscription, a work ChatGPT. */
    fun setup() {
        if (addRow.childCount > 0) return
        ProviderId.values().forEach { provider ->
            val button = button(provider.displayName, tonal = false)
            button.textSize = 10f
            button.layoutParams = LinearLayout.LayoutParams(0, dp(34), 1f).apply { marginEnd = dp(4) }
            button.setOnClickListener { add(provider) }
            addRow.addView(button)
        }
    }

    fun load() {
        activity.lifecycleScope.launch { reload() }
    }

    private suspend fun reload(): List<ServerAccount>? {
        val url = credentials.serverUrl() ?: return null
        if (loading) return null

        loading = true
        return try {
            val accounts = withContext(Dispatchers.IO) {
                ServerClient.accounts(url, credentials.serverToken())
            }
            render(accounts)
            accounts
        } catch (e: ServerException) {
            status.text = e.message
            null
        } finally {
            loading = false
        }
    }

    private fun render(accounts: List<ServerAccount>) {
        val seen = mutableSetOf<String>()
        accounts.forEachIndexed { index, account ->
            seen += account.key
            val row = rowsByKey.getOrPut(account.key) { buildRow(account) }
            update(row, account)
            row.divider.visibility = if (index == 0) View.GONE else View.VISIBLE

            // Re-adding a child moves it to the end, so the order follows the server's.
            (row.container.parent as? ViewGroup)?.removeView(row.container)
            rows.addView(row.container)
        }

        rowsByKey.keys.filter { it !in seen }.forEach { key ->
            rowsByKey.remove(key)?.let { row ->
                closeStep(row)
                rows.removeView(row.container)
            }
        }

        status.text = if (accounts.isEmpty()) {
            activity.getString(R.string.server_accounts_none)
        } else {
            activity.getString(R.string.server_accounts_caption)
        }
    }

    private fun buildRow(account: ServerAccount): Row {
        val container = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(10), 0, dp(10))
        }

        val divider = View(activity).apply {
            setBackgroundColor(ContextCompat.getColor(activity, R.color.hairline))
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(1))
        }
        container.addView(divider)

        val title = title(account.displayName)
        val caption = caption("")
        container.addView(title)
        container.addView(caption)

        val act = button("", tonal = true)
        act.layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(38),
        ).apply { topMargin = dp(8) }
        container.addView(act)

        val secondary = LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            ).apply { topMargin = dp(4) }
        }
        val signOut = button(activity.getString(R.string.server_account_sign_out), tonal = false)
        val rename = button(activity.getString(R.string.server_account_rename), tonal = false)
        val remove = button(activity.getString(R.string.server_account_remove), tonal = false)
        listOf(signOut, rename, remove).forEach { secondary.addView(it) }
        container.addView(secondary)

        val step = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            visibility = View.GONE
        }
        container.addView(step)

        val problem = caption("").apply {
            setTextColor(ContextCompat.getColor(activity, R.color.meter_warning))
        }
        container.addView(problem)

        val row = Row(
            account = account,
            container = container,
            divider = divider,
            title = title,
            status = caption,
            act = act,
            signOut = signOut,
            rename = rename,
            remove = remove,
            step = step,
            problem = problem,
        )

        act.setOnClickListener {
            if (isKeyProvider(row.account.provider)) showKeyStep(row) else startSignIn(row)
        }
        signOut.setOnClickListener { confirmSignOut(row) }
        rename.setOnClickListener { rename(row) }
        remove.setOnClickListener { confirmRemove(row) }

        return row
    }

    private fun update(row: Row, account: ServerAccount) {
        row.account = account
        val keyProvider = isKeyProvider(account.provider)

        row.title.text = account.displayName
        row.status.text = when {
            !account.configured -> activity.getString(R.string.server_account_not_set_up)
            keyProvider -> activity.getString(R.string.server_account_key_stored)
            account.renews -> activity.getString(R.string.server_account_renews)
            else -> activity.getString(R.string.server_account_token_stored)
        }

        row.act.text = if (keyProvider) {
            activity.getString(
                if (account.configured) R.string.server_account_replace_key
                else R.string.server_account_set_key,
            )
        } else {
            activity.getString(
                if (account.configured) R.string.server_account_sign_in_again
                else R.string.server_account_sign_in,
            )
        }

        row.signOut.visibility = if (account.configured) View.VISIBLE else View.GONE
        row.remove.visibility = if (account.removable) View.VISIBLE else View.GONE
    }

    /**
     * A key is write-only here: the server says whether one is stored, never
     * what it is, so this asks for a new one rather than showing the old.
     */
    private fun showKeyStep(row: Row) {
        closeStep(row)
        row.step.visibility = View.VISIBLE
        row.step.addView(
            caption(activity.getString(R.string.server_account_api_key, row.account.displayName)),
        )

        val key = field(activity.getString(R.string.server_token_hint), password = true)
        row.step.addView(key)

        var china: Switch? = null
        if (row.account.provider == ProviderId.KIMI.key) {
            china = Switch(activity).apply {
                text = activity.getString(R.string.server_account_china)
                textSize = 11f
                setTextColor(ContextCompat.getColor(activity, R.color.text_secondary))
                minHeight = 0
                setPadding(0, dp(8), 0, 0)
            }
            row.step.addView(china)
        }

        var budget: EditText? = null
        var spent: EditText? = null
        if (row.account.provider == ProviderId.MISTRAL.key) {
            budget = field(activity.getString(R.string.server_account_budget), password = false)
            budget.inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_DECIMAL
            spent = field(activity.getString(R.string.server_account_spent), password = false)
            spent.inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_DECIMAL
            row.step.addView(budget)
            row.step.addView(spent)
            row.step.addView(caption(activity.getString(R.string.server_account_budget_hint)))
        }

        val save = button(activity.getString(R.string.server_account_save), tonal = true)
        save.layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(38),
        ).apply { topMargin = dp(8) }
        save.setOnClickListener {
            val value = key.text?.toString().orEmpty().trim()
            if (value.isEmpty()) {
                row.problem.text = activity.getString(R.string.server_account_key_first)
                return@setOnClickListener
            }

            save.isEnabled = false
            activity.lifecycleScope.launch {
                try {
                    withContext(Dispatchers.IO) {
                        ServerClient.setKey(
                            url = url(),
                            token = credentials.serverToken(),
                            key = row.account.key,
                            value = value,
                            kimiChina = china?.isChecked,
                            mistralBudget = budget?.text?.toString()?.toDoubleOrNull(),
                            mistralSpent = spent?.text?.toString()?.toDoubleOrNull(),
                        )
                    }
                    closeStep(row)
                    afterChange()
                } catch (e: ServerException) {
                    row.problem.text = e.message
                } finally {
                    save.isEnabled = true
                }
            }
        }
        row.step.addView(save)
        key.requestFocus()
    }

    private fun startSignIn(row: Row) {
        closeStep(row)
        row.act.isEnabled = false

        activity.lifecycleScope.launch {
            val step = try {
                withContext(Dispatchers.IO) {
                    ServerClient.startSignIn(url(), credentials.serverToken(), row.account.key)
                }
            } catch (e: ServerException) {
                row.problem.text = e.message
                row.act.isEnabled = true
                return@launch
            }
            row.act.isEnabled = true

            row.step.visibility = View.VISIBLE
            row.step.addView(caption(step.instructions))

            val open = button(activity.getString(R.string.server_account_open_page), tonal = true)
            open.layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                dp(38),
            ).apply { topMargin = dp(8) }
            open.setOnClickListener { openUrl(step.url) }
            row.step.addView(open)

            if (step.kind == "device") {
                row.step.addView(
                    TextView(activity).apply {
                        text = step.userCode.orEmpty()
                        textSize = 20f
                        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                        setTextColor(ContextCompat.getColor(activity, R.color.text_primary))
                        setPadding(0, dp(10), 0, 0)
                    },
                )
                row.step.addView(caption(activity.getString(R.string.server_account_waiting)))
                row.poll = activity.lifecycleScope.launch { poll(row) }
                return@launch
            }

            row.step.addView(
                caption(
                    activity.getString(
                        if (step.kind == "redirect") R.string.server_account_paste_address
                        else R.string.server_account_paste_code,
                    ),
                ),
            )

            val input = field("", password = false)
            row.step.addView(input)

            val finish = button(activity.getString(R.string.server_account_finish), tonal = true)
            finish.layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                dp(38),
            ).apply { topMargin = dp(8) }
            finish.setOnClickListener {
                val value = input.text?.toString().orEmpty().trim()
                if (value.isEmpty()) {
                    row.problem.text = activity.getString(R.string.server_account_paste_first)
                    return@setOnClickListener
                }

                finish.isEnabled = false
                activity.lifecycleScope.launch {
                    try {
                        withContext(Dispatchers.IO) {
                            ServerClient.completeSignIn(
                                url(),
                                credentials.serverToken(),
                                row.account.key,
                                value,
                            )
                        }
                        closeStep(row)
                        afterChange()
                    } catch (e: ServerException) {
                        row.problem.text = e.message
                    } finally {
                        finish.isEnabled = true
                    }
                }
            }
            row.step.addView(finish)
            input.requestFocus()
        }
    }

    /** A device sign-in finishes on the server; this only watches for it. */
    private suspend fun poll(row: Row) {
        while (true) {
            delay(3_000)
            val state = try {
                withContext(Dispatchers.IO) {
                    ServerClient.signInStatus(url(), credentials.serverToken(), row.account.key)
                }
            } catch (e: ServerException) {
                closeStep(row)
                row.problem.text = e.message
                return
            }

            if (state == "pending") continue
            closeStep(row)
            if (state != "done") row.problem.text = state
            afterChange()
            return
        }
    }

    private fun rename(row: Row) {
        val input = field(activity.getString(R.string.server_account_name_hint), password = false)
        input.setText(row.account.name.orEmpty())

        val dialog = AlertDialog.Builder(activity)
            .setTitle(R.string.server_account_rename)
            .setView(input)
            .setPositiveButton(R.string.server_account_save, null)
            .setNegativeButton(android.R.string.cancel, null)
            .create()

        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).isEnabled = false
                activity.lifecycleScope.launch {
                    try {
                        withContext(Dispatchers.IO) {
                            ServerClient.renameAccount(
                                url(),
                                credentials.serverToken(),
                                row.account.key,
                                input.text?.toString(),
                            )
                        }
                        dialog.dismiss()
                        afterChange()
                    } catch (e: ServerException) {
                        dialog.dismiss()
                        row.problem.text = e.message
                    }
                }
            }
        }
        dialog.show()
    }

    private fun confirmSignOut(row: Row) {
        AlertDialog.Builder(activity)
            .setTitle(R.string.server_account_sign_out_title)
            .setMessage(activity.getString(R.string.server_account_sign_out_question, row.account.displayName))
            .setPositiveButton(R.string.server_account_sign_out) { _, _ ->
                activity.lifecycleScope.launch {
                    try {
                        withContext(Dispatchers.IO) {
                            ServerClient.signOut(url(), credentials.serverToken(), row.account.key)
                        }
                        closeStep(row)
                        afterChange()
                    } catch (e: ServerException) {
                        row.problem.text = e.message
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun confirmRemove(row: Row) {
        AlertDialog.Builder(activity)
            .setTitle(R.string.server_account_remove_title)
            .setMessage(activity.getString(R.string.server_account_remove_question, row.account.displayName))
            .setPositiveButton(R.string.server_account_remove) { _, _ ->
                activity.lifecycleScope.launch {
                    try {
                        withContext(Dispatchers.IO) {
                            ServerClient.removeAccount(url(), credentials.serverToken(), row.account.key)
                        }
                        closeStep(row)
                        afterChange()
                    } catch (e: ServerException) {
                        row.problem.text = e.message
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun add(provider: ProviderId) {
        activity.lifecycleScope.launch {
            val account = try {
                withContext(Dispatchers.IO) {
                    ServerClient.addAccount(url(), credentials.serverToken(), provider.key, null)
                }
            } catch (e: ServerException) {
                status.text = e.message
                return@launch
            }

            reload()
            val row = rowsByKey[account.key] ?: return@launch
            if (isKeyProvider(account.provider)) showKeyStep(row) else startSignIn(row)
        }
    }

    /** The server polls an account the moment its credentials change. */
    private fun afterChange() {
        load()
        onChanged()
    }

    private fun closeStep(row: Row) {
        row.poll?.cancel()
        row.poll = null
        row.step.removeAllViews()
        row.step.visibility = View.GONE
        row.problem.text = ""
    }

    private fun url(): String =
        credentials.serverUrl() ?: throw ServerException("No server address is set.")

    private fun isKeyProvider(provider: String): Boolean =
        provider == ProviderId.MISTRAL.key || provider == ProviderId.KIMI.key

    // ---- Small views ------------------------------------------------------

    private fun button(text: String, tonal: Boolean): MaterialButton =
        MaterialButton(activity).apply {
            this.text = text
            isAllCaps = false
            textSize = 12f
            cornerRadius = dp(20)
            minHeight = 0
            minimumHeight = 0
            insetTop = 0
            insetBottom = 0
            setPadding(dp(12), 0, dp(12), 0)
            backgroundTintList = ColorStateList.valueOf(
                ContextCompat.getColor(
                    activity,
                    if (tonal) R.color.control_fill else android.R.color.transparent,
                ),
            )
            setTextColor(
                ContextCompat.getColor(
                    activity,
                    if (tonal) R.color.text_primary else R.color.text_secondary,
                ),
            )
            layoutParams = LinearLayout.LayoutParams(0, dp(34), 1f)
        }

    private fun field(hint: String, password: Boolean): EditText =
        EditText(activity).apply {
            this.hint = hint
            textSize = 12f
            setTextColor(ContextCompat.getColor(activity, R.color.text_primary))
            setHintTextColor(ContextCompat.getColor(activity, R.color.text_tertiary))
            setBackgroundResource(R.drawable.field_bg)
            setPadding(dp(12), dp(10), dp(12), dp(10))
            isSingleLine = true
            importantForAutofill = View.IMPORTANT_FOR_AUTOFILL_NO
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            ).apply { topMargin = dp(6) }
            if (password) transformationMethod = PasswordTransformationMethod.getInstance()
        }

    private fun caption(text: String): TextView =
        TextView(activity).apply {
            this.text = text
            textSize = 11f
            setTextColor(ContextCompat.getColor(activity, R.color.text_tertiary))
            setPadding(0, dp(4), 0, 0)
        }

    private fun title(text: String): TextView =
        TextView(activity).apply {
            this.text = text
            textSize = 14f
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setTextColor(ContextCompat.getColor(activity, R.color.text_primary))
        }

    private fun dp(value: Int): Int =
        (value * activity.resources.displayMetrics.density).roundToInt()

    private class Row(
        var account: ServerAccount,
        val container: LinearLayout,
        val divider: View,
        val title: TextView,
        val status: TextView,
        val act: MaterialButton,
        val signOut: MaterialButton,
        val rename: MaterialButton,
        val remove: MaterialButton,
        val step: LinearLayout,
        val problem: TextView,
    ) {
        var poll: Job? = null
    }
}
