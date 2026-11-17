using Microsoft.Data.Sqlite;

namespace AiUsage.Server.Tests;

internal static class Clock
{
    public static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L);

    public static DateTimeOffset Minutes(double minutes) => T0.AddMinutes(minutes);
}

internal static class Make
{
    public static ProviderUsage Ok(ProviderId id, params UsageWindow[] windows) => new()
    {
        Id = id,
        State = UsageState.Ok,
        Windows = windows,
    };

    public static ProviderUsage Failed(ProviderId id, string message, bool transient) => new()
    {
        Id = id,
        State = UsageState.Error,
        Message = message,
        Transient = transient,
    };

    public static Snapshot Snapshot(DateTimeOffset at, params ProviderUsage[] usages) => new(usages, at);
}

/// <summary>A database file in a scratch folder, removed when the test is done.</summary>
internal sealed class TempStore : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ai-usage-tests", Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(_folder, "usage.db");

    public Store Open() => new(DatabasePath);

    public void Dispose()
    {
        // The pool keeps the file open after the connection is disposed.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }
}

public class StoreTests
{
    private static readonly string[] Claude = ["claude"];

    [Fact]
    public void A_flat_window_is_stored_once_and_then_only_as_a_heartbeat()
    {
        using var temp = new TempStore();
        using var store = temp.Open();

        // Polled every two minutes for half an hour without moving.
        for (var minute = 0; minute <= 30; minute += 2)
        {
            store.Record(
                Make.Snapshot(Clock.Minutes(minute), Make.Ok(ProviderId.Claude, new UsageWindow("7d", 80f))),
                Claude);
        }

        // Sixteen readings came in; the first is kept, then one a quarter of an
        // hour later to say "still flat".
        var series = store.Series("claude", "7d", Clock.T0);
        Assert.Equal([Clock.Minutes(0), Clock.Minutes(16)], series.Select(r => r.At).ToArray());
    }

    [Fact]
    public void Every_move_is_stored_however_close_together()
    {
        using var temp = new TempStore();
        using var store = temp.Open();

        store.Record(Make.Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, new UsageWindow("5h", 80f))), Claude);
        store.Record(Make.Snapshot(Clock.Minutes(1), Make.Ok(ProviderId.Claude, new UsageWindow("5h", 79f))), Claude);
        store.Record(Make.Snapshot(Clock.Minutes(2), Make.Ok(ProviderId.Claude, new UsageWindow("5h", 77.5f))), Claude);

        Assert.Equal([80f, 79f, 77.5f], store.Series("claude", "5h", Clock.T0).Select(r => r.Percent).ToArray());
    }

    [Fact]
    public void A_reset_time_that_only_jitters_is_not_a_change()
    {
        // ChatGPT gives the reset as seconds from now, so it lands a little
        // differently on every fetch.
        var first = new Reading(Clock.Minutes(0), 50f, Clock.Minutes(300), false);

        Assert.False(Store.WorthKeeping(first, new Reading(Clock.Minutes(2), 50f, Clock.Minutes(300.5), false)));
        Assert.True(Store.WorthKeeping(first, new Reading(Clock.Minutes(2), 50f, Clock.Minutes(600), false)));
        Assert.True(Store.WorthKeeping(first, new Reading(Clock.Minutes(2), 50f, null, false)));
        Assert.True(Store.WorthKeeping(first, new Reading(Clock.Minutes(2), 50f, Clock.Minutes(300), true)));
    }

    [Fact]
    public void Only_the_providers_asked_this_round_gain_a_reading()
    {
        using var temp = new TempStore();
        using var store = temp.Open();
        var snapshot = Make.Snapshot(
            Clock.Minutes(0),
            Make.Ok(ProviderId.Claude, new UsageWindow("7d", 80f)),
            Make.Ok(ProviderId.Kimi, new UsageWindow("Monthly", 15f)));

        // Kimi's part of the snapshot is carried over from an earlier round.
        store.Record(snapshot, Claude);

        Assert.Single(store.Series("claude", "7d", Clock.T0));
        Assert.Empty(store.Series("kimi", "Monthly", Clock.T0));
        Assert.Equal([("claude", "7d")], store.SeriesKeys(Clock.T0));
    }

    [Fact]
    public void A_restart_resumes_from_what_was_stored()
    {
        using var temp = new TempStore();
        var reset = Clock.Minutes(600);

        using (var store = temp.Open())
        {
            store.Record(
                Make.Snapshot(Clock.Minutes(0), Make.Ok(ProviderId.Claude, new UsageWindow("7d", 80f, reset))),
                Claude);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = temp.Open();

        // The snapshot is back, and so is the memory of the last row: the same
        // reading two minutes later is still not worth a second one.
        Assert.Equal(80f, reopened.LoadSnapshot()!.Usages[0].Windows[0].RemainingPercent);
        reopened.Record(
            Make.Snapshot(Clock.Minutes(2), Make.Ok(ProviderId.Claude, new UsageWindow("7d", 80f, reset))),
            Claude);

        var reading = Assert.Single(reopened.Series("claude", "7d", Clock.T0));
        Assert.Equal(reset, reading.ResetAt);
        Assert.Single(reopened.Combined(Clock.T0));
    }

    [Fact]
    public void A_provider_in_error_or_a_window_with_only_a_balance_stores_nothing()
    {
        using var temp = new TempStore();
        using var store = temp.Open();

        store.Record(
            Make.Snapshot(
                Clock.Minutes(0),
                Make.Failed(ProviderId.Claude, "Token rejected", transient: false),
                Make.Ok(ProviderId.Kimi, new UsageWindow("balance", Detail: "¥12.00 available"))),
            ["claude", "kimi"]);

        Assert.Equal(0, store.ReadingCount());
    }
}

