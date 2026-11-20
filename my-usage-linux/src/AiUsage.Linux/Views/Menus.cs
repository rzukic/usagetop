using Avalonia.Controls;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>The right-click menus: a limit hidden from the overview or shown in it again.</summary>
public static class Menus
{
    /// <summary>
    /// "Hide Weekly from the overview", or "Show … in the overview" for one
    /// already hidden. Hidden, it stays on its account's page; with a server the
    /// list is the server's, for every app that reads from it.
    /// </summary>
    public static ContextMenu Hide(AppModel model, ProviderUsage usage, UsageWindow window)
    {
        var hidden = model.IsHidden(usage, window);
        var name = WindowName.Display(window.Label);
        var item = new MenuItem
        {
            Header = hidden ? $"Show {name} in the overview" : $"Hide {name} from the overview",
            Icon = Icons.Glyph(Icons.EyeOff, 14, Look.Secondary),
        };
        var key = Limits.Key(usage, window);
        item.Click += (_, _) => _ = model.SetHiddenAsync(key, !hidden);
        return new ContextMenu { Items = { item } };
    }
}
