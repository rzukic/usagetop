using System.IO;
using System.Text.Json;
using AiUsage.Data;

namespace AiUsage.Ui;

/// <summary>
/// The panel's own preferences, as the Mac app keeps them: how much of it is
/// glass, how solid its tiles are, the order of the accounts, whether the
/// overview puts the account closest to running out first and which limits it
/// leaves out. Kept in a small file beside the app's other state; a change is
/// raised at once so the panel follows a slider while it moves.
/// </summary>
public sealed class PanelPrefs
{
    private static PanelPrefs? _current;

    public static PanelPrefs Current => _current ??= Load();

    /// <summary>The defaults in place of what is saved, for pictures that should not depend on this PC.</summary>
    public static void UseDefaults() => _current = new PanelPrefs();

    /// <summary>0 is a flat, opaque panel; 1 is the backdrop with only its tint.</summary>
    public double Glass { get; set; } = 1;

    /// <summary>0 is the faint, see-through tile; 1 is an opaque one.</summary>
    public double Tiles { get; set; }

    /// <summary>Account keys in the order wanted; accounts not in it follow, by provider.</summary>
    public List<string> Order { get; set; } = [];

    public bool OverviewByRisk { get; set; } = true;

    /// <summary>
    /// Limits left out of the overview, the tray and the rail's rings, as
    /// "account|label" (<see cref="Limits.Key(string, string)"/>, the key their
    /// history is filed under). They stay on their account's page. On this PC
    /// only, and set aside while a server keeps the list for every app
    /// (<see cref="UsageRepository.HiddenLimits"/>).
    /// </summary>
    public List<string> HiddenLimits { get; set; } = [];

    /// <summary>Hides or shows one limit, keeping the order the others were hidden in.</summary>
    public void SetHidden(string key, bool hide) => Update(p => p.HiddenLimits = Limits.With(p.HiddenLimits, key, hide));

    /// <summary>
    /// What the tray icon shows, the Windows side of the Mac's menu bar setting:
    /// false is the combined level, true the one limit closest to running out.
    /// </summary>
    public bool TrayShowsClosest { get; set; }

    /// <summary>The figure as a number in place of the ring: a 16px icon has no room beside it.</summary>
    public bool TrayShowsNumber { get; set; }

    /// <summary>
    /// Also take GitHub pre-releases (v1.2.1-beta.1). Releases still come too:
    /// whichever is newest wins, so a beta user is never left behind a release.
    /// </summary>
    public bool BetaUpdates { get; set; }

    public event Action? Changed;

    private static string FilePath => Path.Combine(AppPaths.Root, "panel.json");

    /// <summary>Saves and tells every open panel.</summary>
    public void Update(Action<PanelPrefs> change)
    {
        change(this);
        Glass = Math.Clamp(Glass, 0, 1);
        Tiles = Math.Clamp(Tiles, 0, 1);
        Changed?.Invoke();
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (IOException)
        {
            // A preference that did not save is not worth interrupting anyone over.
        }
    }

    private static PanelPrefs Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<PanelPrefs>(File.ReadAllText(FilePath)) is { } loaded)
            {
                loaded.HiddenLimits ??= [];
                return loaded;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }
        return new PanelPrefs();
    }
}
