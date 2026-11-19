using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiUsage.Net;

namespace AiUsage.Data;

/// <summary>Everything one call to the server returns: the reading and the history behind it.</summary>
public sealed record ServerState(
    Snapshot Snapshot,
    DateTimeOffset ServerTime,
    IReadOnlyList<UsagePoint> Combined,
    IReadOnlyDictionary<string, IReadOnlyList<UsagePoint>> Series)
{
    /// <summary>
    /// Reads a <c>/api/state</c> answer. The shape is the Android app's own cache
    /// (camelCase, states in capitals), which is why this is not SnapshotCodec.
    /// Null when the server has not completed a first poll yet.
    /// </summary>
    public static ServerState? Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Not a JSON object");
        if (root["snapshot"] is not JsonObject snapshot) return null;

        var usages = new List<ProviderUsage>();
        foreach (var node in snapshot["usages"] as JsonArray ?? [])
        {
            if (node is not JsonObject usage) continue;
            if (ProviderCatalog.FromKey(Text(usage["id"]) ?? "") is not { } info) continue;

            var windows = new List<UsageWindow>();
            foreach (var entry in usage["windows"] as JsonArray ?? [])
            {
                if (entry is not JsonObject window) continue;
                windows.Add(new UsageWindow(
                    Text(window["label"]) ?? "",
                    Number(window["remainingPercent"]) is { } percent ? (float)percent : null,
                    Instant(window["resetAt"]),
                    Text(window["detail"]),
                    Flag(window["countsTowardCombined"], fallback: true)));
            }

            // "claude-2" for a second account; older servers send no account at all.
            var slot = Account.Parse(Text(usage["account"]))?.Slot ?? "";

            usages.Add(new ProviderUsage
            {
                Id = info.Id,
                Slot = slot,
                AccountName = Text(usage["accountName"]),
                State = Text(usage["state"]) switch
                {
                    "OK" => UsageState.Ok,
                    "NOT_CONFIGURED" => UsageState.NotConfigured,
                    _ => UsageState.Error,
                },
                Windows = windows,
                Message = Text(usage["message"]),
                Plan = Text(usage["plan"]),
                FetchedAt = Instant(usage["fetchedAt"]) ?? DateTimeOffset.UtcNow,
                Stale = Flag(usage["stale"], fallback: false),
                Transient = Flag(usage["transient"], fallback: false),
            });
        }

        var series = new Dictionary<string, IReadOnlyList<UsagePoint>>(StringComparer.Ordinal);
        foreach (var (key, value) in root["series"] as JsonObject ?? [])
        {
            series[key] = Points(value);
        }

        return new ServerState(
            new Snapshot(usages, Instant(snapshot["fetchedAt"]) ?? DateTimeOffset.UtcNow),
            Number(root["serverTime"]) is { } millis
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)millis)
                : DateTimeOffset.UtcNow,
            Points(root["combined"]),
            series);
    }

    /// <summary>
    /// The same state with every moment in it moved by <paramref name="offset"/>:
    /// readings, resets, history. Moved by now minus <see cref="ServerTime"/>, a
    /// sample saved weeks ago looks as if it had just been taken.
    /// </summary>
    public ServerState ShiftedBy(TimeSpan offset)
    {
        List<UsagePoint> Move(IReadOnlyList<UsagePoint> points) =>
            points.Select(p => p with { At = p.At + offset }).ToList();

        var usages = Snapshot.Usages.Select(u => u with
        {
            FetchedAt = u.FetchedAt + offset,
            Windows = u.Windows.Select(w => w with { ResetAt = w.ResetAt + offset }).ToList(),
        }).ToList();

        return new ServerState(
            new Snapshot(usages, Snapshot.FetchedAt + offset),
            ServerTime + offset,
            Move(Combined),
            Series.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<UsagePoint>)Move(pair.Value), StringComparer.Ordinal));
    }

    private static List<UsagePoint> Points(JsonNode? node)
    {
        var points = new List<UsagePoint>();
        foreach (var entry in node as JsonArray ?? [])
        {
            if (entry is not JsonArray { Count: >= 2 } pair) continue;
            if (Number(pair[0]) is not { } at || Number(pair[1]) is not { } percent) continue;
            points.Add(new UsagePoint(DateTimeOffset.FromUnixTimeMilliseconds((long)at), (float)percent));
        }
        return points;
    }

    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    internal static bool Flag(JsonNode? node, bool fallback) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static DateTimeOffset? Instant(JsonNode? node) =>
        Text(node) is { } text && DateTimeOffset.TryParse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var at)
            ? at
            : null;
}

/// <summary>A failure talking to the server, worded for the status line.</summary>
public sealed class ServerException(string message, bool needsUser = false) : Exception(message)
{
    /// <summary>True when waiting will not help: the token is wrong, not the network.</summary>
    public bool NeedsUser { get; } = needsUser;
}

