using System.Globalization;
using System.Text.Json;
using AiUsage.Data;
using AiUsage.Net;
using static AiUsage.Providers.ProviderHelpers;

namespace AiUsage.Providers;

/// <summary>
/// Muse Code (Meta) reports a rolling 5-hour window and a weekly window.
///
/// The endpoint is a POST that also performs onboarding; `onboard: false` keeps it
/// read-only. `resets_at` is Unix seconds, not milliseconds.
/// </summary>
public sealed class MuseProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.Muse;

    private const string Endpoint = "https://api.meta.ai/muse-code/key";

    public async Task<ProviderUsage> FetchAsync(
        Credentials credentials,
        CancellationToken cancellation = default)
    {
        var token = credentials.Token(Id);
        if (token == null) return NotConfigured(Id, "No token");

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {token}",
            ["x-api-version"] = "1.0.0",
            ["Accept"] = "application/json",
        };

        HttpResponse response;
        try
        {
            response = await Http.PostJsonAsync(Endpoint, headers, "{\"onboard\":false}", cancellation);
        }
        catch (Exception exception)
        {
            return NetworkError(Id, exception);
        }

        if (response.Code is 401 or 403)
        {
            return new ProviderUsage
            {
                Id = Id,
                State = UsageState.Error,
                Message = "Sign in again",
            };
        }
        if (response.Code != 200) return HttpError(Id, response.Code);

        using var document = Parse(response.Body);
        if (document == null)
        {
            return new ProviderUsage { Id = Id, State = UsageState.Error, Message = "Unexpected response" };
        }

        var root = document.RootElement;
        if (!Flag(root, "is_subs_active"))
        {
            return new ProviderUsage { Id = Id, State = UsageState.Error, Message = "No subscription" };
        }

        var usage = Object(root, "subs_usage");

        var windows = new List<UsageWindow>();
        if (usage is { } subs)
        {
            if (Window("5h", Object(subs, "window")) is { } fiveHour) windows.Add(fiveHour);
            if (Window("Weekly", Object(subs, "weekly")) is { } weekly) windows.Add(weekly);
        }

        if (windows.Count == 0)
        {
            // Meta omits `subs_usage` while the five-hour window is idle, even when
            // the weekly allowance has been spent. That is a transient failure, so
            // the last reading stands in and <see cref="RollOver"/> can rebuild it.
            return new ProviderUsage
            {
                Id = Id,
                State = UsageState.Error,
                Message = "No usage reported",
                Transient = true,
            };
        }

        return new ProviderUsage
        {
            Id = Id,
            State = UsageState.Ok,
            Windows = windows,
            Plan = PlanName(Text(root, "subs_tier_name")),
        };
    }

    private static UsageWindow? Window(string label, JsonElement? json)
    {
        if (json is not { } element) return null;

        var used = Number(element, "used_percent");
        if (double.IsNaN(used)) return null;

        return new UsageWindow(
            label,
            (float)Math.Clamp(100.0 - used, 0.0, 100.0),
            ResetAt(element));
    }

    /// <summary>`resets_at` is Unix seconds here, unlike every other provider.</summary>
    private static DateTimeOffset? ResetAt(JsonElement json)
    {
        var seconds = Number(json, "resets_at");
        if (!double.IsNaN(seconds)) return seconds <= 0.0 ? null : DateTimeOffset.FromUnixTimeSeconds((long)seconds);

        // A string form is tolerated in case Meta ever switches to ISO.
        return ParseInstant(Text(json, "resets_at"));
    }

    /// <summary>`subs_tier_name` reads "Muse Code Pro Usage"; only "Pro" is useful.</summary>
    private static string? PlanName(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return null;

        var name = tier.Replace("Muse Code ", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Usage", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Meta stops reporting a window once it has rolled over: the payload keeps the
    /// old `resets_at` and the old percentage, so a window that has already reset
    /// still reads as spent. When the last fetch failed and a reset has since
    /// passed, the window is known to be full again, so it is rebuilt at 100%.
    ///
    /// Only applied to stale data -- a live response is authoritative.
    /// </summary>
    public static ProviderUsage RollOver(ProviderUsage usage, DateTimeOffset now)
    {
        // Only Muse goes quiet like this. Another provider's kept reading failed
        // for a different reason, and guessing it back to full would be inventing
        // a number.
        if (usage.Id != ProviderId.Muse || !usage.Stale || usage.Windows.Count == 0) return usage;

        var rolled = false;
        var windows = new List<UsageWindow>(usage.Windows.Count);
        foreach (var window in usage.Windows)
        {
            if (window.ResetAt is { } resetAt && resetAt <= now)
            {
                windows.Add(window with { RemainingPercent = 100f, ResetAt = null, Detail = null });
                rolled = true;
            }
            else
            {
                windows.Add(window);
            }
        }

        return rolled ? usage with { Windows = windows } : usage;
    }
}
