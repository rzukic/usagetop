namespace AiUsage.Tests;

public class ServerStateTests
{
    private const string Answer =
        """
        {
          "serverTime": 1794428250000,
          "snapshot": {
            "fetchedAt": "2026-11-11T20:17:30.000Z",
            "combinedPercent": 46.2,
            "combinedProviders": 2,
            "usages": [
              {
                "id": "mistral", "state": "OK", "plan": "Education", "message": null,
                "fetchedAt": "2026-11-11T20:17:30.000Z", "stale": false, "transient": false,
                "windows": [
                  { "label": "Vibe", "remainingPercent": 77.25, "resetAt": "2026-12-01T00:00:00.000Z",
                    "detail": "left", "countsTowardCombined": true },
                  { "label": "API credit", "remainingPercent": null, "resetAt": null,
                    "detail": "12 left", "countsTowardCombined": false }
                ]
              },
              { "id": "kimi", "state": "ERROR", "message": "Token rejected", "plan": null,
                "fetchedAt": "2026-11-11T20:17:30.000Z", "stale": false, "transient": false, "windows": [] },
              { "id": "claude", "state": "NOT_CONFIGURED", "message": null, "plan": null,
                "fetchedAt": "2026-11-11T20:17:30.000Z", "stale": false, "transient": false, "windows": [] },
              { "id": "a-provider-from-the-future", "state": "OK", "windows": [] }
            ]
          },
          "combined": [[1794428000000, 47.0], [1794428250000, 46.2]],
          "series": { "mistral|Vibe": [[1794428250000, 77.25]] }
        }
        """;

    [Fact]
    public void An_answer_becomes_the_snapshot_and_series_the_screens_draw()
    {
        var state = ServerState.Parse(Answer)!;

        Assert.Equal(DateTimeOffset.Parse("2026-11-11T20:17:30Z"), state.Snapshot.FetchedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794428250000), state.ServerTime);

        // The provider this build has never heard of is dropped, not fatal.
        Assert.Equal(
            [ProviderId.Mistral, ProviderId.Kimi, ProviderId.Claude],
            state.Snapshot.Usages.Select(u => u.Id));

        var mistral = state.Snapshot.Usages[0];
        Assert.Equal(UsageState.Ok, mistral.State);
        Assert.Equal("Education", mistral.Plan);
        Assert.Equal(
            new UsageWindow("Vibe", 77.25f, DateTimeOffset.Parse("2026-12-01T00:00:00Z"), "left"),
            mistral.Windows[0]);
        Assert.Null(mistral.Windows[1].RemainingPercent);
        Assert.False(mistral.Windows[1].CountsTowardCombined);

        Assert.Equal(UsageState.Error, state.Snapshot.Usages[1].State);
        Assert.Equal("Token rejected", state.Snapshot.Usages[1].Message);
        Assert.Equal(UsageState.NotConfigured, state.Snapshot.Usages[2].State);