public class ScheduleTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Active = TimeSpan.FromMinutes(2);

    private static Schedule New() => new(Idle, Active, Clock.T0);

    private static ProviderUsage Claude(float percent, DateTimeOffset? resetAt = null) =>
        Make.Ok(ProviderId.Claude, new UsageWindow("5h", percent, resetAt));

    /// <summary>When Claude is next due, given everything else is far in the future.</summary>
    private static DateTimeOffset NextClaude(Schedule schedule, DateTimeOffset now)
    {
        foreach (var info in ProviderCatalog.All.Where(info => info.Id != ProviderId.Claude))
        {
            schedule.Report(null, Make.Failed(info.Id, "Token rejected", transient: false), now.AddDays(1));
        }
        return schedule.NextDue();
    }

    [Fact]
    public void Everything_is_due_the_moment_the_server_starts()
    {
        Assert.Equal(ProviderCatalog.All.Count, New().Due(Clock.T0).Count);
    }

    [Fact]
    public void A_window_nobody_is_spending_is_asked_at_the_slow_pace()
    {
        var schedule = New();
        schedule.Report(Claude(80f), Claude(80f), Clock.T0);

        Assert.Equal(Clock.T0 + Idle, NextClaude(schedule, Clock.T0));
    }

    [Fact]
    public void A_window_that_moved_is_asked_quickly_until_it_has_been_still_a_while()
    {
        var schedule = New();

        schedule.Report(Claude(80f), Claude(78f), Clock.T0);
        Assert.Equal(Clock.T0 + Active, NextClaude(schedule, Clock.T0));

        // Still for ten minutes: recently enough to keep the fast pace.
        var later = Clock.Minutes(10);
        schedule.Report(Claude(78f), Claude(78f), later);
        Assert.Equal(later + Active, NextClaude(schedule, later));

        // Still for twenty: back to the slow one.
        var muchLater = Clock.Minutes(20);
        schedule.Report(Claude(78f), Claude(78f), muchLater);
        Assert.Equal(muchLater + Idle, NextClaude(schedule, muchLater));
    }

    [Fact]
    public void A_rate_limited_provider_is_asked_less_and_less_up_to_a_ceiling()
    {
        var schedule = New();
        var limited = Claude(80f) with { Stale = true, Message = "Rate limited" };

        var waits = new List<TimeSpan>();
        var now = Clock.T0;
        for (var i = 0; i < 4; i++)
        {
            schedule.Report(Claude(80f), limited, now);
            var due = NextClaude(schedule, now);
            waits.Add(due - now);
            now = due;
        }

        Assert.Equal(
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), Schedule.Ceiling, Schedule.Ceiling],
            waits);

        // One good answer and it is trusted again.
        schedule.Report(limited, Claude(80f), now);
        Assert.Equal(now + Idle, NextClaude(schedule, now));
    }

    [Fact]
    public void A_refill_due_before_the_next_turn_is_caught_just_after_it_happens()
    {
        var schedule = New();
        var reset = Clock.Minutes(3);

        schedule.Report(Claude(10f, reset), Claude(10f, reset), Clock.T0);

        Assert.Equal(reset.AddSeconds(30), NextClaude(schedule, Clock.T0));
    }

    [Fact]
    public void A_reset_claimed_seconds_away_cannot_make_the_server_hammer()
    {
        var schedule = New();
        var reset = Clock.T0.AddSeconds(5);

        schedule.Report(Claude(10f, reset), Claude(10f, reset), Clock.T0);

        Assert.Equal(Clock.T0.AddMinutes(1), NextClaude(schedule, Clock.T0));
    }

    [Fact]
    public void A_rejected_token_waits_for_the_user_and_a_sign_in_hurries_it()
    {
        var schedule = New();
        schedule.Report(Claude(80f), Make.Failed(ProviderId.Claude, "Token rejected", transient: false), Clock.T0);
        Assert.Equal(Clock.T0 + Schedule.Ceiling, NextClaude(schedule, Clock.T0));

        schedule.Hurry(ProviderId.Claude, Clock.Minutes(1));

        Assert.Contains("claude", schedule.Due(Clock.Minutes(1)));
    }
}

