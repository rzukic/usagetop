using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// Settings, in the Mac's order: the server's accounts, the server itself, the
/// order of the accounts, hidden limits, what the top bar shows, the look, and
/// general switches. The page is made once each time it is opened and kept
/// while it is, so a new reading never wipes what is being typed; the sections
/// without fields are drawn again when something in them changes.
/// </summary>
public sealed class SettingsPage : StackPanel
{
    private readonly AppModel _model;
    private readonly Action _saved;
    private readonly Action _applyLook;
    private readonly Action<Pane> _go;
    private readonly StackPanel _sections = new() { Spacing = 10 };
    private AccountsSection? _accounts;
    private Border? _accountsCard;
    private readonly TextBox _address;
    private readonly TextBox _token;
    private readonly TextBlock _result;
    private readonly Border _test;
    private readonly Border _save;
    private bool _busy;

    public SettingsPage(AppModel model, Action saved, Action applyLook, Action<Pane> go)
    {
        _model = model;
        _saved = saved;
        _applyLook = applyLook;
        _go = go;
        Spacing = 10;
        UseFieldLook(this);

        _address = Field("usage.example.com");
        _token = Field("The server's token");
        _token.PasswordChar = '•';
        _token.RevealPassword = false;
        _result = Bits.Paragraph("", 11.5, Look.Secondary);
        _result.IsVisible = false;

        _test = Bits.SoftButton("Test connection", "Asks the server for its reading, without saving anything", () => _ = TestAsync());
        _save = Bits.SoftButton("Save", "Read from this server", () => _ = SaveAsync(), prominent: true);

        var server = new StackPanel { Spacing = 6 };
        server.Children.Add(Bits.Text("Server", 13, FontWeight.SemiBold, Look.Primary));
        server.Children.Add(Bits.Paragraph(
            "Your usagetop server and its token, the same two the other apps ask for. " +
            "Its accounts and their sign-ins are set up above once it is saved.",
            11.5, Look.Secondary));
        server.Children.Add(Label("Address"));
        server.Children.Add(_address);
        server.Children.Add(Label("Token"));
        server.Children.Add(TokenRow());
        server.Children.Add(_result);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(_test);
        Grid.SetColumn(_save, 2);
        buttons.Children.Add(_save);
        server.Children.Add(buttons);

        // The accounts first, as on the Mac: without them the server has nothing to show.
        ShowAccounts();
        Children.Add(Bits.Card(server, padding: 14));
        Children.Add(_sections);
        DrawSections();

        _address.TextChanged += (_, _) => UpdateButtons();
        _token.TextChanged += (_, _) => UpdateButtons();
        _address.KeyDown += SaveOnEnter;
        _token.KeyDown += SaveOnEnter;
        Load();
    }

    /// <summary>The field that wants the keyboard when the page opens: the first one still empty.</summary>
    public void FocusFirstEmpty()
    {
        if (string.IsNullOrEmpty(_address.Text)) _address.Focus();
        else if (string.IsNullOrEmpty(_token.Text)) _token.Focus();
    }

    private string SavedAddress => _model.HasOwnData ? _model.Credentials.ServerUrl() ?? "" : "";

    private string SavedToken => _model.HasOwnData ? _model.Credentials.ServerToken() ?? "" : "";

    private bool Dirty => (_address.Text ?? "").Trim() != SavedAddress || (_token.Text ?? "") != SavedToken;

