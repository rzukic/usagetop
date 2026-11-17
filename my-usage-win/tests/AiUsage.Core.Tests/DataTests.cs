namespace AiUsage.Tests;

/// <summary>Shared clock helpers, so every test reads in hours or minutes from one instant.</summary>
internal static class Clock
{
    public static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L);

    public static DateTimeOffset Hours(double hours) => T0.AddHours(hours);

    public static DateTimeOffset Minutes(double minutes) => T0.AddMinutes(minutes);
}

public class CombinedUsageTests
{
    private static ProviderUsage Usage(ProviderId id, params UsageWindow[] windows) => new()
    {
        Id = id,
        State = UsageState.Ok,
        Windows = windows,
    };

    private static string[] Labels(IEnumerable<UsageWindow> windows) => windows.Select(w => w.Label).ToArray();

    [Fact]
    public void A_five_hour_window_is_not_counted_even_when_it_resets_near_the_weekly_one()
    {
        // Muse: the 5h window is empty and resets 22h before the weekly one, which
        // used to put it inside the same-tier tolerance and halve the average.
        var windows = CombinedUsage.LongWindows(Usage(
            ProviderId.Muse,
            new UsageWindow("5h", 0f, Clock.Hours(1)),
            new UsageWindow("Weekly", 66f, Clock.Hours(23))));

        Assert.Equal(["Weekly"], Labels(windows));
    }

    [Fact]
    public void Two_windows_resetting_at_the_same_instant_are_both_counted()
    {
        var windows = CombinedUsage.LongWindows(Usage(
            ProviderId.Muse,
            new UsageWindow("Monthly", 100f, Clock.Hours(500)),
            new UsageWindow("Top-up", 84f, Clock.Hours(500))));

        Assert.Equal(["Monthly", "Top-up"], Labels(windows));
    }

    [Fact]
    public void Mistrals_api_credit_is_left_out_of_the_combined_figure()
    {
        var windows = CombinedUsage.LongWindows(Usage(
            ProviderId.Mistral,
            new UsageWindow("API", 100f, Clock.Hours(500), CountsTowardCombined: false),
            new UsageWindow("Vibe", 82f, Clock.Hours(500))));

        Assert.Equal(["Vibe"], Labels(windows));
        Assert.Equal(82f, CombinedUsage.Level(windows));
    }

    [Fact]
    public void A_model_scoped_slice_of_the_weekly_limit_is_not_counted_twice()
    {
        var windows = CombinedUsage.LongWindows(Usage(
            ProviderId.Claude,
            new UsageWindow("7d", 19f, Clock.Hours(72)),
            new UsageWindow("Fable 7d", 100f, Clock.Hours(72), CountsTowardCombined: false)));

        Assert.Equal(["7d"], Labels(windows));
    }

    [Fact]
    public void A_provider_that_only_exposes_a_short_window_still_counts()
    {
        var windows = CombinedUsage.LongWindows(Usage(
            ProviderId.Muse,
            new UsageWindow("5h", 40f, Clock.Hours(2))));

        Assert.Equal(["5h"], Labels(windows));
    }

    [Fact]
    public void A_subscription_counts_once_however_many_windows_it_reports()
    {
        var snapshot = new Snapshot(
            [
                Usage(ProviderId.Claude, new UsageWindow("7d", 10f, Clock.Hours(72))),
                Usage(ProviderId.ChatGpt, new UsageWindow("7d", 30f, Clock.Hours(72))),
            ],
            Clock.T0);

        Assert.Equal(20f, CombinedUsage.Of(snapshot).Percent);
        Assert.Equal(2, CombinedUsage.Of(snapshot).Providers);
    }
}

public class ProjectionTests
{
    private static Projection.Track Track(
        double percent,
        double? recent,
        double? usual = null,
        DateTimeOffset? resetAt = null,
        TimeSpan? period = null,
        double ease = 3.0) => new(recent, usual ?? recent, percent, Clock.T0, resetAt, period, ease);

    /// <summary>The sample closest to the target, since the line is drawn from fixed steps.</summary>
    private static float PercentAt(IReadOnlyList<UsagePoint> points, DateTimeOffset target) =>
        points.MinBy(p => Math.Abs((p.At - target).TotalMilliseconds)).Percent;

    private static UsagePoint Point(double hours, float percent) => new(Clock.Hours(hours), percent);

