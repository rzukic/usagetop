using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// One provider in full, or every subscription averaged into one drawdown. The
/// list stays a summary; the history and the money figures live here. It is a
/// control rather than a window so the tray popup can show the same page.
/// </summary>
public partial class DetailView : UserControl
{
    private static readonly TimeSpan[] Spans =
    [
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(30),
        TimeSpan.FromDays(90),
    ];

    /// <summary>The account shown ("claude", "claude-2"), or null for the combined view.</summary>
    private string? _account;
    private int _range;

    /// <summary>
    /// Raised when a provider in the combined view is picked. The host decides
    /// whether that means another window or another page in the same popup.
    /// </summary>
    public event Action<string>? Navigate;

    public DetailView()
    {
        InitializeComponent();

        // Loaded and Unloaded pair up, but a control that is shown again would
        // otherwise subscribe twice.
        Loaded += (_, _) =>
        {
            App.Repository.Changed -= OnChanged;
            App.Repository.Changed += OnChanged;
        };
        Unloaded += (_, _) => App.Repository.Changed -= OnChanged;
    }

    /// <summary>Null shows the combined view.</summary>
    public void Show(string? account)
    {
        _account = account;
        Render();
        Scroller.ScrollToTop();
    }

    private void OnChanged(Snapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnChanged(snapshot));
            return;
        }

        Render();
    }

    private void Render()
    {
        Body.Children.Clear();
        if (_account is { } key) RenderProvider(key);
        else RenderCombined();
    }

    // ---- One provider -----------------------------------------------------

    private void RenderProvider(string key)
    {
        var snapshot = App.Repository.Cached();
        var usage = snapshot?.Usages.FirstOrDefault(u => u.AccountKey == key);

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (usage?.Plan is { Length: > 0 } plan)
        {
            header.Children.Add(Badge(plan));
        }
        header.Children.Add(new TextBlock
        {
            Text = Describe(usage),
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        Body.Children.Add(header);

        var windows = usage?.Windows ?? [];
        if (windows.Count == 0)
        {
            Body.Children.Add(new TextBlock
            {
                Text = usage == null
                    ? "Not configured"
                    : usage.State == UsageState.NotConfigured
                        ? usage.Message ?? "Not configured"
                        : usage.Message ?? "Refresh failed",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var window in windows)
        {
            Body.Children.Add(WindowCard(key, window));
        }
    }

    /// <summary>One window: its figure, its reset, and how it has drawn down.</summary>
    private Border WindowCard(string key, UsageWindow window)
    {
        var stack = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = window.Label.Length == 0 ? "Usage" : window.Label,
            Style = (Style)FindResource("CardTitle"),
        });

        var reset = Format.Reset(window.ResetAt);
        if (reset != null)
        {
            left.Children.Add(new TextBlock
            {
                Text = $"resets in {reset}",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        top.Children.Add(left);

        var percent = window.RemainingPercent;
        var figure = new TextBlock
        {
            Text = percent is { } value ? Format.Percent(value) : window.Detail ?? "—",
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = percent == null ? 14 : 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = percent is { } p
                ? Severity.TextBrush(p)
                : (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(figure, 1);
        top.Children.Add(figure);
        stack.Children.Add(top);

        if (percent != null && window.Detail is { Length: > 0 } detail)
        {
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        // The same forecast the server page draws: the recent burn easing into the
        // usual one, from a week of this window's own readings.
        var now = DateTimeOffset.UtcNow;
        var span = UsageHistory.ChartSpan(window);
        var series = App.Repository.History.Series(key, window.Label, span);
        var track = Projection.For(
            window,
            App.Repository.History.Series(key, window.Label, Projection.UsualBasis),
            now);

        if (track is { Measured: true })
        {
            var (rates, outcome, empties) = Format.Burn(track, Projection.EmptyAt(track, now + Projection.Horizon));
            var caption = new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            caption.Inlines.Add(new System.Windows.Documents.Run(rates));
            caption.Inlines.Add(new System.Windows.Documents.Run(outcome)
            {
                Foreground = empties ? new SolidColorBrush(Severity.Warning) : caption.Foreground,
            });
            stack.Children.Add(caption);
        }

        stack.Children.Add(new DrawdownChart
        {
            Points = series,
            Forecast = track,
            Ahead = span,
            LineColor = percent is { } value2 ? Severity.ColorFor(value2) : Severity.Normal,
            Height = 110,
            Margin = new Thickness(0, 12, 0, 0),
        });

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 12, 0, 0),
            Child = stack,
        };
    }

    private static string Describe(ProviderUsage? usage) => usage switch
    {
        null => "Not configured",
        { State: UsageState.NotConfigured } => usage.Message ?? "Not configured",
        { State: UsageState.Error } => usage.Message ?? "Refresh failed",
        // Numbers kept from an earlier fetch say why they are old, not just how old.
        { Stale: true, Message: { Length: > 0 } reason } =>
            $"{reason} · as of {Format.Ago(usage.FetchedAt)}",
        _ => $"Updated {Format.Ago(usage.FetchedAt)}",
    };

    // ---- Combined ---------------------------------------------------------

    private void RenderCombined()
    {
        var snapshot = App.Repository.Cached();
        var combined = snapshot == null ? null : CombinedUsage.Of(snapshot);
        var percent = combined?.Percent;

        var figure = new TextBlock
        {
            Text = percent is { } value ? Format.Percent(value) : "—",
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 44,
            FontWeight = FontWeights.SemiBold,
            Foreground = percent is { } p
                ? Severity.TextBrush(p)
                : (Brush)FindResource("TextTertiaryBrush"),
        };
        Body.Children.Add(figure);

        Body.Children.Add(new TextBlock
        {
            Text = combined == null || combined.Providers == 0
                ? "No subscriptions yet"
                : $"across {combined.Providers} subscription{(combined.Providers == 1 ? "" : "s")}",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 2, 0, 0),
        });

        // Range picker
        var track = new Border
        {
            Style = (Style)FindResource("SegmentTrack"),
            Margin = new Thickness(0, 14, 0, 0),
        };
        var segments = new UniformGrid { Columns = 3 };
        var labels = new[] { "7d", "30d", "3m" };

        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = new RadioButton
            {
                Style = (Style)FindResource("Segment"),
                Content = labels[i],
                IsChecked = i == _range,
                GroupName = "range",
            };
            button.Checked += (_, _) =>
            {
                _range = index;
                Render();
            };
            segments.Children.Add(button);
        }
        track.Child = segments;
        Body.Children.Add(track);

        var span = Spans[_range];
        var now = DateTimeOffset.UtcNow;
        var points = App.Repository.History.Combined()
            .Where(p => now - p.At <= span)
            .ToList();

        // Each window is carried forward on its own burn, the recent rate easing
        // into the usual one. One rate fitted to the average would claim Mistral
        // is being spent as fast as Claude, which is what made the numbers nonsense.
        var contributions = snapshot == null ? [] : CombinedUsage.Contributions(snapshot);
        var tracks = contributions
            .Select(c => Projection.For(
                c.Window,
                App.Repository.History.Series(c.AccountKey, c.Window.Label, Projection.UsualBasis),
                now))
            .Where(t => t != null)
            .Select(t => t!)
            .ToList();

        var resets = contributions
            .Where(c => c.Window.ResetAt is { } at && at > now)
            .GroupBy(c => c.Window.ResetAt!.Value)
            .Select(g => new DrawdownChart.Reset(g.Key, g.Select(c => c.Provider).Distinct().ToList()))
            .OrderBy(r => r.At)
            .ToList();

        var projection = Projection.Combined(
            tracks,
            now,
            now + span / 2,
            points.Count > 0 ? points[^1].Percent : null);

        var chart = new DrawdownChart
        {
            Points = points,
            LineColor = Severity.ColorFor(percent ?? 100f),
            Span = span,
            Resets = resets,
            Projection = projection,
            Height = 220,
            Margin = new Thickness(0, 16, 0, 0),
        };
        Body.Children.Add(chart);

        // The chart projects the fitted burn rate past now, so the caption can
        // answer the question the screen exists for: when does it run out?
        var exhaustion = chart.ProjectedExhaustion();
        Body.Children.Add(new TextBlock
        {
            Text = points.Count < 2
                ? ""
                : exhaustion == null
                    ? "Holding steady at this rate"
                    : $"At this rate, empty around {Format.Day(exhaustion.Value)}",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 8, 0, 0),
        });

        Body.Children.Add(new TextBlock
        {
            Text = "What counts",
            Style = (Style)FindResource("CardTitle"),
            Margin = new Thickness(0, 22, 0, 0),
        });

        var rows = 0;
        foreach (var usage in snapshot?.Usages ?? [])
        {
            var windows = CombinedUsage.LongWindows(usage);
            if (CombinedUsage.Level(windows) is not { } level) continue;

            Body.Children.Add(CombinedRow(usage, windows, level));
            rows++;
        }

        if (rows == 0)
        {
            Body.Children.Add(new TextBlock
            {
                Text = "No subscriptions yet",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    /// <summary>One provider's contribution: its mark, its windows, its level.</summary>
    private Border CombinedRow(ProviderUsage usage, IReadOnlyList<UsageWindow> windows, float level)
    {
        var id = usage.Id;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mark = new System.Windows.Shapes.Path
        {
            Data = ProviderArt.Logo(id),
            Fill = (Brush)FindResource("TextPrimaryBrush"),
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        grid.Children.Add(mark);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = usage.DisplayName,
            Style = (Style)FindResource("CardTitle"),
        });

        // "7d · resets 3d 4h", or just the labels when the provider gives no reset.
        var labels = string.Join(" · ", windows.Select(w => w.Label).Where(l => l.Length > 0));
        var reset = windows
            .Select(w => w.ResetAt)
            .Where(at => at != null)
            .Max()
            is { } furthest
            ? Format.Reset(furthest)
            : null;
        var caption = string.Join(
            " · ",
            new[] { labels, reset == null ? null : $"resets in {reset}" }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        text.Children.Add(new TextBlock
        {
            Text = caption,
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var figure = new TextBlock
        {
            Text = Format.Percent(level),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Severity.TextBrush(level),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(figure, 2);
        grid.Children.Add(figure);

        // The same bar the provider cards carry, so the level reads at a glance
        // without parsing the figure.
        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(new Meter
        {
            Fill = level / 100.0,
            FillBrush = Severity.MeterBrush(level),
            TrackBrush = (Brush)FindResource("MeterTrackBrush"),
            Height = 5,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(13, 11, 13, 11),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = stack,
        };
        card.MouseLeftButtonUp += (_, _) => Navigate?.Invoke(usage.AccountKey);
        return card;
    }

    private Border Badge(string text) => new()
    {
        Background = (Brush)FindResource("ControlFillBrush"),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 2, 7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("Hint"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
        },
    };
}
