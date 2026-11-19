using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage;

/// <summary>
/// The reading as text, for where there is no tray to draw in: a status bar's
/// own module (Waybar, Polybar, i3blocks) runs `usagetop status` and shows what
/// it prints.
/// </summary>
public static class Status
{
    private static readonly JsonWriterOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>One shown limit of one account.</summary>
    public sealed record Limit(ProviderUsage Usage, UsageWindow Window)
    {
        public string Name => $"{Usage.DisplayName} {WindowName.Display(Window.Label)}";
    }

    /// <summary>Every shown limit with a level, the one closest to running out first.</summary>
    public static List<Limit> Closest(AppModel model) =>
        (model.Snapshot?.Usages ?? [])
            .Where(u => u.State == UsageState.Ok)
            .SelectMany(u => model.Shown(u).Where(w => w.RemainingPercent != null).Select(w => new Limit(u, w)))
            .OrderBy(l => l.Window.RemainingPercent)
            .ToList();

    /// <summary>What the icon and a bar's text stand for: the combined level, or the tightest limit when there is none.</summary>
    public static float? Headline(AppModel model) =>
        model.Combined?.Percent ?? Closest(model).FirstOrDefault()?.Window.RemainingPercent;

    public static string Class(float? percent) => percent switch
    {
        null => "unknown",
        <= 15 => "critical",
        <= 40 => "warning",
        _ => "normal",
    };

    /// <summary>"51% left" and a line for each limit: also the tray icon's tooltip.</summary>
    public static string Text(AppModel model, int limits = 4)
    {
        var lines = new List<string>
        {
            Headline(model) is { } percent ? $"{Format.Percent(percent)} left" : "No reading yet",
        };
        foreach (var limit in Closest(model).Take(limits))
        {
            var reset = Format.Reset(limit.Window.ResetAt) is { } left ? $" · resets in {left}" : "";
            lines.Add($"{limit.Name} {Format.Percent(limit.Window.RemainingPercent!.Value)}{reset}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Waybar's custom-module format: text, tooltip, a class to style by, and the level.</summary>
    public static string Waybar(AppModel model)
    {
        var percent = Headline(model);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            json.WriteString("text", percent is { } p ? Format.Percent(p) : "–");
            json.WriteString("tooltip", Text(model, limits: 12));
            json.WriteString("class", Class(percent));
            json.WriteNumber("percentage", percent is { } level ? (int)Math.Round(level) : 0);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Everything, for a script: the combined level and every shown limit.</summary>
    public static string AsJson(AppModel model)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            if (model.Combined?.Percent is { } combined) json.WriteNumber("combined", Math.Round(combined, 1));
            else json.WriteNull("combined");
            json.WriteBoolean("sample", model.IsSample);
            if (model.Snapshot is { } snapshot) json.WriteString("readAt", snapshot.FetchedAt.ToString("O"));
            json.WriteStartArray("limits");
            foreach (var limit in Closest(model))
            {
                json.WriteStartObject();
                json.WriteString("account", limit.Usage.AccountKey);
                json.WriteString("name", limit.Usage.DisplayName);
                json.WriteString("limit", WindowName.Display(limit.Window.Label));
                json.WriteNumber("percent", Math.Round(limit.Window.RemainingPercent!.Value, 1));
                if (limit.Window.ResetAt is { } reset) json.WriteString("resetAt", reset.ToString("O"));
                json.WriteString("class", Class(limit.Window.RemainingPercent));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
