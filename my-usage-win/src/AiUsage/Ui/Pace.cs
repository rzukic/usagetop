using AiUsage.Data;

namespace AiUsage.Ui;

/// <summary>
/// How a window is being spent against an even spread over its cycle, as the
/// server page and the Mac app read it: the tick on the bar is where an even
/// spread would be now, and five points either side of it is on pace.
/// </summary>
public static class Pace
{
    public enum Kind { Empty, Ahead, On, Under }

    /// <summary>What an even spread since the last refill would leave now, or null.</summary>
    public static double? EvenPercent(UsageWindow window, DateTimeOffset now)
    {
        if (Projection.PeriodOf(window.Label) is not { } period || window.ResetAt is not { } reset) return null;
        var left = reset - now;
        if (left <= TimeSpan.Zero || left > period) return null;
        return left / period * 100;
    }

    /// <summary>Ahead runs out before the reset; under has room to spare. A window at 0% is empty.</summary>
    public static Kind? Of(UsageWindow window, DateTimeOffset now)
    {
        if (window.RemainingPercent is { } level && level < 0.5f) return Kind.Empty;
        if (EvenPercent(window, now) is not { } even || window.RemainingPercent is not { } percent) return null;
        var gap = percent - even;
        return gap < -5 ? Kind.Ahead : gap > 5 ? Kind.Under : Kind.On;
    }

    public static string Text(Kind kind) => kind switch
    {
        Kind.Empty => "Empty",
        Kind.Ahead => "Ahead of pace",
        Kind.On => "On pace",
        _ => "Under pace",
    };

    public static string Help(Kind kind) => kind switch
    {
        Kind.Empty => "Nothing left until it refills.",
        Kind.Ahead => "Used faster than an even spread: at this rate it runs out before it resets.",
        Kind.On => "About what an even spread over the cycle would leave.",
        _ => "Used slower than an even spread: room to spare before it resets.",
    };
}

/// <summary>
/// The name a window is shown by. Providers name the same period differently
/// ("7d" from one, "Weekly" from another); on screen a period has one name.
/// The label itself stays as sent: history is filed under it.
/// </summary>
public static class WindowName
{
    private static readonly System.Text.RegularExpressions.Regex Days =
        new(@"^(?:(.*\S)\s+)?(\d+)\s*[dD]$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>"7d" → "Weekly", "Fable 7d" → "Fable weekly", "1d" → "Daily"; anything else as it is.</summary>
    public static string Display(string label)
    {
        var trimmed = label.Trim();
        var match = Days.Match(trimmed);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var days)) return trimmed;
        var word = days switch { 1 => "Daily", 7 => "Weekly", _ => null };
        if (word == null) return trimmed;
        return match.Groups[1].Success ? $"{match.Groups[1].Value} {word.ToLowerInvariant()}" : word;
    }

    private static readonly System.Text.RegularExpressions.Regex ModelPeriod =
        new(@"^(.*\S)\s+(\d+)\s*[mhdMHD]$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The shortest name that still tells a window apart within its provider,
    /// for a bar: "Fable 7d" goes by its model ("Fable"), the rest by
    /// <see cref="Display"/>. The Mac's WindowName.compact.
    /// </summary>
    public static string Compact(string label)
    {
        var trimmed = label.Trim();
        var match = ModelPeriod.Match(trimmed);
        return match.Success ? match.Groups[1].Value : Display(trimmed);
    }
}
