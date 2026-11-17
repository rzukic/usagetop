using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AiUsage.Data;

namespace AiUsage.Ui;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>One row inside a provider's card: a label, a meter and a percentage.</summary>
public sealed class WindowRow : Observable
{
    private readonly UsageWindow _window;

    public WindowRow(UsageWindow window)
    {
        _window = window;
        Label = window.Label;
        HasPercent = window.RemainingPercent != null;

        if (window.RemainingPercent is { } percent)
        {
            Fill = Math.Clamp(percent / 100.0, 0.0, 1.0);
            MeterBrush = Severity.MeterBrush(percent);
            ValueBrush = Severity.TextBrush(percent);
            Value = Format.Percent(percent);
        }
        else
        {
            MeterBrush = Brushes.Transparent;
            ValueBrush = SecondaryBrush;
            Value = window.Detail ?? "—";
        }

        Reset = Format.Reset(window.ResetAt) ?? "";
    }

    private static readonly Brush SecondaryBrush = Frozen(Color.FromRgb(0x9B, 0x9B, 0x9B));

    public string Label { get; }

    public bool HasPercent { get; }

    public double Fill { get; }

    public Brush MeterBrush { get; }

    public Brush ValueBrush { get; }

    public string Value { get; }

    public string Reset { get; }

    public bool HasReset => Reset.Length > 0;

    public UsageWindow Window => _window;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>One provider's section: its mark, its name, its plan and its windows.</summary>
public sealed class ProviderCard : Observable
{
    public ProviderCard(ProviderId id, ProviderUsage? usage)
    {
        Id = id;
        var info = ProviderCatalog.Of(id);
        Name = usage?.DisplayName ?? info.DisplayName;
        AccountKey = usage?.AccountKey ?? info.Key;
        Logo = ProviderArt.Logo(id);

        Plan = usage?.Plan ?? "";
        HasPlan = Plan.Length > 0;

        // A reading kept from an earlier fetch is marked so it never reads as live.
        Stale = usage is { Stale: true } ? $"· {Format.Ago(usage.FetchedAt)}" : "";
        HasStale = Stale.Length > 0;

        var windows = usage?.Windows ?? [];
        Windows = new ObservableCollection<WindowRow>(windows.Select(w => new WindowRow(w)));
        HasWindows = Windows.Count > 0;

        Note = HasWindows
            ? ""
            : usage == null
                ? "Not configured"
                : usage.State == UsageState.NotConfigured
                    ? usage.Message ?? "Not configured"
                    : usage.Message ?? "Refresh failed";
        HasNote = Note.Length > 0;
    }

    public ProviderId Id { get; }

    /// <summary>"claude", or "claude-2" for a second account; what the detail page is opened with.</summary>
    public string AccountKey { get; }

    public string Name { get; }

    public Geometry Logo { get; }

    public string Plan { get; }

    public bool HasPlan { get; }

    public string Stale { get; }

    public bool HasStale { get; }

    public ObservableCollection<WindowRow> Windows { get; }

    public bool HasWindows { get; }

    public string Note { get; }

    public bool HasNote { get; }
}

/// <summary>
/// What every screen binds to: the provider cards, the combined figure and the
/// one-line status. One instance per window, all fed from the same repository.
/// </summary>
public sealed class UsageViewModel : Observable
{
    private static readonly Brush TertiaryBrush = Frozen(Color.FromRgb(0x7A, 0x7A, 0x7A));

    private string _combinedValue = "—";
    private Brush _combinedBrush = TertiaryBrush;
    private string _combinedCaption = "No subscriptions yet";
    private string _status = "Never updated";
    private bool _refreshing;

    public UsageViewModel()
    {
        Cards = new ObservableCollection<ProviderCard>(
            ProviderCatalog.All.Select(info => new ProviderCard(info.Id, null)));
    }

    public ObservableCollection<ProviderCard> Cards { get; }

    public string CombinedValue
    {
        get => _combinedValue;
        private set => Set(ref _combinedValue, value);
    }

    public Brush CombinedBrush
    {
        get => _combinedBrush;
        private set => Set(ref _combinedBrush, value);
    }

    public string CombinedCaption
    {
        get => _combinedCaption;
        private set => Set(ref _combinedCaption, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool Refreshing
    {
        get => _refreshing;
        set => Set(ref _refreshing, value);
    }

    /// <summary>Rebuilds every card from a snapshot. Null means nothing has been read yet.</summary>
    public void Apply(Snapshot? snapshot)
    {
        // Every provider's first account has a card even before it reports, so
        // the list shows what can be set up. Accounts added on a server follow
        // their provider's.
        var usages = snapshot?.Usages ?? [];
        var cards = new List<ProviderCard>();
        foreach (var info in ProviderCatalog.All)
        {
            var first = usages.FirstOrDefault(u => u.Id == info.Id && u.Slot.Length == 0);
            cards.Add(new ProviderCard(info.Id, first));
            cards.AddRange(usages
                .Where(u => u.Id == info.Id && u.Slot.Length > 0)
                .Select(u => new ProviderCard(info.Id, u)));
        }

        // Replaced in place where the count allows, so a list that is not
        // changing shape does not flicker.
        for (var i = 0; i < cards.Count; i++)
        {
            if (i < Cards.Count) Cards[i] = cards[i];
            else Cards.Add(cards[i]);
        }
        while (Cards.Count > cards.Count) Cards.RemoveAt(Cards.Count - 1);

        var combined = snapshot == null ? null : CombinedUsage.Of(snapshot);
        var percent = combined?.Percent;

        CombinedValue = percent == null ? "—" : Format.Percent(percent.Value);
        CombinedBrush = percent == null ? TertiaryBrush : Severity.TextBrush(percent.Value);
        CombinedCaption = combined == null || combined.Providers == 0
            ? "No subscriptions yet"
            : $"across {combined.Providers} subscription{(combined.Providers == 1 ? "" : "s")}";

        Status = Format.Status(snapshot, App.Repository.ServerProblem, App.Repository.UsesServer);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
