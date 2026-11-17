using System.Security.Cryptography;
using AiUsage.Auth;
using AiUsage.Providers;

namespace AiUsage.Tests;

/// <summary>A scratch folder that cleans up after the test that asked for it.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "ai-usage-tests",
        Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Write(string contents, params string[] parts)
    {
        var path = File(parts);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }
}

public class SealerTests
{
    [Fact]
    public void A_sealed_value_opens_again_and_never_looks_the_same_twice()
    {
        var sealer = new AesSealer(RandomNumberGenerator.GetBytes(32));

        var first = sealer.Seal("sk-secret");
        var second = sealer.Seal("sk-secret");

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("sk-secret", first);
        Assert.Equal("sk-secret", sealer.Unseal(first));
        Assert.Equal("sk-secret", sealer.Unseal(second));
    }

    [Fact]
    public void The_wrong_key_or_a_changed_byte_opens_nothing()
    {
        var sealer = new AesSealer(RandomNumberGenerator.GetBytes(32));
        var other = new AesSealer(RandomNumberGenerator.GetBytes(32));
        var value = sealer.Seal("sk-secret");

        var bytes = Convert.FromBase64String(value);
        bytes[^1] ^= 0x01;

        Assert.Null(other.Unseal(value));
        Assert.Null(sealer.Unseal(Convert.ToBase64String(bytes)));
        Assert.Null(sealer.Unseal("not base64 at all"));
    }

    [Fact]
    public void The_same_passphrase_gives_the_same_key_after_a_restart()
    {
        var value = AesSealer.FromPassphrase("correct horse").Seal("sk-secret");

        Assert.Equal("sk-secret", AesSealer.FromPassphrase("correct horse").Unseal(value));
        Assert.Null(AesSealer.FromPassphrase("wrong horse").Unseal(value));
    }
}

public class CredentialsTests
{
    private static readonly AesSealer Sealer = new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Tokens_survive_a_restart_and_are_not_on_disk_in_the_clear()
    {
        using var folder = new TempFolder();
        var path = folder.File("credentials.json");

        new Credentials(path, Sealer).SetSession(
            ProviderId.Claude, "access-1", "refresh-1", 1234L, null, borrowed: false);

        var reopened = new Credentials(path, Sealer);
        Assert.Equal("access-1", reopened.Token(ProviderId.Claude));
        Assert.Equal("refresh-1", reopened.RefreshToken(ProviderId.Claude));
        Assert.Equal(1234L, reopened.ExpiresAt(ProviderId.Claude));
        Assert.True(reopened.IsSignedIn(ProviderId.Claude));

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("access-1", raw);
        Assert.DoesNotContain("refresh-1", raw);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_token_pasted_by_hand_ends_the_session_it_replaces()
    {
        using var folder = new TempFolder();
        var credentials = new Credentials(folder.File("credentials.json"), Sealer);
        credentials.SetSession(ProviderId.ChatGpt, "access-1", "refresh-1", 1234L, "acct", borrowed: false);

        credentials.SetManualToken(ProviderId.ChatGpt, "  pasted  ");

        // The old refresh token would otherwise renew the old account's token
        // straight back over the pasted one.
        Assert.Equal("pasted", credentials.Token(ProviderId.ChatGpt));
        Assert.Null(credentials.RefreshToken(ProviderId.ChatGpt));
        Assert.Equal(0L, credentials.ExpiresAt(ProviderId.ChatGpt));
        Assert.Equal("acct", credentials.AccountId(ProviderId.ChatGpt));
    }

    [Fact]
    public void Clearing_the_field_signs_the_provider_out()
    {
        using var folder = new TempFolder();
        var credentials = new Credentials(folder.File("credentials.json"), Sealer);
        credentials.SetSession(ProviderId.ChatGpt, "access-1", "refresh-1", 1234L, "acct", borrowed: false);

        credentials.SetManualToken(ProviderId.ChatGpt, "");

        Assert.Null(credentials.Token(ProviderId.ChatGpt));
        Assert.Null(credentials.RefreshToken(ProviderId.ChatGpt));
        Assert.Null(credentials.AccountId(ProviderId.ChatGpt));
    }

    [Fact]
    public void A_file_sealed_under_another_key_reads_as_empty_rather_than_failing()
    {
        using var folder = new TempFolder();
        var path = folder.File("credentials.json");
        new Credentials(path, Sealer).SetToken(ProviderId.Kimi, "key");

        var stranger = new Credentials(path, new AesSealer(RandomNumberGenerator.GetBytes(32)));

        Assert.Null(stranger.Token(ProviderId.Kimi));
    }
}

public class CliImportTests
{
    private static readonly AesSealer Sealer = new(RandomNumberGenerator.GetBytes(32));

