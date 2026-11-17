using System.Windows.Media;

namespace AiUsage.Ui;

/// <summary>
/// The panel's typeface: Inter, shipped with the app (Assets/Fonts, SIL Open
/// Font License). It is the nearest thing to the Mac's San Francisco, and in
/// WPF its weights hold apart, which Segoe UI Variable's do not reliably. Inter
/// Display, its cut for large sizes, is set tighter for titles and figures.
/// Segoe UI stands in if the resources cannot be read.
/// </summary>
public static class PanelFonts
{
    private static readonly Uri Base = new("pack://application:,,,/");

    public static readonly FontFamily Text = new(Base, "./Assets/Fonts/#Inter, Segoe UI");

    public static readonly FontFamily Display = new(Base, "./Assets/Fonts/#Inter Display, ./Assets/Fonts/#Inter, Segoe UI");
}
