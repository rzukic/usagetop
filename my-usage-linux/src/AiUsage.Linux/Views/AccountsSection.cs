using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// The server's accounts, as the Windows and Mac apps show them: each with how
/// it is set up and the one thing to do next (sign in, set a key), the rarer
/// actions (rename, sign out, remove) behind "…", and a row of providers to add
/// another account of. Every step happens in place, under its row: a dialog
/// would take the focus and close the panel. Keys and sessions stay on the
/// server; nothing here reads them back.
/// </summary>
public sealed class AccountsSection : StackPanel
{
    private readonly AppModel _model;
    private readonly Func<Task> _changed;
    private readonly TextBlock _status;
    private readonly StackPanel _host = new() { Spacing = 0 };
    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private bool _loading;

    public AccountsSection(AppModel model, Func<Task> changed)
    {
        _model = model;
        _changed = changed;
        Spacing = 6;

        Children.Add(Bits.Text("Accounts", 13, FontWeight.SemiBold, Look.Primary));
        _status = Bits.Paragraph("Reading…", 11.5, Look.Secondary);
        Children.Add(_status);
        Children.Add(_host);

        // Another account of a provider: a second Claude subscription, a work ChatGPT.
        var label = Bits.Text("Add an account", 11, FontWeight.SemiBold, Look.Secondary);
        label.Margin = new Thickness(0, 8, 0, 2);
        Children.Add(label);
        var add = new WrapPanel();
        foreach (var info in ProviderCatalog.All)
        {
            var button = Bits.SoftButton(info.DisplayName, $"Add a {info.DisplayName} account to the server", () => _ = AddAsync(info));
            button.Margin = new Thickness(0, 0, 6, 6);
            add.Children.Add(button);
        }
        Children.Add(add);

        _ = LoadAsync();
    }

    private string Url => _model.Credentials.ServerUrl() ?? throw new ServerException("No server address is set.");

    private string? Token => _model.Credentials.ServerToken();

