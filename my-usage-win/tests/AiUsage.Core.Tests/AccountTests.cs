using System.Security.Cryptography;
using AiUsage.Providers;

namespace AiUsage.Tests;

public class AccountKeyTests
{
    [Theory]
    [InlineData("claude", ProviderId.Claude, "")]
    [InlineData("claude-2", ProviderId.Claude, "2")]
    [InlineData("CHATGPT-14", ProviderId.ChatGpt, "14")]
    public void A_key_names_a_provider_and_a_slot(string key, ProviderId provider, string slot)
    {
        Assert.Equal((provider, slot), Account.Parse(key));
        Assert.Equal(key.ToLowerInvariant(), Account.KeyOf(provider, slot));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("nope-2")]
    [InlineData("claude-")]
    [InlineData("claude-2-3")]
    [InlineData("claude-../x")]
    [InlineData("claude-A")]
    public void Anything_else_is_not_an_account(string? key)
    {
        Assert.Null(Account.Parse(key));
    }

    [Fact]
    public void An_account_is_named_after_its_provider_and_what_the_user_called_it()
    {
        Assert.Equal("Claude", new Account(ProviderId.Claude).DisplayName);
        Assert.Equal("Claude · Work", new Account(ProviderId.Claude, "2", " Work ").DisplayName);
    }
}

public class AccountCredentialTests
{
    private static Credentials Open(TempFolder folder, byte[] key) =>
        new(folder.File("credentials.json"), new AesSealer(key));

    [Fact]
    public void The_list_starts_empty_and_accounts_follow_their_provider_as_they_are_added()
    {
        using var folder = new TempFolder();
        var credentials = Open(folder, RandomNumberGenerator.GetBytes(32));

        Assert.Empty(credentials.Accounts());

        var kimi = credentials.AddAccount(ProviderId.Kimi, null);
        var personal = credentials.AddAccount(ProviderId.Claude, null);
        var work = credentials.AddAccount(ProviderId.Claude, "Work");

        // A provider's first account has the plain key; the next ones are numbered.
        Assert.Equal(("kimi", "claude", "claude-2"), (kimi.Key, personal.Key, work.Key));
        Assert.Equal(["claude", "claude-2", "kimi"], credentials.Accounts().Select(a => a.Key));
        Assert.Equal("Work", credentials.FindAccount(work.Key)!.Name);
    }

    [Fact]
    public void A_sign_in_or_a_key_puts_a_first_account_in_the_list()
    {
        using var folder = new TempFolder();
        var credentials = Open(folder, RandomNumberGenerator.GetBytes(32));

        credentials.SetManualToken(ProviderId.Mistral, "key");
        credentials.SetSession(ProviderId.Claude, "access", "refresh", 0L, null, borrowed: false);
        credentials.SetManualToken(ProviderId.Kimi, "");

        Assert.Equal(["claude", "mistral"], credentials.Accounts().Select(a => a.Key));
    }

