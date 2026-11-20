using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace AiUsage.Ui;

/// <summary>
/// The panel's scroll bar: a thin pill in the label colour over the page's
/// margin, with no track and no arrows, as Windows draws it. It is out of sight
/// until the pointer is over what scrolls, as the Mac's overlay scroller is; it
/// firms up under the pointer and while dragged.
/// </summary>
public static class ScrollBars
{
    private const double Width = 10;
    private const double Pill = 5;

    public static Style Style() => new(x => x.OfType<ScrollBar>())
    {
        Setters =
        {
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ScrollBar>(Build)),
            new Setter(Layoutable.MinWidthProperty, 0d),
            new Setter(Layoutable.MinHeightProperty, 0d),
        },
    };

    private static Control Build(ScrollBar bar, INameScope scope)
    {
        var vertical = bar.Orientation == Orientation.Vertical;
        var pill = new Border
        {
            CornerRadius = new CornerRadius(Pill / 2),
            Width = vertical ? Pill : double.NaN,
            Height = vertical ? double.NaN : Pill,
            HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center,
        };
        var thumb = new Thumb
        {
            MinHeight = vertical ? 28 : 0,
            MinWidth = vertical ? 0 : 28,
            Opacity = 0,
            Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) } },
            Template = new FuncControlTemplate<Thumb>((_, _) => pill),
        };
        var track = new Track
        {
            Name = "PART_Track",
            Orientation = bar.Orientation,
            IsDirectionReversed = vertical,
            Thumb = thumb,
        };
        track[!Track.MinimumProperty] = bar[!RangeBase.MinimumProperty];
        track[!Track.MaximumProperty] = bar[!RangeBase.MaximumProperty];
        track[!!Track.ValueProperty] = bar[!!RangeBase.ValueProperty];
        track[!Track.ViewportSizeProperty] = bar[!ScrollBar.ViewportSizeProperty];
        track.RegisterInNameScope(scope);

        var root = new Panel
        {
            Background = Avalonia.Media.Brushes.Transparent,
            Width = vertical ? Width : double.NaN,
            Height = vertical ? double.NaN : Width,
            Children = { track },
        };

        // How strong the pill is: dragged, under the pointer, over what scrolls,
        // or not there. The colour is read each time, so it follows light and dark.
        var dragging = false;
        ScrollViewer? viewer = null;
        void Show()
        {
            pill.Background = Look.Ink(1);
            thumb.Opacity = dragging ? 0.55
                : thumb.IsPointerOver ? 0.42
                : bar.IsPointerOver || viewer?.IsPointerOver == true ? 0.24
                : 0;
        }
        void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == InputElement.IsPointerOverProperty) Show();
        }
        thumb.DragStarted += (_, _) => { dragging = true; Show(); };
        thumb.DragCompleted += (_, _) => { dragging = false; Show(); };
        thumb.PropertyChanged += Changed;
        bar.PropertyChanged += Changed;
        root.AttachedToVisualTree += (_, _) =>
        {
            viewer = bar.FindAncestorOfType<ScrollViewer>();
            if (viewer != null) viewer.PropertyChanged += Changed;
            Show();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            if (viewer != null) viewer.PropertyChanged -= Changed;
            viewer = null;
        };
        return root;
    }
}
