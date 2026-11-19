using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiUsage.Data;

namespace AiUsage.Server;

/// <summary>
/// The commands for looking after a running server from its own shell. They go
/// through the HTTP API rather than opening the data folder: the server holds
/// the credentials in memory, and a second process writing the file underneath
/// it would have its changes overwritten at the next token renewal.
/// </summary>
public static class Cli
{
    private const string Help =
        """
        aiusage-server                     run the server
        aiusage-server status              what every provider last reported
        aiusage-server refresh             ask every provider now
        aiusage-server signin <provider>   sign in to claude, chatgpt, muse or antigravity
        aiusage-server key <provider>      store an API key for mistral or kimi
        aiusage-server signout <provider>  forget a provider's credentials
        aiusage-server token               print the token clients must send

        Everything except "token" talks to the server, so it has to be running.
        Settings come from the environment: AIUSAGE_DATA, AIUSAGE_URLS,
        AIUSAGE_SECRET, AIUSAGE_API_TOKEN, AIUSAGE_NOTIFY_URL, AIUSAGE_ALERT_BELOW,
        AIUSAGE_POLL_SECONDS, AIUSAGE_ACTIVE_POLL_SECONDS.
        """;

    public static async Task<int> RunAsync(string[] args, ServerOptions options)
    {
        var verb = args[0].ToLowerInvariant();

        try
        {
            switch (verb)
            {
                case "help" or "--help" or "-h":
                    Console.WriteLine(Help);
                    return 0;

                case "token":
                    Console.WriteLine(Secrets.ApiToken(options));
                    return 0;
            }

            using var client = Client(options);

            switch (verb)
            {
                case "status":
                    return await StatusAsync(client, refresh: false);
                case "refresh":
                    return await StatusAsync(client, refresh: true);
                case "signin":
                    return await SignInAsync(client, ProviderArg(args));
                case "key":
                    return await KeyAsync(client, ProviderArg(args));
                case "signout":
                    await EnsureAsync(await client.DeleteAsync($"api/credentials/{ProviderArg(args)}"));
                    Console.WriteLine("Signed out.");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.\n");
                    Console.Error.WriteLine(Help);
                    return 2;
            }
        }
        catch (HttpRequestException exception) when (exception.StatusCode == null)
        {
            Console.Error.WriteLine($"The server is not answering at {BaseAddress(options)}. Is it running?");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string ProviderArg(string[] args)
    {
        if (args.Length < 2) throw new InvalidOperationException("Which provider? " + Providers());
        var key = args[1].ToLowerInvariant();
        if (Account.Parse(key) == null)
        {
            throw new InvalidOperationException($"Unknown provider '{args[1]}'. " + Providers());
        }
        return key;
    }

    private static string Providers() =>
        "One of: " + string.Join(", ", ProviderCatalog.All.Select(info => info.Key))
        + ", or an added account such as claude-2 (see `status`).";

    private static async Task<int> StatusAsync(HttpClient client, bool refresh)
    {
        using var response = refresh
            ? await client.PostAsync("api/refresh", null)
            : await client.GetAsync("api/snapshot");

        if ((int)response.StatusCode == 503)
        {
            Console.WriteLine("No reading yet. Give it a few seconds after starting.");
            return 0;
        }
        await EnsureAsync(response);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        foreach (var usage in root.GetProperty("usages").EnumerateArray())
        {
            var provider = ProviderCatalog.FromKey(usage.GetProperty("id").GetString() ?? "")?.DisplayName ?? "?";
            var accountName = usage.TryGetProperty("accountName", out var named) ? named.GetString() : null;
            var accountKey = usage.TryGetProperty("account", out var keyed) ? keyed.GetString() : null;
            var name = accountName is { Length: > 0 } ? $"{provider} · {accountName}"
                : accountKey is { } k && k.Contains('-') ? $"{provider} ({k})"
                : provider;
            var plan = usage.GetProperty("plan").GetString() ?? "";
            var message = usage.GetProperty("message").GetString();
            var stale = usage.GetProperty("stale").GetBoolean();

            var windows = usage.GetProperty("windows").EnumerateArray()
                .Select(window => window.GetProperty("remainingPercent").ValueKind == JsonValueKind.Number
                    ? $"{window.GetProperty("label").GetString()} {window.GetProperty("remainingPercent").GetDouble():0}%"
                    : window.GetProperty("detail").GetString() ?? "")
                .Where(text => text.Length > 0)
                .ToList();

            var summary = windows.Count > 0
                ? string.Join("  ", windows) + (stale ? $"  (kept: {message})" : "")
                : message ?? usage.GetProperty("state").GetString() ?? "";

            Console.WriteLine($"{name,-20}{plan,-11}{summary}");
        }

        if (root.GetProperty("combinedPercent").ValueKind == JsonValueKind.Number)
        {
            Console.WriteLine($"\nAll usage: {root.GetProperty("combinedPercent").GetDouble():0}% left "
                + $"across {root.GetProperty("combinedProviders").GetInt32()} subscriptions");
        }
        return 0;
    }

    private static async Task<int> SignInAsync(HttpClient client, string provider)
    {
        using var started = await client.PostAsync($"api/signin/{provider}/start", null);
        await EnsureAsync(started);

        using var step = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var kind = step.RootElement.GetProperty("kind").GetString();

        Console.WriteLine(step.RootElement.GetProperty("instructions").GetString());
        Console.WriteLine();
        Console.WriteLine(step.RootElement.GetProperty("url").GetString());
        Console.WriteLine();

        if (kind == "device")
        {
            Console.WriteLine($"Code: {step.RootElement.GetProperty("userCode").GetString()}");
            Console.WriteLine("Waiting for you to approve it…");
            return await AwaitDeviceAsync(client, provider);
        }

        Console.Write("Paste here: ");
        var pasted = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(pasted))
        {
            Console.Error.WriteLine("Nothing pasted; sign-in abandoned.");
            return 1;
        }

        using var completed = await client.PostAsJsonAsync($"api/signin/{provider}/complete", new { input = pasted });
        await EnsureAsync(completed);
        Console.WriteLine("Signed in.");
        return 0;
    }

    private static async Task<int> AwaitDeviceAsync(HttpClient client, string provider)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            using var response = await client.GetAsync($"api/signin/{provider}/status");
            await EnsureAsync(response);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = document.RootElement.GetProperty("status").GetString() ?? "";

            if (status == "done")
            {
                Console.WriteLine("Signed in.");
                return 0;
            }
            if (status.StartsWith("failed", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(status);
                return 1;
            }
        }

        Console.Error.WriteLine("Gave up waiting.");
        return 1;
    }

