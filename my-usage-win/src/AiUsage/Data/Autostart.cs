using Microsoft.Win32;

namespace AiUsage.Data;

/// <summary>
/// The Run key entry that starts the app with Windows. A tray app that has to be
/// launched by hand is not really living in the taskbar.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "usagetop";

    /// <summary>The entry's name before the app was called usagetop; cleared whenever the entry is written.</summary>
    private const string LegacyValueName = "AiUsage";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string || key?.GetValue(LegacyValueName) is string;
        }
    }

    /// <summary>
    /// Writes the quoted path of the running executable, so a build in a different
    /// folder replaces the old entry instead of leaving a dead one behind.
    /// </summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (enabled)
        {
            var path = Environment.ProcessPath;
            if (path != null) key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
