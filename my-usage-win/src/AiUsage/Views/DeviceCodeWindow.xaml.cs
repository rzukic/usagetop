using System.Diagnostics;
using System.Windows;

namespace AiUsage.Views;

/// <summary>
/// Shows the device code while the browser waits for it. It stays on top of the
/// browser so the code is readable without switching back.
/// </summary>
public partial class DeviceCodeWindow : Window
{
    private readonly string _url;

    public DeviceCodeWindow(string code, string url)
    {
        InitializeComponent();
        CodeText.Text = code;
        _url = url;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CodeText.Text);
        }
        catch
        {
            // Another process holding the clipboard is not worth an error dialog.
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        }
        catch
        {
            // The browser is already open from the sign-in attempt.
        }
    }
}
