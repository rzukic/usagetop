using System.Text.Json;
using AiUsage.Data;

namespace AiUsage.Auth;

/// <summary>
/// Picks up the sessions the official CLIs already have on this machine, so the app
/// works on first launch for anyone who is signed in to Claude Code, Codex or the
/// Muse CLI.
///
/// Claude's and ChatGPT's sessions are borrowed, not copied. Their refresh tokens
/// are single-use: whoever renews first leaves the other holding a dead one, so an
/// app that renewed a copy would sign the CLI out (or be signed out by it). Only
/// the access token is taken, it is read from the CLI's file again before every
/// fetch, and renewing it is left to the CLI. Signing in through the browser
/// replaces the loan with a session of the app's own.
/// </summary>
public static class CliImport
{
    private sealed record CliSession(string Access, string? Refresh, long ExpiresAt, string? AccountId);

    private static readonly ProviderId[] Lendable = [ProviderId.Claude, ProviderId.ChatGpt];

    /// <summary>Imports whatever it finds and reports which providers it filled in.</summary>
    public static IReadOnlyList<ProviderId> Run(Credentials credentials, string? home = null)
    {
        home ??= DefaultHome;
        var imported = new List<ProviderId>();

        foreach (var id in Lendable)
        {
            // A session of the app's own outlives the CLI's and renews itself, so
            // it is never traded for a loan.
            if (credentials.IsSignedIn(id) && !SharesGrant(credentials, id, home)) continue;

            if (Read(id, home) is not { } session) continue;
            Borrow(credentials, id, session);
            imported.Add(id);
        }

        if (ImportMuse(credentials, home)) imported.Add(ProviderId.Muse);

        return imported;
    }

    /// <summary>
    /// Brings every borrowed token up to date with its CLI, and turns a session
    /// copied by an earlier build into a loan. Cheap enough to run before every
    /// fetch: it reads two small files and writes nothing unless one changed.
    /// </summary>
    public static void Sync(Credentials credentials, string? home = null)
    {
        home ??= DefaultHome;

        foreach (var id in Lendable)
        {
            var borrowed = credentials.Borrowed(id);
            if (!borrowed && !SharesGrant(credentials, id, home)) continue;

            if (Read(id, home) is not { } session) continue;
            if (borrowed && session.Access == credentials.Token(id)) continue;

            Borrow(credentials, id, session);
        }
    }

    /// <summary>
    /// True when the refresh token on file is the very one the CLI holds: an
    /// earlier build copied it, and neither side has renewed since. That is the
    /// one moment the copy can still be handed back without anyone losing a
    /// session.
    /// </summary>
    private static bool SharesGrant(Credentials credentials, ProviderId id, string home) =>
        credentials.RefreshToken(id) is { } ours
        && Read(id, home) is { Refresh: { } theirs }
        && ours == theirs;

    private static void Borrow(Credentials credentials, ProviderId id, CliSession session) =>
        credentials.SetSession(
            id,
            session.Access,
            refresh: null,
            session.ExpiresAt,
            session.AccountId,
            borrowed: true);

    private static string DefaultHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static CliSession? Read(ProviderId id, string home) => id switch
    {
        ProviderId.Claude => ReadClaude(home),
        ProviderId.ChatGpt => ReadChatGpt(home),
        _ => null,
    };

    /// <summary>Claude Code keeps its OAuth session in ~/.claude/.credentials.json.</summary>
    private static CliSession? ReadClaude(string home)
    {
        var root = ReadJson(Path.Combine(home, ".claude", ".credentials.json"));
        if (Child(root, "claudeAiOauth") is not { } oauth) return null;

        var access = Text(oauth, "accessToken");
        if (access == null) return null;

        var expiresAt = oauth.TryGetProperty("expiresAt", out var expires)
            && expires.ValueKind == JsonValueKind.Number
            && expires.TryGetInt64(out var at)
            ? at
            : 0L;

        return new CliSession(access, Text(oauth, "refreshToken"), expiresAt, null);
    }

    /// <summary>Codex keeps its ChatGPT session in ~/.codex/auth.json.</summary>
    private static CliSession? ReadChatGpt(string home)
    {
        var root = ReadJson(Path.Combine(home, ".codex", "auth.json"));
        if (Child(root, "tokens") is not { } tokens) return null;

        var access = Text(tokens, "access_token");
        if (access == null) return null;

        return new CliSession(access, Text(tokens, "refresh_token"), 0L, Text(tokens, "account_id"));
    }

    /// <summary>
    /// The Muse CLI keeps its device-code token in ~/.config/muse/auth.json. It is
    /// long-lived and has no refresh token behind it, so a plain copy is safe.
    /// </summary>
    private static bool ImportMuse(Credentials credentials, string home)
    {
        var root = ReadJson(Path.Combine(home, ".config", "muse", "auth.json"));
        if (Child(Child(root, "providers"), "meta") is not { } meta) return false;

        var access = Text(meta, "access_token");
        if (access == null) return false;

        credentials.SetToken(ProviderId.Muse, access);
        return true;
    }

    private static JsonElement? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A nested object, or null when the file is not shaped the way we expect.</summary>
    private static JsonElement? Child(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } parent
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
