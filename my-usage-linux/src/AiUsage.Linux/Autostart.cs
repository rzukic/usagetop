namespace AiUsage;

/// <summary>
/// "Open at login": an entry in the XDG autostart folder that every common
/// desktop reads at login, starting the app as the tray icon alone. With the
/// GNOME extension it is not needed there: the extension starts the app.
/// </summary>
public static class Autostart
{
    private static string EntryPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "usagetop.desktop");

    public static bool Enabled => File.Exists(EntryPath);

    public static void Set(bool on)
    {
        if (!on)
        {
            if (File.Exists(EntryPath)) File.Delete(EntryPath);
            return;
        }

        // The program itself, or the link to it on the PATH where there is one:
        // a link survives the app being moved to a new folder by an update.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var link = Path.Combine(home, ".local", "bin", "usagetop");
        var program = File.Exists(link) ? link : Environment.ProcessPath ?? "usagetop";
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        File.WriteAllText(EntryPath, string.Join('\n',
        [
            "[Desktop Entry]",
            "Type=Application",
            "Name=usagetop",
            "Comment=How much of each AI subscription is left",
            $"Exec=\"{program}\" --hidden",
            "Icon=usagetop",
            "Terminal=false",
            "X-GNOME-Autostart-enabled=true",
            "",
        ]));
    }
}
