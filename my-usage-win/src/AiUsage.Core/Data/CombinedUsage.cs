using System.Text.RegularExpressions;

namespace AiUsage.Data;

/// <summary>
/// Every subscription's long windows rolled into one figure.
///
/// The APIs only ever expose percentages, never token counts, so "total tokens
/// available" can only be the average of what is left. Each subscription counts
/// once, however many windows it reports, which is what the caption promises. The
/// short windows are deliberately left out: a 5-hour limit resets several times a
/// day and would drag the line around without saying anything about the month.
/// </summary>
public static class CombinedUsage
{
    /// <summary>The combined figure plus what went into it.</summary>
    public sealed record Combined(float? Percent, int Providers, int Windows);

    /// <summary>A window that goes into the average, with the provider and account it belongs to.</summary>
    public sealed record Contribution(ProviderId Provider, UsageWindow Window, string? Account = null)
    {
        /// <summary>What its history is filed under: "claude", or "claude-2".</summary>
        public string AccountKey => Account ?? ProviderCatalog.Of(Provider).Key;
    }

    /// <summary>
    /// The windows that count as a provider's long limit: the tier that resets
    /// furthest away. Claude and ChatGPT contribute their 7-day window, Kimi its
    /// monthly one, Mistral its Vibe allowance, Muse its weekly one, Antigravity its
    /// Gemini group's weekly one.
    ///
    /// Windows that do not count toward the combined figure are left out: Claude's
    /// Fable window is a slice of its 7-day one, and Mistral's API credit cannot be
    /// spent on coding workflows. Antigravity's Claude-and-GPT and Argon limits are
    /// budgets beside its Gemini one, not the plan's main allowance.
    /// </summary>
    public static IReadOnlyList<UsageWindow> LongWindows(ProviderUsage usage)
    {
        // A five-hour window resets several times a day, so it says nothing about
        // the month. It has to go before the tier is picked: a short window that
        // happens to reset within a day of the long one would otherwise be read as
        // the same tier and averaged in.
        var longWindows = usage.Windows
            .Where(w => w.RemainingPercent != null && w.CountsTowardCombined && !IsShortWindow(w))
            .ToList();

        // A provider that only exposes a short window still counts -- dropping it
        // would silently remove a subscription from the average.
        var rated = longWindows.Count > 0
            ? longWindows
            : usage.Windows.Where(w => w.RemainingPercent != null && w.CountsTowardCombined).ToList();

        if (rated.Count == 0) return [];

        // Without any reset time there is no way to tell the tiers apart, so every
        // window stands in rather than none.
        var resets = rated.Where(w => w.ResetAt != null).Select(w => w.ResetAt!.Value).ToList();
        if (resets.Count == 0) return rated;
        var furthest = resets.Max();

        return rated
            .Where(w => w.ResetAt == null || furthest - w.ResetAt.Value <= SameTier)
            .ToList();
    }

    /// <summary>"5h" is a short window; "7d", "Weekly" and "Monthly" are not.</summary>
    private static bool IsShortWindow(UsageWindow window)
    {
        var match = DurationLabel.Match(window.Label.Trim());
        if (!match.Success) return false;
        if (!double.TryParse(match.Groups[1].Value, out var amount)) return false;

        var hours = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "m" => amount / 60.0,
            "h" => amount,
            "d" => amount * 24.0,
            _ => double.NaN,
        };
        return !double.IsNaN(hours) && hours < 24.0;
    }

    /// <summary>
    /// Every window that goes into the average, flattened. The chart needs them
    /// individually to know how far each reset lifts the combined level, and whose
    /// mark to put above it.
    /// </summary>
    public static IReadOnlyList<Contribution> Contributions(Snapshot snapshot) => snapshot.Usages
        .Where(u => u.State == UsageState.Ok)
        .SelectMany(u => LongWindows(u).Select(w => new Contribution(u.Id, w, u.AccountKey)))
        .ToList();

    /// <summary>
    /// One provider's long windows as a single level. The row on the All usage
    /// screen and the combined figure both go through here, so the two cannot
    /// drift apart.
    /// </summary>
    public static float? Level(IReadOnlyList<UsageWindow> windows)
    {
        var rated = windows.Where(w => w.RemainingPercent != null)
            .Select(w => w.RemainingPercent!.Value)
            .ToList();
        return rated.Count == 0 ? null : (float)rated.Average();
    }

    /// <summary>
    /// The average of each subscription's own level. A subscription counts once
    /// however many budgets it reports, which is what the caption promises: a
    /// second budget is not a second subscription.
    /// </summary>
    public static Combined Of(Snapshot snapshot)
    {
        var contributing = snapshot.Usages
            .Where(u => u.State == UsageState.Ok)
            .Select(LongWindows)
            .Where(w => w.Count > 0)
            .ToList();

        var levels = contributing
            .Select(Level)
            .Where(l => l != null)
            .Select(l => l!.Value)
            .ToList();

        return new Combined(
            Percent: levels.Count == 0 ? null : (float)levels.Average(),
            Providers: levels.Count,
            Windows: contributing.Sum(w => w.Count(x => x.RemainingPercent != null)));
    }

    /// <summary>Windows resetting within a day of each other belong to the same tier.</summary>
    private static readonly TimeSpan SameTier = TimeSpan.FromHours(24);

    /// <summary>A window label that is a plain duration, e.g. "5h" or "7d".</summary>
    private static readonly Regex DurationLabel =
        new(@"^(\d+)\s*([mhd])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