    [Fact]
    public void A_store_from_before_keeps_the_first_accounts_that_hold_something_and_drops_the_empty_ones()
    {
        using var folder = new TempFolder();
        var key = RandomNumberGenerator.GetBytes(32);
        var credentials = Open(folder, key);
        credentials.SetManualToken(ProviderId.Mistral, "key");
        credentials.SetSession(ProviderId.Claude, "access", "refresh", 0L, null, borrowed: false);
        credentials.For("2").SetManualToken(ProviderId.Claude, "work");

        // The book as it was written before the list could be emptied: no record
        // of which first accounts are in it, because every one always was.
        typeof(Credentials)
            .GetMethod("Set", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(credentials, ["accounts", """{"Next":3,"Extra":["claude-2"],"Names":{"claude":"Personal","claude-2":"Work"}}"""]);

        var reopened = Open(folder, key);
        Assert.Equal(["claude", "claude-2", "mistral"], reopened.Accounts().Select(a => a.Key));
        Assert.Equal("Personal", reopened.FindAccount("claude")!.Name);
        Assert.Equal("access", reopened.Token(ProviderId.Claude));

        // Settled once: a key added later does not bring back an empty account.
        Assert.Equal(["claude", "claude-2", "mistral"], Open(folder, key).Accounts().Select(a => a.Key));
    }

    [Fact]
    public void A_first_account_can_be_removed_and_added_again_under_its_own_key()
    {
        using var folder = new TempFolder();
        var credentials = Open(folder, RandomNumberGenerator.GetBytes(32));
        credentials.SetManualToken(ProviderId.Kimi, "kimi-key");
        credentials.SetKimiChina(true);
        credentials.RenameAccount("kimi", "Home");

        Assert.True(credentials.RemoveAccount("kimi"));
        Assert.Empty(credentials.Accounts());
        Assert.Null(credentials.Token(ProviderId.Kimi));
        Assert.False(credentials.KimiChina());
        Assert.DoesNotContain("kimi-key", File.ReadAllText(folder.File("credentials.json")));

        var again = credentials.AddAccount(ProviderId.Kimi, null);
        Assert.Equal("kimi", again.Key);
        Assert.Null(again.Name);
    }

    [Fact]
    public void Each_account_keeps_its_own_session_and_settings()
    {
        using var folder = new TempFolder();
        var key = RandomNumberGenerator.GetBytes(32);
        var credentials = Open(folder, key);
        credentials.AddAccount(ProviderId.Claude, null);
        var second = credentials.AddAccount(ProviderId.Claude, "Work");

        credentials.SetSession(ProviderId.Claude, "first-access", "first-refresh", 0L, null, borrowed: false);
        credentials.For(second.Slot).SetSession(ProviderId.Claude, "second-access", "second-refresh", 0L, null, borrowed: false);
        credentials.For(second.Slot).SetKimiChina(true);

        var reopened = Open(folder, key);
        Assert.Equal("first-access", reopened.Token(ProviderId.Claude));
        Assert.Equal("first-refresh", reopened.RefreshToken(ProviderId.Claude));
        Assert.Equal("second-access", reopened.For("2").Token(ProviderId.Claude));
        Assert.Equal("second-refresh", reopened.For("2").RefreshToken(ProviderId.Claude));
        Assert.False(reopened.KimiChina());
        Assert.True(reopened.For("2").KimiChina());

        // Signing one out leaves the other alone.
        reopened.For("2").SignOut(ProviderId.Claude);
        Assert.Equal("first-access", reopened.Token(ProviderId.Claude));
        Assert.Null(reopened.For("2").Token(ProviderId.Claude));
    }

    [Fact]
    public void A_removed_account_takes_its_tokens_with_it_and_its_slot_is_never_handed_out_again()
    {
        using var folder = new TempFolder();
        var credentials = Open(folder, RandomNumberGenerator.GetBytes(32));
        credentials.AddAccount(ProviderId.ChatGpt, null);
        var second = credentials.AddAccount(ProviderId.ChatGpt, "Work");
        credentials.For(second.Slot).SetManualToken(ProviderId.ChatGpt, "work-token");

        Assert.True(credentials.RemoveAccount(second.Key));
        Assert.Null(credentials.FindAccount(second.Key));
        Assert.Null(credentials.For(second.Slot).Token(ProviderId.ChatGpt));
        Assert.DoesNotContain("work-token", File.ReadAllText(folder.File("credentials.json")));

        var third = credentials.AddAccount(ProviderId.ChatGpt, null);
        Assert.NotEqual(second.Slot, third.Slot);
        Assert.NotEqual("", third.Slot);
    }

    [Fact]
    public void A_name_can_be_given_changed_and_taken_away_even_on_a_first_account()
    {
        using var folder = new TempFolder();
        var credentials = Open(folder, RandomNumberGenerator.GetBytes(32));
        credentials.AddAccount(ProviderId.Claude, null);

        credentials.RenameAccount("claude", "Personal");
        Assert.Equal("Claude · Personal", credentials.FindAccount("claude")!.DisplayName);

        credentials.RenameAccount("claude", "  ");
        Assert.Null(credentials.FindAccount("claude")!.Name);
    }
}

public class AccountFetchTests
{
    /// <summary>Reports the token it was handed as the plan, so a test can see which account it read.</summary>
    private sealed class EchoProvider(ProviderId id) : IUsageProvider
    {
        public ProviderId Id => id;

        public Task<ProviderUsage> FetchAsync(Credentials credentials, CancellationToken cancellation = default) =>
            Task.FromResult(credentials.Token(id) is { } token
                ? new ProviderUsage { Id = id, Plan = token, Windows = [new UsageWindow("7d", 50f)] }
                : new ProviderUsage { Id = id, State = UsageState.NotConfigured, Message = "No token" });
    }

