namespace AiUsage.Data;

/// <summary>
/// The straight line through a drawdown once its resets are taken out.
///
/// A window that resets every week saws up and down, and a line fitted to that
/// sawtooth says nothing useful. Adding each refill back in turns the series into
/// cumulative consumption, which only ever rises, so a least-squares line through
/// it is the actual burn rate. Subtracting the refills again puts the line back in
/// percent space, where it steps up wherever the allowance was topped up.
/// </summary>
public static class Trendline
{
    /// <summary>A window resetting: the level jumped back up by <see cref="Amount"/> points.</summary>
    public sealed record Refill(DateTimeOffset At, float Amount);

    /// <summary>The fitted line in percent space, plus where the refills happened.</summary>
    public sealed record Fit(
        IReadOnlyList<UsagePoint> Vertices,
        IReadOnlyList<Refill> Refills,
        /// <summary>Percent per hour the line falls at between refills; negative.</summary>
        double Rate);

    /// <summary>A jump this large is a reset, not a reading drifting.</summary>
    private const float RefillJump = 1.5f;

    private const double MillisPerHour = 3_600_000.0;

    public static Fit? Of(IReadOnlyList<UsagePoint> points)
    {
        if (points.Count < 2) return null;
        var sorted = points.OrderBy(p => p.At).ToList();
        var origin = sorted[0].At;

        var refills = new List<Refill>();
        for (var i = 1; i < sorted.Count; i++)
        {
            var jump = sorted[i].Percent - sorted[i - 1].Percent;
            if (jump >= RefillJump) refills.Add(new Refill(sorted[i].At, jump));
        }

        // How much had been topped up by `at`, optionally counting a refill there.
        double RefilledAt(DateTimeOffset at, bool inclusive) => refills
            .Where(r => r.At < at || (inclusive && r.At == at))
            .Sum(r => (double)r.Amount);

        var hours = sorted.Select(p => (p.At - origin).TotalMilliseconds / MillisPerHour).ToList();
        var consumed = sorted.Select(p => RefilledAt(p.At, true) + (100.0 - p.Percent)).ToList();

        var meanX = hours.Average();
        var meanY = consumed.Average();
        var covariance = 0.0;
        var variance = 0.0;
        for (var i = 0; i < hours.Count; i++)
        {
            covariance += (hours[i] - meanX) * (consumed[i] - meanY);
            variance += (hours[i] - meanX) * (hours[i] - meanX);
        }
        if (variance <= 0.0) return null;

        var slope = covariance / variance;
        var intercept = meanY - slope * meanX;

        double ConsumptionAt(DateTimeOffset at) =>
            intercept + slope * ((at - origin).TotalMilliseconds / MillisPerHour);

        // Both sides of every refill, so the line steps up where the level did.
        var events = sorted.Select(p => p.At)
            .Concat(refills.Select(r => r.At))
            .Distinct()
            .OrderBy(a => a)
            .ToList();

        var vertices = new List<UsagePoint>(events.Count + refills.Count);
        foreach (var at in events)
        {
            var before = 100.0 - (ConsumptionAt(at) - RefilledAt(at, false));
            var after = 100.0 - (ConsumptionAt(at) - RefilledAt(at, true));
            if (Math.Abs(before - after) > double.Epsilon)
            {
                vertices.Add(new UsagePoint(at, (float)Math.Clamp(before, 0.0, 100.0)));
            }
            vertices.Add(new UsagePoint(at, (float)Math.Clamp(after, 0.0, 100.0)));
        }

        return new Fit(vertices, refills, -slope);
    }
}