public class AlertsTests
{
    private const float Low = 10f;

    private static Snapshot Claude(float fiveHour, DateTimeOffset? resetAt = null) => Make.Snapshot(
        Clock.T0,
        Make.Ok(ProviderId.Claude, new UsageWindow("5h", fiveHour, resetAt)));

    [Fact]
    public void A_window_falling_under_the_line_is_announced_once()
    {
        var crossing = Alerts.Between(Claude(12f), Claude(8f, Clock.Minutes(130)), Low, Clock.T0);
        var stillLow = Alerts.Between(Claude(8f), Claude(6f), Low, Clock.T0);

        var alert = Assert.Single(crossing);
        Assert.Equal("Claude 5h: 8% left", alert.Title);
        Assert.Equal("Resets in 2h 10m.", alert.Body);
        Assert.False(alert.Urgent);
        Assert.Empty(stillLow);
    }

    [Fact]
    public void Running_out_completely_is_urgent()
    {
        var alert = Assert.Single(Alerts.Between(Claude(12f), Claude(0f), Low, Clock.T0));

        Assert.True(alert.Urgent);
    }

    [Fact]
    public void A_refill_after_running_low_is_announced()
    {
        var alert = Assert.Single(Alerts.Between(Claude(3f), Claude(100f), Low, Clock.T0));

        Assert.Equal("Claude 5h is back", alert.Title);
    }

    [Fact]
    public void A_provider_that_needs_the_user_is_announced_once_and_a_passing_failure_never()
    {
        var rejected = Make.Snapshot(Clock.T0, Make.Failed(ProviderId.Claude, "Token rejected", transient: false));
        var limited = Make.Snapshot(Clock.T0, Make.Failed(ProviderId.Claude, "Rate limited", transient: true));

        var alert = Assert.Single(Alerts.Between(Claude(50f), rejected, Low, Clock.T0));
        Assert.Equal("Claude needs attention", alert.Title);
        Assert.Contains("Token rejected", alert.Body);
        Assert.True(alert.Urgent);

        Assert.Empty(Alerts.Between(rejected, rejected, Low, Clock.T0));
        Assert.Empty(Alerts.Between(Claude(50f), limited, Low, Clock.T0));
    }

    [Fact]
    public void A_first_snapshot_announces_nothing()
    {
        Assert.Empty(Alerts.Between(null, Claude(2f), Low, Clock.T0));
    }
}

public class WireTests
{
    private static List<UsagePoint> Sawtooth(int count) => Enumerable.Range(0, count)
        .Select(i => new UsagePoint(Clock.Minutes(i), 100f - i % 100))
        .ToList();

    [Fact]
    public void A_short_series_is_sent_as_it_is()
    {
        var points = Sawtooth(50);

        Assert.Same(points, Wire.Downsample(points, 600));
    }

