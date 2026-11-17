using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiUsage.Data;

namespace AiUsage.Ui;

/// <summary>One line in the priority list: either a bucket heading or a row.</summary>
public abstract class PriorityEntry;

/// <summary>A dim heading above a run of rows, e.g. "RUNNING OUT".</summary>
public sealed class PriorityGroup(string label) : PriorityEntry
{
    public string Label { get; } = label.ToUpperInvariant();
}

/// <summary>
/// One line of the list, in the phone widget's shape: a level ring carrying a
/// mark, a name and a subtitle, then the figure and the reset.
/// </summary>
public abstract class PriorityItem : PriorityEntry
{
    private static readonly Brush Track = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Secondary = Frozen(Color.FromRgb(0x9B, 0x9B, 0x9B));
    private static readonly Brush Tertiary = Frozen(Color.FromRgb(0x7A, 0x7A, 0x7A));
    private static readonly Brush Primary = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));

    protected PriorityItem(
        string title,
        string subtitle,
        Geometry? logo,
        double? percent,
        string value,
        string? reset)
    {
        Title = title;
        Subtitle = subtitle;
        Logo = logo;
        Percent = percent;
        Value = value;
        Reset = reset ?? "";

        FillBrush = percent is { } filled ? Severity.MeterBrush((float)filled) : Track;
        ValueBrush = percent is { } text ? Severity.TextBrush((float)text) : Secondary;
        LogoBrush = percent is > 0 ? Primary : Tertiary;
        Dim = percent is not > 0;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public double? Percent { get; }

    public Brush FillBrush { get; }

    public Brush TrackBrush => Track;

    public Geometry? Logo { get; }

    public Brush LogoBrush { get; }

    public string Value { get; }

    public Brush ValueBrush { get; }

    public string Reset { get; }

    public bool HasReset => Reset.Length > 0;

    /// <summary>A row with nothing left, or nothing to report, dims its name.</summary>
    public bool Dim { get; }

    /// <summary>The combined figure sits above the buckets and reads as a card.</summary>
    public virtual bool Summary => false;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>One quota window of one provider.</summary>
public sealed class PriorityRow : PriorityItem
{
    public PriorityRow(ProviderUsage usage, UsageWindow? window)
        : base(
            usage.DisplayName,
            string.IsNullOrWhiteSpace(window?.Label) ? Note(usage) : window!.Label,
            ProviderArt.Logo(usage.Id),
            window?.RemainingPercent,
            window?.RemainingPercent is { } percent
                ? $"{Format.Percent(percent)} left"
                : window?.Detail ?? "—",
            Format.Reset(window?.ResetAt))
    {
        Id = usage.Id;
        AccountKey = usage.AccountKey;
        Bucket = PriorityList.BucketOf(window);
        TimeLeft = window?.ResetAt is { } reset ? reset - DateTimeOffset.UtcNow : null;
    }

    public ProviderId Id { get; }

    /// <summary>Which account the row belongs to, for opening its page.</summary>
    public string AccountKey { get; }

    /// <summary>Which heading this row sits under.</summary>
    public string Bucket { get; }

    /// <summary>How long until the window resets; null when the provider does not say.</summary>
    public TimeSpan? TimeLeft { get; }

    private static string Note(ProviderUsage usage) => usage.State switch
    {
        UsageState.NotConfigured => usage.Message ?? "Not configured",
        UsageState.Error => usage.Message ?? "Error",
        _ => usage.Message ?? "No data",
    };
}

/// <summary>The combined figure, pinned above the buckets.</summary>
public sealed class PrioritySummary : PriorityItem
{
    public PrioritySummary(Snapshot snapshot) : this(CombinedUsage.Of(snapshot))
    {
    }

    private PrioritySummary(CombinedUsage.Combined combined)
        : base(
            "All usage",
            combined.Providers == 0
                ? "No subscriptions yet"
                : $"across {combined.Providers} subscription{(combined.Providers == 1 ? "" : "s")}",
            null,
            combined.Percent,
            combined.Percent is { } percent ? $"{Format.Percent(percent)} left" : "—",
            null)
    {
    }

    public override bool Summary => true;
}

/// <summary>
/// Builds the list the tray popup shows: the combined figure, then every window of
/// every provider, bucketed by how close it is to running out.
/// </summary>
public static class PriorityList
{
    /// <summary>Headings in the order they are shown: dead first, then forfeit, then fine.</summary>
    private static readonly string[] Buckets =
        ["Running out", Forfeit, "Used up", "Healthy", "Unavailable"];

    private const string Forfeit = "Use it or lose it";

    /// <summary>
    /// The last quarter of a window, capped at half a day. Past that point the quota
    /// you have not spent is about to be taken back, so it is worth a nudge.
    /// </summary>
    private static readonly TimeSpan ForfeitCap = TimeSpan.FromHours(12);

    public static IEnumerable<PriorityEntry> Build(Snapshot? snapshot)
    {
        var usages = snapshot?.Usages ?? [];
        if (usages.Count == 0) yield break;

        yield return new PrioritySummary(snapshot!);

        var rows = usages
            .SelectMany(usage => (usage.Windows.Count > 0
                    ? usage.Windows
                    : [new UsageWindow("")])
                .Select(window => new PriorityRow(usage, window)))
            .ToList();

        foreach (var bucket in Buckets)
        {
            var group = rows.Where(row => row.Bucket == bucket).ToList();
            if (group.Count == 0) continue;

            // The forfeit bucket is about the clock, not the level: soonest first.
            var ordered = bucket == Forfeit
                ? group.OrderBy(row => row.TimeLeft ?? TimeSpan.MaxValue)
                : group.OrderBy(row => row.Percent ?? double.MaxValue);

            yield return new PriorityGroup(bucket);
            foreach (var row in ordered) yield return row;
        }
    }

    /// <summary>Which heading a window belongs under.</summary>
    public static string BucketOf(UsageWindow? window)
    {
        var percent = window?.RemainingPercent;
        return percent switch
        {
            null => "Unavailable",
            <= 0 => "Used up",
            <= 40 => "Running out",
            _ => Forfeiting(window!) ? Forfeit : "Healthy",
        };
    }

    /// <summary>Healthy, but the window is nearly over and the quota goes with it.</summary>
    private static bool Forfeiting(UsageWindow window)
    {
        if (window.ResetAt is not { } reset) return false;
        var left = reset - DateTimeOffset.UtcNow;
        return left > TimeSpan.Zero && left <= Deadline(window.Label);
    }

    private static TimeSpan Deadline(string label)
    {
        if (Length(label) is not { } length) return ForfeitCap;
        var quarter = TimeSpan.FromTicks(length.Ticks / 4);
        return quarter < ForfeitCap ? quarter : ForfeitCap;
    }

    /// <summary>"5h", "7d" and "30m" say how long the window is; "Monthly" does not.</summary>
    private static TimeSpan? Length(string label)
    {
        var match = DurationLabel.Match(label.Trim());
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value, out var amount)) return null;

        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            "d" => TimeSpan.FromDays(amount),
            _ => null,
        };
    }

    /// <summary>A window label that is a plain duration, e.g. "5h" or "7d".</summary>
    private static readonly Regex DurationLabel =
        new(@"^(\d+)\s*([mhd])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

/// <summary>What the tray popup binds to.</summary>
public sealed class PriorityViewModel : Observable
{
    private string _status = "Never updated";

    public ObservableCollection<PriorityEntry> Entries { get; } = [];

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public void Apply(Snapshot? snapshot)
    {
        Entries.Clear();
        foreach (var entry in PriorityList.Build(snapshot)) Entries.Add(entry);

        Status = Format.Status(snapshot, App.Repository.ServerProblem, App.Repository.UsesServer);
    }
}

/// <summary>Picks the heading template or the row template for each entry.</summary>
public sealed class PriorityTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Group { get; set; }

    public DataTemplate? Row { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is PriorityGroup ? Group : Row;
}