    [Fact]
    public void Each_window_falls_at_its_own_rate_not_at_the_average_of_them()
    {
        var points = Projection.Combined([Track(100.0, 0.0), Track(50.0, 10.0)], Clock.T0, Clock.Hours(4));

        Assert.Equal(75f, PercentAt(points, Clock.T0), 0.5f);
        Assert.Equal(70f, PercentAt(points, Clock.Hours(1)), 0.5f);
        Assert.Equal(65f, PercentAt(points, Clock.Hours(2)), 0.5f);
    }

    [Fact]
    public void A_window_that_runs_out_stops_pulling_the_average_down()
    {
        var points = Projection.Combined([Track(80.0, 0.0), Track(20.0, 10.0)], Clock.T0, Clock.Hours(4));

        Assert.Equal(50f, PercentAt(points, Clock.T0), 0.5f);
        Assert.Equal(45f, PercentAt(points, Clock.Hours(1)), 0.5f);
        Assert.Equal(40f, PercentAt(points, Clock.Hours(3)), 0.5f);
        Assert.Equal(40f, PercentAt(points, Clock.Hours(4)), 0.5f);
    }

    [Fact]
    public void A_reset_puts_the_window_back_to_full_in_one_step()
    {
        var track = Track(50.0, 10.0, resetAt: Clock.Hours(2));
        var points = Projection.Path(track, Clock.T0, Clock.Hours(4));

        Assert.Equal(40f, PercentAt(points, Clock.Hours(1)), 0.5f);
        Assert.Equal(30f, PercentAt(points, Clock.Hours(2).AddMilliseconds(-1)), 0.5f);
        Assert.Equal(100f, PercentAt(points, Clock.Hours(2)), 0.5f);
        Assert.Equal(90f, PercentAt(points, Clock.Hours(3)), 0.5f);
    }

    [Fact]
    public void Every_later_cycle_refills_too_when_the_period_is_known()
    {
        var track = Track(50.0, 10.0, resetAt: Clock.Hours(2), period: TimeSpan.FromHours(5));

        Assert.Equal(100.0, Projection.LevelAt(track, Clock.Hours(7)), 3);
        Assert.Equal(80.0, Projection.LevelAt(track, Clock.Hours(9)), 3);
    }

    [Fact]
    public void The_recent_rate_gives_way_to_the_usual_one()
    {
        // Ten points an hour lately, one point an hour usually.
        var track = Track(100.0, recent: 10.0, usual: 1.0, ease: 3.0);

        Assert.Equal(10.0, Projection.RateAt(track, 0), 3);
        Assert.Equal(1.0, Projection.RateAt(track, 30), 2);
        // 1 * 3 + 9 * 3 * (1 - e^-1): well short of the 30 a straight line would drain.
        Assert.Equal(100.0 - 20.067, Projection.LevelAt(track, Clock.Hours(3)), 2);
    }

    [Fact]
    public void An_idle_spell_does_not_stop_the_usual_burn()
    {
        var track = Track(60.0, recent: 0.0, usual: 2.0);

        Assert.True(track.Draining);
        // 2 * 24 drained, less the 2 * 3 the quiet start held back.
        Assert.Equal(18.0, Projection.LevelAt(track, Clock.Hours(24)), 1);
        Assert.NotNull(Projection.EmptyAt(track, Clock.Hours(100)));
    }

    [Fact]
    public void Either_rate_stands_in_for_the_other_when_missing()
    {
        Assert.Equal(90.0, Projection.LevelAt(new(null, 2.0, 100.0, Clock.T0, null, null), Clock.Hours(5)), 3);
        Assert.Equal(90.0, Projection.LevelAt(new(2.0, null, 100.0, Clock.T0, null, null), Clock.Hours(5)), 3);

        var unknown = new Projection.Track(null, null, 70.0, Clock.T0, null, null);
        Assert.False(unknown.Measured);
        Assert.Equal(70.0, Projection.LevelAt(unknown, Clock.Hours(50)), 3);
    }

    [Fact]
    public void Empty_is_when_it_runs_dry_unless_the_reset_comes_first()
    {
        var empty = Projection.EmptyAt(Track(20.0, 10.0), Clock.Hours(100));
        Assert.NotNull(empty);
        Assert.Equal(2.0, (empty!.Value - Clock.T0).TotalHours, 2);

        Assert.Null(Projection.EmptyAt(Track(20.0, 10.0, resetAt: Clock.Hours(1)), Clock.Hours(100)));
        Assert.Null(Projection.EmptyAt(Track(20.0, 0.0), Clock.Hours(100)));
    }