    [Fact]
    public void A_long_series_is_cut_down_without_losing_its_peaks_or_its_end()
    {
        // Ten refills over a thousand minutes: 100 down to 1, then back up.
        var points = Sawtooth(1000);

        var kept = Wire.Downsample(points, 100);

        Assert.InRange(kept.Count, 50, 101);
        Assert.Equal(points[^1], kept[^1]);
        Assert.True(kept.Zip(kept.Skip(1)).All(pair => pair.First.At < pair.Second.At));

        // Every refill's top and every trough's bottom survives: that edge is
        // what the chart is for.
        Assert.Equal(10, kept.Count(p => p.Percent == 100f));
        Assert.Equal(10, kept.Count(p => p.Percent == 1f));
    }

    [Fact]
    public void A_snapshot_is_written_the_way_the_android_cache_reads_it()
    {
        var snapshot = Make.Snapshot(
            DateTimeOffset.Parse("2026-11-08T02:04:49Z"),
            Make.Ok(
                ProviderId.Claude,
                new UsageWindow("7d", 19f, DateTimeOffset.Parse("2026-11-11T19:59:59Z")),
                new UsageWindow("Fable 7d", 100f, CountsTowardCombined: false)) with { Plan = "Max" },
            Make.Failed(ProviderId.Kimi, "Token rejected", transient: false));

        var json = Wire.Snapshot(snapshot);

        Assert.Equal("2026-11-08T02:04:49.000Z", (string?)json["fetchedAt"]);
        Assert.Equal(19.0, (double?)json["combinedPercent"]);

        var claude = json["usages"]![0]!;
        Assert.Equal("claude", (string?)claude["id"]);
        Assert.Equal("OK", (string?)claude["state"]);
        Assert.Equal("Max", (string?)claude["plan"]);
        Assert.Equal("2026-11-11T19:59:59.000Z", (string?)claude["windows"]![0]!["resetAt"]);
        Assert.False((bool)claude["windows"]![1]!["countsTowardCombined"]!);
        Assert.Null(claude["windows"]![1]!["resetAt"]);

        Assert.Equal("ERROR", (string?)json["usages"]![1]!["state"]);
    }
}

public class ClientContractTests
{
    [Fact]
    public void What_the_server_sends_is_what_the_windows_app_reads()
    {
        var reset = DateTimeOffset.Parse("2026-11-11T19:59:59Z");
        var snapshot = Make.Snapshot(
            DateTimeOffset.Parse("2026-11-08T02:04:49Z"),
            Make.Ok(
                ProviderId.Claude,
                new UsageWindow("7d", 19f, reset, "detail"),
                new UsageWindow("Fable 7d", 100f, CountsTowardCombined: false)) with { Plan = "Max" },
            Make.Failed(ProviderId.Kimi, "Token rejected", transient: false),
            Make.Ok(ProviderId.Mistral, new UsageWindow("Vibe", 77.25f)) with { Stale = true, Message = "Rate limited" });

        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["serverTime"] = Clock.T0.ToUnixTimeMilliseconds(),
            ["snapshot"] = Wire.Snapshot(snapshot),
            ["combined"] = Wire.Series([new UsagePoint(Clock.T0, 19f)]),
            ["series"] = new System.Text.Json.Nodes.JsonObject
            {
                [Store.SeriesKey("claude", "7d")] = Wire.Series([new UsagePoint(Clock.T0, 19f)]),
            },
        };

        var read = ServerState.Parse(body.ToJsonString())!;

