using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiUsage.Auth;
using AiUsage.Data;

namespace AiUsage.Server;

/// <summary>
/// The HTTP surface. Reading is one call that returns everything a client draws;
/// the rest is for putting credentials in and taking them out.
/// </summary>
public static class Api
{
    /// <summary>How far back a client may ask for, and how much it gets if it does not say.</summary>
    private const int DefaultDays = 35;
    private const int MaxDays = 400;
    private const int DefaultPoints = 600;
    private const int MaxPoints = 5000;

    public sealed record CredentialBody(
        string? Token,
        string? RefreshToken,
        long? ExpiresAt,
        string? AccountId,
        bool? KimiChina = null,
        double? MistralBudget = null,
        double? MistralSpent = null);

    public sealed record PasteBody(string? Input);

    public sealed record AccountBody(string? Provider, string? Name);

    public sealed record PriceBody(double? Monthly);

    public static void Map(WebApplication app, string apiToken)
    {
        // Open, and says nothing: for a supervisor to tell the process is up.
        app.MapGet("/healthz", () => Results.Text("ok"));

        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
            Authorized(context.HttpContext.Request, apiToken)
                ? await next(context)
                : Results.Json(new { error = "Missing or wrong bearer token" }, statusCode: 401));

        // Everything a client needs to draw every screen, in one round trip.
        // `since` (epoch millis) is for a client that already has the history up
        // to its last call and only wants what happened after it.
        api.MapGet("/state", (Poller poller, Store store, int? days, int? points, long? since) =>
        {
            var from = Since(DateTimeOffset.UtcNow, days, since);
            var max = Math.Clamp(points ?? DefaultPoints, 4, MaxPoints);

            var series = new JsonObject();
            foreach (var (provider, window) in store.SeriesKeys(from))
            {
                var readings = store.Series(provider, window, from)
                    .Select(r => new UsagePoint(r.At, r.Percent))
                    .ToList();
                series[Store.SeriesKey(provider, window)] = Wire.Series(Wire.Downsample(readings, max));
            }

            return Results.Json(new JsonObject
            {
                ["serverTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["snapshot"] = poller.Current is { } snapshot ? Wire.Snapshot(snapshot) : null,
                ["combined"] = Wire.Series(Wire.Downsample(store.Combined(from), max)),
                ["series"] = series,
            });
        });

        api.MapGet("/snapshot", (Poller poller) => poller.Current is { } snapshot
            ? Results.Json(Wire.Snapshot(snapshot))
            : Results.Json(new { error = "No reading yet" }, statusCode: 503));

        api.MapPost("/refresh", async (Poller poller, CancellationToken cancellation) =>
            await poller.RefreshNowAsync(cancellation) is { } snapshot
                ? Results.Json(Wire.Snapshot(snapshot))
                : Results.Json(new { error = "No reading yet" }, statusCode: 503));

        // Every account and whether it has something stored. Never the tokens
        // themselves: nothing a client does needs them back.
        api.MapGet("/credentials", (Credentials credentials) => Results.Json(
            credentials.Accounts().Select(account => Describe(account, credentials)).ToList()));

        // What the plans have been worth: every finished cycle of every long
        // window, and what each account costs a month.
        api.MapGet("/plans", (Store store, Poller poller) =>
            Results.Json(Wire.Plans(Plans.Of(store, poller.Current, DateTimeOffset.UtcNow))));

        // What an account actually costs. Null forgets it and falls back to the
        // guess for its plan, which is only ever a starting point.
        api.MapPut("/plans/price/{key}", (string key, PriceBody body, Store store) =>
        {
            if (body.Monthly is { } monthly && (monthly < 0 || monthly > 10_000))
            {
                return Results.Json(new { error = "That is not a monthly price" }, statusCode: 400);
            }

            store.SetPrice(key.Trim().ToLowerInvariant(), body.Monthly);
            return Results.NoContent();
        });

        // An API key, or a whole session handed over by an app that signed in.
        // The app must forget the session once this succeeds: see SignInService.
        api.MapPut("/credentials/{key}", (string key, CredentialBody body, Credentials credentials, Poller poller) =>
        {
            if (Find(key, credentials) is not { } account) return Unknown(key);
            if (string.IsNullOrWhiteSpace(body.Token)) return Results.Json(new { error = "token is required" }, statusCode: 400);

            var id = account.Provider;
            var view = credentials.For(account.Slot);
            if (string.IsNullOrEmpty(body.RefreshToken))
            {
                view.SetManualToken(id, body.Token);
                if (body.AccountId is { Length: > 0 } accountId) view.SetAccountId(id, accountId);
            }
            else
            {
                view.SetSession(id, body.Token, body.RefreshToken, body.ExpiresAt ?? 0L, body.AccountId, borrowed: false);
            }

            // The settings that travel with a key, so a hand-over is complete.
            if (id == ProviderId.Kimi && body.KimiChina is { } china) view.SetKimiChina(china);
            if (id == ProviderId.Mistral)
            {
                if (body.MistralBudget is { } budget) view.SetMistralBudget(budget);
                if (body.MistralSpent is { } spent) view.SetMistralSpent(spent);
            }

            TokenRefresher.ForgetRejections();
            poller.Hurry(account.Key);
            return Results.NoContent();
        });

        api.MapDelete("/credentials/{key}", (string key, Credentials credentials, Poller poller) =>
        {
            if (Find(key, credentials) is not { } account) return Unknown(key);
            credentials.For(account.Slot).SignOut(account.Provider);
            poller.Hurry(account.Key);
            return Results.NoContent();
        });

        // Another account of a provider: a second Claude subscription, a work
        // ChatGPT. It starts empty; sign it in or give it a key next.
        api.MapPost("/accounts", (AccountBody body, Credentials credentials, Poller poller) =>
        {
            if (ProviderCatalog.FromKey(body.Provider ?? "") is not { } info) return Unknown(body.Provider ?? "");

            var account = credentials.AddAccount(info.Id, body.Name);
            poller.Hurry(account.Key);
            return Results.Json(Describe(account, credentials), statusCode: 201);
        });

        api.MapPatch("/accounts/{key}", (string key, AccountBody body, Credentials credentials, Poller poller) =>
        {
            if (Find(key, credentials) is not { } account) return Unknown(key);
            credentials.RenameAccount(account.Key, body.Name);

            // The name travels with the reading, so the next one carries it.
            poller.Hurry(account.Key);
            return Results.Json(Describe(credentials.FindAccount(account.Key)!, credentials));
        });

        // Only an added account can go; the first of each provider is signed out instead.
        api.MapDelete("/accounts/{key}", async (string key, Credentials credentials, Poller poller, SignInService signIn, CancellationToken cancellation) =>
        {
            if (Find(key, credentials) is not { } account) return Unknown(key);
            if (account.IsDefault)
            {
                return Results.Json(new { error = "The first account of a provider can be signed out, not removed." }, statusCode: 400);
            }

            credentials.RemoveAccount(account.Key);
            signIn.Forget(account);
            await poller.ForgetAsync(account.Key, cancellation);
            return Results.NoContent();
        });

        api.MapPost("/signin/{key}/start", async (string key, Credentials credentials, SignInService signIn, CancellationToken cancellation) =>
        {
            if (Find(key, credentials) is not { } account) return Unknown(key);
            try
            {
                var step = await signIn.StartAsync(account, cancellation);
                return Results.Json(new { kind = step.Kind, url = step.Url, userCode = step.UserCode, instructions = step.Instructions });
            }
            catch (Exception exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: 400);
            }
        });

