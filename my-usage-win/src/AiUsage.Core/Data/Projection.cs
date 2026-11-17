using System.Text.RegularExpressions;

namespace AiUsage.Data;

/// <summary>
/// Where a window is heading, and the combined level carried forward one window
/// at a time. The server page works the same way (app.js), so the apps and the
/// page draw the same forecast.
///
/// A window gets two burn rates from its own readings: the recent one, over the
/// last few hours, and the usual one, over the last week. Neither is the
/// forecast on its own. Use comes in bursts, so the recent rate alone says
/// "empty in two hours" after an hour of heavy work and "never" over lunch; the
/// usual rate already counts the nights and the idle stretches, but it is slow
/// to see a change of pace. So the line leaves now at the recent rate and eases
/// into the usual one:
///
///   rate(t) = usual + (recent - usual) * e^(-t / ease)
///
/// with the ease as long as the recent rate was measured over: a rate taken over
/// three hours says something about the next few hours, and little about the
/// week after. A five-hour window is therefore forecast mostly on its recent
/// rate, a weekly one mostly on its usual one, without a rule for either.
///
/// For the combined figure, every window drains at its own pace and the average
/// is taken afterwards: a single rate fitted to the average would claim Mistral
/// is spent as fast as Claude. A window that reaches zero stays at zero, and it
/// stays in the average -- "62% left" must never rise because something emptied.
/// </summary>
public static class Projection
{
    /// <summary>How far back the recent rate looks, and so how long it holds sway.</summary>
    public static readonly TimeSpan RecentBasis = TimeSpan.FromHours(3);

    /// <summary>How far back the usual rate looks: long enough to take in the nights.</summary>
    public static readonly TimeSpan UsualBasis = TimeSpan.FromDays(7);

    /// <summary>How far ahead a window is looked at for when it runs dry.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(35);

    /// <summary>Below this, in points an hour, a window counts as not draining.</summary>
    public const double Idle = 0.05;

    /// <summary>A jump this large is a reset, not a reading drifting.</summary>
    private const float RefillJump = 1.5f;

    /// <summary>How many samples a forward line is drawn from.</summary>
    private const int Steps = 160;

    private const double MillisPerHour = 3_600_000.0;

    /// <summary>One window, and how fast it is being spent.</summary>
    public sealed record Track(
        /// <summary>Points an hour over <see cref="RecentBasis"/>; null with too little to go on.</summary>
        double? Recent,
        /// <summary>Points an hour over <see cref="UsualBasis"/>; null with too little to go on.</summary>
        double? Usual,
        /// <summary>The level now, and when now is.</summary>
        double Percent,
        DateTimeOffset At,
        /// <summary>The next refill, and how often it repeats when the label says so.</summary>
        DateTimeOffset? ResetAt,
        TimeSpan? Period,
        /// <summary>How quickly the recent rate gives way to the usual one, in hours.</summary>
        double EaseHours = 3.0)
    {
        /// <summary>True when either rate says something, so there is a forecast to draw.</summary>
        public bool Measured => Recent != null || Usual != null;

        /// <summary>True when the forecast moves at all.</summary>
        public bool Draining => (Recent ?? 0) > Idle || (Usual ?? 0) > Idle;

        /// <summary>The next refill after now, if one is known.</summary>
        public DateTimeOffset? NextReset => ResetAt is { } at && at > At ? at : null;
    }

    /// <summary>
    /// A window's track. A window with no readings to go on still gets one --
    /// it holds its level -- so the combined average keeps counting it.
    /// </summary>
    public static Track? For(
        UsageWindow window,
        IReadOnlyList<UsagePoint> history,
        DateTimeOffset now,
        TimeSpan? recent = null)
    {
        if (window.RemainingPercent is not { } percent) return null;
        var basis = recent ?? RecentBasis;
        return new Track(
            Recent: RateOver(history, basis, now),
            Usual: RateOver(history, UsualBasis, now),
            Percent: percent,
            At: now,
            ResetAt: window.ResetAt,
            Period: PeriodOf(window.Label),
            EaseHours: basis.TotalHours);
    }

    /// <summary>
    /// Points of remaining-percent drained an hour over the last
    /// <paramref name="basis"/>, refills left out. A level holds until the next
    /// reading. Null with under half an hour of readings (or half the basis, if
    /// that is shorter) to go on.
    /// </summary>
    public static double? RateOver(IReadOnlyList<UsagePoint> points, TimeSpan basis, DateTimeOffset now)
    {
        if (points.Count == 0) return null;
        var cutoff = now - basis;
        var start = points[0].At > cutoff ? points[0].At : cutoff;
        var hours = Hours(start, now);
        if (hours < Math.Min(0.5, basis.TotalHours / 2)) return null;

        var drained = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            var at = points[i].At;
            if (at <= cutoff || at > now) continue;
            var step = points[i - 1].Percent - points[i].Percent;
            if (-step >= RefillJump) continue;
            drained += step;
        }

