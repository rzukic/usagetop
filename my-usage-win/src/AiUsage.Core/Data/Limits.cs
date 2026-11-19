namespace AiUsage.Data;

/// <summary>
/// A limit is one window of one account, keyed as its history is filed:
/// "antigravity|Claude &amp; GPT 7d", "claude-2|Fable 7d". Hiding one leaves it
/// out of the overview, the tray and the rail's ring; it stays on its account's
/// page. The combined figure does not look at what is hidden.
/// </summary>
public static class Limits
{
    /// <summary>"claude|7d": the account's key, a bar, the window's label as sent.</summary>
    public static string Key(string accountKey, string label) => $"{accountKey}|{label}";

    public static string Key(ProviderUsage usage, UsageWindow window) => Key(usage.AccountKey, window.Label);

    /// <summary>A key back into its account and label; null for anything without a bar.</summary>
    public static (string Account, string Label)? Split(string key)
    {
        var bar = key.IndexOf('|');
        return bar <= 0 ? null : (key[..bar], key[(bar + 1)..]);
    }

    public static bool IsHidden(IReadOnlyCollection<string> hidden, ProviderUsage usage, UsageWindow window) =>
        hidden.Count > 0 && hidden.Contains(Key(usage, window));

    /// <summary>The windows the overview shows for an account, in their order.</summary>
    public static IReadOnlyList<UsageWindow> Shown(ProviderUsage usage, IReadOnlyCollection<string> hidden) =>
        hidden.Count == 0 ? usage.Windows : usage.Windows.Where(w => !IsHidden(hidden, usage, w)).ToList();

    /// <summary>
    /// The window that decides how an account is doing: the lowest level among
    /// the shown ones that count toward the combined figure, or among all the
    /// shown ones when none does. Null when nothing shown has a level.
    /// </summary>
    public static UsageWindow? Tightest(ProviderUsage usage, IReadOnlyCollection<string>? hidden = null)
    {
        var shown = Shown(usage, hidden ?? []).Where(w => w.RemainingPercent != null).ToList();
        var counted = shown.Where(w => w.CountsTowardCombined).ToList();
        var pool = counted.Count > 0 ? counted : shown;
        return pool.OrderBy(w => w.RemainingPercent).FirstOrDefault();
    }
}
