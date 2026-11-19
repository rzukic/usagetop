using System.Runtime.InteropServices;

namespace AiUsage;

/// <summary>
/// What kind of desktop this is, as far as it changes what the app does: Linux
/// has no one way to show a tray icon, float a window or blur behind it, so the
/// app looks at where it is and takes what is on offer. `usagetop doctor` prints it.
/// </summary>
public static class Desktop
{
    private static readonly string[] TilingNames =
    [
        "sway", "i3", "hyprland", "bspwm", "awesome", "dwm", "niri", "river", "qtile", "xmonad",
        "herbstluftwm", "leftwm", "spectrwm", "wayfire", "labwc", "dwl",
    ];

    /// <summary>"KDE", "GNOME", "ubuntu:GNOME", "sway", "Hyprland"; empty when the session does not say.</summary>
    public static string Name { get; } =
        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
        ?? Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP")
        ?? Environment.GetEnvironmentVariable("DESKTOP_SESSION")
        ?? "";

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsKde => Has("KDE");

    public static bool IsGnome => Has("GNOME");

    /// <summary>A window manager that tiles: a panel there is a window like any other unless it asks to float.</summary>
    public static bool IsTiling { get; } = TilingNames.Any(Has);

    public static bool IsWayland { get; } =
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>Desktops whose panel, and so whose tray, sits along the bottom unless moved.</summary>
    public static bool PanelAtBottom => IsKde || Has("Cinnamon") || Has("LXQt");

    /// <summary>
    /// Whether windows can be see-through. A Wayland compositor always
    /// composites; on X11 it takes a compositing manager, which a bare i3 or
    /// bspwm does not have. Without one a rounded, shadowed panel comes out
    /// with black corners, so the panel is drawn square and opaque instead.
    /// </summary>
    public static bool IsComposited { get; } = DetectCompositing();

    /// <summary>Where a socket for this user and session belongs.</summary>
    public static string RuntimeDir
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)) return runtime;
            var fallback = Path.Combine(Path.GetTempPath(), $"usagetop-{Environment.UserName}");
            Directory.CreateDirectory(fallback);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fallback, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return fallback;
        }
    }

    public static string Describe() => string.Join(Environment.NewLine,
    [
        $"desktop      {(Name.Length == 0 ? "(not reported)" : Name)}",
        $"session      {(IsWayland ? "Wayland" : "X11")}",
        $"tiling       {(IsTiling ? "yes" : "no")}",
        $"compositing  {(IsComposited ? "yes" : "no: the panel is drawn square and opaque")}",
        $"blur         {(IsKde && IsComposited ? "asked of KWin" : "left to the compositor, if it blurs see-through windows")}",
        $"socket       {Ipc.SocketPath}",
    ]);

    private static bool Has(string name) =>
        Name.Split(':', ';').Any(part => part.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool DetectCompositing()
    {
        if (!OperatingSystem.IsLinux() || IsWayland) return true;
        try
        {
            var display = XOpenDisplay(null);
            if (display == IntPtr.Zero) return true;
            try
            {
                // A compositing manager owns _NET_WM_CM_S<screen> for as long as it runs.
                var atom = XInternAtom(display, $"_NET_WM_CM_S{XDefaultScreen(display)}", false);
                return XGetSelectionOwner(display, atom) != IntPtr.Zero;
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr atom);
}
