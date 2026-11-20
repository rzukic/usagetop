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

    /// <summary>
    /// The limits a bar shows, as the Mac's menu bar picks them: closest to
    /// running out first, the sooner reset first between equals, and none that
    /// is already empty, since there is nothing left to watch in it until it resets.
    /// </summary>
    public static List<Limit> ForBar(AppModel model, int count) => ForBar(Closest(model), count, DateTimeOffset.UtcNow);

    internal static List<Limit> ForBar(IEnumerable<Limit> limits, int count, DateTimeOffset now) =>
        limits
            .Where(l => Pace.Of(l.Window, now) != Pace.Kind.Empty)
            .OrderBy(l => l.Window.RemainingPercent)
            .ThenBy(l => l.Window.ResetAt ?? DateTimeOffset.MaxValue)
            .Take(count)
            .ToList();

    /// <summary>
    /// Each limit's name in a bar, short: "Weekly", "5h", "Fable". Two limits of
    /// one account that would read the same keep their full names; a long one is
    /// cut so it cannot crowd the bar. As the Mac's menu bar.
    /// </summary>
    public static List<string> BarLabels(IReadOnlyList<Limit> limits, bool slim = false)
    {
        var compact = limits.Select(l => WindowName.Compact(l.Window.Label)).ToList();
        return limits.Select((limit, i) =>
        {
            var clashes = limits.Where((other, j) => j != i
                && other.Usage.AccountKey == limit.Usage.AccountKey && compact[j] == compact[i]).Any();
            var label = clashes ? WindowName.Display(limit.Window.Label) : compact[i];
            if (slim) label = Slim(label);
            return label.Length <= 12 ? label : label[..11] + "…";
        }).ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex Periods =
        new(@"\b(daily|weekly|monthly)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A period by its length rather than its word, for a bar of text, where the
    /// limits sit side by side and every letter counts: "Weekly" is "7d", as
    /// "5h" already is; "Gemini weekly" is "Gemini 7d".
    /// </summary>
    public static string Slim(string label) =>
        Periods.Replace(label, match => match.Value.ToLowerInvariant() switch
        {
            "daily" => "1d",
            "weekly" => "7d",
            _ => "1mo",
        });

    /// <summary>
    /// The limit a tray icon rings in place of the combined level: with Settings
    /// › Top bar on one or more limits, the closest to running out; otherwise none.
    /// </summary>
    public static Limit? ForTray(AppModel model) =>
        PanelPrefs.Current.BarLimits > 0 ? ForBar(model, 1).FirstOrDefault() : null;

    /// <summary>What the icon and a bar's text stand for: the combined level, or the tightest limit when there is none.</summary>
    public static float? Headline(AppModel model) =>
        model.Combined?.Percent ?? Closest(model).FirstOrDefault()?.Window.RemainingPercent;

    /// <summary>The account's own part of its name, for a bar that already shows the provider's mark.</summary>
    public static string? AccountLabel(ProviderUsage usage) => Account.LabelOf(usage.AccountName, usage.Slot);

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

    /// <summary>One limit in a bar's text: whose, which, and what is left of it.</summary>
    public sealed record BarPart(string Whose, string Label, float Percent, ProviderId? Provider = null, string? Initials = null)
    {
        public string Figure => Format.Percent(Percent);
    }

    /// <summary>
    /// Each provider's mark as a character of the font usagetop installs
    /// (fonts/usagetop-marks.otf, made by fonts/build-font.py, whose numbers
    /// these are), for bars that show text.
    /// </summary>
    public static readonly IReadOnlyDictionary<ProviderId, char> Marks = new Dictionary<ProviderId, char>
    {
        [ProviderId.Claude] = '\uE000',
        [ProviderId.ChatGpt] = '\uE001',
        [ProviderId.Mistral] = '\uE002',
        [ProviderId.Kimi] = '\uE003',
        [ProviderId.Muse] = '\uE004',
        [ProviderId.Antigravity] = '\uE005',
    };

    public const string MarksFont = "usagetop marks";

    /// <summary>
    /// Whether the marks' font is where fontconfig finds it. Without it the
    /// characters would show as boxes, so the bar names the provider instead.
    /// </summary>
    public static bool MarksInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");
        return new[] { Path.Combine(data, "fonts"), Path.Combine(home, ".fonts"), "/usr/share/fonts", "/usr/local/share/fonts" }
            .Any(folder =>
            {
                try
                {
                    return Directory.Exists(folder)
                        && Directory.EnumerateFiles(folder, "usagetop-marks.otf", SearchOption.AllDirectories).Any();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            });
    }

    /// <summary>
    /// The limits Settings › Top bar asks for, as a bar of text shows them:
    /// "Kimi" "7d" "11%", an account with a name of its own by its initials
    /// ("Claude RZ"). Empty with the top bar on the combined ring.
    /// </summary>
    public static List<BarPart> BarParts(AppModel model)
    {
        var count = PanelPrefs.Current.BarLimits;
        if (count == 0) return [];
        var limits = ForBar(model, count);
        var labels = BarLabels(limits, slim: true);
        return limits.Select((limit, i) =>
        {
            var whose = ProviderCatalog.Of(limit.Usage.Id).DisplayName;
            var initials = AccountLabel(limit.Usage) is { } account ? Account.Initials(account) : null;
            if (initials != null) whose += " " + initials;
            return new BarPart(whose, labels[i], limit.Window.RemainingPercent!.Value, limit.Usage.Id, initials);
        }).ToList();
    }

    /// <summary>
    /// Pango markup for a bar of text (Waybar, Xfce's Generic Monitor), as the
    /// GNOME bar draws it: the limit's name a little fainter, and a figure that
    /// warns in the level's colour, each its own rather than the bar's.
    /// </summary>
    public static string BarMarkup(IEnumerable<BarPart> parts, bool darkBar, bool marks = false) =>
        // An em space between limits: the word spaces inside one would leave
        // them hard to tell apart.
        string.Join("\u2003", parts.Select(part =>
        {
            var figure = part.Percent <= 40
                ? $"<span color=\"{Hex(darkBar ? Look.LightOf(part.Percent) : Look.ColorOf(part.Percent))}\">{part.Figure}</span>"
                : part.Figure;
            // With the marks' font, the provider's mark stands where its name
            // would, and an account with a name of its own adds its initials,
            // small and heavy, as the badge on a mark elsewhere.
            var whose = marks && part.Provider is { } id && Marks.TryGetValue(id, out var mark)
                ? $"<span font_family=\"{MarksFont}\">{mark}</span>"
                  + (part.Initials is { } initials ? $"<span size=\"smaller\" weight=\"heavy\">{Escape(initials)}</span>" : "")
                : Escape(part.Whose);
            return $"{whose} <span alpha=\"70%\">{Escape(part.Label)}</span> {figure}";
        }));

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? "";

    private static string Hex(Avalonia.Media.Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>
    /// Xfce's Generic Monitor plugin: it runs `usagetop status --genmon` and
    /// shows the text in the panel, the tooltip over it, and runs the click.
    /// </summary>
    public static string Genmon(AppModel model)
    {
        var parts = BarParts(model);
        var text = parts.Count > 0
            ? BarMarkup(parts, Look.Dark, MarksInstalled())
            : Headline(model) is { } percent ? Format.Percent(percent) : "–";
        return $"<txt>{text}</txt><tool>{Escape(Text(model, limits: 12))}</tool><txtclick>usagetop toggle</txtclick>";
    }

    /// <summary>Waybar's custom-module format: text, tooltip, a class to style by, and the level.</summary>
    public static string Waybar(AppModel model)
    {
        // As Settings › Top bar has it: the combined level, or the limits closest
        // to running out, each as "Kimi Weekly 11%", an account with a name of its
        // own told apart by its initials ("Claude RZ 5h 73%"). The class and level
        // are then the tightest one's.
        // Waybar's bar is dark nearly always, whatever the desktop's setting.
        var parts = BarParts(model);
        var percent = parts.Count > 0 ? parts[0].Percent : Headline(model);
        var text = parts.Count > 0 ? BarMarkup(parts, darkBar: true, MarksInstalled()) : percent is { } p ? Format.Percent(p) : "–";
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            json.WriteString("text", text);
            json.WriteString("tooltip", Text(model, limits: 12));
            if (parts.Count > 0)
            {
                // Each figure has its own level's colour already: "limits" lets a
                // style that colours the module by its level leave these alone
                // (#custom-usagetop.critical:not(.limits)).
                json.WriteStartArray("class");
                json.WriteStringValue(Class(percent));
                json.WriteStringValue("limits");
                json.WriteEndArray();
            }
            else
            {
                json.WriteString("class", Class(percent));
            }
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
            // What a desktop's bar shows, decided here so that whatever draws it
            // (the GNOME extension) need not know the rules. As the Mac's menu
            // bar: the combined level in a ring, with its figure or without, or
            // the limits closest to running out.
            var prefs = PanelPrefs.Current;
            json.WriteStartArray("bar");
            if (prefs.BarLimits == 0)
            {
                var level = model.Combined?.Percent;
                json.WriteStartObject();
                json.WriteString("kind", "combined");
                if (level is { } percent) json.WriteNumber("percent", Math.Round(percent, 1));
                else json.WriteNull("percent");
                json.WriteString("text", level is { } shown && prefs.BarPercent ? Format.Percent(shown) : "");
                json.WriteString("title", level is { } spoken ? $"{Format.Percent(spoken)} left in all" : "usagetop");
                json.WriteString("class", Class(level));
                json.WriteEndObject();
            }
            else
            {
                var limits = ForBar(model, prefs.BarLimits);
                var labels = BarLabels(limits);
                for (var i = 0; i < limits.Count; i++)
                {
                    var limit = limits[i];
                    json.WriteStartObject();
                    json.WriteString("kind", "limit");
                    json.WriteString("label", labels[i]);
                    json.WriteString("provider", ProviderCatalog.Of(limit.Usage.Id).Key);
                    // Two accounts of one provider would look the same in a bar without it.
                    if (AccountLabel(limit.Usage) is { } account) json.WriteString("account", account);
                    json.WriteString("title", limit.Name);
                    json.WriteNumber("percent", Math.Round(limit.Window.RemainingPercent!.Value, 1));
                    json.WriteString("text", Format.Percent(limit.Window.RemainingPercent!.Value));
                    json.WriteString("class", Class(limit.Window.RemainingPercent));
                    json.WriteEndObject();
                }
            }
            json.WriteEndArray();
            json.WriteStartArray("limits");
            foreach (var limit in Closest(model))
            {
                json.WriteStartObject();
                json.WriteString("account", limit.Usage.AccountKey);
                json.WriteString("provider", ProviderCatalog.Of(limit.Usage.Id).Key);
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
