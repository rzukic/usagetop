using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AiUsage.Auth;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// The full window: the same cards as the flyout, plus the settings that decide
/// where the numbers come from.
/// </summary>
public partial class MainWindow : Window
{
    private const string MistralKeysUrl = "https://console.mistral.ai/api-keys";
    private const string KimiKeysUrl = "https://www.kimi.ai/code/console";

    private readonly UsageViewModel _model = new();
    private readonly Dictionary<ProviderId, PasswordBox> _tokenFields = [];
    private readonly Dictionary<ProviderId, TextBox> _tokenReveals = [];
    private readonly Dictionary<ProviderId, TextBlock> _statusLabels = [];
    private readonly Dictionary<ProviderId, string> _loadedTokens = [];
    private CheckBox? _kimiChina;
    private CheckBox? _autostart;
    private TextBox? _mistralBudget;
    private TextBox? _mistralSpent;
    private TextBox? _serverUrl;
    private PasswordBox? _serverToken;
    private TextBlock? _serverStatus;
    private Button? _website;
    private Button? _handOver;
    private Storyboard? _spin;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;

        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        App.Repository.Changed += OnChanged;
        _model.Apply(App.Repository.Cached());

        BuildSettings();
        LoadSettings();

        Activated += (_, _) =>
        {
            if (!App.Repository.IsFresh()) _ = RefreshAsync();
        };
    }

    private void OnChanged(Snapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnChanged(snapshot));
            return;
        }

        _model.Apply(snapshot);
        _model.Refreshing = false;
        StopSpin();
        RefreshStatuses(snapshot);
    }

    // ---- Tabs -------------------------------------------------------------

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (UsagePanel == null || SettingsPanel == null) return;

        var usage = UsageTab.IsChecked == true;
        UsagePanel.Visibility = usage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = usage ? Visibility.Collapsed : Visibility.Visible;

        // The server's accounts may have changed on the website since this was built.
        if (!usage && App.Repository.UsesServer) _ = LoadServerAccountsAsync();
    }

    // ---- Usage ------------------------------------------------------------

    private void OnProviderClick(object sender, MouseButtonEventArgs e)
    {
        if (VisualTree.DataContextOf<ProviderCard>(e.OriginalSource as DependencyObject) is not { } card) return;
        ((App)Application.Current).ShowDetail(card.AccountKey);
    }

    private void OnCombinedClick(object sender, MouseButtonEventArgs e) =>
        ((App)Application.Current).ShowDetail(null);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync(force: true);

    private async Task RefreshAsync(bool force = false)
    {
        _model.Refreshing = true;
        StartSpin();
        await ((App)Application.Current).RefreshAsync(force);
        _model.Refreshing = false;
        StopSpin();
    }

    private void StartSpin()
    {
        var animation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(animation, RefreshRotation);
        Storyboard.SetTargetProperty(animation, new PropertyPath("Angle"));

        _spin = new Storyboard();
        _spin.Children.Add(animation);
        _spin.Begin();
    }

    private void StopSpin()
    {
        _spin?.Stop();
        _spin = null;
        RefreshRotation.Angle = 0;
    }

    // ---- Settings ---------------------------------------------------------

    /// <summary>
    /// One card per provider, built here rather than in XAML: five near-identical
    /// blocks of markup would be five places to forget a change.
    /// </summary>
    private void BuildSettings()
    {
        SettingsHost.Children.Clear();
        _tokenFields.Clear();
        _tokenReveals.Clear();
        _statusLabels.Clear();
        _loadedTokens.Clear();
        ClearServerRows();
        _serverAccountsHost = null;
        _serverAccountsStatus = null;
        _kimiChina = null;
        _mistralBudget = null;
        _mistralSpent = null;
        _handOver = null;

        SettingsHost.Children.Add(ServerCard());

        if (App.Repository.UsesServer)
        {
            // The server holds the accounts, so its own card replaces the five
            // below: a key typed here belongs to the server, not to this app.
            SettingsHost.Children.Add(ServerAccountsCard());
            SettingsHost.Children.Add(StartupCard());
            _ = LoadServerAccountsAsync();
            return;
        }

        var import = new Button
        {
            Content = "Import from installed CLIs",
            Style = (Style)FindResource("PillButton"),
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = "Uses the sessions Claude Code, Codex and the Muse CLI already have. "
                + "Claude's and ChatGPT's are borrowed and stay the CLI's to renew; "
                + "sign in below for a session that renews itself.",
        };
        import.Click += (_, _) =>
        {
            CliImport.Run(App.Repository.Credentials);
            LoadSettings();
            _ = RefreshAsync();
        };
        SettingsHost.Children.Add(import);

        SettingsHost.Children.Add(StartupCard());

        foreach (var info in ProviderCatalog.All)
        {
            SettingsHost.Children.Add(ProviderCardFor(info));
        }
    }

    /// <summary>
    /// The Run key entry, so the tray icon is there after a reboot without the app
    /// having to be launched by hand.
    /// </summary>
    private Border StartupCard()
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "Background",
            Style = (Style)FindResource("CardTitle"),
        });

        _autostart = new CheckBox
        {
            Content = "Start with Windows",
            Style = (Style)FindResource("Check"),
            Margin = new Thickness(0, 10, 0, 0),
            IsChecked = Autostart.Enabled,
        };
        _autostart.Checked += (_, _) => Autostart.Set(true);
        _autostart.Unchecked += (_, _) => Autostart.Set(false);
        stack.Children.Add(_autostart);

        stack.Children.Add(new TextBlock
        {
            Text = "Refreshes every 15 minutes while it runs, and whenever a window is opened.",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    /// <summary>
    /// Where the readings come from. With an address here the app asks the
    /// server and nothing else; without one it asks the providers itself.
    /// </summary>
    private Border ServerCard()
    {
        var stack = new StackPanel();

        var header = new Grid();
        header.Children.Add(new TextBlock
        {
            Text = "Server",
            Style = (Style)FindResource("CardTitle"),
        });
        _serverStatus = new TextBlock
        {
            Style = (Style)FindResource("Hint"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MaxWidth = 260,
            TextWrapping = TextWrapping.Wrap,
        };
        header.Children.Add(_serverStatus);
        stack.Children.Add(header);

        stack.Children.Add(new TextBlock
        {
            Text = "Address",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 10, 0, 4),
        });
        _serverUrl = new TextBox { Style = (Style)FindResource("Field") };
        stack.Children.Add(_serverUrl);

        stack.Children.Add(new TextBlock
        {
            Text = "Token",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 10, 0, 4),
        });
        _serverToken = new PasswordBox
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 12,
            Padding = new Thickness(10, 8, 10, 8),
            Background = (Brush)FindResource("ControlFillBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            CaretBrush = (Brush)FindResource("TextPrimaryBrush"),
            BorderThickness = new Thickness(0),
        };
        stack.Children.Add(_serverToken);

        stack.Children.Add(new TextBlock
        {
            Text = "A usagetop server, e.g. http://100.64.0.7:8377, and the token from "
                + "`aiusage-server token`. While an address is set, this app reads everything "
                + "from the server and the sign-ins below are not used. Clear it to go back.",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        _website = new Button
        {
            Content = "Open the website",
            Style = (Style)FindResource("PillButton"),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            ToolTip = "The server's own page, with the same readings and the accounts it follows.",
        };
        _website.Click += (_, _) =>
        {
            if (App.Repository.Credentials.ServerUrl() is { } url) OpenUrl(url);
        };
        stack.Children.Add(_website);

        _handOver = new Button
        {
            Content = "Move this app's sign-ins to the server",
            Style = (Style)FindResource("PillButton"),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = "Sends the server each session and key this app holds, then signs this app "
                + "out of Claude and ChatGPT: their sessions can only have one holder.",
        };
        _handOver.Click += (_, _) => _ = HandOverAsync();
        stack.Children.Add(_handOver);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    /// <summary>
    /// Gives the server what this app holds. A session with a refresh token is
    /// removed here once the server has it; a key is static and stays, so the app
    /// still works if the server is taken away again.
    /// </summary>
    private async Task HandOverAsync()
    {
        var credentials = App.Repository.Credentials;
        if (credentials.ServerUrl() is not { } url)
        {
            _serverStatus!.Text = "Save a server address first";
            return;
        }

        var own = ProviderCatalog.All
            .Where(info => credentials.Token(info.Id) != null && !credentials.Borrowed(info.Id))
            .ToList();
        var borrowed = ProviderCatalog.All
            .Where(info => credentials.Token(info.Id) != null && credentials.Borrowed(info.Id))
            .Select(info => info.DisplayName)
            .ToList();

        if (own.Count == 0)
        {
            _serverStatus!.Text = borrowed.Count > 0
                ? $"{string.Join(", ", borrowed)}: a CLI's session cannot be moved; sign in on the server"
                : "Nothing to move";
            return;
        }

        var leaving = own.Where(info => credentials.IsSignedIn(info.Id)).Select(info => info.DisplayName).ToList();
        var question = $"Send {string.Join(", ", own.Select(info => info.DisplayName))} to {url}?";
        if (leaving.Count > 0)
        {
            question += $"\n\nThis app will be signed out of {string.Join(" and ", leaving)}; "
                + "the server becomes the only holder of those sessions.";
        }
        if (MessageBox.Show(this, question, "Move sign-ins to the server", MessageBoxButton.OKCancel)
            != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true);
        var moved = new List<string>();
        try
        {
            foreach (var info in own)
            {
                var session = credentials.IsSignedIn(info.Id);
                await ServerClient.HandOverAsync(url, credentials.ServerToken(), info.Id, credentials);

                // Single-use refresh token: the moment the server has it, ours is a liability.
                if (session) credentials.SignOut(info.Id);
                moved.Add(info.DisplayName);
            }

            LoadSettings();
            _serverStatus!.Text = borrowed.Count > 0
                ? $"Moved {string.Join(", ", moved)}. {string.Join(", ", borrowed)}: sign in on the server"
                : $"Moved {string.Join(", ", moved)}";
        }
        catch (ServerException problem)
        {
            LoadSettings();
            _serverStatus!.Text = moved.Count > 0
                ? $"{problem.Message} after moving {string.Join(", ", moved)}"
                : problem.Message;
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshAsync(force: true);
    }

    private Border ProviderCardFor(ProviderInfo info)
    {
        var stack = new StackPanel();

        var header = new Grid();
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new System.Windows.Shapes.Path
                {
                    Data = ProviderArt.Logo(info.Id),
                    Fill = (Brush)FindResource("TextPrimaryBrush"),
                    Width = 13,
                    Height = 13,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = info.DisplayName,
                    Style = (Style)FindResource("CardTitle"),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        });

        var status = new TextBlock
        {
            Style = (Style)FindResource("Hint"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MaxWidth = 200,
            TextWrapping = TextWrapping.Wrap,
        };
        _statusLabels[info.Id] = status;
        header.Children.Add(status);
        stack.Children.Add(header);

        // The two providers with a real browser sign-in, and Muse's device code.
        if (OAuthConfig.ForProvider(info.Id) is { } config)
        {
            var button = new Button
            {
                Content = $"Sign in with {info.DisplayName}",
                Style = (Style)FindResource("PillButton"),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            button.Click += (_, _) => _ = SignInAsync(config);
            stack.Children.Add(button);
        }
        else if (info.Id == ProviderId.Muse)
        {
            var button = new Button
            {
                Content = "Sign in with Meta",
                Style = (Style)FindResource("PillButton"),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            button.Click += (_, _) => _ = SignInMuseAsync();
            stack.Children.Add(button);
        }

        // The key consoles, for the two providers that only issue static keys.
        if (info.Id is ProviderId.Mistral or ProviderId.Kimi)
        {
            var link = new Button
            {
                Content = "Open key console",
                Style = (Style)FindResource("QuietButton"),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
            };
            link.Click += (_, _) => OpenUrl(info.Id == ProviderId.Mistral ? MistralKeysUrl : KimiKeysUrl);
            stack.Children.Add(link);
        }

        stack.Children.Add(SecretField(info.Id));

        if (info.Id == ProviderId.Kimi)
        {
            _kimiChina = new CheckBox
            {
                Content = "Use China endpoint (api.moonshot.cn)",
                Style = (Style)FindResource("Check"),
                Margin = new Thickness(0, 10, 0, 0),
            };
            stack.Children.Add(_kimiChina);
        }

        if (info.Id == ProviderId.Mistral)
        {
            _mistralBudget = NumberField(stack, "Monthly budget (€)", "Only used when the key cannot read billing");
            _mistralSpent = NumberField(stack, "Spent so far (€)", null);
        }

        if (info.Id == ProviderId.Muse)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Or paste from %USERPROFILE%\\.config\\muse\\auth.json → providers.meta.access_token",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    /// <summary>
    /// A token field that is masked until asked otherwise. A settings screen showing
    /// four live tokens in plain text is one screenshot away from leaking them.
    /// </summary>
    private Grid SecretField(ProviderId id)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var masked = new PasswordBox
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 12,
            Padding = new Thickness(10, 8, 10, 8),
            Background = (Brush)FindResource("ControlFillBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            CaretBrush = (Brush)FindResource("TextPrimaryBrush"),
            BorderThickness = new Thickness(0),
        };
        _tokenFields[id] = masked;

        var plain = new TextBox
        {
            Style = (Style)FindResource("Field"),
            Visibility = Visibility.Collapsed,
        };
        _tokenReveals[id] = plain;

        var reveal = new CheckBox
        {
            Content = "Show",
            Style = (Style)FindResource("Check"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(reveal, 1);

        reveal.Checked += (_, _) =>
        {
            plain.Text = masked.Password;
            plain.Visibility = Visibility.Visible;
            masked.Visibility = Visibility.Collapsed;
        };
        reveal.Unchecked += (_, _) =>
        {
            masked.Password = plain.Text;
            masked.Visibility = Visibility.Visible;
            plain.Visibility = Visibility.Collapsed;
        };

        grid.Children.Add(masked);
        grid.Children.Add(plain);
        grid.Children.Add(reveal);
        return grid;
    }

    private static TextBox? NumberField(Panel host, string label, string? hint)
    {
        host.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.FindResource("Hint"),
            Margin = new Thickness(0, 10, 0, 4),
        });

        var field = new TextBox
        {
            Style = (Style)Application.Current.FindResource("Field"),
        };
        host.Children.Add(field);

        if (hint != null)
        {
            host.Children.Add(new TextBlock
            {
                Text = hint,
                Style = (Style)Application.Current.FindResource("Hint"),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return field;
    }

    private void LoadSettings()
    {
        var credentials = App.Repository.Credentials;

        foreach (var info in ProviderCatalog.All)
        {
            var token = credentials.Token(info.Id) ?? "";
            _loadedTokens[info.Id] = token;
            if (_tokenFields.TryGetValue(info.Id, out var masked)) masked.Password = token;
            if (_tokenReveals.TryGetValue(info.Id, out var reveal)) reveal.Text = token;
        }

        if (_serverUrl != null) _serverUrl.Text = credentials.ServerUrl() ?? "";
        if (_serverToken != null) _serverToken.Password = credentials.ServerToken() ?? "";

        if (_website != null)
        {
            _website.Visibility = credentials.ServerUrl() != null ? Visibility.Visible : Visibility.Collapsed;
        }

        // Nothing to move once the server holds everything this app had.
        if (_handOver != null)
        {
            var own = ProviderCatalog.All.Any(info =>
                credentials.Token(info.Id) != null && !credentials.Borrowed(info.Id));
            _handOver.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_kimiChina != null) _kimiChina.IsChecked = credentials.KimiChina();
        if (_mistralBudget != null)
        {
            _mistralBudget.Text = credentials.MistralBudget() is { } budget ? Format.Number(budget) : "";
        }
        if (_mistralSpent != null)
        {
            _mistralSpent.Text = credentials.MistralSpent() is { } spent ? Format.Number(spent) : "";
        }

        RefreshStatuses(App.Repository.Cached());
    }

    private void SaveSettings()
    {
        var credentials = App.Repository.Credentials;

        foreach (var info in ProviderCatalog.All)
        {
            if (!_tokenFields.TryGetValue(info.Id, out var masked)) continue;
            if (!_tokenReveals.TryGetValue(info.Id, out var reveal)) continue;

            var value = reveal.Visibility == Visibility.Visible ? reveal.Text : masked.Password;

            // Only a field the user actually edited is written. The window may
            // have been open for hours, and writing back what it loaded would put
            // an access token that has since been renewed over its replacement.
            if (value.Trim() == _loadedTokens.GetValueOrDefault(info.Id, "")) continue;

            credentials.SetManualToken(info.Id, value);
            _loadedTokens[info.Id] = value.Trim();
        }

        if (_serverUrl != null && _serverToken != null)
        {
            var typed = _serverUrl.Text.Trim();
            var url = ServerClient.NormalizeUrl(typed);

            // Something typed that is not an address is left unsaved rather than
            // read as "no server", which would quietly switch the app back.
            if (typed.Length == 0 || url != null)
            {
                credentials.SetServer(url, _serverToken.Password);
                _serverUrl.Text = url ?? "";
            }
            else if (_serverStatus != null)
            {
                _serverStatus.Text = "Not an address";
            }
        }

        if (_kimiChina != null) credentials.SetKimiChina(_kimiChina.IsChecked == true);

        // The fields are absent in server mode; writing null would wipe what the
        // app still holds for when the address is cleared again.
        if (_mistralBudget != null) credentials.SetMistralBudget(ParseNumber(_mistralBudget.Text));
        if (_mistralSpent != null) credentials.SetMistralSpent(ParseNumber(_mistralSpent.Text));
    }

    private static double? ParseNumber(string? text) =>
        double.TryParse(text, out var value) ? value : null;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var wasServer = App.Repository.UsesServer;
        SaveSettings();

        // Setting or clearing the address swaps the whole page: the cards below
        // belong to whichever side now holds the accounts.
        if (App.Repository.UsesServer != wasServer)
        {
            BuildSettings();
            LoadSettings();
        }

        _ = RefreshAsync(force: true);
    }

    /// <summary>The one-line summary under each provider's name in Settings.</summary>
    private void RefreshStatuses(Snapshot? snapshot)
    {
        var credentials = App.Repository.Credentials;

        if (_serverStatus != null && _serverStatus.Text != "Not an address")
        {
            _serverStatus.Text = !App.Repository.UsesServer ? "Off"
                : App.Repository.ServerProblem ?? "Connected";
        }

        foreach (var info in ProviderCatalog.All)
        {
            if (!_statusLabels.TryGetValue(info.Id, out var label)) continue;
            var usage = snapshot?.Usages.FirstOrDefault(u => u.Id == info.Id && u.Slot.Length == 0);
            label.Text = Describe(usage, credentials, info.Id);
        }
    }

    private static string Describe(ProviderUsage? usage, Credentials credentials, ProviderId id)
    {
        if (usage == null)
        {
            return credentials.IsSignedIn(id) ? "Signed in"
                : credentials.Borrowed(id) ? "Using the CLI's session"
                : "Not configured";
        }

        return usage.State switch
        {
            UsageState.NotConfigured => usage.Message ?? "Not configured",
            UsageState.Error => usage.Message ?? "Error",
            _ => Summarise(usage),
        };
    }

    private static string Summarise(ProviderUsage usage)
    {
        var parts = usage.Windows
            .Select(window => window.RemainingPercent is { } percent
                ? $"{window.Label} {Format.Percent(percent)} left"
                : window.Detail)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        if (usage.Plan is { Length: > 0 } plan) parts.Add(plan);
        if (usage.Stale) parts.Add($"as of {Format.Ago(usage.FetchedAt)}");

        return parts.Count == 0 ? "OK" : string.Join(" · ", parts);
    }

    // ---- Sign-in ----------------------------------------------------------

    private async Task SignInAsync(OAuthConfig config)
    {
        SetBusy(true);
        _statusLabels[config.ProviderId].Text = "Signing in…";

        try
        {
            await OAuthFlow.SignInAsync(
                config,
                App.Repository.Credentials,
                OpenUrl);

            LoadSettings();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _statusLabels[config.ProviderId].Text = $"Sign-in failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SignInMuseAsync()
    {
        SetBusy(true);
        _statusLabels[ProviderId.Muse].Text = "Signing in…";

        try
        {
            var started = await DeviceLogin.StartAsync();
            var dialog = new DeviceCodeWindow(started.UserCode, started.VerificationUrl) { Owner = this };
            dialog.Show();
            OpenUrl(started.VerificationUrl);

            var token = await DeviceLogin.AwaitTokenAsync(started);
            dialog.Close();

            App.Repository.Credentials.SetToken(ProviderId.Muse, token);
            LoadSettings();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _statusLabels[ProviderId.Muse].Text = $"Sign-in failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        SettingsHost.IsEnabled = !busy;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No default browser is a rare but survivable state.
        }
    }
}

/// <summary>
/// Windows 10 1809+ draws the title bar dark on request; without this the app is a
/// black panel under a white strip. Windows 11 also rounds the corners of a window
/// that draws its own frame, which it otherwise leaves square.
/// </summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeLegacy = 19;
    private const int WindowCornerPreference = 33;
    private const int RoundCorners = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }

        // Ignored on Windows 10, which has no rounded corners to ask for.
        var round = RoundCorners;
        DwmSetWindowAttribute(handle, WindowCornerPreference, ref round, sizeof(int));
    }
}
