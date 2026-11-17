using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiUsage.Views;

/// <summary>
/// The caption strip for the windows that draw their own chrome: the title on the
/// left, the three system buttons on the right. Dragging, double-click to maximise
/// and Aero snap come from <see cref="System.Windows.Shell.WindowChrome"/>, so none
/// of that is reimplemented here.
/// </summary>
public partial class TitleBar : UserControl
{
    private const string MaximiseGlyphData = "M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z";
    private const string RestoreGlyphData = "M 2.5,0.5 L 9.5,0.5 L 9.5,7.5 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z";

    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
    }

    private void Attach()
    {
        _window = Window.GetWindow(this);
        if (_window == null) return;

        _window.StateChanged += (_, _) => SyncGlyph();
        SyncGlyph();
    }

    private void SyncGlyph()
    {
        var maximised = _window?.WindowState == WindowState.Maximized;
        MaximiseGlyph.Data = Geometry.Parse(maximised ? RestoreGlyphData : MaximiseGlyphData);
        MaximiseButton.ToolTip = maximised ? "Restore" : "Maximise";
    }

    private void OnMinimise(object sender, RoutedEventArgs e)
    {
        if (_window != null) _window.WindowState = WindowState.Minimized;
    }

    private void OnMaximise(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => _window?.Close();
}