/// <summary>
/// One account the server follows, as the server describes it. It says whether
/// something is stored, never what: a client has no use for the token itself.
/// </summary>
public sealed record ServerAccount(
    string Key,
    string Provider,
    string? Name,
    string DisplayName,
    bool Removable,
    bool Configured,
    bool Renews)
{
    public static ServerAccount Parse(JsonObject node) => new(
        ServerState.Text(node["key"]) ?? "",
        ServerState.Text(node["provider"]) ?? "",
        ServerState.Text(node["name"]),
        ServerState.Text(node["displayName"]) ?? ServerState.Text(node["key"]) ?? "",
        ServerState.Flag(node["removable"], false),
        ServerState.Flag(node["configured"], false),
        ServerState.Flag(node["renews"], false));
}

/// <summary>
/// What the user has to do next to finish a sign-in the server started.
/// "code": approve, paste the code the page shows. "redirect": approve, paste
/// the address the browser lands on. "device": type the code into the page.
/// </summary>
public sealed record ServerSignInStep(string Kind, string Url, string? UserCode, string Instructions)
{
    public static ServerSignInStep Parse(JsonObject node) => new(
        ServerState.Text(node["kind"]) ?? "code",
        ServerState.Text(node["url"]) ?? "",
        ServerState.Text(node["userCode"]),
        ServerState.Text(node["instructions"]) ?? "");
}

/// <summary>
/// The app's side of the server's API. With a server set, this replaces every
/// provider call the app would otherwise make.
/// </summary>
public static class ServerClient
{
    /// <summary>What a first call asks for: everything the charts can show.</summary>
    private const string FullQuery = "days=35&points=1500";

    /// <summary>
    /// "host:8377" or "http://host:8377/" both become "http://host:8377". Null
    /// when the text is not an address at all.
    /// </summary>
    public static string? NormalizeUrl(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "http://" + trimmed;
        trimmed = trimmed.TrimEnd('/');

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0
            ? trimmed
            : null;
    }

    /// <summary>
    /// The reading and its history. <paramref name="since"/> asks only for the
    /// readings after that moment; <paramref name="force"/> has the server ask
    /// every provider first, which it will not do more than twice a minute.
    /// </summary>
    public static async Task<ServerState> FetchAsync(
        string url,
        string? token,
        DateTimeOffset? since = null,
        bool force = false,
        CancellationToken cancellation = default)
    {
        if (force)
        {
            // Best effort: if the forced poll fails, the reading the server
            // already has is still worth showing.
            try
            {
                await Http.PostJsonAsync($"{url}/api/refresh", Headers(token), "{}", cancellation);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
            }
        }

        var query = since is { } from ? $"since={from.ToUnixTimeMilliseconds()}&points=1500" : FullQuery;
        var response = await SendAsync(() => Http.GetAsync($"{url}/api/state?{query}", Headers(token), cancellation));

        try
        {
            return ServerState.Parse(response.Body)
                ?? throw new ServerException("Server has no reading yet");
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new ServerException("Server sent something unreadable");
        }
    }

    /// <summary>
    /// Gives the server a provider's credentials. For Claude and ChatGPT that is
    /// the whole session, refresh token included, and the caller must forget its
    /// own copy once this returns: the refresh token is single-use.
    /// </summary>
    public static async Task HandOverAsync(
        string url,
        string? token,
        ProviderId id,
        Credentials credentials,
        CancellationToken cancellation = default)
    {
        var body = new JsonObject
        {
            ["token"] = credentials.Token(id),
            ["refreshToken"] = credentials.RefreshToken(id),
            ["expiresAt"] = credentials.ExpiresAt(id),
            ["accountId"] = credentials.AccountId(id),
        };
        if (id == ProviderId.Kimi) body["kimiChina"] = credentials.KimiChina();
        if (id == ProviderId.Mistral)
        {
            body["mistralBudget"] = credentials.MistralBudget();
            body["mistralSpent"] = credentials.MistralSpent();
        }

        var key = ProviderCatalog.Of(id).Key;
        await SendAsync(() => Http.PutJsonAsync(
            $"{url}/api/credentials/{key}",
            Headers(token),
            body.ToJsonString(),
            cancellation));
    }

    /// <summary>
    /// The accounts the server follows, in its own order. Each says whether it
    /// has something stored and whether that renews itself; never the token.
    /// </summary>
    public static async Task<IReadOnlyList<ServerAccount>> AccountsAsync(
        string url,
        string? token,
        CancellationToken cancellation = default)
    {
        var response = await SendAsync(() => Http.GetAsync($"{url}/api/credentials", Headers(token), cancellation));
        return Parsed(() => ParseAccounts(response.Body));
    }

