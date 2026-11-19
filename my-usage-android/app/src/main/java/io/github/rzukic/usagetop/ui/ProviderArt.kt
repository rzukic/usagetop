package io.github.rzukic.usagetop.ui

import androidx.annotation.DrawableRes
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.ProviderId

/** The mark drawn inside a provider's level ring. */
object ProviderArt {

    @DrawableRes
    fun logo(id: ProviderId): Int = when (id) {
        ProviderId.CLAUDE -> R.drawable.ic_logo_claude
        ProviderId.CHATGPT -> R.drawable.ic_logo_chatgpt
        ProviderId.MISTRAL -> R.drawable.ic_logo_mistral
        ProviderId.KIMI -> R.drawable.ic_logo_kimi
        ProviderId.MUSE -> R.drawable.ic_logo_muse
        ProviderId.ANTIGRAVITY -> R.drawable.ic_logo_antigravity
    }
}
