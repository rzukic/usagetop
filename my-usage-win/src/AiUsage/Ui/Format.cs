using System.Globalization;

namespace AiUsage.Ui;

/// <summary>The relative-time strings every screen needs.</summary>
public static class Format
{
    /// <summary>
    /// The app speaks English, so its dates and numbers do too: "Wed 21:00" and
    /// "2.1%/h" rather than the system's "Mi 21:00" and "2,1%/h" inside an English
    /// sentence. British English keeps the 24-hour clock and day-before-month.
    /// </summary>
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>A calendar day, e.g. "12 Nov".</summary>
    public static string Day(DateTimeOffset at) =>
        at.ToLocalTime().ToString("d MMM", Culture);

    /// <summary>A moment ahead: "Thu 21:54" within the week, "12 Nov 21:54" past it.</summary>
    public static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var pattern = at - DateTimeOffset.UtcNow < TimeSpan.FromDays(6) ? "ddd HH:mm" : "d MMM HH:mm";
        return local.ToString(pattern, Culture);
    }

    /// <summary>A clock time, e.g. "21:54".</summary>
    public static string Clock(DateTimeOffset at) =>
        at.ToLocalTime().ToString("HH:mm", Culture);

    /// <summary>A burn rate, e.g. "2.1%/h", or "14%/h" once it is large.</summary>
    public static string Rate(double perHour) =>
        perHour < 10
            ? perHour.ToString("0.0", Culture) + "%/h"
            : perHour.ToString("0", Culture) + "%/h";

    /// <summary>
    /// "Burning 2.1%/h · usually 0.6%/h · empty in 3h 20m, Thu 21:54" -- the
    /// rates the forecast is drawn from and where it ends up. The second part is
    /// the warning, so it comes back separately for a caller that colours it.
    /// The server page words it the same way.
    /// </summary>
    public static (string Rates, string Outcome, bool Empties) Burn(
        AiUsage.Data.Projection.Track track,
        DateTimeOffset? emptyAt)
    {
        var hours = Number(Math.Round(track.EaseHours, 1));
        if (!track.Measured) return ("", "", false);
        if (!track.Draining) return ("Not draining lately", "", false);

        var parts = new List<string>();
        if (track.Recent is { } recent)
        {
            parts.Add(recent > AiUsage.Data.Projection.Idle ? $"Burning {Rate(recent)}" : $"Idle for the last {hours}h");
        }
        if (track.Usual is { } usual) parts.Add(parts.Count == 0 ? $"Usually {Rate(usual)}" : $"usually {Rate(usual)}");

        var outcome = emptyAt is { } empty
            ? $"empty in {Span(empty - DateTimeOffset.UtcNow)}, {When(empty)}"
            : track.NextReset != null ? "lasts past the reset" : "no reset reported";
        return (string.Join(" · ", parts) + " · ", outcome, emptyAt != null);
    }

    /// <summary>How long until a window resets, e.g. "3d 23h".</summary>
    public static string? Reset(DateTimeOffset? resetAt)
    {
        if (resetAt == null) return null;
        var remaining = resetAt.Value - DateTimeOffset.UtcNow;
        return remaining <= TimeSpan.Zero ? "now" : Span(remaining);
    }

    /// <summary>A length of time in the same shape, e.g. "3d 23h" or "12m".</summary>
    public static string Span(TimeSpan duration)
    {
        var hours = (long)duration.TotalHours;
        var minutes = duration.Minutes;

        if (hours >= 24)
        {
            var days = hours / 24;
            var rest = hours % 24;
            return rest == 0 ? $"{days}d" : $"{days}d {rest}h";
        }
        return hours >= 1 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    /// <summary>How long ago a reading was taken, e.g. "12m ago".</summary>
    public static string Ago(DateTimeOffset at)
    {
        var minutes = (long)(DateTimeOffset.UtcNow - at).TotalMinutes;
        return minutes switch
        {
            < 1 => "just now",
            < 60 => $"{minutes}m ago",
            _ => $"{minutes / 60}h ago",
        };
    }

    /// <summary>
    /// The line under every list of cards. With a server that could not be
    /// reached, the reading on screen is an old one and has to say so.
    /// </summary>
    public static string Status(AiUsage.Data.Snapshot? snapshot, string? problem, bool viaServer)
    {
        if (problem != null)
        {
            return snapshot == null || snapshot.Usages.Count == 0
                ? problem
                : $"{problem} · reading from {Ago(snapshot.FetchedAt)}";
        }

        if (snapshot == null) return "Never updated";
        return viaServer ? $"Updated {Ago(snapshot.FetchedAt)} · from the server" : $"Updated {Ago(snapshot.FetchedAt)}";
    }

    /// <summary>A percentage with no decimals, e.g. "84%".</summary>
    public static string Percent(float value) =>
        string.Format(Culture, "{0:0}%", value);

    /// <summary>A plain number, dropping a trailing ".0".</summary>
    public static string Number(double value) =>
        value % 1.0 == 0.0
            ? ((long)value).ToString(Culture)
            : value.ToString(Culture);
}