        Assert.Equal(2, state.Combined.Count);
        Assert.Equal(46.2f, state.Combined[1].Percent);
        Assert.Equal(77.25f, state.Series["mistral|Vibe"].Single().Percent);
    }

    [Fact]
    public void A_sample_moved_to_now_moves_every_moment_and_shows_only_itself()
    {
        var state = ServerState.Parse(Answer)!;
        var offset = TimeSpan.FromDays(9);
        var moved = state.ShiftedBy(offset);

        Assert.Equal(state.ServerTime + offset, moved.ServerTime);
        Assert.Equal(state.Snapshot.FetchedAt + offset, moved.Snapshot.FetchedAt);
        Assert.Equal(state.Snapshot.Usages[0].FetchedAt + offset, moved.Snapshot.Usages[0].FetchedAt);
        Assert.Equal(state.Snapshot.Usages[0].Windows[0].ResetAt + offset, moved.Snapshot.Usages[0].Windows[0].ResetAt);
        Assert.Null(moved.Snapshot.Usages[0].Windows[1].ResetAt);
        Assert.Equal(state.Combined.Select(p => p.At + offset), moved.Combined.Select(p => p.At));
        Assert.Equal(state.Series["mistral|Vibe"][0].At + offset, moved.Series["mistral|Vibe"][0].At);

        var repository = new UsageRepository(moved, Credentials.InMemory(new AesSealer(new byte[32])));
        Assert.Same(moved.Snapshot, repository.Cached());
        Assert.False(repository.UsesServer);
        Assert.Equal(moved.Combined, repository.History.Combined());
        Assert.Same(moved.Snapshot, repository.RefreshAsync(force: true).Result);
    }

    [Fact]
    public void A_server_that_keeps_hidden_limits_sends_them_with_the_state()
    {
        var json = Answer.TrimEnd().TrimEnd('}') + """, "hiddenLimits": ["antigravity|Claude & GPT 7d", "claude-2|Fable 7d", 7] }""";
        var state = ServerState.Parse(json)!;

        // Anything in the list that is not a key is skipped, not fatal.
        Assert.Equal(["antigravity|Claude & GPT 7d", "claude-2|Fable 7d"], state.HiddenLimits);
        Assert.Equal(state.HiddenLimits, state.ShiftedBy(TimeSpan.FromDays(9)).HiddenLimits);

        Assert.Empty(ServerState.Parse(Answer.TrimEnd().TrimEnd('}') + """, "hiddenLimits": [] }""")!.HiddenLimits!);
    }

    [Fact]
    public void A_server_too_old_to_keep_hidden_limits_sends_none()
    {
        var state = ServerState.Parse(Answer)!;
        Assert.Null(state.HiddenLimits);
        Assert.Null(state.ShiftedBy(TimeSpan.FromDays(9)).HiddenLimits);
    }

    [Fact]
    public void The_hidden_list_the_server_answers_a_change_with_is_read_whole()
    {
        Assert.Equal(["muse|Daily", "claude|Fable 7d"], ServerClient.ParseHidden("""["muse|Daily", "claude|Fable 7d"]"""));
        Assert.Empty(ServerClient.ParseHidden("[]"));
        Assert.Throws<FormatException>(() => ServerClient.ParseHidden("""{ "error": "Give the limit as ?limit=account|label" }"""));
    }

    [Fact]
    public void A_sample_with_hidden_limits_hides_nothing()
    {
        // `--state` draws a server's answer without a server: its list is not in force.
        var json = Answer.TrimEnd().TrimEnd('}') + """, "hiddenLimits": ["mistral|Vibe"] }""";
        var repository = new UsageRepository(ServerState.Parse(json)!, Credentials.InMemory(new AesSealer(new byte[32])));

        Assert.Null(repository.ServerHidden);
        Assert.False(repository.HidesOnServer);
        Assert.Empty(repository.HiddenLimits([]));
    }

    [Fact]
    public void The_servers_hidden_limits_replace_this_pcs_only_while_it_keeps_them()
    {
        var credentials = Credentials.InMemory(new AesSealer(new byte[32]));
        var repository = new UsageRepository(ServerState.Parse(Answer)!, credentials);
        string[] local = ["claude|5h"];

        // No server: this PC's list.
        repository.KeepHidden("http://nas.local:8377", ["mistral|Vibe"]);
        Assert.False(repository.HidesOnServer);
        Assert.Equal(local, repository.HiddenLimits(local));

        // The server that sent a list: its list alone, not merged with this PC's.
        credentials.SetServer("http://nas.local:8377", "secret");
        Assert.True(repository.HidesOnServer);
        Assert.Equal(["mistral|Vibe"], repository.HiddenLimits(local));

        // An empty list from the server is still the server's: nothing hidden.
        repository.KeepHidden("http://nas.local:8377", []);
        Assert.Empty(repository.HiddenLimits(local));

        // A server too old to keep one: this PC's list again.
        repository.KeepHidden("http://nas.local:8377", null);
        Assert.False(repository.HidesOnServer);
        Assert.Equal(local, repository.HiddenLimits(local));

        // Another server's list is not this one's.
        repository.KeepHidden("http://nas.local:8377", ["mistral|Vibe"]);
        credentials.SetServer("http://other.local:8377", "secret");
        Assert.Equal(local, repository.HiddenLimits(local));

        credentials.SetServer(null, null);
        Assert.Equal(local, repository.HiddenLimits(local));
    }

    [Fact]
    public async Task A_server_that_cannot_be_told_keeps_its_last_list()
    {
        var credentials = Credentials.InMemory(new AesSealer(new byte[32]));
        var repository = new UsageRepository(ServerState.Parse(Answer)!, credentials);
        // Nothing listens on port 9 of this machine; the call fails at once.
        credentials.SetServer("http://127.0.0.1:9", "secret");
        repository.KeepHidden("http://127.0.0.1:9", ["mistral|Vibe"]);

        await repository.SetHiddenOnServerAsync("kimi|Weekly", hide: true);

        Assert.Equal(["mistral|Vibe"], repository.ServerHidden);
        Assert.Equal("Server unreachable", repository.ServerProblem);
    }

    [Fact]
    public void A_server_that_has_not_polled_yet_has_no_state()
    {
        Assert.Null(ServerState.Parse("""{ "serverTime": 1, "snapshot": null, "combined": [], "series": {} }"""));
    }

    [Theory]
    [InlineData("100.64.0.7:8377", "http://100.64.0.7:8377")]
    [InlineData(" http://nas.local:8377/ ", "http://nas.local:8377")]
    [InlineData("https://usage.example.org", "https://usage.example.org")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("ftp://nas.local", null)]
    [InlineData("http://", null)]
    public void An_address_is_tidied_or_turned_down(string typed, string? expected)
    {
        Assert.Equal(expected, ServerClient.NormalizeUrl(typed));
    }
}

