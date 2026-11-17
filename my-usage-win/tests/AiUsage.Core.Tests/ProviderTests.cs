using System.Text.Json;
using AiUsage.Providers;

namespace AiUsage.Tests;

public class ClaudeProviderTests
{
    private static List<UsageWindow> Scoped(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ClaudeProvider.Scoped(document.RootElement);
    }

    [Fact]
    public void The_fable_weekly_limit_becomes_a_window_of_its_own()
    {
        var windows = Scoped(
            """
            [
              {"kind":"session","group":"session","percent":9,"severity":"normal","is_active":false},
              {"kind":"weekly_all","group":"weekly","percent":68,"severity":"normal","is_active":false},
              {"kind":"weekly_scoped","group":"weekly","percent":100,"severity":"critical",
               "is_active":true,"resets_at":"2026-11-16T09:00:00Z",
               "scope":{"model":{"id":null,"display_name":"Fable"}}}
            ]
            """);

        var fable = Assert.Single(windows);
        Assert.Equal("Fable 7d", fable.Label);
        Assert.Equal(0f, fable.RemainingPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T09:00:00Z"), fable.ResetAt);
        Assert.False(fable.CountsTowardCombined);
    }

    [Fact]
    public void A_scoped_limit_that_names_no_model_is_skipped()
    {
        Assert.Empty(Scoped("""[{"kind":"weekly_scoped","percent":40,"scope":{"model":{"display_name":""}}}]"""));
    }

    [Fact]
    public void A_response_without_a_limits_array_adds_nothing()
    {
        Assert.Empty(ClaudeProvider.Scoped(null));
    }
}

public class MistralProviderTests
{
    private static List<UsageWindow> Budgets(string json)
    {
        using var document = JsonDocument.Parse(json);
        return MistralProvider.Budgets(document.RootElement);
    }

    [Fact]
    public void The_api_credit_is_kept_out_of_the_combined_figure()
    {
        var windows = Budgets(
            """
            {
              "api_budget": {"usage_percentage": 0, "initial_budget": 12.75,
                             "currency": "EUR", "reset_at": "2026-12-01T00:00:00Z"},
              "vibe_budget": {"usage_percentage": 18, "initial_budget": 127.5,
                              "currency": "EUR", "reset_at": "2026-12-01T00:00:00Z"}
            }
            """);

        var api = Assert.Single(windows, w => w.Label == "API");
        var vibe = Assert.Single(windows, w => w.Label == "Vibe");

        Assert.False(api.CountsTowardCombined);
        Assert.True(vibe.CountsTowardCombined);
        Assert.Equal(82f, vibe.RemainingPercent);
        Assert.Equal("€104.55 of €127.50 left", vibe.Detail);
    }

    [Fact]
    public void A_response_with_only_the_vibe_budget_still_reports_it()
    {
        var windows = Budgets("""{"vibe_budget":{"usage_percentage":50,"initial_budget":100,"currency":"EUR"}}""");

        var vibe = Assert.Single(windows);
        Assert.Equal("Vibe", vibe.Label);
        Assert.Equal(50f, vibe.RemainingPercent);
    }
}

public class MuseProviderTests
{
    private static readonly DateTimeOffset Now = Clock.T0;

    /// <summary>What the repository keeps when Meta answers without `subs_usage`.</summary>
    private static ProviderUsage Stale(params UsageWindow[] windows) => new()
    {
        Id = ProviderId.Muse,
        State = UsageState.Ok,
        Windows = windows,
        Message = "No usage reported",
        Stale = true,
    };

    [Fact]
    public void A_window_whose_reset_has_passed_is_full_again()
    {
        var rolled = MuseProvider.RollOver(
            Stale(
                new UsageWindow("5h", 0f, Now.AddSeconds(-60)),
                new UsageWindow("Weekly", 66f, Now.AddDays(1))),
            Now);

        Assert.Equal(100f, rolled.Windows[0].RemainingPercent);
        Assert.Null(rolled.Windows[0].ResetAt);
        Assert.Equal(66f, rolled.Windows[1].RemainingPercent);
        Assert.Equal(Now.AddDays(1), rolled.Windows[1].ResetAt);
    }

    [Fact]
    public void A_window_that_has_not_reset_keeps_its_reading()
    {
        var reading = Stale(new UsageWindow("5h", 0f, Now.AddMinutes(10)));

        Assert.Same(reading, MuseProvider.RollOver(reading, Now));
    }

    [Fact]
    public void A_reading_from_this_fetch_is_left_alone()
    {
        var fresh = Stale(new UsageWindow("5h", 0f, Now.AddSeconds(-60))) with { Stale = false };

        Assert.Same(fresh, MuseProvider.RollOver(fresh, Now));
    }

    [Fact]
    public void Another_providers_stale_reading_is_left_alone()
    {
        var other = Stale(new UsageWindow("5h", 0f, Now.AddSeconds(-60))) with { Id = ProviderId.Claude };

        Assert.Same(other, MuseProvider.RollOver(other, Now));
    }
}

public class ProviderHelpersTests
{
    [Theory]
    [InlineData(401, true, false)]
    [InlineData(403, false, false)]
    [InlineData(429, false, true)]
    [InlineData(503, false, true)]
    [InlineData(404, false, false)]
    public void A_status_says_whether_to_renew_the_token_or_keep_the_last_reading(
        int code,
        bool rejected,
        bool transient)
    {
        var usage = ProviderHelpers.HttpError(ProviderId.Claude, code);

        Assert.Equal(UsageState.Error, usage.State);
        Assert.Equal(rejected, usage.Rejected);
        Assert.Equal(transient, usage.Transient);
    }
}
