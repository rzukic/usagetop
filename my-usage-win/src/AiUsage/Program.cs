using AiUsage.Data;
using Velopack;

namespace AiUsage;

/// <summary>
/// The entry point, ahead of WPF's own: the installer (Velopack) starts the app
/// with its own arguments at install, update and uninstall, and those have to
/// be answered before any window or tray icon exists. Otherwise it falls
/// through to the app as before.
///
/// The app is installed per user into %LocalAppData%\usagetop, apart from its
/// data in %AppData%\AiUsage, so uninstalling never takes the sign-ins or the
/// history with it.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            // A tray app is meant to be there after a restart, so a fresh install
            // starts with Windows; Settings › General turns it off.
            .OnAfterInstallFastCallback(_ => Autostart.Set(true))
            // The Run key holds the executable's path; keep it pointing at this copy.
            .OnAfterUpdateFastCallback(_ =>
            {
                if (Autostart.Enabled) Autostart.Set(true);
            })
            .OnBeforeUninstallFastCallback(_ => Autostart.Set(false))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