    private static void WriteClaude(TempFolder home, string access, string refresh) => home.Write(
        $$"""
        {"claudeAiOauth": {"accessToken": "{{access}}", "refreshToken": "{{refresh}}", "expiresAt": 1800000000000} }
        """,
        ".claude", ".credentials.json");

    private static void WriteCodex(TempFolder home, string access, string refresh) => home.Write(
        $$"""
        {"OPENAI_API_KEY": null,
         "tokens": {"access_token": "{{access}}", "refresh_token": "{{refresh}}", "account_id": "acct-1"} }
        """,
        ".codex", "auth.json");

    private static Credentials NewCredentials(TempFolder folder) =>
        new(folder.File("app", "credentials.json"), Sealer);

    [Fact]
    public void A_cli_session_is_borrowed_without_its_refresh_token()
    {
        using var home = new TempFolder();
        WriteClaude(home, "claude-access", "claude-refresh");
        WriteCodex(home, "codex-access", "codex-refresh");
        var credentials = NewCredentials(home);

        var imported = CliImport.Run(credentials, home.Path);

        Assert.Equal([ProviderId.Claude, ProviderId.ChatGpt], imported);

        // The refresh token is single-use. Holding a copy would mean that
        // whichever of us renewed first signed the other out.
        Assert.Equal("claude-access", credentials.Token(ProviderId.Claude));
        Assert.Null(credentials.RefreshToken(ProviderId.Claude));
        Assert.True(credentials.Borrowed(ProviderId.Claude));
        Assert.Equal(1_800_000_000_000L, credentials.ExpiresAt(ProviderId.Claude));

        Assert.Equal("codex-access", credentials.Token(ProviderId.ChatGpt));
        Assert.Null(credentials.RefreshToken(ProviderId.ChatGpt));
        Assert.Equal("acct-1", credentials.AccountId(ProviderId.ChatGpt));
    }

    [Fact]
    public void A_borrowed_token_follows_the_cli_when_it_renews()
    {
        using var home = new TempFolder();
        WriteClaude(home, "access-1", "refresh-1");
        var credentials = NewCredentials(home);
        CliImport.Run(credentials, home.Path);

        WriteClaude(home, "access-2", "refresh-2");
        CliImport.Sync(credentials, home.Path);

        Assert.Equal("access-2", credentials.Token(ProviderId.Claude));
        Assert.Null(credentials.RefreshToken(ProviderId.Claude));
    }

    [Fact]
    public void A_session_of_the_apps_own_is_never_traded_for_a_loan()
    {
        using var home = new TempFolder();
        WriteClaude(home, "cli-access", "cli-refresh");
        var credentials = NewCredentials(home);
        credentials.SetSession(ProviderId.Claude, "own-access", "own-refresh", 0L, null, borrowed: false);

        var imported = CliImport.Run(credentials, home.Path);
        CliImport.Sync(credentials, home.Path);

        Assert.Empty(imported);
        Assert.Equal("own-access", credentials.Token(ProviderId.Claude));
        Assert.Equal("own-refresh", credentials.RefreshToken(ProviderId.Claude));
        Assert.False(credentials.Borrowed(ProviderId.Claude));
    }

    [Fact]
    public void A_refresh_token_copied_by_an_earlier_build_is_handed_back()
    {
        using var home = new TempFolder();
        WriteCodex(home, "cli-access", "shared-refresh");
        var credentials = NewCredentials(home);

        // What the first version of the import left behind: the CLI's own
        // refresh token, not yet spent by either side.
        credentials.SetSession(ProviderId.ChatGpt, "old-access", "shared-refresh", 0L, "acct-1", borrowed: false);

        CliImport.Sync(credentials, home.Path);

        Assert.Null(credentials.RefreshToken(ProviderId.ChatGpt));
        Assert.True(credentials.Borrowed(ProviderId.ChatGpt));
        Assert.Equal("cli-access", credentials.Token(ProviderId.ChatGpt));
    }

    [Fact]
    public void A_missing_or_unreadable_cli_file_changes_nothing()
    {
        using var home = new TempFolder();
        home.Write("[1, 2, 3]", ".claude", ".credentials.json");
        home.Write("not json", ".codex", "auth.json");
        var credentials = NewCredentials(home);

        Assert.Empty(CliImport.Run(credentials, home.Path));
        CliImport.Sync(credentials, home.Path);

        Assert.Null(credentials.Token(ProviderId.Claude));
        Assert.Null(credentials.Token(ProviderId.ChatGpt));
    }
}

public class UsageFetcherTests
{
    private static readonly AesSealer Sealer = new(RandomNumberGenerator.GetBytes(32));

