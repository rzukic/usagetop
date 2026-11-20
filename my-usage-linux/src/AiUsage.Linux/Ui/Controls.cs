using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiUsage.Ui;

/// <summary>A label and an on/off switch in the panel's own look: a capsule with a knob.</summary>
public sealed class Switch : Grid
{
    private readonly Border _track;
    private readonly Ellipse _knob;
    private readonly Action<bool> _changed;

    public Switch(string label, bool on, Action<bool> changed)
    {
        _changed = changed;
        On = on;
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Background = Brushes.Transparent;

        var text = Bits.Text(label, 12, FontWeight.Normal, Look.Primary);
        text.TextWrapping = TextWrapping.Wrap;
        Children.Add(text);

        _knob = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        _track = new Border
        {
            Width = 34,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _knob,
        };
        Grid.SetColumn(_track, 1);
        Children.Add(_track);
        Draw();

        Bits.Clickable(this, label, () =>
        {
            On = !On;
            Draw();
            _changed(On);
        });
        Avalonia.Automation.AutomationProperties.SetName(this, label);
    }

    public bool On { get; private set; }

    private void Draw()
    {
        _track.Background = On ? Look.Solid(Look.Normal) : Look.Ink(0.2);
        _knob.Margin = new Thickness(On ? 14 : 0, 0, 0, 0);
    }
}

/// <summary>A row of choices in one capsule, the chosen one filled: the Mac's segmented picker.</summary>
public sealed class Segmented : Border
{
    public Segmented(IReadOnlyList<string> choices, int chosen, Action<int> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var selected = i == chosen;
            var label = Bits.Text(choices[i], 11.5, selected ? FontWeight.SemiBold : FontWeight.Medium, selected ? Look.Primary : Look.Secondary);
            var segment = new Border
            {
                Padding = new Thickness(10, 3.5),
                CornerRadius = new CornerRadius(999),
                Background = selected ? Look.Ink(Look.Dark ? 0.2 : 0.12) : Brushes.Transparent,
                Child = label,
            };
            Bits.Clickable(segment, choices[i], () => changed(index), over =>
            {
                if (!selected) segment.Background = over ? Look.Ink(0.08) : Brushes.Transparent;
            });
            row.Children.Add(segment);
        }
        Child = row;
        Padding = new Thickness(2);
        CornerRadius = new CornerRadius(999);
        Background = Look.Ink(0.07);
        BorderBrush = Look.Ink(0.1);
        BorderThickness = new Thickness(0.5);
        HorizontalAlignment = HorizontalAlignment.Left;
    }
}
