using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// Settings › Server and Accounts, as the Mac panel has them: where the server
/// is and its token, and the accounts it follows, with their sign-ins. The same
/// calls as the dashboard's (MainWindow.Server.cs), laid out for the panel.
///
/// Two things differ from the rest of the panel. These sections are built once
/// and kept, so a half-typed address or a sign-in in progress survives the
/// panel redrawing around it. And while one is in progress the panel holds open
/// (see <see cref="HoldsOpen"/>): a sign-in sends the user to the browser, and a
/// flyout that closed on losing focus would take the step with it.
/// </summary>
public sealed partial class PanelView
{
    private FrameworkElement? _serverSection;
    private TextBox? _address;
    private PasswordBox? _token;
    private TextBlock? _serverResult;
    private Button? _save;
    private Button? _test;

    private FrameworkElement? _accountsSection;
    private StackPanel? _accountsHost;
    private TextBlock? _accountsStatus;
    private readonly Dictionary<string, AccountRow> _rows = new(StringComparer.Ordinal);
    private bool _accountsLoading;

    private static readonly SolidColorBrush Good = Frozen(Color.FromRgb(0x5B, 0xC4, 0x8A));
    private static readonly SolidColorBrush Bad = Frozen(Severity.Critical);
    private static readonly SolidColorBrush Warn = Frozen(Severity.Warning);

    /// <summary>
    /// True while Settings has something in progress: an address or token typed
    /// and not saved, or an account's step open. The window then stays up when
    /// it loses focus; Escape or the tray icon still close it.
    /// </summary>
    public bool HoldsOpen => _page == PageKind.Settings && (ServerDirty || _rows.Values.Any(r => r.Step.Children.Count > 0));

    /// <summary>Closes whatever Settings has in progress; true when there was something.</summary>
    private bool CancelInProgress()
    {
        var any = false;
        foreach (var row in _rows.Values)
        {
            if (row.Step.Children.Count == 0) continue;
            CloseStep(row);
            any = true;
        }
        if (ServerDirty)
        {
            LoadServerFields();
            any = true;
        }
        return any;
    }

    // ---- Server -----------------------------------------------------------

    private bool ServerDirty =>
        _address != null && _token != null
        && (_address.Text.Trim() != (App.Repository.Credentials.ServerUrl() ?? "")
            || _token.Password != (App.Repository.Credentials.ServerToken() ?? ""));

    private FrameworkElement ServerSection()
    {
        if (_serverSection != null)
        {
            // Changed in the dashboard meanwhile: show it, unless something is being typed here.
            if (!ServerDirty) LoadServerFields();
            return _serverSection;
        }

        var stack = new StackPanel();

        stack.Children.Add(FieldLabel("Address"));
        _address = new TextBox { Style = Res("PanelField") };
        stack.Children.Add(_address);

        stack.Children.Add(FieldLabel("Token", new Thickness(0, 10, 0, 5)));
        _token = new PasswordBox { Style = Res("PanelSecret") };
        stack.Children.Add(_token);

        stack.Children.Add(Text(
            "A usagetop server, e.g. usage.example.com, and the token from `aiusage-server token`. "
            + "Without an address the app asks the providers itself, with the sign-ins in the dashboard.",
            Caption, FontWeights.Normal, Secondary, new Thickness(0, 7, 0, 0)));

        _serverResult = Text("", Caption, FontWeights.Normal, Secondary, new Thickness(0, 8, 0, 0));
        _serverResult.Visibility = Visibility.Collapsed;
        stack.Children.Add(_serverResult);

        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _test = SoftButton("Test connection", "Asks the server for its reading, without saving anything");
        _test.Click += (_, _) => _ = TestServerAsync();
        buttons.Children.Add(_test);
        _save = new Button { Style = Res("AccentButton"), Content = "Save", ToolTip = "Use this server" };
        _save.Click += (_, _) => SaveServer();
        Grid.SetColumn(_save, 2);
        buttons.Children.Add(_save);
        stack.Children.Add(buttons);

        _address.TextChanged += (_, _) => UpdateServerButtons();
        _token.PasswordChanged += (_, _) => UpdateServerButtons();
        _address.KeyDown += SaveOnEnter;
        _token.KeyDown += SaveOnEnter;

        LoadServerFields();
        _serverSection = Section("Server", "", stack);
        return _serverSection;
    }