    internal static IReadOnlyList<ServerAccount> ParseAccounts(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray array) throw new FormatException("Not a JSON array");
        return array.OfType<JsonObject>().Select(ServerAccount.Parse).ToList();
    }

    /// <summary>Another account of a provider: a second Claude subscription, a work ChatGPT.</summary>
    public static async Task<ServerAccount> AddAccountAsync(
        string url,
        string? token,
        string provider,
        string? name,
        CancellationToken cancellation = default)
    {
        var body = new JsonObject
        {
            ["provider"] = provider,
            ["name"] = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
        };
        var response = await SendAsync(() => Http.PostJsonAsync(
            $"{url}/api/accounts", Headers(token), body.ToJsonString(), cancellation));
        return Parsed(() => ParseAccount(response.Body));
    }

    public static Task RenameAccountAsync(
        string url,
        string? token,
        string key,
        string? name,
        CancellationToken cancellation = default)
    {
        var body = new JsonObject { ["name"] = string.IsNullOrWhiteSpace(name) ? null : name.Trim() };
        return SendAsync(() => Http.PatchJsonAsync(
            $"{url}/api/accounts/{Escape(key)}", Headers(token), body.ToJsonString(), cancellation));
    }

    /// <summary>Only an added account can go; the first of each provider is signed out instead.</summary>
    public static Task RemoveAccountAsync(
        string url,
        string? token,
        string key,
        CancellationToken cancellation = default) =>
        SendAsync(() => Http.DeleteAsync($"{url}/api/accounts/{Escape(key)}", Headers(token), cancellation));

    public static Task SignOutAsync(
        string url,
        string? token,
        string key,
        CancellationToken cancellation = default) =>
        SendAsync(() => Http.DeleteAsync($"{url}/api/credentials/{Escape(key)}", Headers(token), cancellation));

    /// <summary>
    /// A key for an account the server follows. The settings that travel with a
    /// key go along, so a Kimi region or a Mistral budget is not lost by moving
    /// the account to the server.
    /// </summary>
    public static Task SetKeyAsync(
        string url,
        string? token,
        string key,
        string value,
        bool? kimiChina = null,
        double? mistralBudget = null,
        double? mistralSpent = null,
        CancellationToken cancellation = default)
    {
        var body = new JsonObject { ["token"] = value };
        if (kimiChina is { } china) body["kimiChina"] = china;
        if (mistralBudget is { } budget) body["mistralBudget"] = budget;
        if (mistralSpent is { } spent) body["mistralSpent"] = spent;

        return SendAsync(() => Http.PutJsonAsync(
            $"{url}/api/credentials/{Escape(key)}", Headers(token), body.ToJsonString(), cancellation));
    }

    public static async Task<ServerSignInStep> StartSignInAsync(
        string url,
        string? token,
        string key,
        CancellationToken cancellation = default)
    {
        var response = await SendAsync(() => Http.PostJsonAsync(
            $"{url}/api/signin/{Escape(key)}/start", Headers(token), "{}", cancellation));
        return Parsed(() => JsonNode.Parse(response.Body) is JsonObject node
            ? ServerSignInStep.Parse(node)
            : throw new FormatException("Not a JSON object"));
    }

    /// <summary>Finishes a "code" or "redirect" sign-in with what the user pasted.</summary>
    public static Task CompleteSignInAsync(
        string url,
        string? token,
        string key,
        string input,
        CancellationToken cancellation = default)
    {
        var body = new JsonObject { ["input"] = input };
        return SendAsync(() => Http.PostJsonAsync(
            $"{url}/api/signin/{Escape(key)}/complete", Headers(token), body.ToJsonString(), cancellation));
    }

    /// <summary>"pending", "done", or "failed: reason" for the last sign-in of an account.</summary>
    public static async Task<string> SignInStatusAsync(
        string url,
        string? token,
        string key,
        CancellationToken cancellation = default)
    {
        var response = await SendAsync(() => Http.GetAsync(
            $"{url}/api/signin/{Escape(key)}/status", Headers(token), cancellation));
        return Parsed(() => JsonNode.Parse(response.Body) is JsonObject node
            ? ServerState.Text(node["status"]) ?? "none"
            : "none");
    }

    private static ServerAccount ParseAccount(string json) =>
        JsonNode.Parse(json) is JsonObject node
            ? ServerAccount.Parse(node)
            : throw new FormatException("Not a JSON object");

    /// <summary>An answer that is not the shape expected is the server's fault, not the network's.</summary>
    private static T Parsed<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new ServerException("Server sent something unreadable");
        }
    }

    private static string Escape(string key) => Uri.EscapeDataString(key);

    private static async Task<HttpResponse> SendAsync(Func<Task<HttpResponse>> send)
    {
        HttpResponse response;
        try
        {
            response = await send();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new ServerException("Server unreachable");
        }

        if (response.Code is >= 200 and < 300) return response;
        if (response.Code == 401) throw new ServerException("Server refused the token", needsUser: true);

        // The server words its refusals ("No account 'x'", "token is required");
        // that is more use than the status code.
        throw new ServerException(ErrorOf(response.Body) ?? $"Server answered {response.Code}");
    }

    private static string? ErrorOf(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject root
                && ServerState.Text(root["error"]) is { Length: > 0 } error
                ? error
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Headers(string? token) =>
        string.IsNullOrEmpty(token) ? [] : new() { ["Authorization"] = $"Bearer {token}" };
}
