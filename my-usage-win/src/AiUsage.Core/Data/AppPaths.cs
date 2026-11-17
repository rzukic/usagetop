namespace AiUsage.Data;

/// <summary>Where the app keeps its state: one folder under %APPDATA%.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AiUsage");

    public static string Credentials => Path.Combine(Root, "credentials.json");

    public static string History => Path.Combine(Root, "history.json");

    public static string Snapshot => Path.Combine(Root, "snapshot.json");

    public static string Log => Path.Combine(Root, "ai-usage.log");

    public static void Ensure() => Directory.CreateDirectory(Root);
}