    /// <summary>Asks the server for its accounts and lays the rows out in its order, keeping open steps.</summary>
    public async Task LoadAsync()
    {
        if (_loading || _model.Credentials.ServerUrl() == null) return;
        _loading = true;
        try
        {
            var accounts = await ServerClient.AccountsAsync(Url, Token);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            _host.Children.Clear();
            foreach (var account in accounts)
            {
                seen.Add(account.Key);
                if (!_rows.TryGetValue(account.Key, out var row))
                {
                    row = new Row(this, account);
                    _rows[account.Key] = row;
                }
                row.Update(account);
                row.Root.BorderThickness = new Thickness(0, _host.Children.Count == 0 ? 0 : 0.75, 0, 0);
                _host.Children.Add(row.Root);
            }
            foreach (var key in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _rows[key].CloseStep();
                _rows.Remove(key);
            }
            _status.Text = accounts.Count == 0
                ? "The server follows no accounts yet."
                : "Keys and sessions stay on the server; this app never reads them back.";
        }
        catch (ServerException problem)
        {
            _status.Text = problem.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task AddAsync(ProviderInfo info)
    {
        try
        {
            var account = await ServerClient.AddAccountAsync(Url, Token, info.Key, null);
            await LoadAsync();
            if (_rows.TryGetValue(account.Key, out var row)) await row.ActAsync();
        }
        catch (ServerException problem)
        {
            _status.Text = problem.Message;
        }
    }

    /// <summary>The server polls an account as soon as its credentials change: read again shortly after.</summary>
    private async Task AfterChangeAsync()
    {
        await LoadAsync();
        await Task.Delay(TimeSpan.FromSeconds(4));
        await _changed();
    }

    private static bool IsKeyProvider(string provider) =>
        ProviderCatalog.FromKey(provider)?.Id is ProviderId.Mistral or ProviderId.Kimi;

    private static TextBox Field(string placeholder = "", bool secret = false) => new()
    {
        PlaceholderText = placeholder,
        PasswordChar = secret ? '•' : default,
        FontSize = 12.5,
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(10, 7),
        MinHeight = 34,
    };

    private static TextBlock FieldLabel(string text)
    {
        var label = Bits.Text(text, 11, FontWeight.SemiBold, Look.Secondary);
        label.Margin = new Thickness(0, 10, 0, 4);
        return label;
    }

    /// <summary>One account on the server, with its step open under it while there is one.</summary>
    private sealed class Row
    {
        private readonly AccountsSection _section;
        private readonly TextBlock _title;
        private readonly TextBlock _state;
        private readonly Border _act;
        private readonly TextBlock _actLabel;
        private readonly WrapPanel _extras;
        private readonly Border _signOut;
        private readonly Border _remove;
        private readonly StackPanel _step = new();
        private readonly TextBlock _problem;
        private DispatcherTimer? _poll;

        public Row(AccountsSection section, ServerAccount account)
        {
            _section = section;
            Account = account;

            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            var id = ProviderCatalog.FromKey(account.Provider)?.Id ?? ProviderId.Claude;
            head.Children.Add(new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = Look.Ink(0.07),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Icons.Glyph(ProviderArt.Logo(id), 13, Look.Primary),
            });

            var names = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _title = Bits.Text("", 12.5, FontWeight.SemiBold, Look.Primary);
            _state = Bits.Text("", 11, FontWeight.Normal, Look.Secondary);
            names.Children.Add(_title);
            names.Children.Add(_state);
            Grid.SetColumn(names, 1);
            head.Children.Add(names);

            _actLabel = Bits.Text("", 11.5, FontWeight.Medium, Look.Primary);
            _act = Bits.Capsule(_actLabel, Look.Ink(0.09), Look.Ink(0.12), new Thickness(11, 5));
            Bits.Clickable(_act, "", () => _ = ActAsync(), over => _act.Background = Look.Ink(over ? 0.16 : 0.09));
            Grid.SetColumn(_act, 2);
            head.Children.Add(_act);

            _extras = new WrapPanel { Margin = new Thickness(36, 8, 0, 0), IsVisible = false };
            var more = Bits.IconButton(Icons.More, "More", () => _extras.IsVisible = !_extras.IsVisible, size: 26);
            more.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(more, 3);
            head.Children.Add(more);

            var rename = Bits.SoftButton("Rename", "Give this account a name of its own", ShowRename);
            _signOut = Bits.SoftButton("Sign out", "Forget this account's sign-in on the server", () => Confirm(
                $"Sign the server out of {Account.DisplayName}?", "Sign out",
                () => ServerClient.SignOutAsync(_section.Url, _section.Token, Account.Key)));
            _remove = Bits.SoftButton("Remove", "Stop following this account", () => Confirm(
                $"Remove {Account.DisplayName} from the server? Its sign-in is forgotten; its history stays.", "Remove",
                () => ServerClient.RemoveAccountAsync(_section.Url, _section.Token, Account.Key)));
            foreach (var button in new[] { rename, _signOut, _remove })
            {
                button.Margin = new Thickness(0, 0, 6, 6);
                _extras.Children.Add(button);
            }

            _step.Margin = new Thickness(36, 0, 0, 0);
            _problem = Bits.Paragraph("", 11, Look.Solid(Look.Warning));
            _problem.Margin = new Thickness(36, 6, 0, 0);
            _problem.IsVisible = false;

            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(_extras);
            stack.Children.Add(_step);
            stack.Children.Add(_problem);
            Root = new Border
            {
                BorderBrush = Look.Ink(0.1),
                Padding = new Thickness(0, 10),
                Child = stack,
            };
        }

        public ServerAccount Account { get; private set; }

        public Border Root { get; }

        private bool IsKey => IsKeyProvider(Account.Provider);

        public void Update(ServerAccount account)
        {
            Account = account;
            _title.Text = account.DisplayName;
            _state.Text = !account.Configured ? "Not set up"
                : IsKey ? "Key stored"
                : account.Renews ? "Signed in · renews itself"
                : "Token stored";
            _state.Foreground = account.Configured ? Look.Secondary : Look.Solid(Look.Warning);
            _actLabel.Text = IsKey
                ? (account.Configured ? "Replace key" : "Set key")
                : (account.Configured ? "Sign in again" : "Sign in");
            ToolTip.SetTip(_act, IsKey ? "Give the server this account's API key" : "Sign the server in to this account, in your browser");
            _signOut.IsVisible = account.Configured;
            _remove.IsVisible = account.Removable;
        }

        public Task ActAsync()
        {
            if (IsKey)
            {
                ShowKey();
                return Task.CompletedTask;
            }
            return StartSignInAsync();
        }

        /// <summary>A key is write-only: the server says whether one is stored, never what it is.</summary>
        private void ShowKey()
        {
            OpenStep();
            var id = ProviderCatalog.FromKey(Account.Provider)?.Id;
            _step.Children.Add(FieldLabel($"{Account.DisplayName} API key"));
            var field = Field("Paste the key", secret: true);
            _step.Children.Add(field);

            Switch? china = null;
            TextBox? budget = null, spent = null;
            if (id == ProviderId.Kimi)
            {
                china = new Switch("China endpoint (api.moonshot.cn)", false, _ => { });
                china.Margin = new Thickness(0, 10, 0, 0);
                _step.Children.Add(china);
            }
            if (id == ProviderId.Mistral)
            {
                _step.Children.Add(FieldLabel("Monthly budget (€)"));
                budget = Field();
                _step.Children.Add(budget);
                _step.Children.Add(FieldLabel("Spent so far (€)"));
                spent = Field();
                _step.Children.Add(spent);
                _step.Children.Add(Bits.Paragraph("Only used when the key cannot read billing.", 11, Look.Secondary));
            }

            _step.Children.Add(StepButtons("Save", async () =>
            {
                var value = (field.Text ?? "").Trim();
                if (value.Length == 0) throw new ServerException("Paste a key first.");
                await ServerClient.SetKeyAsync(_section.Url, _section.Token, Account.Key, value,
                    china?.On, Number(budget?.Text), Number(spent?.Text));
                CloseStep();
                await _section.AfterChangeAsync();
            }));
            Dispatcher.UIThread.Post(() => field.Focus());
        }

        /// <summary>
        /// The server starts the sign-in and says what the user does next: open
        /// a page and paste back what it shows, or type a code into it while
        /// this waits. The page opens in the browser, which closes the panel;
        /// the step is still here when it is opened again.
        /// </summary>
        private async Task StartSignInAsync()
        {
            _act.IsEnabled = false;
            ShowProblem(null);
            try
            {
                var step = await ServerClient.StartSignInAsync(_section.Url, _section.Token, Account.Key);
                OpenStep();
                var instructions = Bits.Paragraph(step.Instructions, 11.5, Look.Secondary);
                instructions.Margin = new Thickness(0, 10, 0, 0);
                _step.Children.Add(instructions);
                var open = Bits.SoftButton("Open the sign-in page ↗", step.Url, () => OpenUrl(step.Url));
                open.HorizontalAlignment = HorizontalAlignment.Left;
                open.Margin = new Thickness(0, 8, 0, 0);
                _step.Children.Add(open);

                if (step.Kind == "device")
                {
                    // The code to type on the page, large enough to read across, and copyable.
                    var code = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
                    code.Children.Add(Bits.Text(step.UserCode ?? "", 22, FontWeight.SemiBold, Look.Primary));
                    code.Children.Add(Bits.SoftButton("Copy", "Copy the code", () => _ = CopyAsync(step.UserCode ?? "")));
                    _step.Children.Add(code);
                    _step.Children.Add(Bits.Text("Waiting for you to approve…", 11, FontWeight.Normal, Look.Secondary));
                    var cancel = Bits.SoftButton("Cancel", "Stop waiting", CloseStep);
                    cancel.HorizontalAlignment = HorizontalAlignment.Left;
                    cancel.Margin = new Thickness(0, 10, 0, 0);
                    _step.Children.Add(cancel);
                    _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    _poll.Tick += (_, _) => _ = PollAsync();
                    _poll.Start();
                    return;
                }

                _step.Children.Add(FieldLabel(step.Kind == "redirect" ? "The address the browser landed on" : "The code the page shows"));
                var field = Field();
                _step.Children.Add(field);
                _step.Children.Add(StepButtons("Finish", async () =>
                {
                    var value = (field.Text ?? "").Trim();
                    if (value.Length == 0) throw new ServerException("Paste what the page showed first.");
                    await ServerClient.CompleteSignInAsync(_section.Url, _section.Token, Account.Key, value);
                    CloseStep();
                    await _section.AfterChangeAsync();
                }));
            }
            catch (ServerException problem)
            {
                ShowProblem(problem.Message);
            }
            finally
            {
                _act.IsEnabled = true;
            }
        }

        private async Task PollAsync()
        {
            try
            {
                var status = await ServerClient.SignInStatusAsync(_section.Url, _section.Token, Account.Key);
                if (status == "pending") return;
                CloseStep();
                if (status != "done") ShowProblem(status);
                await _section.AfterChangeAsync();
            }
            catch (ServerException problem)
            {
                CloseStep();
                ShowProblem(problem.Message);
            }
        }

        private void ShowRename()
        {
            OpenStep();
            _step.Children.Add(FieldLabel("Name, e.g. Work (empty for none)"));
            var field = Field();
            field.Text = Account.Name ?? "";
            _step.Children.Add(field);
            _step.Children.Add(StepButtons("Save", async () =>
            {
                await ServerClient.RenameAccountAsync(_section.Url, _section.Token, Account.Key, field.Text ?? "");
                CloseStep();
                await _section.AfterChangeAsync();
            }));
            Dispatcher.UIThread.Post(() =>
            {
                field.Focus();
                field.SelectAll();
            });
        }

        /// <summary>A question asked in place: a dialog would take the focus and close the panel.</summary>
        private void Confirm(string question, string action, Func<Task> perform)
        {
            OpenStep();
            var text = Bits.Paragraph(question, 11.5, Look.Primary);
            text.Margin = new Thickness(0, 10, 0, 0);
            _step.Children.Add(text);
            _step.Children.Add(StepButtons(action, async () =>
            {
                await perform();
                CloseStep();
                await _section.AfterChangeAsync();
            }, destructive: true));
        }

        /// <summary>Cancel on the left, the step's action on the right; a server's refusal shows under the row.</summary>
        private Grid StepButtons(string action, Func<Task> perform, bool destructive = false)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 12, 0, 2) };
            grid.Children.Add(Bits.SoftButton("Cancel", "Leave it as it is", CloseStep));
            var busy = false;
            Border? go = null;
            go = Bits.SoftButton(action, "", () =>
            {
                if (busy) return;
                busy = true;
                go!.Opacity = 0.5;
                ShowProblem(null);
                _ = Run();
            }, prominent: !destructive);
            if (destructive && go.Child is TextBlock label) label.Foreground = Look.Solid(Look.Critical);
            Grid.SetColumn(go, 2);
            grid.Children.Add(go);
            return grid;

            async Task Run()
            {
                try
                {
                    await perform();
                }
                catch (ServerException problem)
                {
                    ShowProblem(problem.Message);
                }
                finally
                {
                    busy = false;
                    go!.Opacity = 1;
                }
            }
        }

        private void OpenStep()
        {
            CloseStep();
            _extras.IsVisible = false;
        }

        public void CloseStep()
        {
            _poll?.Stop();
            _poll = null;
            _step.Children.Clear();
            ShowProblem(null);
        }

        private void ShowProblem(string? text)
        {
            _problem.Text = text ?? "";
            _problem.IsVisible = !string.IsNullOrEmpty(text);
        }

        private void OpenUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                _ = TopLevel.GetTopLevel(Root)?.Launcher.LaunchUriAsync(uri);
            }
        }

        private async Task CopyAsync(string text)
        {
            if (TopLevel.GetTopLevel(Root)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }

        private static double? Number(string? text) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }
}
