using AiUsage.Data;

namespace AiUsage.Server.Tests;

public class PlansTests
{
    private static readonly string[] Claude = ["claude"];

    /// <summary>A 7-day window whose cycle ends at <paramref name="reset"/>.</summary>
    private static UsageWindow Week(float remaining, DateTimeOffset reset) =>
        new("7d", remaining, reset);

    private static Snapshot Snapshot(DateTimeOffset at, params ProviderUsage[] usages) =>
        Make.Snapshot(at, usages);

    [Fact]
    public void A_finished_cycle_used_whatever_it_dropped_to()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = Clock.Minutes(7 * 24 * 60);

        // Down to 22% and back up to 100 at the reset.
        store.Record(Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(100f, reset))), Claude);
        store.Record(Snapshot(Clock.Minutes(60), Make.Ok(ProviderId.Claude, Week(64f, reset))), Claude);
        store.Record(Snapshot(Clock.Minutes(120), Make.Ok(ProviderId.Claude, Week(22f, reset))), Claude);

        var account = Assert.Single(Plans.Of(store, Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(22f, reset))), reset.AddMinutes(1)));
        var period = Assert.Single(account.Periods);

        Assert.Equal(78f, period.Used, 0.01f);
        Assert.Equal(22f, period.Lowest, 0.01f);
        Assert.Equal(reset, period.End);
        Assert.Equal(reset.AddDays(-7), period.Start);
    }

    [Fact]
    public void A_cycle_that_has_not_reset_yet_is_left_out()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = Clock.Minutes(7 * 24 * 60);

        store.Record(Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(100f, reset))), Claude);
        store.Record(Snapshot(Clock.Minutes(60), Make.Ok(ProviderId.Claude, Week(40f, reset))), Claude);

        var account = Assert.Single(Plans.Of(store, Snapshot(Clock.Minutes(60), Make.Ok(ProviderId.Claude, Week(40f, reset))), Clock.Minutes(60)));
        Assert.Empty(account.Periods);
    }

    [Fact]
    public void A_five_hour_window_is_a_rate_limit_not_a_plan()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = Clock.Minutes(300);

        store.Record(Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, new UsageWindow("5h", 100f, reset))), Claude);
        store.Record(Snapshot(Clock.Minutes(60), Make.Ok(ProviderId.Claude, new UsageWindow("5h", 20f, reset))), Claude);

        var accounts = Plans.Of(store, Snapshot(Clock.Minutes(60), Make.Ok(ProviderId.Claude, new UsageWindow("5h", 20f, reset))), reset.AddMinutes(1));
        Assert.Empty(accounts);
    }

    [Fact]
    public void A_reset_that_only_jitters_stays_one_cycle()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = Clock.Minutes(7 * 24 * 60);

        // ChatGPT gives the reset as seconds from now, so it lands a little
        // differently on every fetch.
        store.Record(Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(100f, reset))), Claude);
        store.Record(Snapshot(Clock.Minutes(60), Make.Ok(ProviderId.Claude, Week(70f, reset.AddSeconds(40)))), Claude);
        store.Record(Snapshot(Clock.Minutes(120), Make.Ok(ProviderId.Claude, Week(30f, reset.AddSeconds(90)))), Claude);

        var account = Assert.Single(Plans.Of(store, Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(30f, reset))), reset.AddMinutes(5)));
        Assert.Single(account.Periods);
    }

    [Fact]
    public void Two_cycles_are_two_periods()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var first = Clock.Minutes(7 * 24 * 60);
        var second = first.AddDays(7);

        store.Record(Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(100f, first))), Claude);
        store.Record(Snapshot(Clock.Minutes(60), Make.Ok(ProviderId.Claude, Week(50f, first))), Claude);
        store.Record(Snapshot(Clock.Minutes(120), Make.Ok(ProviderId.Claude, Week(100f, second))), Claude);
        store.Record(Snapshot(Clock.Minutes(180), Make.Ok(ProviderId.Claude, Week(90f, second))), Claude);

        var account = Assert.Single(Plans.Of(store, Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(90f, second))), second.AddMinutes(1)));

        Assert.Equal(2, account.Periods.Count);

        // Newest first: the cycle that just ended, then the one before it.
        Assert.Equal([10f, 50f], account.Periods.Select(p => p.Used).ToArray());
    }

    [Fact]
    public void A_monthly_window_starts_a_calendar_month_back()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        store.Record(Snapshot(reset.AddDays(-20), Make.Ok(ProviderId.Kimi, new UsageWindow("Monthly", 100f, reset))), ["kimi"]);
        store.Record(Snapshot(reset.AddDays(-1), Make.Ok(ProviderId.Kimi, new UsageWindow("Monthly", 35f, reset))), ["kimi"]);

        var account = Assert.Single(Plans.Of(store, Snapshot(reset.AddDays(-1), Make.Ok(ProviderId.Kimi, new UsageWindow("Monthly", 35f, reset))), reset.AddMinutes(1)));
        var period = Assert.Single(account.Periods);

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), period.Start);
        Assert.Equal(65f, period.Used, 0.01f);
    }

    [Fact]
    public void A_cycle_nobody_watched_to_the_end_says_so()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = Clock.Minutes(7 * 24 * 60);

        // The last reading is a day before the reset: the server was down.
        store.Record(Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(100f, reset))), Claude);
        store.Record(Snapshot(reset.AddDays(-1), Make.Ok(ProviderId.Claude, Week(60f, reset))), Claude);

        var account = Assert.Single(Plans.Of(store, Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(60f, reset))), reset.AddMinutes(1)));
        Assert.False(Assert.Single(account.Periods).Complete);
    }

    [Fact]
    public void A_price_set_on_the_page_wins_over_the_guess()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = Clock.Minutes(7 * 24 * 60);
        var usage = Make.Ok(ProviderId.Claude, Week(100f, reset)) with { Plan = "Max" };

        store.Record(Snapshot(Clock.Minutes(0), usage), Claude);
        store.Record(Snapshot(Clock.Minutes(60), usage with { Windows = [Week(40f, reset)] }), Claude);

        var guessed = Assert.Single(Plans.Of(store, Snapshot(Clock.Minutes(60), usage), reset.AddMinutes(1)));
        Assert.Equal(100, guessed.Monthly);
        Assert.True(guessed.Assumed);

        store.SetPrice("claude", 120);

        var set = Assert.Single(Plans.Of(store, Snapshot(Clock.Minutes(60), usage), reset.AddMinutes(1)));
        Assert.Equal(120, set.Monthly);
        Assert.False(set.Assumed);
    }

    [Fact]
    public void A_plan_nobody_knows_the_price_of_has_none()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = Clock.Minutes(7 * 24 * 60);
        var usage = Make.Ok(ProviderId.Muse, Week(100f, reset)) with { Plan = "High" };

        store.Record(Snapshot(Clock.Minutes(0), usage), ["muse"]);

        var account = Assert.Single(Plans.Of(store, Snapshot(Clock.Minutes(0), usage), reset.AddMinutes(1)));
        Assert.Null(account.Monthly);
        Assert.False(account.Assumed);
    }

    [Fact]
    public void A_price_can_be_forgotten_again()
    {
        using var temp = new TempStore();
        using var store = temp.Open();

        store.SetPrice("claude", 120);
        Assert.Equal(120, store.Price("claude"));

        store.SetPrice("claude", null);
        Assert.Null(store.Price("claude"));
    }

    [Fact]
    public void A_failed_fetch_does_not_lose_the_plan_name()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = Clock.Minutes(7 * 24 * 60);
        var usage = Make.Ok(ProviderId.Claude, Week(100f, reset)) with { Plan = "Max" };

        store.Record(Snapshot(Clock.Minutes(0), usage), Claude);
        store.Record(Snapshot(Clock.Minutes(60), usage with { Windows = [Week(40f, reset)] }), Claude);

        // The provider answers 401: the entry is replaced and carries no plan.
        var failed = usage with { State = UsageState.Error, Plan = null, Windows = [] };
        store.Record(Snapshot(Clock.Minutes(120), failed), Claude);

        var account = Assert.Single(Plans.Of(store, Snapshot(Clock.Minutes(120), failed), reset.AddMinutes(1)));
        Assert.Equal("Max", account.Plan);
        Assert.Equal(100, account.Monthly);
    }

    [Fact]
    public void An_account_that_is_gone_still_has_its_history()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var reset = Clock.Minutes(7 * 24 * 60);

        store.Record(Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, Week(100f, reset))), Claude);
        store.Record(Snapshot(Clock.Minutes(60), Make.Ok(ProviderId.Claude, Week(40f, reset))), Claude);

        // The account has been removed, so the snapshot no longer mentions it.
        var account = Assert.Single(Plans.Of(store, null, reset.AddMinutes(1)));
        Assert.Equal("Claude", account.DisplayName);
        Assert.Single(account.Periods);
    }
}