    /// <summary>A provider that answers from a script, one entry per call.</summary>
    private sealed class Scripted(ProviderId id, params Func<ProviderUsage>[] answers) : IUsageProvider
    {
        public ProviderId Id => id;

        public int Calls { get; private set; }

        public Task<ProviderUsage> FetchAsync(Credentials credentials, CancellationToken cancellation = default)
        {
            var answer = answers[Math.Min(Calls, answers.Length - 1)];
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private static ProviderUsage Ok(ProviderId id, float percent) => new()
    {
        Id = id,
        Windows = [new UsageWindow("7d", percent)],
    };

    private static Credentials NewCredentials(TempFolder folder) =>
        new(folder.File("credentials.json"), Sealer);

    [Fact]
    public async Task A_rejected_token_is_renewed_and_tried_once_more()
    {
        using var folder = new TempFolder();
        var provider = new Scripted(
            ProviderId.ChatGpt,
            () => ProviderHelpers.HttpError(ProviderId.ChatGpt, 401),
            () => Ok(ProviderId.ChatGpt, 42f));
        var renewals = 0;
        var fetcher = new UsageFetcher([provider], (_, _, _) =>
        {
            renewals++;
            return Task.FromResult(true);
        });

        var snapshot = await fetcher.FetchAsync(NewCredentials(folder), previous: null);

        var usage = Assert.Single(snapshot.Usages);
        Assert.Equal(UsageState.Ok, usage.State);
        Assert.Equal(42f, usage.Windows[0].RemainingPercent);
        Assert.Equal(1, renewals);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task A_token_that_cannot_be_renewed_is_reported_once_not_retried()
    {
        using var folder = new TempFolder();
        var provider = new Scripted(ProviderId.Claude, () => ProviderHelpers.HttpError(ProviderId.Claude, 401));
        var fetcher = new UsageFetcher([provider], (_, _, _) => Task.FromResult(false));

        var snapshot = await fetcher.FetchAsync(NewCredentials(folder), previous: null);

        Assert.Equal("Token rejected", snapshot.Usages[0].Message);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task A_borrowed_token_that_expired_says_where_to_go()
    {
        using var folder = new TempFolder();
        var credentials = NewCredentials(folder);
        credentials.SetSession(ProviderId.Claude, "lent", null, 0L, null, borrowed: true);
        var provider = new Scripted(ProviderId.Claude, () => ProviderHelpers.HttpError(ProviderId.Claude, 401));
        var fetcher = new UsageFetcher([provider], (_, _, _) => Task.FromResult(false));

        var snapshot = await fetcher.FetchAsync(credentials, previous: null);

        Assert.Equal(UsageState.Error, snapshot.Usages[0].State);
        Assert.Contains("sign in", snapshot.Usages[0].Message);
    }

    [Fact]
    public async Task A_rate_limit_keeps_the_last_reading_and_says_so()
    {
        using var folder = new TempFolder();
        var previous = new Snapshot([Ok(ProviderId.Claude, 61f)], Clock.T0);
        var provider = new Scripted(ProviderId.Claude, () => ProviderHelpers.HttpError(ProviderId.Claude, 429));
        var fetcher = new UsageFetcher([provider], (_, _, _) => Task.FromResult(false));

        var snapshot = await fetcher.FetchAsync(NewCredentials(folder), previous);

        var usage = snapshot.Usages[0];
        Assert.Equal(UsageState.Ok, usage.State);
        Assert.True(usage.Stale);
        Assert.Equal("Rate limited", usage.Message);
        Assert.Equal(61f, usage.Windows[0].RemainingPercent);
    }

    [Fact]
    public async Task A_provider_that_throws_does_not_take_the_others_down()
    {
        using var folder = new TempFolder();
        var broken = new Scripted(ProviderId.Kimi, () => throw new HttpRequestException("boom"));
        var fine = new Scripted(ProviderId.Mistral, () => Ok(ProviderId.Mistral, 80f));
        var fetcher = new UsageFetcher([broken, fine], (_, _, _) => Task.FromResult(false));

        var snapshot = await fetcher.FetchAsync(NewCredentials(folder), previous: null);

        var kimi = snapshot.Usages.Single(u => u.Id == ProviderId.Kimi);
        Assert.Equal(UsageState.Error, kimi.State);
        Assert.True(kimi.Transient);
        Assert.Equal(UsageState.Ok, snapshot.Usages.Single(u => u.Id == ProviderId.Mistral).State);
    }
}