public class ServerAccountTests
{
    private const string Answer =
        """
        [
          { "key": "claude", "provider": "claude", "name": null, "displayName": "Claude",
            "removable": false, "configured": true, "renews": true },
          { "key": "claude-2", "provider": "claude", "name": "Work", "displayName": "Claude · Work",
            "removable": true, "configured": false, "renews": false }
        ]
        """;

    [Fact]
    public void The_accounts_the_server_follows_come_back_without_their_tokens()
    {
        var accounts = ServerClient.ParseAccounts(Answer);

        Assert.Equal(["claude", "claude-2"], accounts.Select(a => a.Key));
        Assert.Equal("claude", accounts[0].Provider);
        Assert.Equal("Claude", accounts[0].DisplayName);
        Assert.Null(accounts[0].Name);
        Assert.False(accounts[0].Removable);
        Assert.True(accounts[0].Configured);
        Assert.True(accounts[0].Renews);

        Assert.Equal("Work", accounts[1].Name);
        Assert.Equal("Claude · Work", accounts[1].DisplayName);
        Assert.True(accounts[1].Removable);
        Assert.False(accounts[1].Configured);
    }

    [Fact]
    public void An_answer_that_is_not_a_list_is_a_server_problem()
    {
        Assert.Throws<FormatException>(() => ServerClient.ParseAccounts("""{ "error": "nope" }"""));
    }

    [Fact]
    public void A_sign_in_step_says_what_the_user_has_to_do()
    {
        var step = ServerSignInStep.Parse(System.Text.Json.Nodes.JsonNode.Parse(
            """
            { "kind": "device", "url": "https://www.meta.ai/device", "userCode": "ABCD-1234",
              "instructions": "Open the link and enter the code." }
            """)!.AsObject());

        Assert.Equal("device", step.Kind);
        Assert.Equal("https://www.meta.ai/device", step.Url);
        Assert.Equal("ABCD-1234", step.UserCode);
        Assert.Equal("Open the link and enter the code.", step.Instructions);
    }
}

public class ServerHistoryTests
{
    private static readonly TimeSpan Retain = UsageHistory.Retain;

    private static List<UsagePoint> Every(int minutes, int from, int count, float percent) =>
        Enumerable.Range(0, count)
            .Select(i => new UsagePoint(Clock.Minutes(from + i * minutes), percent))
            .ToList();

    [Fact]
    public void A_first_sync_keeps_what_the_app_drew_before_the_server_existed()
    {
        // The app has ten hours; the server only started five hours in.
        var ours = Every(15, 0, 40, 50f);
        var theirs = Every(5, 300, 60, 60f);
        var now = theirs[^1].At;

        var merged = UsageHistory.Splice(ours, theirs, now, Retain);

        Assert.Equal(ours[0], merged[0]);
        Assert.All(merged.Where(p => p.At < Clock.Minutes(300)), p => Assert.Equal(50f, p.Percent));
        Assert.All(merged.Where(p => p.At >= Clock.Minutes(300)), p => Assert.Equal(60f, p.Percent));
        Assert.Equal(theirs[^1], merged[^1]);
    }

    [Fact]
    public void A_top_up_replaces_the_overlap_instead_of_doubling_it()
    {
        var synced = Every(5, 0, 24, 60f);

        // Ten minutes of overlap, then two new readings.
        var topUp = Every(5, 105, 5, 60f);
        var now = topUp[^1].At;

        var merged = UsageHistory.Splice(synced, topUp, now, Retain);

        Assert.Equal(merged.Select(p => p.At).Distinct().Count(), merged.Count);
        Assert.Equal(26, merged.Count);
        Assert.Equal(Clock.Minutes(125), merged[^1].At);
    }

    [Fact]
    public void An_empty_answer_changes_nothing()
    {
        var ours = Every(15, 0, 4, 50f);

        Assert.Equal(ours, UsageHistory.Splice(ours, [], Clock.Minutes(60), Retain));
    }
}

public class ServerSettingTests
{
    [Fact]
    public void A_server_is_remembered_and_clearing_the_address_forgets_the_token_too()
    {
        using var folder = new TempFolder();
        var path = folder.File("credentials.json");
        var credentials = new Credentials(path, new AesSealer(new byte[32]));

        Assert.Null(credentials.ServerUrl());

        credentials.SetServer("http://nas:8377", " secret ");
        var reopened = new Credentials(path, new AesSealer(new byte[32]));
        Assert.Equal("http://nas:8377", reopened.ServerUrl());
        Assert.Equal("secret", reopened.ServerToken());
        Assert.DoesNotContain("secret", File.ReadAllText(path));

        credentials.SetServer(null, "secret");
        Assert.Null(credentials.ServerUrl());
        Assert.Null(credentials.ServerToken());
    }
}
