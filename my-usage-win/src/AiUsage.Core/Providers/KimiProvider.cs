using System.Globalization;
using System.Text.Json;
using AiUsage.Data;
using AiUsage.Net;
using static AiUsage.Providers.ProviderHelpers;

namespace AiUsage.Providers;

/// <summary>
/// Kimi sells two unrelated things:
///
///  - Kimi Code, a subscription with a rolling 5-hour window and a monthly total,
///    reported by `GET https://api.kimi.ai/coding/v1/usages`.
///  - The Open Platform, prepaid credit in CNY, reported by
///    `GET /v1/users/me/balance` on the Moonshot hosts.
///
/// The subscription is tried first. The balance is only a fallback, for keys that
/// are not Kimi Code keys -- otherwise a subscriber sees a meaningless ¥0.00.
/// </summary>
public sealed class KimiProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.Kimi;

    /// <summary>Overseas first, then China; a key is only valid on one of them.</summary>
    private static readonly string[] PlanHosts = ["https://api.kimi.ai", "https://api.kimi.com"];

    private const string GlobalBase = "https://api.moonshot.ai";
    private const string ChinaBase = "https://api.moonshot.cn";

    public async Task<ProviderUsage> FetchAsync(
        Credentials credentials,
        CancellationToken cancellation = default)
    {
        var token = credentials.Token(Id);
        if (token == null) return NotConfigured(Id, "No key");

        return await PlanUsageAsync(token, cancellation)
            ?? await BalanceUsageAsync(token, credentials.KimiChina(), cancellation);
    }

    /// <summary>Rolling 5-hour and monthly windows of a Kimi Code subscription.</summary>
    private static async Task<ProviderUsage?> PlanUsageAsync(string token, CancellationToken cancellation)
    {
        // Kimi Code runs two deployments with separate keys: api.kimi.ai for
        // overseas accounts and api.kimi.com for China. Try both.
        foreach (var host in PlanHosts)
        {
            var usage = await PlanUsageOnAsync(host, token, cancellation);
            if (usage != null) return usage;
        }
        return null;
    }

    private static async Task<ProviderUsage?> PlanUsageOnAsync(
        string host,
        string token,
        CancellationToken cancellation)
    {
        // The Kimi Code API is both OpenAI- and Anthropic-compatible, and the two
        // protocols authenticate differently. Try both before giving up.
        foreach (var headers in new[] { BearerHeaders(token), ApiKeyHeaders(token) })
        {
            HttpResponse response;
            try
            {
                response = await Http.GetAsync($"{host}/coding/v1/usages", headers, cancellation);
            }
            catch
            {
                return null;
            }

            if (response.Code is 401 or 403) continue;
            if (response.Code != 200) return null;

            var usage = ParsePlan(response.Body);
            if (usage == null) return null;

            return usage with { Plan = await PlanNameAsync(host, headers, cancellation) };
        }
        return null;
    }

    /// <summary>
    /// The response carries two generations of the same data:
    ///
    ///  - `usages`: ratio pools, one per window (`limit_5h`, `limit_month_total`,
    ///    `limit_month_code`), each `{ used_ratio, reset_time }`.
    ///  - `limits`: legacy request counts, each `{ window, detail: { limit, used,
    ///    resetTime } }`.
    ///
    /// The ratio pools are preferred; the counts only fill in when they are absent.
    /// </summary>
    private static ProviderUsage? ParsePlan(string body)
    {
        using var document = Parse(body);
        if (document == null) return null;
        var root = document.RootElement;

        var ratioWindows = new List<UsageWindow>();
        if (Object(root, "usages") is { } usages)
        {
            if (RatioWindow("5h", Object(usages, "limit_5h")) is { } fiveHour) ratioWindows.Add(fiveHour);
            if (RatioWindow("Monthly", Object(usages, "limit_month_total")) is { } monthly) ratioWindows.Add(monthly);
        }
        if (ratioWindows.Count > 0)
        {
            return new ProviderUsage { Id = ProviderId.Kimi, State = UsageState.Ok, Windows = ratioWindows };
        }

        var countWindows = new List<UsageWindow>();
        if (Array(root, "limits") is { } limits)
        {
            var entries = limits.EnumerateArray().ToList();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var detail = Object(entry, "detail") ?? entry;
                if (CountWindow(LabelFor(entry, index, entries.Count), detail) is { } window)
                {
                    countWindows.Add(window);
                }
            }
        }

        if (countWindows.Count == 0) return null;
        return new ProviderUsage { Id = ProviderId.Kimi, State = UsageState.Ok, Windows = countWindows };
    }

    /// <summary>The usages payload has no tier; `/coding/v1/me` reports `user_level_name`.</summary>
    private static async Task<string?> PlanNameAsync(
        string host,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellation)
    {
        try
        {
            var response = await Http.GetAsync($"{host}/coding/v1/me", headers, cancellation);
            if (response.Code != 200) return null;

            using var document = Parse(response.Body);
            if (document == null) return null;

            var name = Text(document.RootElement, "user_level_name");
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static UsageWindow? RatioWindow(string label, JsonElement? json)
    {
        if (json is not { } element) return null;

        var usedRatio = Number(element, "used_ratio");
        if (double.IsNaN(usedRatio)) return null;

        return new UsageWindow(
            label,
            (float)Math.Clamp((1.0 - usedRatio) * 100.0, 0.0, 100.0),
            ParseInstant(Text(element, "reset_time")));
    }

    private static UsageWindow? CountWindow(string label, JsonElement json)
    {
        var limit = Number(json, "limit");
        if (double.IsNaN(limit) || limit <= 0.0) return null;

        var used = Math.Clamp(Number(json, "used", 0.0), 0.0, limit);
        return new UsageWindow(
            label,
            (float)Math.Clamp((limit - used) / limit * 100.0, 0.0, 100.0),
            ResetAt(json),
            string.Format(CultureInfo.InvariantCulture, "{0:0} of {1:0} left", limit - used, limit));
    }

    /// <summary>Legacy entries describe their window as `{ duration, timeUnit }`.</summary>
    private static string LabelFor(JsonElement entry, int index, int total)
    {
        var window = Object(entry, "window");
        var duration = window is { } w ? Integer(w, "duration", 0L) : 0L;
        var unit = window is { } u ? Text(u, "timeUnit") ?? "" : "";

        var minutes = unit.Contains("MINUTE", StringComparison.OrdinalIgnoreCase) ? duration
            : unit.Contains("HOUR", StringComparison.OrdinalIgnoreCase) ? duration * 60
            : unit.Contains("DAY", StringComparison.OrdinalIgnoreCase) ? duration * 24 * 60
            : 0L;

        return minutes switch
        {
            <= 0L => total > 1 ? $"window {index + 1}" : "limit",
            < 60L => $"{minutes}m",
            < 1440L => $"{minutes / 60}h",
            _ => $"{minutes / 1440}d",
        };
    }

    /// <summary>`resetTime` arrives as epoch millis, epoch seconds or an ISO timestamp.</summary>
    private static DateTimeOffset? ResetAt(JsonElement json)
    {
        if (!json.TryGetProperty("resetTime", out var value)) return null;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
            {
                var raw = value.GetInt64();
                return DateTimeOffset.FromUnixTimeMilliseconds(raw < 100_000_000_000L ? raw * 1000 : raw);
            }
            case JsonValueKind.String:
            {
                var text = value.GetString();
                if (long.TryParse(text, out var epoch))
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(
                        epoch < 100_000_000_000L ? epoch * 1000 : epoch);
                }
                return ParseInstant(text);
            }
            default:
                return null;
        }
    }

    /// <summary>Prepaid Open Platform credit, for keys that are not Kimi Code keys.</summary>
    private static async Task<ProviderUsage> BalanceUsageAsync(
        string token,
        bool china,
        CancellationToken cancellation)
    {
        var baseUrl = china ? ChinaBase : GlobalBase;

        HttpResponse response;
        try
        {
            response = await Http.GetAsync($"{baseUrl}/v1/users/me/balance", BearerHeaders(token), cancellation);
        }
        catch (Exception exception)
        {
            return NetworkError(ProviderId.Kimi, exception);
        }

        if (response.Code != 200) return HttpError(ProviderId.Kimi, response.Code);

        using var document = Parse(response.Body);
        if (document == null)
        {
            return new ProviderUsage
            {
                Id = ProviderId.Kimi,
                State = UsageState.Error,
                Message = "Unexpected response",
            };
        }

        var data = Object(document.RootElement, "data") ?? document.RootElement;
        var available = Number(data, "available_balance");
        if (double.IsNaN(available))
        {
            return new ProviderUsage
            {
                Id = ProviderId.Kimi,
                State = UsageState.Error,
                Message = "No balance in response",
            };
        }

        // A Kimi Code subscriber who pasted an Open Platform key lands here: the
        // plan endpoint rejects the key and the prepaid credit is empty. Point at
        // the right console instead of showing a meaningless ¥0.00.
        if (available <= 0.0)
        {
            return new ProviderUsage
            {
                Id = ProviderId.Kimi,
                State = UsageState.Error,
                Message = "Kimi Code key needed",
            };
        }

        return new ProviderUsage
        {
            Id = ProviderId.Kimi,
            State = UsageState.Ok,
            Windows =
            [
                new UsageWindow(
                    "balance",
                    Detail: string.Format(CultureInfo.InvariantCulture, "¥{0:0.00} available", available)),
            ],
        };
    }

    private static Dictionary<string, string> BearerHeaders(string token) => new()
    {
        ["Authorization"] = $"Bearer {token}",
        ["Accept"] = "application/json",
    };

    private static Dictionary<string, string> ApiKeyHeaders(string token) => new()
    {
        ["x-api-key"] = token,
        ["Accept"] = "application/json",
    };
}