        Assert.Equal(snapshot.FetchedAt, read.Snapshot.FetchedAt);
        Assert.Equal(snapshot.Usages.Count, read.Snapshot.Usages.Count);
        foreach (var (sent, got) in snapshot.Usages.Zip(read.Snapshot.Usages))
        {
            Assert.Equal(sent.Id, got.Id);
            Assert.Equal(sent.State, got.State);
            Assert.Equal(sent.Plan, got.Plan);
            Assert.Equal(sent.Message, got.Message);
            Assert.Equal(sent.Stale, got.Stale);
            Assert.Equal(sent.Transient, got.Transient);
            Assert.Equal(sent.Windows, got.Windows);
        }
        Assert.Equal([new UsagePoint(Clock.T0, 19f)], read.Series["claude|7d"]);
        Assert.Equal(Clock.T0, read.ServerTime);
    }

    [Fact]
    public void A_client_can_ask_for_only_what_is_new_and_nonsense_is_clamped()
    {
        var now = Clock.T0;

        Assert.Equal(now.AddDays(-35), Api.Since(now, days: null, since: null));
        Assert.Equal(now.AddDays(-2), Api.Since(now, days: 2, since: null));

        var recent = now.AddMinutes(-20);
        Assert.Equal(recent, Api.Since(now, days: 2, since: recent.ToUnixTimeMilliseconds()));

        Assert.Equal(now.AddDays(-400), Api.Since(now, days: null, since: long.MinValue));
        Assert.Equal(now, Api.Since(now, days: null, since: long.MaxValue));
    }

    [Fact]
    public void The_page_is_built_into_the_binary_and_runs_no_inline_script()
    {
        var html = System.Text.Encoding.UTF8.GetString(WebUi.Read("web/index.html"));

        Assert.Contains("<script src=\"app.js\"></script>", html);
        Assert.DoesNotContain("style=", html);
        Assert.DoesNotContain("onclick", html);
        Assert.NotEmpty(WebUi.Read("web/app.js"));
        Assert.NotEmpty(WebUi.Read("web/app.css"));
        Assert.Contains("script-src 'self'", WebUi.ContentSecurityPolicy);
    }
}

public class SignInTests
{
    [Theory]
    // Anthropic's page shows the two joined by a hash.
    [InlineData("abc123#state-9", "abc123", "state-9")]
    // OpenAI's arrives as the address of a page that did not load.
    [InlineData("http://localhost:1455/auth/callback?code=abc%2F123&scope=openid+profile&state=state-9", "abc/123", "state-9")]
    [InlineData("  bare-code  ", "bare-code", null)]
    public void Whatever_the_user_pastes_gives_up_its_code(string pasted, string code, string? state)
    {
        Assert.Equal((code, state), SignInService.Parse(pasted));
    }
}