        api.MapPost("/signin/{key}/complete", async (string key, PasteBody body, Credentials credentials, SignInService signIn, CancellationToken cancellation) =>
        {
            if (Find(key, credentials) is not { } account) return Unknown(key);
            try
            {
                await signIn.CompleteAsync(account, body.Input ?? "", cancellation);
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: 400);
            }
        });

        api.MapGet("/signin/{key}/status", (string key, Credentials credentials, SignInService signIn) =>
            Find(key, credentials) is { } account
                ? Results.Json(new { status = signIn.Status(account) ?? "none" })
                : Unknown(key));
    }

    /// <summary>What a client is told about an account: never a token.</summary>
    private static object Describe(Account account, Credentials credentials)
    {
        var view = credentials.For(account.Slot);
        return new
        {
            key = account.Key,
            provider = ProviderCatalog.Of(account.Provider).Key,
            name = account.Name,
            displayName = account.DisplayName,
            removable = !account.IsDefault,
            configured = view.Token(account.Provider) != null,
            renews = view.IsSignedIn(account.Provider),
        };
    }

    /// <summary>Where a /state answer starts: an explicit instant if given, else so many days back.</summary>
    internal static DateTimeOffset Since(DateTimeOffset now, int? days, long? since)
    {
        var oldest = now.AddDays(-MaxDays);
        if (since is not { } millis) return now.AddDays(-Math.Clamp(days ?? DefaultDays, 1, MaxDays));

        // Clamped rather than trusted: a nonsense value must not throw.
        var floor = oldest.ToUnixTimeMilliseconds();
        return DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(millis, floor, now.ToUnixTimeMilliseconds()));
    }

    /// <summary>"claude" or "claude-2", if it is an account that exists.</summary>
    private static Account? Find(string key, Credentials credentials) =>
        credentials.FindAccount(key.Trim().ToLowerInvariant());

    private static IResult Unknown(string key) =>
        Results.Json(new { error = $"No account '{key}'" }, statusCode: 404);

    /// <summary>Compared in constant time, so the token cannot be guessed a character at a time.</summary>
    internal static bool Authorized(HttpRequest request, string apiToken)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;

        var presented = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(apiToken);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
