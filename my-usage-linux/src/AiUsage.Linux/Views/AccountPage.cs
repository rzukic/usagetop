using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// One account: every window with its history and where it is heading. The
/// limits shown in the overview come first, the hidden ones after.
/// </summary>
public static class AccountPage
{
    public static Control Build(AppModel model, ProviderUsage usage, DateTimeOffset now)
    {
        var page = new StackPanel { Spacing = 10 };

        if (usage.State != UsageState.Ok)
        {
            var unset = usage.State == UsageState.NotConfigured;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var mark = Icons.Glyph(Icons.Warning, 13, unset ? Look.Secondary : Look.Solid(Look.Warning));
            mark.Margin = new Thickness(0, 1, 8, 0);
            mark.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(mark);
            var words = Bits.Paragraph(unset ? "Not set up" : usage.Message ?? "Unavailable", 12, Look.Secondary);
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            page.Children.Add(Bits.Card(row, padding: 14));
            return page;
        }

        if (usage.Windows.Count == 0)
        {
            page.Children.Add(Bits.Card(Bits.Text("No limits reported", 12, FontWeight.Normal, Look.Secondary), padding: 14));
            return page;
        }

        var shown = model.Shown(usage);
        var hidden = usage.Windows.Where(w => !shown.Contains(w)).ToList();
        foreach (var window in shown) page.Children.Add(WindowCard(model, usage, window, now));
        // Hidden ones after the shown ones, dimmed; a right-click shows them again.

        if (hidden.Count > 0)
        {
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(4, 6, 0, 0) };
            heading.Children.Add(Icons.Glyph(Icons.EyeOff, 11.5, Look.Secondary));
            heading.Children.Add(Bits.Text("Hidden from the overview", 11, FontWeight.SemiBold, Look.Secondary));
            page.Children.Add(heading);
            foreach (var window in hidden)
            {
                var card = WindowCard(model, usage, window, now);
                card.Opacity = 0.6;
                page.Children.Add(card);
            }
        }
        return page;
    }

    private static Control WindowCard(AppModel model, ProviderUsage usage, UsageWindow window, DateTimeOffset now)
    {
        var stack = new StackPanel { Spacing = 9 };
        stack.Children.Add(Rows.Window(window, now, detail: true));

        // A window that refills within a day charts half a day, a couple of its
        // cycles: a week of 5-hour cycles is a comb.
        var span = Projection.PeriodOf(window.Label) is { } period && period < TimeSpan.FromDays(1)
            ? TimeSpan.FromHours(12)
            : UsageHistory.ChartSpan(window);
        var series = model.Series(usage, window, span);
        if (series.Count > 1) stack.Children.Add(new HistoryChart(series, span, now, 96, model.WindowForecast(usage, window, now), axes: true));

        if (model.Track(usage, window, now) is { Measured: true } track)
        {
            var (rates, outcome, empties) = Format.Burn(track, Projection.EmptyAt(track, now + Projection.Horizon));
            rates = rates.EndsWith(" · ", StringComparison.Ordinal) ? rates[..^3] : rates;
            var sentence = outcome.Length == 0 ? null : char.ToUpperInvariant(outcome[0]) + outcome[1..];
            stack.Children.Add(Rows.Burn(
                !track.Draining ? Icons.Moon : empties ? Icons.Flame : Icons.Gauge,
                empties ? Look.Solid(Look.Critical) : Look.Secondary,
                rates, sentence, alarming: empties));
        }

        var card = Bits.Card(stack, padding: 13);
        card.ContextMenu = Menus.Hide(model, usage, window);
        ToolTip.SetTip(card, model.IsHidden(usage, window)
            ? "Hidden from the overview. Right-click to show it there again."
            : "Right-click to hide it from the overview");
        return card;
    }
}