    private void Load()
    {
        _address.Text = SavedAddress;
        _token.Text = SavedToken;
        if (SavedAddress.Length > 0)
        {
            Show(_model.Problem is { } problem ? problem : $"Reading from {Host(SavedAddress)}", _model.Problem == null ? null : Look.Critical);
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        Enable(_save, !_busy && Dirty);
        Enable(_test, !_busy && (_address.Text ?? "").Trim().Length > 0);
    }

    private static void Enable(Border button, bool enabled)
    {
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1 : 0.45;
    }

    private void Show(string text, Color? tint = null)
    {
        _result.Text = text;
        _result.Foreground = tint is { } colour ? Look.Solid(colour) : Look.Secondary;
        _result.IsVisible = text.Length > 0;
    }

    private void SaveOnEnter(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        e.Handled = true;
        _ = SaveAsync();
    }

    /// <summary>The Mac's test: reach the server with what is typed, and say what it holds.</summary>
    private async Task TestAsync()
    {
        if (_busy) return;
        if (ServerClient.NormalizeUrl(_address.Text) is not { } url)
        {
            Show("That is not an address", Look.Critical);
            return;
        }

        _busy = true;
        UpdateButtons();
        Show("Reaching the server…");
        try
        {
            Show(await Describe(url, Token()), Look.Fresh);
        }
        catch (ServerException problem)
        {
            Show(problem.Message, Look.Critical);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    /// <summary>
    /// Saves the pair and reads from it at once. An empty address turns the
    /// server off; something that is not an address is refused rather than
    /// read as "no server".
    /// </summary>
    private async Task SaveAsync()
    {
        if (_busy || !Dirty) return;
        var typed = (_address.Text ?? "").Trim();
        var url = ServerClient.NormalizeUrl(typed);
        if (typed.Length > 0 && url == null)
        {
            Show("That is not an address", Look.Critical);
            return;
        }

        _model.Credentials.SetServer(url, Token());
        _address.Text = SavedAddress;
        if (url == null)
        {
            Show("Server off");
            _model.ShowOwn();
            DrawSections();
            _saved();
            UpdateButtons();
            return;
        }

        _busy = true;
        UpdateButtons();
        Show("Saved · reading from the server…");
        try
        {
            _model.ShowOwn();
            await _model.RefreshAsync(force: false);
            Show(_model.Problem is { } problem
                ? $"Saved, but: {problem}"
                : $"Saved · {Count(_model.Snapshot?.Usages.Count ?? 0)} from {Host(url)}",
                _model.Problem == null ? Look.Fresh : Look.Critical);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
            DrawSections();
            _saved();
        }
    }

    private string? Token() => (_token.Text ?? "").Trim() is { Length: > 0 } token ? token : null;

    /// <summary>The server's accounts, while this app reads from a server and shows its own data.</summary>
    private void ShowAccounts()
    {
        var wanted = _model.UsesServer;
        if (wanted && _accountsCard == null)
        {
            _accounts = new AccountsSection(_model, async () =>
            {
                await _model.RefreshAsync(force: true);
                _saved();
            });
            _accountsCard = Bits.Card(_accounts, padding: 14);
            Children.Insert(0, _accountsCard);
        }
        else if (!wanted && _accountsCard != null)
        {
            Children.Remove(_accountsCard);
            _accountsCard = null;
            _accounts = null;
        }
    }

    /// <summary>Order, hidden limits, top bar, look, general: drawn again whenever one of them changes.</summary>
    public void DrawSections()
    {
        ShowAccounts();
        _sections.Children.Clear();
        var prefs = PanelPrefs.Current;
        var accounts = _model.RailOrder();
        if (accounts.Count > 0) _sections.Children.Add(Section("Order", OrderRows(prefs, accounts)));
        if (!_model.IsSample || _model.Hidden.Count > 0) _sections.Children.Add(Section("Hidden limits", HiddenRows()));
        _sections.Children.Add(Section("Top bar", TopBar(prefs)));
        _sections.Children.Add(Section("Look", LookRows(prefs)));
        _sections.Children.Add(Section("General", General()));
        _sections.Children.Add(Section("Updates", Bits.Paragraph(
            $"usagetop {Program.Version}. This build does not update itself yet; new ones come with releases.",
            11.5, Look.Secondary)));

        var keys = Bits.Text("Ctrl+R refresh · Ctrl+1–9 accounts · Ctrl+, settings · Esc close", 10, FontWeight.Normal, Look.Secondary);
        keys.HorizontalAlignment = HorizontalAlignment.Center;
        keys.Margin = new Thickness(0, 2, 0, 4);
        _sections.Children.Add(keys);
    }

    /// <summary>Something changed that other parts of the panel show: the rail, the overview, the bar.</summary>
    private void Changed()
    {
        DrawSections();
        _saved();
    }

    private static Border Section(string title, Control content)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Bits.Text(title, 13, FontWeight.SemiBold, Look.Primary));
        stack.Children.Add(content);
        return Bits.Card(stack, padding: 14);
    }

    // ---- Order ----

    private Control OrderRows(PanelPrefs prefs, List<ProviderUsage> accounts)
    {
        var stack = new StackPanel { Spacing = 2 };
        for (var i = 0; i < accounts.Count; i++)
        {
            var index = i;
            var usage = accounts[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 1) };
            row.Children.Add(new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = Look.Ink(0.07),
                Child = Icons.Glyph(ProviderArt.Logo(usage.Id), 12, Look.Primary),
            });
            var name = Bits.Text(usage.DisplayName, 12, FontWeight.Normal, Look.Primary);
            name.Margin = new Thickness(10, 0, 8, 0);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (i < 9)
            {
                var shortcut = Bits.Text($"Ctrl+{i + 1}", 10.5, FontWeight.Normal, Look.Secondary);
                shortcut.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetColumn(shortcut, 2);
                row.Children.Add(shortcut);
            }
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var up = Bits.IconButton(Icons.Up, "Move up", () => Move(accounts, index, -1), size: 24);
            var down = Bits.IconButton(Icons.Down, "Move down", () => Move(accounts, index, 1), size: 24);
            up.IsEnabled = i > 0;
            up.Opacity = i > 0 ? 1 : 0.35;
            down.IsEnabled = i < accounts.Count - 1;
            down.Opacity = i < accounts.Count - 1 ? 1 : 0.35;
            arrows.Children.Add(up);
            arrows.Children.Add(down);
            Grid.SetColumn(arrows, 3);
            row.Children.Add(arrows);
            stack.Children.Add(row);
        }
        stack.Children.Add(Note("The rail and Ctrl+1–9 follow this order."));
        stack.Children.Add(new Switch("Most at risk first on the overview", prefs.OverviewByRisk, on =>
        {
            _model.Prefer(p => p.OverviewByRisk = on);
            Changed();
        }) { Margin = new Thickness(0, 8, 0, 0) });
        if (prefs.Order.Count > 0)
        {
            var reset = Bits.SoftButton("Reset order", "Back to the default order", () =>
            {
                _model.Prefer(p => p.Order = []);
                Changed();
            });
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(reset);
        }
        return stack;
    }

    private void Move(List<ProviderUsage> accounts, int index, int by)
    {
        var target = index + by;
        if (target < 0 || target >= accounts.Count) return;
        var keys = accounts.Select(u => u.AccountKey).ToList();
        (keys[index], keys[target]) = (keys[target], keys[index]);
        _model.Prefer(p => p.Order = keys);
        Changed();
    }

    // ---- Hidden limits ----

    private Control HiddenRows()
    {
        var stack = new StackPanel { Spacing = 4 };
        var hidden = _model.Hidden.ToList();
        if (hidden.Count == 0) stack.Children.Add(Bits.Text("None hidden.", 12, FontWeight.Normal, Look.Primary));
        foreach (var key in hidden)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = Bits.Text(_model.HiddenName(key), 12, FontWeight.Normal, Look.Primary);
            name.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(name);
            var show = Bits.SoftButton("Show", "Back on the overview, in the top bar and in the rail's ring", async () =>
            {
                await _model.SetHiddenAsync(key, hide: false);
                Changed();
            });
            Grid.SetColumn(show, 1);
            row.Children.Add(show);
            stack.Children.Add(row);
        }
        stack.Children.Add(Note(_model.UsesServer
            ? "Kept by the server, for every app that reads from it. Left out of the overview, the top bar and the rail's rings; each still shows on its account's page. Right-click a limit to hide or show it."
            : "Left out of the overview, the top bar and the rail's rings; each still shows on its account's page. Right-click a limit to hide or show it."));
        return stack;
    }

    // ---- Top bar ----

    private Control TopBar(PanelPrefs prefs)
    {
        var stack = new StackPanel { Spacing = 6 };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(Bits.Text("Show", 12, FontWeight.Normal, Look.Primary));
        var choice = new Segmented(["Ring", "1", "2", "3", "4"], prefs.BarLimits, chosen =>
        {
            _model.Prefer(p => p.BarLimits = chosen);
            Changed();
        });
        Grid.SetColumn(choice, 1);
        row.Children.Add(choice);
        stack.Children.Add(row);
        stack.Children.Add(Note(prefs.BarLimits == 0
            ? "The combined figure, in a ring."
            : $"The {(prefs.BarLimits == 1 ? "limit" : $"{prefs.BarLimits} limits")} closest to running out."));
        if (prefs.BarLimits == 0)
        {
            stack.Children.Add(new Switch("Percentage beside the ring", prefs.BarPercent, on =>
            {
                _model.Prefer(p => p.BarPercent = on);
                Changed();
            }));
        }
        if (Desktop.IsGnome)
        {
            stack.Children.Add(Note("On GNOME the top bar item is the usagetop extension; it follows this within a few seconds."));
        }
        return stack;
    }

    // ---- Look ----

    private Control LookRows(PanelPrefs prefs)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(SliderRow("Glass", prefs.Glass,
            value => value < 0.02 ? "Flat" : value > 0.98 ? "Clear" : $"{Math.Round(value * 100)}% glass",
            value => PanelPrefs.Current.Update(p => p.Glass = value)));
        stack.Children.Add(SliderRow("Tiles", prefs.Tiles,
            value => value < 0.02 ? "See-through" : value > 0.98 ? "Solid" : $"{Math.Round(value * 100)}% solid",
            value => PanelPrefs.Current.Update(p => p.Tiles = value)));
        stack.Children.Add(Note(Desktop.IsKde
            ? "How much of the desktop shows through the panel, blurred, and how solid the tiles are apart from it."
            : "How much of the desktop shows through the panel, and how solid the tiles are apart from it. Only KDE blurs what shows through, so elsewhere it stays faint."));
        return stack;
    }

    /// <summary>A setting from 0 to 1, followed by the panel as it moves.</summary>
    private Control SliderRow(string title, double value, Func<double, string> name, Action<double> set)
    {
        var stack = new StackPanel { Spacing = 0 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(Bits.Text(title, 12, FontWeight.Normal, Look.Primary));
        var shown = Bits.Text(name(value), 11, FontWeight.Normal, Look.Secondary, digits: true);
        Grid.SetColumn(shown, 1);
        head.Children.Add(shown);
        stack.Children.Add(head);
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, Margin = new Thickness(0, -4, 0, -6) };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            shown.Text = name(e.NewValue);
            _applyLook();
        };
        stack.Children.Add(slider);
        return stack;
    }

    // ---- General ----

    private Control General()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new Switch("Open at login", Autostart.Enabled, Autostart.Set));
        if (Desktop.IsGnome)
        {
            stack.Children.Add(Note("Not needed with the GNOME extension, which starts the app itself."));
        }

        var sample = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        sample.Children.Add(Bits.Text(_model.IsSample ? "Showing sample data" : "Sample data", 12, FontWeight.Normal, Look.Primary));
        var toggle = Bits.SoftButton(
            _model.IsSample ? (_model.SavedServerUrl != null ? "Back to the server" : "Leave") : "Preview",
            _model.IsSample ? "Your own usage again" : "Made-up usage, to see what everything looks like",
            () =>
            {
                if (_model.IsSample) _model.ShowOwn();
                else _model.ShowSample();
                _ = _model.RefreshAsync();
                _go(new Pane.Overview());
            });
        Grid.SetColumn(toggle, 1);
        sample.Children.Add(toggle);
        stack.Children.Add(sample);
        return stack;
    }

    private static TextBlock Note(string text) => Bits.Paragraph(text, 11, Look.Secondary);

    private static async Task<string> Describe(string url, string? token)
    {
        try
        {
            var state = await ServerClient.FetchAsync(url, token, since: DateTimeOffset.UtcNow);
            var count = state.Snapshot.Usages.Count;
            return count == 0 ? "Reached the server, but it has no reading yet" : $"Reached the server · {Count(count)}";
        }
        catch (ServerException problem) when (problem.Message == "Server has no reading yet")
        {
            return "Reached the server, but it has no reading yet";
        }
    }

    private static string Count(int subscriptions) => $"{subscriptions} subscription{(subscriptions == 1 ? "" : "s")}";

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    // ---- Look ----

    private static TextBlock Label(string text)
    {
        var label = Bits.Text(text, 11, FontWeight.SemiBold, Look.Secondary);
        label.Margin = new Thickness(0, 6, 0, 0);
        return label;
    }

    private static TextBox Field(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        FontSize = 12.5,
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(10, 7),
        MinHeight = 34,
    };

    /// <summary>The token's field, with a way to see what was pasted into it.</summary>
    private Control TokenRow()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(_token);
        var reveal = Bits.SoftButton("Show", "Show or hide the token", () =>
        {
            _token.RevealPassword = !_token.RevealPassword;
        });
        reveal.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(reveal, 1);
        row.Children.Add(reveal);
        return row;
    }

    /// <summary>The theme's text fields in the panel's own quiet fill and hairline, in every state.</summary>
    private static void UseFieldLook(Control scope)
    {
        var fill = Look.Ink(0.06);
        var fillOver = Look.Ink(0.09);
        var edge = Look.Ink(0.14);
        var resources = scope.Resources;
        resources["TextControlBackground"] = fill;
        resources["TextControlBackgroundPointerOver"] = fillOver;
        resources["TextControlBackgroundFocused"] = fillOver;
        resources["TextControlBorderBrush"] = edge;
        resources["TextControlBorderBrushPointerOver"] = Look.Ink(0.22);
        resources["TextControlBorderBrushFocused"] = Look.Solid(Look.Normal);
        resources["TextControlForeground"] = Look.Primary;
        resources["TextControlForegroundPointerOver"] = Look.Primary;
        resources["TextControlForegroundFocused"] = Look.Primary;
        resources["TextControlPlaceholderForeground"] = Look.Ink(0.38);
        resources["TextControlPlaceholderForegroundPointerOver"] = Look.Ink(0.38);
        resources["TextControlPlaceholderForegroundFocused"] = Look.Ink(0.3);
        resources["TextControlSelectionHighlightColor"] = Look.Solid(Look.Normal, 0.45);
    }
}
