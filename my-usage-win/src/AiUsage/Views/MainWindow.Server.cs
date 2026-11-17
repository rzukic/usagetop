using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// The server's accounts, managed from here. In server mode the app's own
/// sign-ins are not used, so the settings show this instead: add an account,
/// set or replace its key, sign in again, rename it, sign it out, remove it.
/// The server never hands a token back, so nothing here can read a key or a
/// session — only replace one.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, ServerAccountRow> _serverRows = new(StringComparer.Ordinal);
    private StackPanel? _serverAccountsHost;
    private TextBlock? _serverAccountsStatus;
    private bool _serverAccountsLoading;

    private Border ServerAccountsCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Accounts on the server",
            Style = (Style)FindResource("CardTitle"),
        });

        _serverAccountsStatus = new TextBlock
        {
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Reading…",
        };
        stack.Children.Add(_serverAccountsStatus);

        _serverAccountsHost = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(_serverAccountsHost);

        // Another account of a provider: a second Claude subscription, a work ChatGPT.
        stack.Children.Add(new TextBlock
        {
            Text = "Add another account",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 12, 0, 0),
        });

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var info in ProviderCatalog.All)
        {
            var button = new Button
            {
                Content = info.DisplayName,
                Style = (Style)FindResource("QuietButton"),
                Margin = new Thickness(0, 0, 14, 4),
                Padding = new Thickness(0),
            };
            button.Click += (_, _) => _ = AddServerAccountAsync(info);
            buttons.Children.Add(button);
        }
        stack.Children.Add(buttons);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    /// <summary>Drops the rows and any sign-in still being watched, before the page is rebuilt.</summary>
    private void ClearServerRows()
    {
        foreach (var row in _serverRows.Values) CloseServerStep(row);
        _serverRows.Clear();
    }

    /// <summary>Asks the server for its accounts and redraws the rows in its order.</summary>
    private async Task LoadServerAccountsAsync()
    {
        if (_serverAccountsHost == null || _serverAccountsLoading) return;
        if (App.Repository.Credentials.ServerUrl() is not { } url) return;

        _serverAccountsLoading = true;
        try
        {
            var accounts = await ServerClient.AccountsAsync(url, App.Repository.Credentials.ServerToken());
            if (_serverAccountsHost == null) return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var account in accounts)
            {
                seen.Add(account.Key);
                if (!_serverRows.TryGetValue(account.Key, out var row))
                {
                    row = BuildServerRow(account);
                    _serverRows[account.Key] = row;
                }
                UpdateServerRow(row, account);
                // Appending a child already in place moves it, so the order follows the server's.
                _serverAccountsHost.Children.Add(row.Card);
            }

            foreach (var (key, row) in _serverRows.ToList())
            {
                if (seen.Contains(key)) continue;
                CloseServerStep(row);
                _serverAccountsHost.Children.Remove(row.Card);
                _serverRows.Remove(key);
            }

            if (_serverAccountsStatus != null)
            {
                _serverAccountsStatus.Text = accounts.Count == 0
                    ? "The server follows no accounts yet."
                    : "Keys and sessions stay on the server; this app never reads them back.";
            }
        }
        catch (ServerException problem)
        {
            if (_serverAccountsStatus != null) _serverAccountsStatus.Text = problem.Message;
        }
        finally
        {
            _serverAccountsLoading = false;
        }
    }

    private ServerAccountRow BuildServerRow(ServerAccount account)
    {
        var stack = new StackPanel();

        var title = new TextBlock { Style = (Style)FindResource("CardTitle") };
        var status = new TextBlock
        {
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(title);
        stack.Children.Add(status);

        var act = new Button
        {
            Style = (Style)FindResource("PillButton"),
            Margin = new Thickness(0, 0, 8, 4),
        };
        var signOut = new Button
        {
            Content = "Sign out",
            Style = (Style)FindResource("QuietButton"),
            Margin = new Thickness(0, 0, 8, 4),
        };
        var rename = new Button
        {
            Content = "Rename",
            Style = (Style)FindResource("QuietButton"),
            Margin = new Thickness(0, 0, 8, 4),
        };
        var remove = new Button
        {
            Content = "Remove",
            Style = (Style)FindResource("QuietButton"),
            Margin = new Thickness(0, 0, 8, 4),
        };

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(act);
        buttons.Children.Add(signOut);
        buttons.Children.Add(rename);
        buttons.Children.Add(remove);
        stack.Children.Add(buttons);

        var step = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        stack.Children.Add(step);

        var problem = new TextBlock
        {
            Style = (Style)FindResource("Hint"),
            Foreground = (Brush)FindResource("MeterWarningBrush"),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(problem);

        var row = new ServerAccountRow
        {
            Account = account,
            Card = new Border
            {
                BorderBrush = (Brush)FindResource("HairlineBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 10, 0, 10),
                Child = stack,
            },
            Title = title,
            Status = status,
            Act = act,
            SignOut = signOut,
            Rename = rename,
            Remove = remove,
            Step = step,
            Problem = problem,
        };

        act.Click += (_, _) => _ = ActAsync(row);
        signOut.Click += (_, _) => _ = SignOutAsync(row);
        rename.Click += (_, _) => ShowRenameStep(row);
        remove.Click += (_, _) => _ = RemoveAsync(row);

        return row;
    }

    private void UpdateServerRow(ServerAccountRow row, ServerAccount account)
    {
        row.Account = account;
        var keyProvider = IsKeyProvider(account.Provider);

        row.Title.Text = account.DisplayName;
        row.Status.Text = !account.Configured ? "Not set up"
            : keyProvider ? "Key stored"
            : account.Renews ? "Signed in · renews itself"
            : "Token stored";

        row.Act.Content = keyProvider
            ? (account.Configured ? "Replace key" : "Set key")
            : (account.Configured ? "Sign in again" : "Sign in");
        row.SignOut.Visibility = account.Configured ? Visibility.Visible : Visibility.Collapsed;
        row.Remove.Visibility = account.Removable ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsKeyProvider(string provider) =>
        ProviderCatalog.FromKey(provider)?.Id is ProviderId.Mistral or ProviderId.Kimi;

    private async Task ActAsync(ServerAccountRow row)
    {
        if (IsKeyProvider(row.Account.Provider)) ShowKeyStep(row);
        else await StartServerSignInAsync(row);
    }

    /// <summary>
    /// A key is write-only here: the server says whether one is stored, never
    /// what it is, so this asks for a new one rather than showing the old.
    /// </summary>
    private void ShowKeyStep(ServerAccountRow row)
    {
        CloseServerStep(row);
        row.Step.Visibility = Visibility.Visible;

        row.Step.Children.Add(new TextBlock
        {
            Text = $"{row.Account.DisplayName} API key",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var field = new PasswordBox
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 12,
            Padding = new Thickness(10, 8, 10, 8),
            Background = (Brush)FindResource("ControlFillBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            CaretBrush = (Brush)FindResource("TextPrimaryBrush"),
            BorderThickness = new Thickness(0),
        };
        row.Step.Children.Add(field);

        CheckBox? china = null;
        TextBox? budget = null;
        TextBox? spent = null;
        var id = ProviderCatalog.FromKey(row.Account.Provider)?.Id;

        if (id == ProviderId.Kimi)
        {
            china = new CheckBox
            {
                Content = "Use China endpoint (api.moonshot.cn)",
                Style = (Style)FindResource("Check"),
                Margin = new Thickness(0, 10, 0, 0),
            };
            row.Step.Children.Add(china);
        }

        if (id == ProviderId.Mistral)
        {
            budget = NumberField(row.Step, "Monthly budget (€)", "Only used when the key cannot read billing");
            spent = NumberField(row.Step, "Spent so far (€)", null);
        }

        var save = new Button
        {
            Content = "Save",
            Style = (Style)FindResource("PillButton"),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        save.Click += async (_, _) =>
        {
            var value = field.Password.Trim();
            if (value.Length == 0)
            {
                row.Problem.Text = "Paste a key first.";
                return;
            }

            save.IsEnabled = false;
            try
            {
                await ServerClient.SetKeyAsync(
                    ServerUrl(),
                    ServerToken(),
                    row.Account.Key,
                    value,
                    china?.IsChecked,
                    ParseNumber(budget?.Text),
                    ParseNumber(spent?.Text));

                CloseServerStep(row);
                await AfterServerChangeAsync();
            }
            catch (ServerException problem)
            {
                row.Problem.Text = problem.Message;
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        row.Step.Children.Add(save);
        field.Focus();
    }

    private async Task StartServerSignInAsync(ServerAccountRow row)
    {
        CloseServerStep(row);
        row.Act.IsEnabled = false;
        try
        {
            var step = await ServerClient.StartSignInAsync(ServerUrl(), ServerToken(), row.Account.Key);

            row.Step.Visibility = Visibility.Visible;
            row.Step.Children.Add(new TextBlock
            {
                Text = step.Instructions,
                Style = (Style)FindResource("Hint"),
                TextWrapping = TextWrapping.Wrap,
            });

            var open = new Button
            {
                Content = "Open the sign-in page",
                Style = (Style)FindResource("PillButton"),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            open.Click += (_, _) => OpenUrl(step.Url);
            row.Step.Children.Add(open);

            if (step.Kind == "device")
            {
                row.Step.Children.Add(new TextBlock
                {
                    Text = step.UserCode ?? "",
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 10, 0, 0),
                });
                row.Step.Children.Add(new TextBlock
                {
                    Text = "Waiting for you to approve…",
                    Style = (Style)FindResource("Hint"),
                    Margin = new Thickness(0, 4, 0, 0),
                });

                row.Poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                row.Poll.Tick += (_, _) => _ = PollServerSignInAsync(row);
                row.Poll.Start();
                return;
            }

            row.Step.Children.Add(new TextBlock
            {
                Text = step.Kind == "redirect"
                    ? "The address the browser landed on"
                    : "The code the page shows",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 10, 0, 4),
            });

            var field = new TextBox { Style = (Style)FindResource("Field") };
            row.Step.Children.Add(field);

            var finish = new Button
            {
                Content = "Finish",
                Style = (Style)FindResource("PillButton"),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            finish.Click += async (_, _) =>
            {
                var value = field.Text.Trim();
                if (value.Length == 0)
                {
                    row.Problem.Text = "Paste what the page showed first.";
                    return;
                }

                finish.IsEnabled = false;
                try
                {
                    await ServerClient.CompleteSignInAsync(ServerUrl(), ServerToken(), row.Account.Key, value);
                    CloseServerStep(row);
                    await AfterServerChangeAsync();
                }
                catch (ServerException problem)
                {
                    row.Problem.Text = problem.Message;
                }
                finally
                {
                    finish.IsEnabled = true;
                }
            };
            row.Step.Children.Add(finish);
            field.Focus();
        }
        catch (ServerException problem)
        {
            row.Problem.Text = problem.Message;
        }
        finally
        {
            row.Act.IsEnabled = true;
        }
    }

    private async Task PollServerSignInAsync(ServerAccountRow row)
    {
        try
        {
            var status = await ServerClient.SignInStatusAsync(ServerUrl(), ServerToken(), row.Account.Key);
            if (status == "pending") return;

            CloseServerStep(row);
            if (status != "done") row.Problem.Text = status;
            await AfterServerChangeAsync();
        }
        catch (ServerException problem)
        {
            CloseServerStep(row);
            row.Problem.Text = problem.Message;
        }
    }

    private void ShowRenameStep(ServerAccountRow row)
    {
        CloseServerStep(row);
        row.Step.Visibility = Visibility.Visible;

        row.Step.Children.Add(new TextBlock
        {
            Text = "Name, e.g. Work (empty for none)",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var field = new TextBox
        {
            Style = (Style)FindResource("Field"),
            Text = row.Account.Name ?? "",
        };
        row.Step.Children.Add(field);

        var save = new Button
        {
            Content = "Save",
            Style = (Style)FindResource("PillButton"),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                await ServerClient.RenameAccountAsync(ServerUrl(), ServerToken(), row.Account.Key, field.Text);
                CloseServerStep(row);
                await AfterServerChangeAsync();
            }
            catch (ServerException problem)
            {
                row.Problem.Text = problem.Message;
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        row.Step.Children.Add(save);
        field.Focus();
    }

    private async Task SignOutAsync(ServerAccountRow row)
    {
        if (MessageBox.Show(
                this,
                $"Sign the server out of {row.Account.DisplayName}?",
                "Sign out",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await ServerClient.SignOutAsync(ServerUrl(), ServerToken(), row.Account.Key);
            CloseServerStep(row);
            await AfterServerChangeAsync();
        }
        catch (ServerException problem)
        {
            row.Problem.Text = problem.Message;
        }
    }

    private async Task RemoveAsync(ServerAccountRow row)
    {
        if (MessageBox.Show(
                this,
                $"Remove {row.Account.DisplayName} from the server? Its sign-in is forgotten; its history stays on disk.",
                "Remove account",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await ServerClient.RemoveAccountAsync(ServerUrl(), ServerToken(), row.Account.Key);
            CloseServerStep(row);
            await AfterServerChangeAsync();
        }
        catch (ServerException problem)
        {
            row.Problem.Text = problem.Message;
        }
    }

    private async Task AddServerAccountAsync(ProviderInfo info)
    {
        try
        {
            var account = await ServerClient.AddAccountAsync(ServerUrl(), ServerToken(), info.Key, null);
            await LoadServerAccountsAsync();

            if (!_serverRows.TryGetValue(account.Key, out var row)) return;
            row.Card.BringIntoView();
            if (IsKeyProvider(account.Provider)) ShowKeyStep(row);
            else await StartServerSignInAsync(row);
        }
        catch (ServerException problem)
        {
            if (_serverAccountsStatus != null) _serverAccountsStatus.Text = problem.Message;
        }
    }

    /// <summary>The server polls an account the moment its credentials change.</summary>
    private async Task AfterServerChangeAsync()
    {
        await LoadServerAccountsAsync();
        await Task.Delay(TimeSpan.FromSeconds(4));
        await RefreshAsync();
    }

    private static void CloseServerStep(ServerAccountRow row)
    {
        row.Poll?.Stop();
        row.Poll = null;
        row.Step.Children.Clear();
        row.Step.Visibility = Visibility.Collapsed;
        row.Problem.Text = "";
    }

    private string ServerUrl() =>
        App.Repository.Credentials.ServerUrl() ?? throw new ServerException("No server address is set.");

    private string? ServerToken() => App.Repository.Credentials.ServerToken();

    private sealed class ServerAccountRow
    {
        public required ServerAccount Account { get; set; }
        public required Border Card { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Status { get; init; }
        public required Button Act { get; init; }
        public required Button SignOut { get; init; }
        public required Button Rename { get; init; }
        public required Button Remove { get; init; }
        public required StackPanel Step { get; init; }
        public required TextBlock Problem { get; init; }
        public DispatcherTimer? Poll { get; set; }
    }
}