    private void SaveOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveServer();
        e.Handled = true;
    }

    private void LoadServerFields()
    {
        if (_address == null || _token == null) return;
        _address.Text = App.Repository.Credentials.ServerUrl() ?? "";
        _token.Password = App.Repository.Credentials.ServerToken() ?? "";
        UpdateServerButtons();
    }

    private void UpdateServerButtons()
    {
        if (_save != null) _save.IsEnabled = ServerDirty;
        if (_test != null && _address != null) _test.IsEnabled = _address.Text.Trim().Length > 0;
    }

    private void ShowServerResult(string? text, Brush? brush = null)
    {
        if (_serverResult == null) return;
        _serverResult.Text = text ?? "";
        _serverResult.Foreground = brush ?? Secondary;
        _serverResult.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The Mac's test: reach the server with what is typed, and say what it holds.</summary>
    private async Task TestServerAsync()
    {
        if (_address == null || _token == null || _test == null) return;
        if (ServerClient.NormalizeUrl(_address.Text) is not { } url)
        {
            ShowServerResult("That is not an address", Bad);
            return;
        }

        _test.IsEnabled = false;
        ShowServerResult("Reaching the server…");
        try
        {
            var token = _token.Password is { Length: > 0 } typed ? typed : null;
            var state = await ServerClient.FetchAsync(url, token, since: DateTimeOffset.UtcNow);
            var count = state.Snapshot.Usages.Count;
            ShowServerResult(count == 0
                ? "Reached the server, but it has no reading yet"
                : $"Reached the server · {count} subscription{(count == 1 ? "" : "s")}", Good);
        }
        catch (ServerException problem) when (problem.Message == "Server has no reading yet")
        {
            ShowServerResult("Reached the server, but it has no reading yet", Good);
        }
        catch (ServerException problem)
        {
            ShowServerResult(problem.Message, Bad);
        }
        finally
        {
            UpdateServerButtons();
        }
    }

    /// <summary>
    /// Saves the pair and starts reading from it. An empty address turns the
    /// server off; something that is not an address is refused rather than read
    /// as "no server", which would quietly switch the app back to its own sign-ins.
    /// </summary>
    private void SaveServer()
    {
        if (_address == null || _token == null || !ServerDirty) return;

        var typed = _address.Text.Trim();
        var url = ServerClient.NormalizeUrl(typed);
        if (typed.Length > 0 && url == null)
        {
            ShowServerResult("That is not an address", Bad);
            return;
        }

        App.Repository.Credentials.SetServer(url, _token.Password);
        LoadServerFields();
        ShowServerResult(url == null ? "Server off: the app asks the providers itself" : "Saved", Good);

        // Another server, other accounts.
        foreach (var row in _rows.Values) CloseStep(row);
        _rows.Clear();
        _accountsHost?.Children.Clear();

        Render();
        _ = RefreshAsync(force: true);
    }

    // ---- Accounts ---------------------------------------------------------

    private FrameworkElement AccountsSection()
    {
        if (_accountsSection != null)
        {
            _ = LoadAccountsAsync();
            return _accountsSection;
        }

        var stack = new StackPanel();
        _accountsStatus = Text("Reading…", Caption, FontWeights.Normal, Secondary);
        stack.Children.Add(_accountsStatus);
        _accountsHost = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(_accountsHost);

        // Another account of a provider: a second Claude subscription, a work ChatGPT.
        stack.Children.Add(FieldLabel("Add another account", new Thickness(0, 12, 0, 6)));
        var add = new WrapPanel();
        foreach (var info in ProviderCatalog.All)
        {
            var button = SoftButton(info.DisplayName, $"Add a {info.DisplayName} account to the server");
            button.Margin = new Thickness(0, 0, 6, 6);
            button.Click += (_, _) => _ = AddAccountAsync(info);
            add.Children.Add(button);
        }
        stack.Children.Add(add);

        _accountsSection = Section("Accounts", "", stack);
        _ = LoadAccountsAsync();
        return _accountsSection;
    }

    /// <summary>Asks the server for its accounts and lays the rows out in its order.</summary>
    private async Task LoadAccountsAsync()
    {
        if (_accountsHost == null || _accountsLoading) return;
        if (App.Repository.Credentials.ServerUrl() is not { } url) return;

        _accountsLoading = true;
        try
        {
            var accounts = await ServerClient.AccountsAsync(url, App.Repository.Credentials.ServerToken());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var account in accounts)
            {
                seen.Add(account.Key);
                if (!_rows.TryGetValue(account.Key, out var row))
                {
                    row = BuildRow(account);
                    _rows[account.Key] = row;
                }
                UpdateRow(row, account);
                _accountsHost.Children.Remove(row.Root);
                _accountsHost.Children.Add(row.Root);
            }

            foreach (var (key, row) in _rows.ToList())
            {
                if (seen.Contains(key)) continue;
                CloseStep(row);
                _accountsHost.Children.Remove(row.Root);
                _rows.Remove(key);
            }

            // A hairline between rows, none above the first.
            for (var i = 0; i < _accountsHost.Children.Count; i++)
            {
                ((Border)_accountsHost.Children[i]).BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0);
            }

            if (_accountsStatus != null)
            {
                _accountsStatus.Text = accounts.Count == 0
                    ? "The server follows no accounts yet."
                    : "Keys and sessions stay on the server; this app never reads them back.";
            }
        }
        catch (ServerException problem)
        {
            if (_accountsStatus != null) _accountsStatus.Text = problem.Message;
        }
        finally
        {
            _accountsLoading = false;
        }
    }

    private AccountRow BuildRow(ServerAccount account)
    {
        var stack = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var id = ProviderCatalog.FromKey(account.Provider)?.Id;
        if (id is { } provider) head.Children.Add(MarkDisc(provider, 26));

        var names = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = Text("", Body, FontWeights.SemiBold, Primary);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        var status = Text("", Caption, FontWeights.Normal, Secondary);
        names.Children.Add(title);
        names.Children.Add(status);
        Grid.SetColumn(names, 1);
        head.Children.Add(names);

        var act = SoftButton("", "");
        act.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(act, 2);
        head.Children.Add(act);

        var more = new Button
        {
            Style = Res("SoftCircle"),
            Content = "",
            Width = 26,
            Height = 26,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "More",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(more, 3);
        head.Children.Add(more);
        stack.Children.Add(head);

        // The rarer actions, behind the "…".
        var extras = new WrapPanel { Margin = new Thickness(36, 8, 0, 0), Visibility = Visibility.Collapsed };
        var rename = SoftButton("Rename", "Give this account a name of its own");
        var signOut = SoftButton("Sign out", "Forget this account's sign-in on the server");
        var remove = SoftButton("Remove", "Stop following this account");
        ((TextBlock)((StackPanel)remove.Content).Children[^1]).Foreground = Bad;
        foreach (var button in new[] { rename, signOut, remove })
        {
            button.Margin = new Thickness(0, 0, 6, 0);
            extras.Children.Add(button);
        }
        stack.Children.Add(extras);

        var step = new StackPanel { Margin = new Thickness(36, 0, 0, 0) };
        stack.Children.Add(step);

        var problem = Text("", Caption, FontWeights.Normal, Warn, new Thickness(36, 6, 0, 0));
        problem.Visibility = Visibility.Collapsed;
        stack.Children.Add(problem);

        var row = new AccountRow
        {
            Account = account,
            Root = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                Padding = new Thickness(0, 10, 0, 10),
                Child = stack,
            },
            Title = title,
            Status = status,
            Act = act,
            Extras = extras,
            SignOut = signOut,
            Remove = remove,
            Step = step,
            Problem = problem,
        };

        act.Click += (_, _) => _ = ActAsync(row);
        more.Click += (_, _) => extras.Visibility = extras.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        rename.Click += (_, _) => ShowRename(row);
        signOut.Click += (_, _) => Confirm(row, $"Sign the server out of {row.Account.DisplayName}?", "Sign out",
            () => ServerClient.SignOutAsync(ServerUrl(), ServerToken(), row.Account.Key));
        remove.Click += (_, _) => Confirm(row,
            $"Remove {row.Account.DisplayName} from the server? Its sign-in is forgotten; its history stays.", "Remove",
            () => ServerClient.RemoveAccountAsync(ServerUrl(), ServerToken(), row.Account.Key));
        return row;
    }

    private static void UpdateRow(AccountRow row, ServerAccount account)
    {
        row.Account = account;
        var key = IsKeyProvider(account.Provider);

        row.Title.Text = account.DisplayName;
        row.Status.Text = !account.Configured ? "Not set up"
            : key ? "Key stored"
            : account.Renews ? "Signed in · renews itself"
            : "Token stored";
        row.Status.Foreground = account.Configured ? Secondary : Warn;

        SetButtonText(row.Act, key
            ? (account.Configured ? "Replace key" : "Set key")
            : (account.Configured ? "Sign in again" : "Sign in"));
        row.SignOut.Visibility = account.Configured ? Visibility.Visible : Visibility.Collapsed;
        row.Remove.Visibility = account.Removable ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetButtonText(Button button, string text) =>
        ((TextBlock)((StackPanel)button.Content).Children[^1]).Text = text;

    private static bool IsKeyProvider(string provider) =>
        ProviderCatalog.FromKey(provider)?.Id is ProviderId.Mistral or ProviderId.Kimi;

    private async Task ActAsync(AccountRow row)
    {
        if (IsKeyProvider(row.Account.Provider)) ShowKeyStep(row);
        else await StartSignInAsync(row);
    }

    /// <summary>A key is write-only: the server says whether one is stored, never what it is.</summary>
    private void ShowKeyStep(AccountRow row)
    {
        OpenStep(row);
        row.Step.Children.Add(FieldLabel($"{row.Account.DisplayName} API key", new Thickness(0, 10, 0, 5)));
        var field = new PasswordBox { Style = Res("PanelSecret") };
        row.Step.Children.Add(field);

        CheckBox? china = null;
        TextBox? budget = null;
        TextBox? spent = null;
        var id = ProviderCatalog.FromKey(row.Account.Provider)?.Id;
        if (id == ProviderId.Kimi)
        {
            china = new CheckBox { Style = Res("Switch"), Content = "China endpoint (api.moonshot.cn)", Margin = new Thickness(0, 10, 0, 0) };
            row.Step.Children.Add(china);
        }
        if (id == ProviderId.Mistral)
        {
            row.Step.Children.Add(FieldLabel("Monthly budget (€)", new Thickness(0, 10, 0, 5)));
            budget = new TextBox { Style = Res("PanelField") };
            row.Step.Children.Add(budget);
            row.Step.Children.Add(FieldLabel("Spent so far (€)", new Thickness(0, 10, 0, 5)));
            spent = new TextBox { Style = Res("PanelField") };
            row.Step.Children.Add(spent);
            row.Step.Children.Add(Text("Only used when the key cannot read billing.", Caption, FontWeights.Normal, Secondary, new Thickness(0, 5, 0, 0)));
        }

        row.Step.Children.Add(StepButtons(row, "Save", async () =>
        {
            var value = field.Password.Trim();
            if (value.Length == 0) throw new ServerException("Paste a key first.");
            await ServerClient.SetKeyAsync(ServerUrl(), ServerToken(), row.Account.Key, value,
                china?.IsChecked, ParseNumber(budget?.Text), ParseNumber(spent?.Text));
            CloseStep(row);
            await AfterChangeAsync();
        }));
        field.Focus();
    }

    private async Task StartSignInAsync(AccountRow row)
    {
        row.Act.IsEnabled = false;
        try
        {
            var step = await ServerClient.StartSignInAsync(ServerUrl(), ServerToken(), row.Account.Key);
            OpenStep(row);
            row.Step.Children.Add(Text(step.Instructions, Caption, FontWeights.Normal, Secondary, new Thickness(0, 10, 0, 0)));

            var open = SoftButton("Open the sign-in page", step.Url, "");
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Margin = new Thickness(0, 8, 0, 0);
            open.Click += (_, _) => OpenUrl(step.Url);
            row.Step.Children.Add(open);

            if (step.Kind == "device")
            {
                // The code to type on the page, large enough to read across, and copyable.
                var code = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                code.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                code.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var digits = Text(step.UserCode ?? "", 22, FontWeights.SemiBold, Primary);
                digits.FontFamily = Display;
                code.Children.Add(digits);
                var copy = new Button { Style = Res("SoftCircle"), Content = "", Width = 26, Height = 26, Margin = new Thickness(10, 0, 0, 0), ToolTip = "Copy the code" };
                copy.Click += (_, _) =>
                {
                    try { Clipboard.SetText(step.UserCode ?? ""); } catch (System.Runtime.InteropServices.ExternalException) { }
                };
                Grid.SetColumn(copy, 1);
                code.Children.Add(copy);
                row.Step.Children.Add(code);
                row.Step.Children.Add(Text("Waiting for you to approve…", Caption, FontWeights.Normal, Secondary, new Thickness(0, 4, 0, 0)));

                var cancel = SoftButton("Cancel", "Stop waiting");
                cancel.HorizontalAlignment = HorizontalAlignment.Left;
                cancel.Margin = new Thickness(0, 10, 0, 0);
                cancel.Click += (_, _) => CloseStep(row);
                row.Step.Children.Add(cancel);

                row.Poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                row.Poll.Tick += (_, _) => _ = PollSignInAsync(row);
                row.Poll.Start();
                return;
            }

            row.Step.Children.Add(FieldLabel(
                step.Kind == "redirect" ? "The address the browser landed on" : "The code the page shows",
                new Thickness(0, 10, 0, 5)));
            var field = new TextBox { Style = Res("PanelField") };
            row.Step.Children.Add(field);
            row.Step.Children.Add(StepButtons(row, "Finish", async () =>
            {
                var value = field.Text.Trim();
                if (value.Length == 0) throw new ServerException("Paste what the page showed first.");
                await ServerClient.CompleteSignInAsync(ServerUrl(), ServerToken(), row.Account.Key, value);
                CloseStep(row);
                await AfterChangeAsync();
            }));
            field.Focus();
        }
        catch (ServerException problem)
        {
            ShowProblem(row, problem.Message);
        }
        finally
        {
            row.Act.IsEnabled = true;
        }
    }

    private async Task PollSignInAsync(AccountRow row)
    {
        try
        {
            var status = await ServerClient.SignInStatusAsync(ServerUrl(), ServerToken(), row.Account.Key);
            if (status == "pending") return;
            CloseStep(row);
            if (status != "done") ShowProblem(row, status);
            await AfterChangeAsync();
        }
        catch (ServerException problem)
        {
            CloseStep(row);
            ShowProblem(row, problem.Message);
        }
    }

    private void ShowRename(AccountRow row)
    {
        OpenStep(row);
        row.Step.Children.Add(FieldLabel("Name, e.g. Work (empty for none)", new Thickness(0, 10, 0, 5)));
        var field = new TextBox { Style = Res("PanelField"), Text = row.Account.Name ?? "" };
        row.Step.Children.Add(field);
        row.Step.Children.Add(StepButtons(row, "Save", async () =>
        {
            await ServerClient.RenameAccountAsync(ServerUrl(), ServerToken(), row.Account.Key, field.Text);
            CloseStep(row);
            await AfterChangeAsync();
        }));
        field.Focus();
        field.SelectAll();
    }

    /// <summary>A question asked in place: a dialog box would take focus and close the panel.</summary>
    private void Confirm(AccountRow row, string question, string action, Func<Task> perform)
    {
        OpenStep(row);
        row.Step.Children.Add(Text(question, Caption, FontWeights.Normal, Primary, new Thickness(0, 10, 0, 0)));
        row.Step.Children.Add(StepButtons(row, action, async () =>
        {
            await perform();
            CloseStep(row);
            await AfterChangeAsync();
        }, destructive: true));
    }

    /// <summary>Cancel on the left, the step's action on the right; a server's refusal shows under the row.</summary>
    private FrameworkElement StepButtons(AccountRow row, string action, Func<Task> perform, bool destructive = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cancel = SoftButton("Cancel", "Leave it as it is");
        cancel.Click += (_, _) => CloseStep(row);
        grid.Children.Add(cancel);

        Button go;
        if (destructive)
        {
            go = SoftButton(action, "");
            ((TextBlock)((StackPanel)go.Content).Children[^1]).Foreground = Bad;
        }
        else
        {
            go = new Button { Style = Res("AccentButton"), Content = action };
        }
        go.Click += async (_, _) =>
        {
            go.IsEnabled = false;
            ShowProblem(row, null);
            try
            {
                await perform();
            }
            catch (ServerException problem)
            {
                ShowProblem(row, problem.Message);
            }
            finally
            {
                go.IsEnabled = true;
            }
        };
        Grid.SetColumn(go, 2);
        grid.Children.Add(go);
        return grid;
    }

    private async Task AddAccountAsync(ProviderInfo info)
    {
        try
        {
            var account = await ServerClient.AddAccountAsync(ServerUrl(), ServerToken(), info.Key, null);
            await LoadAccountsAsync();
            if (!_rows.TryGetValue(account.Key, out var row)) return;
            row.Root.BringIntoView();
            await ActAsync(row);
        }
        catch (ServerException problem)
        {
            if (_accountsStatus != null) _accountsStatus.Text = problem.Message;
        }
    }

    /// <summary>The server polls an account the moment its credentials change; read again shortly after.</summary>
    private async Task AfterChangeAsync()
    {
        await LoadAccountsAsync();
        await Task.Delay(TimeSpan.FromSeconds(4));
        await RefreshAsync(force: true);
    }

    private void OpenStep(AccountRow row)
    {
        CloseStep(row);
        row.Extras.Visibility = Visibility.Collapsed;
        row.Root.BringIntoView();
    }

    private static void CloseStep(AccountRow row)
    {
        row.Poll?.Stop();
        row.Poll = null;
        row.Step.Children.Clear();
        ShowProblem(row, null);
    }

    private static void ShowProblem(AccountRow row, string? text)
    {
        row.Problem.Text = text ?? "";
        row.Problem.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock FieldLabel(string text, Thickness? margin = null) =>
        Text(text, Caption, FontWeights.Medium, Secondary, margin ?? new Thickness(0, 0, 0, 5));

    private static string ServerUrl() =>
        App.Repository.Credentials.ServerUrl() ?? throw new ServerException("No server address is set.");

    private static string? ServerToken() => App.Repository.Credentials.ServerToken();

    private static double? ParseNumber(string? text) =>
        double.TryParse(text, out var value) ? value : null;

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser is a rare but survivable state.
        }
    }

    private sealed class AccountRow
    {
        public required ServerAccount Account { get; set; }
        public required Border Root { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Status { get; init; }
        public required Button Act { get; init; }
        public required WrapPanel Extras { get; init; }
        public required Button SignOut { get; init; }
        public required Button Remove { get; init; }
        public required StackPanel Step { get; init; }
        public required TextBlock Problem { get; init; }
        public DispatcherTimer? Poll { get; set; }
    }
}
