using System.Globalization;
using System.Text.Json;
using AiUsage.Data;
using AiUsage.Net;
using static AiUsage.Providers.ProviderHelpers;

namespace AiUsage.Providers;

/// <summary>
/// Mistral has no documented usage API, but its own billing routes -- the ones the
/// mistral-vibe CLI and OpenUsage read -- accept a normal API key:
///
///   GET /v1/billing/budget        api_budget + vibe_budget: percent, cap, reset
///   GET /v1/billing/subscription  plan name
///
/// Undocumented but first-party, so no browser session or console scraping is
/// involved. Both return 401 without a key, which is how we know they exist.
/// </summary>
public sealed class MistralProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.Mistral;

    private const string Base = "https://api.mistral.ai";

    public async Task<ProviderUsage> FetchAsync(
        Credentials credentials,
        CancellationToken cancellation = default)
    {
        var token = credentials.Token(Id);
        if (token == null) return NotConfigured(Id, "No key");

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {token}",
            ["Accept"] = "application/json",
        };

        HttpResponse response;
        try
        {
            response = await Http.GetAsync($"{Base}/v1/billing/budget", headers, cancellation);
        }
        catch (Exception exception)
        {
            return NetworkError(Id, exception);
        }

        if (response.Code != 200) return ManualFallback(credentials, HttpError(Id, response.Code));

        using var document = Parse(response.Body);
        if (document == null)
        {
            return new ProviderUsage { Id = Id, State = UsageState.Error, Message = "Unexpected response" };
        }

        var windows = Budgets(document.RootElement);
        if (windows.Count == 0)
        {
            return ManualFallback(
                credentials,
                new ProviderUsage { Id = Id, State = UsageState.Error, Message = "No budget in response" });
        }

        return new ProviderUsage
        {
            Id = Id,
            State = UsageState.Ok,
            Windows = windows,
            Plan = await PlanNameAsync(headers, cancellation),
        };
    }

    /// <summary>
    /// The two budgets the billing route reports. The API credit is a separate
    /// pool that coding workflows cannot spend, so it stays on the card but out of
    /// the combined figure; the Vibe allowance is what the subscription gives you.
    /// </summary>
    internal static List<UsageWindow> Budgets(JsonElement root)
    {
        var windows = new List<UsageWindow>();
        if (Object(root, "api_budget") is { } api) windows.Add(Window("API", api, false));
        if (Object(root, "vibe_budget") is { } vibe) windows.Add(Window("Vibe", vibe, true));
        return windows;
    }

    private static UsageWindow Window(string label, JsonElement json, bool countsTowardCombined)
    {
        var usedPercent = (float)Math.Clamp(Number(json, "usage_percentage", 0.0), 0.0, 100.0);
        var cap = Number(json, "initial_budget", 0.0);
        var currency = Text(json, "currency");
        if (string.IsNullOrWhiteSpace(currency) || currency == "null") currency = null;

        var left = cap * (100.0 - usedPercent) / 100.0;

        var detail = cap > 0.0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1:0.00} of {0}{2:0.00} left",
                Symbol(currency),
                left,
                cap)
            : null;

        return new UsageWindow(
            label,
            100f - usedPercent,
            ParseInstant(Text(json, "reset_at")),
            detail,
            countsTowardCombined);
    }

    private static async Task<string?> PlanNameAsync(
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellation)
    {
        try
        {
            var response = await Http.GetAsync($"{Base}/v1/billing/subscription", headers, cancellation);
            if (response.Code != 200) return null;

            using var document = Parse(response.Body);
            if (document == null) return null;

            var name = Text(document.RootElement, "plan_name");
            return string.IsNullOrWhiteSpace(name) ? null : PrettyPlan(name);
        }
        catch
        {
            return null;
        }
    }

    private static string PrettyPlan(string raw) => raw.ToUpperInvariant() switch
    {
        "EDU" => "Education",
        "FREE" => "Free",
        "INDIVIDUAL" => "Pro",
        "TEAM" => "Team",
        _ => char.ToUpperInvariant(raw[0]) + raw[1..],
    };

    /// <summary>Manual budget/spend, kept for keys that lack billing scope.</summary>
    private static ProviderUsage ManualFallback(Credentials credentials, ProviderUsage failure)
    {
        var budget = credentials.MistralBudget();
        if (budget == null) return failure;

        var spent = credentials.MistralSpent() ?? 0.0;
        var used = (float)Math.Clamp(spent / budget.Value * 100.0, 0.0, 100.0);

        return new ProviderUsage
        {
            Id = ProviderId.Mistral,
            State = UsageState.Ok,
            Windows =
            [
                new UsageWindow(
                    "month",
                    100f - used,
                    Detail: string.Format(
                        CultureInfo.InvariantCulture,
                        "€{0:0.00} of €{1:0.00} left",
                        budget.Value - spent,
                        budget.Value)),
            ],
            Message = failure.Message,
        };
    }

    private static string Symbol(string? currency) => currency?.ToUpperInvariant() switch
    {
        "EUR" => "€",
        "USD" => "$",
        "GBP" => "£",
        _ => "",
    };
}
