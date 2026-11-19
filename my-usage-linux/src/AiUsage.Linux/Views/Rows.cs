using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>The lines both pages share: a window with its bar, and where a level is heading.</summary>
public static class Rows
{
    /// <summary>
    /// A window: its name and when it refills, what is left, then the bar. In
    /// the overview only what needs a look gets a badge, and the flame alone;
    /// an account's page spells the pace out (<paramref name="detail"/>).
    /// </summary>
    public static Control Window(UsageWindow window, DateTimeOffset now, bool detail = false)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        name.Children.Add(Bits.Text(WindowName.Display(window.Label), 11.5, FontWeight.SemiBold,
            window.CountsTowardCombined ? Look.Primary : Look.Secondary));
        if (Format.Reset(window.ResetAt) is { } reset)
        {
            name.Children.Add(Bits.Text($"resets in {reset}", 10.5, FontWeight.Normal, Look.Secondary, digits: true));
        }
        head.Children.Add(name);

        var figures = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var pace = Pace.Of(window, now);
        if (pace == Pace.Kind.Empty)
        {
            figures.Children.Add(Bits.PaceBadge(Pace.Kind.Empty));
        }
        else
        {
            if (detail && pace is { } kind) figures.Children.Add(Bits.PaceBadge(kind));
            else if (pace == Pace.Kind.Ahead) figures.Children.Add(Bits.PaceBadge(Pace.Kind.Ahead, compact: true));
            figures.Children.Add(Bits.LevelText(window.RemainingPercent));
        }
        Grid.SetColumn(figures, 2);
        head.Children.Add(figures);

        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(head);
        if (window.RemainingPercent is { } level)
        {
            row.Children.Add(new UsageBar(level, Pace.EvenPercent(window, now)));
        }
        else if (window.Detail is { Length: > 0 } text)
        {
            row.Children.Add(Bits.Text(text, 10.5, FontWeight.Normal, Look.Secondary));
        }
        return row;
    }

    /// <summary>A small glyph, then one or two lines beside it: the rates, and where they end up.</summary>
    public static Control Burn(Geometry glyph, IBrush glyphBrush, string rates, string? outcome, bool alarming)
    {
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(Bits.Text(rates, 11, FontWeight.Normal, Look.Secondary, digits: true));
        if (!string.IsNullOrEmpty(outcome))
        {
            lines.Children.Add(Bits.Text(outcome, 11, alarming ? FontWeight.SemiBold : FontWeight.Normal,
                alarming ? Look.Solid(Look.Critical) : Look.Secondary, digits: true));
        }

        var icon = Icons.Glyph(glyph, 12, glyphBrush);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 6, 0);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(icon);
        Grid.SetColumn(lines, 1);
        row.Children.Add(lines);
        return row;
    }
}
