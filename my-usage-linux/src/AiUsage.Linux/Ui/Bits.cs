using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiUsage.Ui;

/// <summary>The small pieces every page is put together from.</summary>
public static class Bits
{
    private static readonly FontFeatureCollection TabularDigits = FontFeatureCollection.Parse("tnum");

    /// <summary>A line of text. <paramref name="digits"/> keeps figures the same width, so a column of them lines up.</summary>
    public static TextBlock Text(string text, double size, FontWeight weight, IBrush brush, bool digits = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        FontFeatures = digits ? TabularDigits : null,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Text that wraps where the page is narrower than it is long.</summary>
    public static TextBlock Paragraph(string text, double size, IBrush brush) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = size * 1.35,
    };

    /// <summary>A surface for content on the panel: a faint fill and a hairline lit from above.</summary>
    public static Border Card(Control content, double padding = 12, double radius = 18) => new()
    {
        Child = content,
        Padding = new Thickness(padding),
        CornerRadius = new CornerRadius(radius),
        Background = Look.Tile(),
        BorderBrush = Look.TileEdge,
        BorderThickness = new Thickness(0.75),
    };

    /// <summary>A capsule round a child: the shape of chips, badges and soft buttons.</summary>
    public static Border Capsule(Control child, IBrush fill, IBrush edge, Thickness padding) => new()
    {
        Child = child,
        Padding = padding,
        CornerRadius = new CornerRadius(999),
        Background = fill,
        BorderBrush = edge,
        BorderThickness = new Thickness(0.5),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A small label in a capsule: "Sample", "Earlier reading".</summary>
    public static Border Chip(string text, Color? tint = null, Geometry? glyph = null)
    {
        var brush = tint is { } colour ? Look.Solid(colour) : Look.Secondary;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        if (glyph != null) row.Children.Add(Icons.Glyph(glyph, 9.5, brush));
        row.Children.Add(Text(text, 10, FontWeight.SemiBold, brush));
        return Capsule(row, Look.Ink(0.07), Look.Ink(0.08), new Thickness(7, 2.5));
    }

    /// <summary>"Ahead of pace" with a flame; compact is the flame alone.</summary>
    public static Border PaceBadge(Pace.Kind pace, bool compact = false)
    {
        var tint = pace switch
        {
            Pace.Kind.Empty => Look.Critical,
            Pace.Kind.Ahead => Look.Warning,
            Pace.Kind.Under => Look.Normal,
            _ => Look.InkColor,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        if (pace is Pace.Kind.Ahead or Pace.Kind.Empty) row.Children.Add(Icons.Glyph(Icons.Flame, compact ? 10 : 10.5, Look.Solid(tint)));
        // The hue is on the glyph and the capsule; the words stay in the label colour.
        if (!compact) row.Children.Add(Text(Pace.Text(pace), 10, FontWeight.SemiBold, Look.Primary));
        var badge = Capsule(
            row,
            Look.Solid(tint, pace == Pace.Kind.On ? 0.1 : 0.24),
            Look.Solid(tint, pace == Pace.Kind.On ? 0.16 : 0.45),
            compact ? new Thickness(4, 2.5) : new Thickness(7, 3));
        ToolTip.SetTip(badge, Pace.Help(pace));
        return badge;
    }

    /// <summary>A row's figure: "62%" and, quieter, "left".</summary>
    public static Control LevelText(float? percent, double size = 13)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5 };
        var figure = Text(percent is { } p ? Format.Percent(p) : "—", size, FontWeight.SemiBold,
            percent is { } level ? Look.TextOf(level) : Look.Secondary, digits: true);
        row.Children.Add(figure);
        if (percent != null)
        {
            var left = Text("left", size * 0.8, FontWeight.Medium, Look.Secondary);
            left.VerticalAlignment = VerticalAlignment.Bottom;
            left.Margin = new Thickness(0, 0, 0, size * 0.06);
            row.Children.Add(left);
        }
        return row;
    }

    /// <summary>
    /// Something to click that is not a button to look at: it answers the
    /// pointer, Enter and Space, takes the keyboard focus, and says what it does.
    /// <paramref name="hover"/> is told when the pointer or the focus is on it.
    /// </summary>
    public static T Clickable<T>(T control, string help, Action act, Action<bool>? hover = null) where T : Control
    {
        control.Cursor = new Cursor(StandardCursorType.Hand);
        control.Focusable = true;
        ToolTip.SetTip(control, help);
        Avalonia.Automation.AutomationProperties.SetName(control, help);

        var pressed = false;
        control.PointerEntered += (_, _) => hover?.Invoke(true);
        control.PointerExited += (_, _) => { pressed = false; hover?.Invoke(false); };
        control.GotFocus += (_, _) => hover?.Invoke(true);
        control.LostFocus += (_, _) => hover?.Invoke(false);
        control.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) pressed = true;
        };
        control.PointerReleased += (_, e) =>
        {
            if (!pressed || e.InitialPressMouseButton != MouseButton.Left) return;
            pressed = false;
            e.Handled = true;
            act();
        };
        control.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            act();
        };
        return control;
    }

    /// <summary>A glyph in a soft circle: refresh, quit.</summary>
    public static Border IconButton(Geometry glyph, string help, Action act, double size = 30)
    {
        var button = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = Look.Ink(0.09),
            BorderBrush = Look.Ink(0.12),
            BorderThickness = new Thickness(0.5),
            Child = Icons.Glyph(glyph, size * 0.5, Look.Primary),
        };
        return Clickable(button, help, act, over => button.Background = Look.Ink(over ? 0.16 : 0.09));
    }

    /// <summary>Words in a soft capsule: the quiet kind of button.</summary>
    public static Border SoftButton(string text, string help, Action act, bool prominent = false)
    {
        var rest = prominent ? Look.Solid(Look.Normal, 0.9) : Look.Ink(0.09);
        var over = prominent ? Look.Solid(Look.NormalLight, 0.95) : Look.Ink(0.16);
        var label = Text(text, 11.5, FontWeight.Medium, prominent ? Brushes.White : Look.Primary);
        var button = Capsule(label, rest, Look.Ink(0.12), new Thickness(11, 5));
        return Clickable(button, help, act, hovering => button.Background = hovering ? over : rest);
    }
}