public class OptionsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Nothing_set_means_loopback_and_five_minutes()
    {
        var options = ServerOptions.FromEnvironment(Env());

        Assert.Equal("http://127.0.0.1:8377", options.Urls);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Poll);
        Assert.Null(options.NotifyUrl);
    }

    [Fact]
    public void A_polling_interval_cannot_be_set_low_enough_to_hammer()
    {
        var options = ServerOptions.FromEnvironment(Env(("AIUSAGE_POLL_SECONDS", "5"), ("AIUSAGE_ACTIVE_POLL_SECONDS", "1")));

        Assert.Equal(TimeSpan.FromMinutes(1), options.Poll);
        Assert.Equal(TimeSpan.FromMinutes(1), options.ActivePoll);
    }

    [Theory]
    [InlineData("http://0.0.0.0:8377", "http://127.0.0.1:8377/")]
    [InlineData("http://*:9000;http://10.0.0.5:9000", "http://127.0.0.1:9000/")]
    [InlineData("http://100.64.0.7:8377", "http://100.64.0.7:8377/")]
    public void The_command_line_finds_the_server_it_shares_a_machine_with(string urls, string expected)
    {
        Assert.Equal(new Uri(expected), Cli.BaseAddress(new ServerOptions { Urls = urls }));
    }

    [Fact]
    public void Secrets_are_made_once_and_then_read_back()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ai-usage-tests", Guid.NewGuid().ToString("N"));
        var options = new ServerOptions { DataDir = folder };
        try
        {
            var token = Secrets.ApiToken(options, Env());
            var sealedValue = Secrets.Sealer(options, Env()).Seal("sk-secret");

            Assert.Equal(48, token.Length);
            Assert.Equal(token, Secrets.ApiToken(options, Env()));
            Assert.Equal("sk-secret", Secrets.Sealer(options, Env()).Unseal(sealedValue));

            // With a passphrase in the environment the key file is not what opens it.
            Assert.Null(Secrets.Sealer(options, Env(("AIUSAGE_SECRET", "hunter2"))).Unseal(sealedValue));
            Assert.Equal("from-env", Secrets.ApiToken(options, Env(("AIUSAGE_API_TOKEN", "from-env"))));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class AccountTests
{
    private static ProviderUsage Second(ProviderUsage usage, string slot, string? name) =>
        usage with { Slot = slot, AccountName = name };

    [Fact]
    public void A_second_account_keeps_its_own_history_beside_the_first()
    {
        using var temp = new TempStore();
        using var store = temp.Open();

        store.Record(
            Make.Snapshot(
                Clock.Minutes(0),
                Make.Ok(ProviderId.Claude, new UsageWindow("7d", 80f)),
                Second(Make.Ok(ProviderId.Claude, new UsageWindow("7d", 30f)), "2", "Work")),
            ["claude", "claude-2"]);

        Assert.Equal(80f, Assert.Single(store.Series("claude", "7d", Clock.T0)).Percent);
        Assert.Equal(30f, Assert.Single(store.Series("claude-2", "7d", Clock.T0)).Percent);

        // Two subscriptions in the combined line: (80 + 30) / 2.
        Assert.Equal(55f, Assert.Single(store.Combined(Clock.T0)).Percent);

        // And the stored snapshot still knows whose is whose.
        Assert.Equal("Work", store.LoadSnapshot()!.Usages[1].AccountName);
    }

    [Fact]
    public void A_round_is_laid_over_the_last_one_by_account_and_a_removed_account_drops_out()
    {
        var before = Make.Snapshot(
            Clock.Minutes(0),
            Make.Ok(ProviderId.Kimi, new UsageWindow("Monthly", 15f)),
            Second(Make.Ok(ProviderId.Claude, new UsageWindow("7d", 30f)), "2", "Work"),
            Make.Ok(ProviderId.Claude, new UsageWindow("7d", 80f)),
            Second(Make.Ok(ProviderId.Claude, new UsageWindow("7d", 10f)), "3", "Old"));

        var fetched = Make.Snapshot(
            Clock.Minutes(5),
            Second(Make.Ok(ProviderId.Claude, new UsageWindow("7d", 25f)), "2", "Work"));

        var merged = Poller.Merge(before, fetched, new HashSet<string> { "claude", "claude-2", "kimi" });

        Assert.Equal(["claude", "claude-2", "kimi"], merged.Usages.Select(u => u.AccountKey));
        Assert.Equal(25f, merged.Usages[1].Windows[0].RemainingPercent);
        Assert.Equal(80f, merged.Usages[0].Windows[0].RemainingPercent);
    }

    [Fact]
    public void A_new_account_gets_a_clock_of_its_own_and_a_removed_one_loses_it()
    {
        var schedule = new Schedule(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), Clock.T0);
        foreach (var key in schedule.Due(Clock.T0))
        {
            schedule.Report(null, Make.Ok(ProviderCatalog.FromKey(key)!.Id), Clock.T0);
        }
        Assert.Empty(schedule.Due(Clock.Minutes(1)));

        schedule.Hurry("claude-2", Clock.Minutes(1));
        Assert.Equal(["claude-2"], schedule.Due(Clock.Minutes(1)));

        schedule.Forget("claude-2");
        Assert.Empty(schedule.Due(Clock.Minutes(1)));
    }

    [Fact]
    public void An_alert_names_the_account()
    {
        var before = Make.Snapshot(Clock.Minutes(0), Second(Make.Ok(ProviderId.Claude, new UsageWindow("7d", 20f)), "2", "Work"));
        var after = Make.Snapshot(Clock.Minutes(5), Second(Make.Ok(ProviderId.Claude, new UsageWindow("7d", 5f)), "2", "Work"));

        var alert = Assert.Single(Alerts.Between(before, after, 10f, Clock.Minutes(5)));
        Assert.StartsWith("Claude · Work 7d", alert.Title);
    }

    [Fact]
    public void The_wire_says_which_account_a_reading_is()
    {
        var json = Wire.Snapshot(Make.Snapshot(
            Clock.T0,
            Make.Ok(ProviderId.Claude),
            Second(Make.Ok(ProviderId.Claude), "2", "Work")));

        Assert.Equal("claude", (string?)json["usages"]![0]!["account"]);
        Assert.Equal("claude", (string?)json["usages"]![1]!["id"]);
        Assert.Equal("claude-2", (string?)json["usages"]![1]!["account"]);
        Assert.Equal("Work", (string?)json["usages"]![1]!["accountName"]);
    }
}
