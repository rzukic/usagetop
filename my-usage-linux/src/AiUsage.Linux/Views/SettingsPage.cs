using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// Settings: for now the server this app reads from, as the Windows and Mac
/// apps ask for it, an address and the server's token. The page is made once
/// each time it is opened and kept while it is, so a new reading never wipes
/// what is being typed.
/// </summary>
public sealed class SettingsPage : StackPanel
{
    private readonly AppModel _model;
    private readonly Action _saved;
    private readonly TextBox _address;
    private readonly TextBox _token;
    private readonly TextBlock _result;
    private readonly Border _test;
    private readonly Border _save;
    private bool _busy;

    public SettingsPage(AppModel model, Action saved)
    {
        _model = model;
        _saved = saved;
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
            "Accounts and sign-ins are on the server's page.",
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
        Children.Add(Bits.Card(server, padding: 14));

        Children.Add(Bits.Card(About(), padding: 14));

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
            _saved();
        }
    }

    private string? Token() => (_token.Text ?? "").Trim() is { Length: > 0 } token ? token : null;

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

    private static Control About()
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Bits.Text($"usagetop {Program.Version}".TrimEnd(), 13, FontWeight.SemiBold, Look.Primary));
        stack.Children.Add(Bits.Paragraph(
            "On this machine it reads from your server. Signing in to providers here, without a server, is still to come.",
            11.5, Look.Secondary));
        return stack;
    }
}
