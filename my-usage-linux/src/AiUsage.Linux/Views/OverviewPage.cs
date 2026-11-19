using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// Every subscription on one page: the combined figure up top with where it is
/// heading, then each account, the one about to run out first.
/// </summary>
public static class OverviewPage
{
    public static Control Build(AppModel model, DateTimeOffset now, Action<string> open)
    {
        var page = new StackPanel { Spacing = 10 };
        if (model.Combined is { Percent: { } percent } combined)
        {
            page.Children.Add(CombinedCard(model, percent, combined.Providers, now));
        }
        foreach (var usage in model.OverviewOrder())
        {
            page.Children.Add(AccountCard(model, usage, now, () => open(usage.AccountKey)));
        }
        return page;
    }

    /// <summary>The hero: one number for everything, its week, and the forecast.</summary>
    private static Control CombinedCard(AppModel model, float percent, int providers, DateTimeOffset now)
    {
        var stack = new StackPanel { Spacing = 10 };

        var ring = new Ring(percent, 66, 6, Bits.Text("Σ", 19, FontWeight.Bold, Look.Secondary));

        var figures = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var eyebrow = Bits.Text("COMBINED", 9.5, FontWeight.Bold, Look.Secondary);
        eyebrow.LetterSpacing = 0.8;
        figures.Children.Add(eyebrow);

        var ink = Look.TextOf(percent);
        var number = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, -2) };
        number.Children.Add(Baseline(Bits.Text(((int)Math.Round(percent)).ToString(), 40, FontWeight.SemiBold, ink, digits: true), 0));
        number.Children.Add(Baseline(Bits.Text("%", 22, FontWeight.SemiBold, ink), 6.5));
        number.Children.Add(Baseline(Bits.Text(" left", 14, FontWeight.Medium, Look.Secondary), 7.5));
        figures.Children.Add(number);
        figures.Children.Add(Bits.Text($"across {providers} subscription{(providers == 1 ? "" : "s")}", 11, FontWeight.Normal, Look.Secondary));

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        top.Children.Add(ring);
        top.Children.Add(figures);
        stack.Children.Add(top);

        var week = model.CombinedWeek(now);
        if (week.Count > 1) stack.Children.Add(new HistoryChart(week, TimeSpan.FromDays(7), now, 64));

        if (model.CombinedOutlook(now) is { } outlook)
        {
            var lift = outlook is { Draining: true, NextReset: { } at }
                ? $"Next lift {Format.When(at)} — {outlook.NextName} resets"
                : outlook.Draining ? "No reset reported" : null;
            stack.Children.Add(Rows.Burn(
                outlook.Draining ? Icons.Gauge : Icons.Moon, Look.Secondary, outlook.Rates, lift, alarming: false));
        }

        return Bits.Card(stack, padding: 14);
    }

    /// <summary>Text of different sizes on one line sits on one baseline, not one bottom edge.</summary>
    private static TextBlock Baseline(TextBlock text, double lift)
    {
        text.VerticalAlignment = VerticalAlignment.Bottom;
        text.Margin = new Thickness(0, 0, 0, lift);
        return text;
    }

    /// <summary>One account: its mark, its plan, and a bar for every window.</summary>
    private static Control AccountCard(AppModel model, ProviderUsage usage, DateTimeOffset now, Action open)
    {
        var ok = usage.State == UsageState.Ok;
        var stack = new StackPanel { Spacing = 10 };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        head.Children.Add(new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Look.Ink(0.07),
            Child = Icons.Glyph(ProviderArt.Logo(usage.Id), 15, ok ? Look.Primary : Look.Secondary),
        });

        var names = new StackPanel { Margin = new Thickness(9, 0, 4, 0), Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Bits.Text(usage.DisplayName, 13, FontWeight.SemiBold, ok ? Look.Primary : Look.Secondary));
        if (!string.IsNullOrWhiteSpace(usage.Plan)) names.Children.Add(Bits.Text(usage.Plan, 10.5, FontWeight.Medium, Look.Secondary));
        Grid.SetColumn(names, 1);
        head.Children.Add(names);

        if (usage.Stale && ok)
        {
            var earlier = Bits.Chip("Earlier reading", glyph: Icons.Clock);
            earlier.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(earlier, 2);
            head.Children.Add(earlier);
        }

        var chevron = Icons.Glyph(Icons.Chevron, 11, Look.Secondary);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 3);
        head.Children.Add(chevron);
        stack.Children.Add(head);

        if (!ok)
        {
            var unset = usage.State == UsageState.NotConfigured;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(Icons.Glyph(Icons.Warning, 12, unset ? Look.Secondary : Look.Solid(Look.Warning)));
            row.Children.Add(Bits.Text(unset ? "Not set up" : usage.Message ?? "Unavailable", 11.5, FontWeight.Normal, Look.Secondary));
            stack.Children.Add(row);
        }
        else if (usage.Windows.Count == 0)
        {
            stack.Children.Add(Bits.Text("No limits reported", 11.5, FontWeight.Normal, Look.Secondary));
        }
        else
        {
            var shown = model.Shown(usage);
            var hidden = usage.Windows.Count - shown.Count;
            var rows = new StackPanel { Spacing = 7 };
            foreach (var window in shown) rows.Children.Add(Rows.Window(window, now));
            if (hidden > 0)
            {
                var note = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                note.Children.Add(Icons.Glyph(Icons.EyeOff, 11, Look.Secondary));
                note.Children.Add(Bits.Text(shown.Count == 0 ? "All limits hidden" : $"{hidden} hidden", 10.5, FontWeight.Normal, Look.Secondary));
                rows.Children.Add(note);
            }
            stack.Children.Add(rows);
        }

        var card = Bits.Card(stack);
        return Bits.Clickable(card, $"Show {usage.DisplayName}'s history and forecast", open, over =>
        {
            card.Background = Look.Tile(highlighted: over);
            chevron.RenderTransform = over ? new TranslateTransform(2, 0) : null;
        });
    }
}