    [Fact]
    public async Task Every_account_is_asked_with_its_own_token_and_labelled_with_its_own_name()
    {
        using var folder = new TempFolder();
        var credentials = new Credentials(folder.File("credentials.json"), new AesSealer(RandomNumberGenerator.GetBytes(32)));
        credentials.AddAccount(ProviderId.Claude, null);
        var work = credentials.AddAccount(ProviderId.Claude, "Work");
        credentials.SetManualToken(ProviderId.Claude, "personal");
        credentials.For(work.Slot).SetManualToken(ProviderId.Claude, "work");

        var fetcher = new UsageFetcher([new EchoProvider(ProviderId.Claude)], (_, _, _) => Task.FromResult(false));
        var snapshot = await fetcher.FetchAsync(credentials, previous: null);

        Assert.Equal(["claude", "claude-2"], snapshot.Usages.Select(u => u.AccountKey));
        Assert.Equal(["personal", "work"], snapshot.Usages.Select(u => u.Plan));
        Assert.Equal("Claude · Work", snapshot.Usages[1].DisplayName);

        // Both count: two subscriptions, not one.
        Assert.Equal(2, CombinedUsage.Of(snapshot).Providers);

        var onlyWork = await fetcher.FetchAsync(credentials, snapshot, only: ["claude-2"]);
        Assert.Equal(["claude-2"], onlyWork.Usages.Select(u => u.AccountKey));
    }

    [Fact]
    public void A_reading_standing_in_for_a_failed_fetch_takes_the_accounts_current_name()
    {
        var before = new ProviderUsage
        {
            Id = ProviderId.Claude, Slot = "2", AccountName = null, Windows = [new UsageWindow("7d", 40f)],
        };
        var failed = new ProviderUsage
        {
            Id = ProviderId.Claude, Slot = "2", AccountName = "Work",
            State = UsageState.Error, Transient = true, Message = "Rate limited",
        };

        var shown = UsageFetcher.KeepLastGood(failed, before);

        Assert.True(shown.Stale);
        Assert.Equal(40f, shown.Windows[0].RemainingPercent);
        Assert.Equal("Work", shown.AccountName);
        Assert.Equal("Rate limited", shown.Message);
    }

    [Fact]
    public void A_cached_reading_remembers_its_account_and_an_old_cache_reads_as_the_first()
    {
        var snapshot = new Snapshot(
            [new ProviderUsage { Id = ProviderId.Claude, Slot = "2", AccountName = "Work", Windows = [new UsageWindow("7d", 50f)] }],
            DateTimeOffset.Parse("2026-11-12T10:00:00Z"));

        var decoded = SnapshotCodec.Decode(SnapshotCodec.Encode(snapshot))!;
        Assert.Equal("claude-2", decoded.Usages[0].AccountKey);
        Assert.Equal("Work", decoded.Usages[0].AccountName);

        var old = """{"FetchedAt":"2026-11-12T10:00:00Z","Usages":[{"Id":"claude","State":"Ok","Windows":[],"Message":null,"FetchedAt":"2026-11-12T10:00:00Z","Plan":null,"Transient":false,"Stale":false}]}""";
        Assert.Equal("claude", SnapshotCodec.Decode(old)!.Usages[0].AccountKey);
    }

    [Fact]
    public void A_server_reading_says_which_account_it_is()
    {
        var state = ServerState.Parse(
            """
            { "serverTime": 1, "combined": [], "series": {},
              "snapshot": { "fetchedAt": "2026-11-12T10:00:00.000Z", "usages": [
                { "id": "claude", "account": "claude", "state": "OK", "windows": [] },
                { "id": "claude", "account": "claude-2", "accountName": "Work", "state": "OK", "windows": [] } ] } }
            """)!;

        Assert.Equal(["claude", "claude-2"], state.Snapshot.Usages.Select(u => u.AccountKey));
        Assert.Equal("Claude · Work", state.Snapshot.Usages[1].DisplayName);
    }
}

public class AccountBadgeTests
{
    [Theory]
    [InlineData("RZ", "RZ")]
    [InlineData("Work", "W")]
    [InlineData("Claude Pro", "CP")]
    [InlineData("work-laptop", "wl")]
    [InlineData("#2", "#2")]
    [InlineData("12", "12")]
    [InlineData("x", "x")]
    public void A_badge_shows_up_to_two_letters_as_written(string label, string badge) =>
        Assert.Equal(badge, Account.Initials(label));

    [Fact]
    public void A_label_is_the_name_or_the_slot_and_none_for_a_providers_only_account()
    {
        Assert.Equal("Work", Account.LabelOf(" Work ", "2"));
        Assert.Equal("#2", Account.LabelOf(null, "2"));
        Assert.Null(Account.LabelOf(null, ""));
    }
}
