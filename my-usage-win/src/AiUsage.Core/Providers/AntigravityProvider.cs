using System.Text.Json;
using AiUsage.Data;
using AiUsage.Net;
using static AiUsage.Providers.ProviderHelpers;

namespace AiUsage.Providers;

/// <summary>
/// A Google AI plan's Antigravity quota (Pro, Ultra), read from the endpoint the
/// Antigravity CLI's own `/quota` uses. Authenticated with a Google OAuth access
/// token from Antigravity's client.
///
/// The models are split into groups, and each group has a weekly limit and most
/// a five-hour one: "Gemini Models", "Claude and GPT models", and Gemini Argon,
/// which on Pro has a weekly limit of its own. The Gemini group is the plan's
/// main allowance and the only one in the combined figure; the others are
/// separate budgets beside it.
/// </summary>
public sealed class AntigravityProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.Antigravity;

    private const string Base = "https://daily-cloudcode-pa.googleapis.com/v1internal";

    /// <summary>
    /// The project a consumer plan's quota is filed under. A workspace whose
    /// Code Assist is managed in Google Cloud answers with its own project.
    /// </summary>
    private const string ConsumerProject = "aicode-consumers";

    /// <summary>
    /// The quota endpoint only answers clients that name themselves as
    /// Antigravity; anything else is told it has no licence.
    /// </summary>
    private const string UserAgent = "antigravity/cli/1.5.12 (aidev_client; auth_method=consumer)";

    public async Task<ProviderUsage> FetchAsync(
        Credentials credentials,
        CancellationToken cancellation = default)
    {
        var token = credentials.Token(Id);
        if (token == null) return NotConfigured(Id, "Not signed in");

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {token}",
            ["Accept"] = "application/json",
            ["User-Agent"] = UserAgent,
        };

        string? plan = null;
        var project = ConsumerProject;
        HttpResponse response;
        try
        {
            // The plan's name and, for a managed workspace, its project. A failure
            // here other than a refused token still leaves the quota worth asking.
            var load = await Http.PostJsonAsync(
                $"{Base}:loadCodeAssist", headers, """{"metadata":{"ideType":"ANTIGRAVITY"}}""", cancellation);
            if (load.Code == 401) return HttpError(Id, load.Code);
            if (load.Code == 200 && Parse(load.Body) is { } document)
            {
                using (document)
                {
                    (plan, var managed) = Account(document.RootElement);
                    if (managed != null) project = managed;
                }
            }

            response = await Http.PostJsonAsync(
                $"{Base}:retrieveUserQuotaSummary",
                headers,
                JsonSerializer.Serialize(new Dictionary<string, string> { ["project"] = project }),
                cancellation);
        }
        catch (Exception exception)
        {
            return NetworkError(Id, exception);
        }

        if (response.Code == 403)
        {
            // What a Google account without a plan, or one Antigravity has not
            // been opened with yet, is told.
            return new ProviderUsage { Id = Id, State = UsageState.Error, Message = "No Antigravity plan", Plan = plan };
        }
        if (response.Code != 200) return HttpError(Id, response.Code);

        using var quota = Parse(response.Body);
        if (quota == null)
        {
            return new ProviderUsage { Id = Id, State = UsageState.Error, Message = "Unexpected response" };
        }

        var windows = Windows(quota.RootElement);
        if (windows.Count == 0)
        {
            return new ProviderUsage { Id = Id, State = UsageState.Error, Message = "No limits in response", Plan = plan };
        }

        return new ProviderUsage { Id = Id, State = UsageState.Ok, Windows = windows, Plan = plan };
    }

    /// <summary>
    /// The plan's name from loadCodeAssist ("Google AI Pro"; a paid tier over the
    /// free one), and the project to ask for a managed workspace, null otherwise.
    /// </summary>
    internal static (string? Plan, string? Project) Account(JsonElement root)
    {
        var plan = (Object(root, "paidTier") is { } paid ? Text(paid, "name") : null)
            ?? (Object(root, "currentTier") is { } current ? Text(current, "name") : null);

        string? project = null;
        if (Flag(root, "gcpManaged"))
        {
            project = Text(root, "cloudaicompanionProject")
                ?? (Object(root, "cloudaicompanionProject") is { } p ? Text(p, "id") : null);
        }

        return (string.IsNullOrWhiteSpace(plan) ? null : plan.Trim(), project);
    }

    /// <summary>
    /// Every group's buckets as windows. The Gemini group's are plain "5h" and
    /// "7d"; another group's carry its name, "Claude &amp; GPT 7d". A bucket
    /// that is switched off, or that says nothing about what is left, is skipped.
    /// </summary>
    internal static List<UsageWindow> Windows(JsonElement root)
    {
        var windows = new List<UsageWindow>();
        var groups = Array(root, "groups") is { } list ? list.EnumerateArray().ToList() : [];

        // An older answer had no groups, only buckets.
        if (groups.Count == 0)
        {
            if (Array(root, "buckets") is { } buckets) Add(windows, buckets, prefix: "", main: true);
            return windows;
        }

        var main = groups.FindIndex(g => Short(Text(g, "displayName")) == "Gemini");
        if (main < 0) main = 0;

        for (var index = 0; index < groups.Count; index++)
        {
            if (Array(groups[index], "buckets") is not { } buckets) continue;
            var isMain = index == main;
            Add(windows, buckets, isMain ? "" : Short(Text(groups[index], "displayName")), isMain);
        }

        return windows;
    }

    private static void Add(List<UsageWindow> windows, JsonElement buckets, string prefix, bool main)
    {
        foreach (var bucket in buckets.EnumerateArray())
        {
            if (Flag(bucket, "disabled")) continue;
            var fraction = Number(bucket, "remainingFraction");
            if (double.IsNaN(fraction)) continue;

            var span = Span(Text(bucket, "window")) ?? Text(bucket, "displayName") ?? "limit";
            var label = prefix.Length == 0 ? span : $"{prefix} {span}";
            windows.Add(new UsageWindow(
                label,
                (float)Math.Clamp(fraction * 100.0, 0.0, 100.0),
                ParseInstant(Text(bucket, "resetTime")),
                CountsTowardCombined: main));
        }
    }

    /// <summary>"weekly" is "7d" like every other provider's week; "5h" stays.</summary>
    private static string? Span(string? window) => window?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "weekly" or "week" => "7d",
        "daily" or "day" => "1d",
        "monthly" or "month" => "Monthly",
        var other when char.IsDigit(other[0]) => other,
        _ => null,
    };

    /// <summary>"Gemini Models" is "Gemini", "Claude and GPT models" is "Claude &amp; GPT".</summary>
    private static string Short(string? name)
    {
        var text = (name ?? "").Trim();
        if (text.EndsWith(" models", StringComparison.OrdinalIgnoreCase)) text = text[..^" models".Length].TrimEnd();
        return text.Replace(" and ", " & ", StringComparison.Ordinal);
    }
}
