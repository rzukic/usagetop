using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AiUsage.Ui;

/// <summary>
/// A limit on the panel, a row of the overview or a card of an account's page,
/// as something the keyboard and a screen reader can reach. The mouse finds its
/// right-click menu anywhere on it; Tab stops on it, and the menu key or
/// Shift+F10 opens the same menu, which WPF does for whatever has the focus.
/// A plain panel has no automation peer, so without this one its name would
/// never reach UI Automation.
/// </summary>
public sealed class LimitBlock : Decorator
{
    /// <summary>The limit's "account|label" key, so a redrawn page can give it the focus back.</summary>
    public string Key { get; }

    /// <summary>
    /// Wraps <paramref name="child"/>, taking over its margin so the focus ring
    /// hugs the limit and not the gap after it. <paramref name="radius"/> is the
    /// child's own corner, for the ring to follow a little way out.
    /// </summary>
    public LimitBlock(FrameworkElement child, string key, string name, string help, double radius = 0)
    {
        Key = key;
        Margin = child.Margin;
        child.Margin = new Thickness(0);
        Child = child;
        Focusable = true;
        FocusVisualStyle = FocusRing(radius + Gap);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetHelpText(this, help);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(LimitBlock owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(LimitBlock);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    }

    private const double Gap = 4;

    /// <summary>A soft ring around the limit, drawn only when the keyboard brought the focus.</summary>
    private static Style FocusRing(double radius)
    {
        var ring = new FrameworkElementFactory(typeof(Rectangle));
        ring.SetValue(Shape.StrokeProperty, new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)));
        ring.SetValue(Shape.StrokeThicknessProperty, 1.5);
        ring.SetValue(Rectangle.RadiusXProperty, radius);
        ring.SetValue(Rectangle.RadiusYProperty, radius);
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-Gap));
        ring.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = ring }));
        style.Seal();
        return style;
    }
}
