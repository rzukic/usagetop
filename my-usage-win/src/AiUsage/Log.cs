using AiUsage.Data;

namespace AiUsage;

/// <summary>
/// A tray app has nowhere to show a stack trace, so anything that goes wrong is
/// appended to a file next to the cache. It is the only way to find out why the
/// icon went quiet.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.Ensure();
                File.AppendAllText(
                    AppPaths.Log,
                    $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Nothing left to do if even the log cannot be written.
        }
    }
}