    [Fact]
    public void A_burn_rate_leaves_the_refills_out()
    {
        // 100 -> 90, a refill to 100, then -> 95: fifteen points spent in three hours.
        IReadOnlyList<UsagePoint> points = [Point(0, 100), Point(1, 90), Point(2, 100), Point(3, 95)];

        Assert.Equal(5.0, Projection.RateOver(points, TimeSpan.FromDays(7), Clock.Hours(3))!.Value, 3);
        Assert.Equal(5.0, Projection.RateOver(points, TimeSpan.FromHours(1), Clock.Hours(3))!.Value, 3);
        // The level holds from the last reading until now, so the hours since count too.
        Assert.Equal(2.5, Projection.RateOver(points, TimeSpan.FromHours(2), Clock.Hours(4))!.Value, 3);
    }

    [Fact]
    public void A_burn_rate_needs_half_an_hour_of_readings()
    {
        IReadOnlyList<UsagePoint> points = [Point(0, 100), Point(0.2, 90)];

        Assert.Null(Projection.RateOver(points, TimeSpan.FromHours(3), Clock.Hours(0.25)));
        Assert.Null(Projection.RateOver([], TimeSpan.FromHours(3), Clock.Hours(1)));
    }

    [Fact]
    public void The_line_starts_where_the_recorded_series_ended()
    {
        var points = Projection.Combined(
            [Track(100.0, 0.0), Track(50.0, 10.0)],
            Clock.T0,
            Clock.Hours(4),
            anchor: 40f);

        Assert.Equal(40f, points[0].Percent, 0.01f);
    }

    [Theory]
    [InlineData("5h", 5)]
    [InlineData("7d", 168)]
    [InlineData("Weekly", 168)]
    [InlineData("Monthly", 720)]
    [InlineData("Fable 7d", 168)]
    // Mistral's budgets name no duration, but run on its monthly billing cycle.
    [InlineData("Vibe", 720)]
    [InlineData("API", 720)]
    public void A_label_gives_up_its_period(string label, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), Projection.PeriodOf(label));
    }

    [Theory]
    [InlineData("balance")]
    // A version number is not a duration just because it ends in a digit and a letter.
    [InlineData("GPT-5h")]
    public void A_label_with_no_duration_has_no_period(string label)
    {
        Assert.Null(Projection.PeriodOf(label));
    }
}

public class TrendlineTests
{
    private static UsagePoint Point(double minutes, float percent) => new(Clock.Minutes(minutes), percent);

    [Fact]
    public void A_straight_drawdown_needs_no_refills_and_the_line_follows_it()
    {
        var fit = Trendline.Of([Point(0, 90f), Point(60, 80f), Point(120, 70f)])!;

        Assert.Empty(fit.Refills);
        Assert.Equal(90f, fit.Vertices[0].Percent, 0.5f);
        Assert.Equal(70f, fit.Vertices[^1].Percent, 0.5f);
    }

    [Fact]
    public void A_jump_up_is_read_as_a_refill_and_the_trend_steps_with_it()
    {
        var fit = Trendline.Of(
        [
            Point(0, 80f), Point(60, 60f), Point(120, 40f), Point(180, 20f),
            Point(240, 100f), Point(300, 80f),
        ])!;

        var refill = Assert.Single(fit.Refills);
        Assert.Equal(Clock.Minutes(240), refill.At);
        Assert.Equal(80f, refill.Amount, 0.01f);

        // Two vertices at the refill: the line drops to the pre-refill level and
        // jumps back up, so the trend stays parallel to the sawtooth.
        var atRefill = fit.Vertices.Where(v => v.At == Clock.Minutes(240)).ToList();
        Assert.Equal(2, atRefill.Count);
        Assert.True(atRefill[1].Percent > atRefill[0].Percent);
    }

    [Fact]
    public void The_trend_ignores_the_refill_when_measuring_the_burn_rate()
    {
        var fit = Trendline.Of(
        [
            Point(0, 80f), Point(60, 60f), Point(120, 40f), Point(180, 20f),
            Point(240, 100f), Point(300, 80f), Point(360, 60f),
        ])!;

        Assert.True(fit.Vertices[0].Percent > fit.Vertices[^1].Percent);
    }

    [Fact]
    public void The_fitted_rate_is_the_burn_per_hour_and_it_falls()
    {
        var fit = Trendline.Of([Point(0, 90f), Point(60, 70f), Point(120, 50f)])!;

        Assert.Equal(-20.0, fit.Rate, 0.01);
    }

