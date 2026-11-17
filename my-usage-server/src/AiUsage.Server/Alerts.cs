using System.Globalization;
using AiUsage.Data;

namespace AiUsage.Server;

/// <summary>Something worth interrupting the user for.</summary>
public sealed record Alert(string Title, string Body, bool Urgent);

/// <summary>
/// Decides what is worth a notification by comparing one snapshot with the one
/// before it. Every rule fires on a change, never on a state, so a window that
/// sits at 3% for two days is announced once and a restart announces nothing.
/// </summary>
public static class Alerts
{
    /// <summary>A window counts as refilled once it is back above this, having been low.</summary>
    private const float Refilled = 50f;

    public static IReadOnlyList<Alert> Between(Snapshot? before, Snapshot after, float lowBelow, DateTimeOffset now)
    {
        // The first snapshot has nothing to be compared with. Announcing whatever
        // it holds would mean a burst of old news every time the database is new.
        if (before == null) return [];

        var alerts = new List<Alert>();
        var earlier = new Dictionary<string, ProviderUsage>(StringComparer.Ordinal);
        foreach (var usage in before.Usages) earlier[usage.AccountKey] = usage;

        foreach (var usage in after.Usages)
        {
            var name = usage.DisplayName;
            var previous = earlier.GetValueOrDefault(usage.AccountKey);

            // A provider that has stopped answering for a reason only the user
            // can fix. A rate limit or a dropped connection fixes itself.
            if (NeedsUser(usage) && !NeedsUser(previous))
            {
                alerts.Add(new Alert(
                    $"{name} needs attention",
                    $"{usage.Message ?? "It stopped answering"}. Its numbers will not update until this is fixed.",
                    Urgent: true));
                continue;
            }

            if (usage.State != UsageState.Ok || previous is not { State: UsageState.Ok }) continue;

            foreach (var window in usage.Windows)
            {
                if (window.RemainingPercent is not { } percent) continue;
                var was = previous.Windows.FirstOrDefault(w => w.Label == window.Label)?.RemainingPercent;
                if (was is not { } then) continue;

                if (then >= lowBelow && percent < lowBelow)
                {
                    alerts.Add(new Alert(
                        $"{name} {window.Label}: {Percent(percent)} left",
                        ResetLine(window.ResetAt, now) ?? "Running low.",
                        Urgent: percent <= 0f));
                }
                else if (then < lowBelow && percent >= Refilled)
                {
                    alerts.Add(new Alert(
                        $"{name} {window.Label} is back",
                        $"{Percent(percent)} available again.",
                        Urgent: false));
                }
            }
        }

        return alerts;
    }

    private static bool NeedsUser(ProviderUsage? usage) =>
        usage is { State: UsageState.Error, Transient: false };

    private static string Percent(float value) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0}%", Math.Clamp(value, 0f, 100f));

    private static string? ResetLine(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not { } at || at <= now) return null;

        var left = at - now;
        var text = left.TotalHours >= 48 ? $"{(int)left.TotalDays}d"
            : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m"
            : $"{Math.Max(1, left.Minutes)}m";
        return $"Resets in {text}.";
    }
}

/// <summary>
/// Sends an alert as a plain POST: the text as the body, the title in a header.
/// That is ntfy's protocol, and close enough to a webhook that anything which
/// accepts a posted line of text can stand in for it.
/// </summary>
public sealed class Notifier(HttpClient client, string? url, ILogger<Notifier> logger)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(url);

    public async Task SendAsync(Alert alert, CancellationToken cancellation = default)
    {
        logger.LogInformation("Alert: {Title} — {Body}", alert.Title, alert.Body);
        if (!Enabled) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(alert.Body),
            };
            // Headers are ASCII on the wire, and a title can carry a provider's
            // own wording, so anything else is dropped rather than sent mangled.
            request.Headers.TryAddWithoutValidation("Title", Ascii(alert.Title));
            request.Headers.TryAddWithoutValidation("Priority", alert.Urgent ? "high" : "default");
            request.Headers.TryAddWithoutValidation("Tags", alert.Urgent ? "warning" : "bar_chart");

            using var response = await client.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Notification was refused with HTTP {Code}", (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A notification that cannot be sent must never stop the polling.
            logger.LogWarning("Notification could not be sent: {Reason}", exception.Message);
        }
    }

    private static string Ascii(string text) =>
        new(text.Select(c => c is >= ' ' and <= '~' ? c : c == '—' ? '-' : '?').ToArray());
}
