using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using AiUsage.Data;

namespace AiUsage.Providers;

public interface IUsageProvider
{
    ProviderId Id { get; }

    Task<ProviderUsage> FetchAsync(Credentials credentials, CancellationToken cancellation = default);
}

internal static class ProviderHelpers
{
    /// <summary>Parses an ISO timestamp with or without an offset.</summary>
    public static DateTimeOffset? ParseInstant(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }
        return null;
    }

    public static string NetworkMessage(Exception exception) => exception switch
    {
        HttpRequestException { InnerException: SocketException } => "No connection",
        HttpRequestException => "No connection",
        TaskCanceledException => "Timed out",
        _ => Trim(exception.Message),
    };

    private static string Trim(string message) =>
        message.Length <= 60 ? message : message[..60];

    /// <summary>A dropped connection is worth retrying, so the last good reading may stand in.</summary>
    public static ProviderUsage NetworkError(ProviderId id, Exception exception) => new()
    {
        Id = id,
        State = UsageState.Error,
        Message = NetworkMessage(exception),
        Transient = true,
    };

    public static ProviderUsage HttpError(ProviderId id, int code)
    {
        var message = code switch
        {
            401 or 403 => "Token rejected",
            404 => "Endpoint moved",
            429 => "Rate limited",
            _ => $"HTTP {code}",
        };

        // A rate limit or a server fault is temporary; a rejected token is not.
        return new ProviderUsage
        {
            Id = id,
            State = UsageState.Error,
            Message = message,
            Transient = code == 429 || code >= 500,
            // 403 is left out on purpose: it is what an edge returns when it
            // dislikes the caller, and no new token changes that.
            Rejected = code == 401,
        };
    }

    public static ProviderUsage NotConfigured(ProviderId id, string message) => new()
    {
        Id = id,
        State = UsageState.NotConfigured,
        Message = message,
    };

    // ---- JSON -------------------------------------------------------------
    // The providers disagree about whether a number arrives as a number or as a
    // string, so every read goes through here rather than through GetDouble.

    public static double Number(JsonElement element, string name, double fallback = double.NaN)
    {
        if (element.ValueKind != JsonValueKind.Object) return fallback;
        if (!element.TryGetProperty(name, out var value)) return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : fallback,
            _ => fallback,
        };
    }

    public static long Integer(JsonElement element, string name, long fallback = 0L)
    {
        var value = Number(element, name, double.NaN);
        return double.IsNaN(value) ? fallback : (long)value;
    }

    public static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    public static JsonElement? Object(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Object ? value : null;
    }

    public static JsonElement? Array(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Array ? value : null;
    }

    public static bool Flag(JsonElement element, string name, bool fallback = false)
    {
        if (element.ValueKind != JsonValueKind.Object) return fallback;
        if (!element.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public static JsonDocument? Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch
        {
            return null;
        }
    }
}