        return Math.Max(drained / hours, 0.0);
    }

    /// <summary>Points drained from the track's now until <paramref name="hours"/> later.</summary>
    public static double Drained(Track track, double hours)
    {
        if (hours <= 0) return 0;
        var recent = track.Recent ?? track.Usual ?? 0;
        var usual = track.Usual ?? track.Recent ?? 0;
        var ease = Math.Max(track.EaseHours, 0.01);
        return usual * hours + (recent - usual) * ease * (1 - Math.Exp(-hours / ease));
    }

    /// <summary>The points an hour the forecast falls at, <paramref name="hours"/> from now.</summary>
    public static double RateAt(Track track, double hours)
    {
        var recent = track.Recent ?? track.Usual ?? 0;
        var usual = track.Usual ?? track.Recent ?? 0;
        return usual + (recent - usual) * Math.Exp(-Math.Max(hours, 0) / Math.Max(track.EaseHours, 0.01));
    }

    /// <summary>Where a window stands at <paramref name="at"/>, refilling at every reset on the way.</summary>
    public static double LevelAt(Track track, DateTimeOffset at)
    {
        if (at <= track.At) return track.Percent;
        var drained = Drained(track, Hours(track.At, at));

        if (CycleStart(track, at) is { } start)
        {
            return Math.Max(0.0, 100.0 - (drained - Drained(track, Hours(track.At, start))));
        }
        return Math.Max(0.0, track.Percent - drained);
    }

    /// <summary>
    /// When the window runs dry before its next refill, looking no further than
    /// <paramref name="until"/>. Null when it lasts.
    /// </summary>
    public static DateTimeOffset? EmptyAt(Track track, DateTimeOffset until)
    {
        if (track.Percent <= 0) return track.At;
        if (!track.Draining) return null;

        var end = track.NextReset is { } reset && reset < until ? reset : until;
        if (end <= track.At || track.Percent - Drained(track, Hours(track.At, end)) > 0) return null;

        // The level only falls between refills, so halving the gap finds it.
        double low = 0, high = Hours(track.At, end);
        for (var i = 0; i < 48; i++)
        {
            var middle = (low + high) / 2;
            if (track.Percent - Drained(track, middle) > 0) low = middle; else high = middle;
        }
        return track.At.AddHours(high);
    }

    /// <summary>
    /// One window's forecast from <paramref name="from"/> to <paramref name="to"/>,
    /// with both sides of every refill so the line steps straight up.
    /// </summary>
    public static IReadOnlyList<UsagePoint> Path(Track track, DateTimeOffset from, DateTimeOffset to) =>
        Sample([track], from, to, at => LevelAt(track, at));

    /// <summary>
    /// The combined level from <paramref name="from"/> to <paramref name="to"/>.
    /// <paramref name="anchor"/> is the last recorded reading: a forecast is a
    /// shape, so it starts where the measured line ended instead of at the model's
    /// own idea of the current level.
    /// </summary>
    public static IReadOnlyList<UsagePoint> Combined(
        IReadOnlyList<Track> tracks,
        DateTimeOffset from,
        DateTimeOffset to,
        float? anchor = null)
    {
        if (tracks.Count == 0 || to <= from) return [];

        var raw = Sample(tracks, from, to, at => tracks.Average(t => LevelAt(t, at)));
        var offset = anchor.HasValue ? anchor.Value - raw[0].Percent : 0.0;
        return raw
            .Select(r => new UsagePoint(r.At, (float)Math.Clamp(r.Percent + offset, 0.0, 100.0)))
            .ToList();
    }

    private static List<UsagePoint> Sample(
        IReadOnlyList<Track> tracks,
        DateTimeOffset from,
        DateTimeOffset to,
        Func<DateTimeOffset, double> level)
    {
        if (to <= from) return [];
        var span = (to - from).TotalMilliseconds;

        var times = new SortedSet<DateTimeOffset>();
        for (var i = 0; i <= Steps; i++) times.Add(from.AddMilliseconds(span * i / Steps));

        // A refill is a step, not a slope: the moment before it and the moment of it.
        foreach (var track in tracks)
        {
            foreach (var reset in Resets(track, from, to))
            {
                var just = reset.AddMilliseconds(-1);
                if (just > from) times.Add(just);
                times.Add(reset);
            }
        }

        return times.Select(at => new UsagePoint(at, (float)level(at))).ToList();
    }

    /// <summary>Every refill of the window after <paramref name="from"/>, up to <paramref name="to"/>.</summary>
    public static IEnumerable<DateTimeOffset> Resets(Track track, DateTimeOffset from, DateTimeOffset to)
    {
        if (track.NextReset is not { } at) yield break;
        for (var n = 0; at <= to && n < 1000; n++)
        {
            if (at > from) yield return at;
            if (track.Period is not { } period || period <= TimeSpan.Zero) yield break;
            at += period;
        }
    }

    /// <summary>The last refill at or before <paramref name="at"/>, or null if none has come yet.</summary>
    private static DateTimeOffset? CycleStart(Track track, DateTimeOffset at)
    {
        if (track.NextReset is not { } first || first > at) return null;
        if (track.Period is not { } period || period <= TimeSpan.Zero) return first;
        var cycles = Math.Floor((at - first).Ticks / (double)period.Ticks);
        return first + TimeSpan.FromTicks((long)(cycles * period.Ticks));
    }

    private static double Hours(DateTimeOffset from, DateTimeOffset to) =>
        (to - from).TotalMilliseconds / MillisPerHour;

    /// <summary>
    /// How often the window refills, when the label says so. The duration may
    /// follow a name -- "Fable 7d" refills weekly just as "7d" does.
    /// </summary>
    public static TimeSpan? PeriodOf(string label)
    {
        var text = label.Trim().ToLowerInvariant();
        switch (text)
        {
            case "weekly": return TimeSpan.FromDays(7);
            // Mistral's two budgets, "Vibe" and "API", run on its monthly billing cycle.
            case "monthly" or "vibe" or "api": return TimeSpan.FromDays(30);
        }

        var match = DurationLabel.Match(text);
        if (!match.Success) return null;
        if (!long.TryParse(match.Groups[1].Value, out var amount)) return null;

        return match.Groups[2].Value switch
        {
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            "d" => TimeSpan.FromDays(amount),
            _ => null,
        };
    }

    private static readonly Regex DurationLabel =
        new(@"(?:^|\s)(\d+)\s*([mhd])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