    [Fact]
    public void One_reading_is_not_enough_to_fit_a_line()
    {
        Assert.Null(Trendline.Of([Point(0, 50f)]));
        Assert.Null(Trendline.Of([]));
    }
}

public class UsageHistoryTests
{
    private static DateTimeOffset[] Times(IEnumerable<UsagePoint> points) => points.Select(p => p.At).ToArray();

    [Fact]
    public void Quick_flat_refreshes_do_not_keep_the_combined_series_at_one_point()
    {
        var series = UsageHistory.AppendCombined([], Clock.Minutes(0), 50f);
        Assert.Single(series);

        // Refreshes two and four minutes later add nothing and must not replace
        // the first point either.
        series = UsageHistory.AppendCombined(series, Clock.Minutes(2), 50f);
        series = UsageHistory.AppendCombined(series, Clock.Minutes(4), 50f);
        Assert.Equal([Clock.Minutes(0)], Times(series));

        series = UsageHistory.AppendCombined(series, Clock.Minutes(6), 50f);
        Assert.Equal([Clock.Minutes(0), Clock.Minutes(6)], Times(series));
    }

    [Fact]
    public void A_moved_combined_value_replaces_the_reading_a_minute_earlier()
    {
        var series = UsageHistory.AppendCombined([], Clock.Minutes(0), 50f);
        series = UsageHistory.AppendCombined(series, Clock.Minutes(1), 49f);

        Assert.Equal(49f, series[^1].Percent);
        Assert.Equal(Clock.Minutes(1), series[^1].At);
    }

    [Fact]
    public void A_window_keeps_five_weeks_and_thins_out_past_the_first()
    {
        // Six weeks of quarter-hourly readings, the cadence the app refreshes at.
        var now = Clock.T0.AddDays(42);
        var points = Enumerable.Range(0, 42 * 96 + 1)
            .Select(i => new UsagePoint(Clock.T0.AddMinutes(15 * i), 50f))
            .ToList();

        var kept = UsageHistory.Compact(points, now, UsageHistory.Retain);

        Assert.True(now - kept[0].At <= UsageHistory.Retain);
        Assert.Equal(now, kept[^1].At);

        // The newest week is untouched; the weeks before it are hourly.
        var recent = kept.Where(p => now - p.At < TimeSpan.FromDays(7)).ToList();
        var older = kept.Where(p => now - p.At >= TimeSpan.FromDays(8)).ToList();
        Assert.Equal(7 * 96, recent.Count);
        Assert.All(older.Zip(older.Skip(1)), pair =>
            Assert.True(pair.Second.At - pair.First.At >= TimeSpan.FromHours(1)));
        Assert.True(kept.Count < UsageHistory.MaxPoints);
    }

    [Theory]
    [InlineData("5h", 7)]
    [InlineData("7d", 7)]
    [InlineData("Fable 7d", 7)]
    [InlineData("Monthly", 35)]
    [InlineData("Vibe", 35)]
    public void A_windows_chart_looks_back_as_far_as_its_cycle_needs(string label, int days)
    {
        Assert.Equal(TimeSpan.FromDays(days), UsageHistory.ChartSpan(new UsageWindow(label, 50f)));
    }
}

public class SnapshotCodecTests
{
    [Fact]
    public void A_sub_limit_survives_the_cache()
    {
        var reset = DateTimeOffset.Parse("2026-11-11T19:59:59Z");
        var snapshot = new Snapshot(
            [
                new ProviderUsage
                {
                    Id = ProviderId.Claude,
                    Windows =
                    [
                        new UsageWindow("7d", 19f, reset),
                        new UsageWindow("Fable 7d", 100f, reset, CountsTowardCombined: false),
                    ],
                },
            ],
            DateTimeOffset.Parse("2026-11-08T02:04:49Z"));

        var decoded = SnapshotCodec.Decode(SnapshotCodec.Encode(snapshot));

        Assert.NotNull(decoded);
        Assert.Equal(snapshot.Usages[0].Windows, decoded.Usages[0].Windows);
        Assert.Equal(snapshot.FetchedAt, decoded.FetchedAt);
    }

    [Fact]
    public void A_damaged_cache_reads_as_no_cache()
    {
        Assert.Null(SnapshotCodec.Decode("{\"FetchedAt\":"));
        Assert.Null(SnapshotCodec.Decode(""));
        Assert.Null(SnapshotCodec.Decode(null));
    }
}
