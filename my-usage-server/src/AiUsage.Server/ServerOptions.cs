using System.Globalization;
using System.Security.Cryptography;
using AiUsage.Data;

namespace AiUsage.Server;

/// <summary>
/// Everything that can be set from outside, read once from the environment. No
/// config file: a service that polls five endpoints has a handful of knobs, and
/// environment variables are what systemd and containers already speak.
/// </summary>
public sealed record ServerOptions
{
    /// <summary>Where the database, the sealed credentials and the two secrets live.</summary>
    public string DataDir { get; init; } = Path.GetFullPath("data");

    /// <summary>
    /// Loopback unless told otherwise. The API hands out everything the apps
    /// show, so it is not put on a network by default; set it to the address of
    /// a private interface to reach it from a phone.
    /// </summary>
    public string Urls { get; init; } = "http://127.0.0.1:8377";

    /// <summary>How often a provider is asked while nothing is moving.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often while one of its windows is being spent.</summary>
    public TimeSpan ActivePoll { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>An ntfy topic URL, or any endpoint that takes a POSTed line of text. Null sends nothing.</summary>
    public string? NotifyUrl { get; init; }

    /// <summary>A window falling under this many percent is worth a notification.</summary>
    public float AlertBelow { get; init; } = 10f;

    public string Database => Path.Combine(DataDir, "usage.db");

    public string CredentialsPath => Path.Combine(DataDir, "credentials.json");

    public string SecretKeyPath => Path.Combine(DataDir, "secret.key");

    public string ApiTokenPath => Path.Combine(DataDir, "api-token");

    public static ServerOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var defaults = new ServerOptions();

        return new ServerOptions
        {
            DataDir = Text(read, "AIUSAGE_DATA") is { } data ? Path.GetFullPath(data) : defaults.DataDir,
            Urls = Text(read, "AIUSAGE_URLS") ?? defaults.Urls,
            // Floors, not suggestions: these are other people's undocumented
            // endpoints, and a typo of "5" for "300" should not hammer them.
            Poll = Seconds(read, "AIUSAGE_POLL_SECONDS", defaults.Poll, floor: 60),
            ActivePoll = Seconds(read, "AIUSAGE_ACTIVE_POLL_SECONDS", defaults.ActivePoll, floor: 60),
            NotifyUrl = Text(read, "AIUSAGE_NOTIFY_URL"),
            AlertBelow = float.TryParse(
                Text(read, "AIUSAGE_ALERT_BELOW"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var below)
                ? Math.Clamp(below, 0f, 100f)
                : defaults.AlertBelow,
        };
    }

    private static string? Text(Func<string, string?> read, string name) =>
        read(name)?.Trim() is { Length: > 0 } value ? value : null;

    private static TimeSpan Seconds(Func<string, string?> read, string name, TimeSpan fallback, int floor) =>
        int.TryParse(Text(read, name), out var seconds)
            ? TimeSpan.FromSeconds(Math.Max(floor, seconds))
            : fallback;
}

/// <summary>
/// The two secrets the server keeps: the key its credentials are sealed under and
/// the token a client must present.
/// </summary>
public static class Secrets
{
    /// <summary>
    /// The sealer for the credentials file. With AIUSAGE_SECRET set the key never
    /// touches the data folder, so a copy of the folder alone opens nothing.
    /// Without it a random key is kept beside the data: that stops the tokens
    /// being read off a backup or over a shoulder, and nothing more.
    /// </summary>
    public static ISecretSealer Sealer(ServerOptions options, Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        if (read("AIUSAGE_SECRET") is { Length: > 0 } passphrase) return AesSealer.FromPassphrase(passphrase);

        var key = ReadOrCreate(options.SecretKeyPath, () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return new AesSealer(Convert.FromBase64String(key));
    }

    /// <summary>The bearer token clients send, from the environment or generated on first run.</summary>
    public static string ApiToken(ServerOptions options, Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        if (read("AIUSAGE_API_TOKEN")?.Trim() is { Length: > 0 } token) return token;

        return ReadOrCreate(options.ApiTokenPath, () => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
    }

    private static string ReadOrCreate(string path, Func<string> create)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } existing) return existing;

        var value = create();
        AtomicFile.WriteAllText(path, value);

        // Owner-only where the filesystem can express it. On Windows the folder's
        // own ACL is what protects the file.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return value;
    }
}