    private static async Task<int> KeyAsync(HttpClient client, string provider)
    {
        // Read from the terminal rather than taken as an argument, so the key
        // does not end up in the shell's history.
        Console.Write($"Paste the {provider} key: ");
        var key = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("Nothing pasted; nothing stored.");
            return 1;
        }

        using var response = await client.PutAsJsonAsync($"api/credentials/{provider}", new { token = key.Trim() });
        await EnsureAsync(response);
        Console.WriteLine("Stored.");
        return 0;
    }

    /// <summary>Turns the server's `{ "error": ... }` into the exception message.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        string? error = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var value)) error = value.GetString();
        }
        catch (JsonException)
        {
            // Not JSON; fall through to the status code.
        }

        throw new InvalidOperationException(error ?? $"The server answered HTTP {(int)response.StatusCode}.");
    }

    private static HttpClient Client(ServerOptions options)
    {
        var client = new HttpClient { BaseAddress = BaseAddress(options), Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secrets.ApiToken(options));
        return client;
    }

    /// <summary>
    /// The server's own address as seen from the same machine. A wildcard bind is
    /// not somewhere a client can connect to, so it is swapped for loopback.
    /// </summary>
    internal static Uri BaseAddress(ServerOptions options)
    {
        var first = options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        var local = first
            .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://*", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal);
        return new Uri(local.TrimEnd('/') + "/");
    }
}
