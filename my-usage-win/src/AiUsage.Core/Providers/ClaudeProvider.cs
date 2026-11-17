using System.Text.Json;
using AiUsage.Data;
using AiUsage.Net;
using static AiUsage.Providers.ProviderHelpers;

namespace AiUsage.Providers;

/// <summary>
/// Claude Pro/Max rolling limits. Same endpoint Claude Code's `/usage` command
/// uses, authenticated with the OAuth access token rather than an API key.
///
/// Three windows come back: the five-hour session, the weekly total, and -- since
/// Fable 5.2 -- the share of that weekly total the Fable models may use.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.Claude;

    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string Profile = "https://api.anthropic.com/api/oauth/profile";

    public async Task<ProviderUsage> FetchAsync(
        Credentials credentials,
        CancellationToken cancellation = default)
    {
        var token = credentials.Token(Id);
        if (token == null) return NotConfigured(Id, "No token");

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {token}",
            ["anthropic-beta"] = "oauth-2025-04-20",
            ["Accept"] = "application/json",
        };

        HttpResponse response;
        try
        {
            response = await Http.GetAsync(Endpoint, headers, cancellation);
        }
        catch (Exception exception)
        {
            return NetworkError(Id, exception);
        }

        if (response.Code != 200) return HttpError(Id, response.Code);

        using var document = Parse(response.Body);
        if (document == null)
        {
            return new ProviderUsage { Id = Id, State = UsageState.Error, Message = "Unexpected response" };
        }

        var root = document.RootElement;
        var windows = new List<UsageWindow>();

        if (Object(root, "five_hour") is { } fiveHour) windows.Add(Window("5h", fiveHour));
        if (Object(root, "seven_day") is { } sevenDay) windows.Add(Window("7d", sevenDay));
        windows.AddRange(Scoped(Array(root, "limits")));

        if (windows.Count == 0)
        {
            return new ProviderUsage { Id = Id, State = UsageState.Error, Message = "No usage in response" };
        }

        return new ProviderUsage
        {
            Id = Id,
            State = UsageState.Ok,
            Windows = windows,
            Plan = await PlanAsync(headers, cancellation),
        };
    }

    /// <summary>
    /// The usage endpoint carries no tier, but the OAuth profile does:
    /// `organization.organization_type` is `claude_pro`, `claude_max`, and so on.
    /// </summary>
    private static async Task<string?> PlanAsync(
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellation)
    {
        try
        {
            var response = await Http.GetAsync(Profile, headers, cancellation);
            if (response.Code != 200) return null;

            using var document = Parse(response.Body);
            if (document == null) return null;

            var organization = Object(document.RootElement, "organization");
            var type = organization is { } org ? Text(org, "organization_type") : null;

            return type switch
            {
                "claude_pro" => "Pro",
                "claude_max" => "Max",
                "claude_team" => "Team",
                "claude_enterprise" => "Enterprise",
                "claude_free" => "Free",
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static UsageWindow Window(string label, JsonElement json)
    {
        var used = json.TryGetProperty("utilization", out _)
            ? Number(json, "utilization", 0.0)
            : Number(json, "used_percent", 0.0);

        return new UsageWindow(
            label,
            (float)Math.Clamp(100.0 - used, 0.0, 100.0),
            ParseInstant(Text(json, "resets_at")));
    }

    /// <summary>
    /// Per-model weekly limits live in `limits[]` rather than in a flat key: a
    /// `weekly_scoped` entry names the model it applies to and how much of the
    /// weekly allowance that model may use. It is a slice of `seven_day`, not a
    /// second allowance, so it is kept out of the combined figure.
    /// </summary>
    internal static List<UsageWindow> Scoped(JsonElement? limits)
    {
        var windows = new List<UsageWindow>();
        if (limits is not { } array) return windows;

        foreach (var entry in array.EnumerateArray())
        {
            if (Text(entry, "kind") != "weekly_scoped") continue;

            var scope = Object(entry, "scope");
            var model = scope is { } s && Object(s, "model") is { } m ? Text(m, "display_name") : null;
            if (string.IsNullOrWhiteSpace(model)) continue;

            var used = Number(entry, "percent");
            if (double.IsNaN(used)) continue;

            windows.Add(new UsageWindow(
                $"{model} 7d",
                (float)Math.Clamp(100.0 - used, 0.0, 100.0),
                ParseInstant(Text(entry, "resets_at")),
                CountsTowardCombined: false));
        }

        return windows;
    }
}
