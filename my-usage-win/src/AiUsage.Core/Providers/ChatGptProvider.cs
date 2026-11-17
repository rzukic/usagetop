using System.Text.Json;
using AiUsage.Data;
using AiUsage.Net;
using static AiUsage.Providers.ProviderHelpers;

namespace AiUsage.Providers;

/// <summary>
/// ChatGPT Plus/Pro rate-limit windows, read from the same backend endpoint the
/// Codex CLI uses. Authenticated with the ChatGPT session access token.
/// </summary>
public sealed class ChatGptProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.ChatGpt;

    private const string Endpoint = "https://chatgpt.com/backend-api/wham/usage";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ai-usage/1.0";

    public async Task<ProviderUsage> FetchAsync(
        Credentials credentials,
        CancellationToken cancellation = default)
    {
        var token = credentials.Token(Id);
        if (token == null) return NotConfigured(Id, "No token");

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {token}",
            ["Accept"] = "application/json",
            ["User-Agent"] = UserAgent,
        };
        if (credentials.AccountId(Id) is { } account) headers["chatgpt-account-id"] = account;

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
        var rateLimit = Object(root, "rate_limit") ?? root;

        var windows = new List<UsageWindow>();
        if (Object(rateLimit, "primary_window") is { } primary) windows.Add(Window(primary));
        if (Object(rateLimit, "secondary_window") is { } secondary) windows.Add(Window(secondary));

        if (windows.Count == 0)
        {
            return new ProviderUsage { Id = Id, State = UsageState.Error, Message = "No limits in response" };
        }

        return new ProviderUsage
        {
            Id = Id,
            State = UsageState.Ok,
            Windows = windows,
            Plan = PrettyPlan(Text(root, "plan_type")),
        };
    }

    /// <summary>The usage payload names the tier directly: `plan_type` is `plus`, `pro`, ...</summary>
    private static string? PrettyPlan(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return raw.ToLowerInvariant() switch
        {
            "free" => "Free",
            "plus" => "Plus",
            "pro" => "Pro",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            _ => char.ToUpperInvariant(raw[0]) + raw[1..],
        };
    }

    private static UsageWindow Window(JsonElement json)
    {
        var used = (float)Math.Clamp(Number(json, "used_percent", 0.0), 0.0, 100.0);

        var resetAfter = Integer(json, "reset_after_seconds", -1L);
        var resetAt = resetAfter >= 0L
            ? DateTimeOffset.UtcNow.AddSeconds(resetAfter)
            : (DateTimeOffset?)null;

        return new UsageWindow(LabelFor(Integer(json, "limit_window_seconds", 0L)), 100f - used, resetAt);
    }

    private static string LabelFor(long seconds) => seconds switch
    {
        <= 0L => "limit",
        < 3_600L => $"{seconds / 60}m",
        < 86_400L => $"{seconds / 3_600}h",
        _ => $"{seconds / 86_400}d",
    };
}
