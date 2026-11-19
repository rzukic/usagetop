using System.Windows;
using System.Windows.Controls;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// Settings › Updates: the running version, what the updater is doing, and the
/// beta switch. Built once and kept, like the Server section, and its lines are
/// rewritten in place when the updater moves on: Settings is not redrawn on its
/// own (it would lose what is being typed in the server fields), so this is
/// what keeps "Downloading 1.2.1… 40%" moving.
/// </summary>
public sealed partial class PanelView
{
    private FrameworkElement? _updatesSection;
    private TextBlock? _updateVersion;
    private TextBlock? _updateStatus;
    private Button? _checkNow;
    private FrameworkElement? _restartRow;

    /// <summary>Whether the rail's gear carries the update dot, so it is redrawn only when that changes.</summary>
    private bool _railShowsUpdate;

    private static bool UpdateWaiting => Updater.Current.State == Updater.Phase.Ready;

    private void OnUpdaterChanged()
    {
        ShowUpdateState();
        if (_railShowsUpdate != UpdateWaiting) RenderRail(App.Repository.Cached());
    }

    private FrameworkElement UpdatesSection()
    {
        if (_updatesSection != null)
        {
            ShowUpdateState();
            return _updatesSection;
        }

        var updater = Updater.Current;
        var prefs = PanelPrefs.Current;
        var stack = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _updateVersion = Text("", Body, FontWeights.Normal, Primary);
        _updateStatus = Text("", Caption, FontWeights.Normal, Secondary, new Thickness(0, 2, 10, 0));
        words.Children.Add(_updateVersion);
        words.Children.Add(_updateStatus);
        head.Children.Add(words);
        _checkNow = SoftButton("Check now", "Look for a newer version now", "\uE72C");
        _checkNow.VerticalAlignment = VerticalAlignment.Center;
        _checkNow.Click += (_, _) => _ = updater.CheckAsync();
        Grid.SetColumn(_checkNow, 1);
        head.Children.Add(_checkNow);
        stack.Children.Add(head);

        var restartRow = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var restart = new Button
        {
            Style = Res("AccentButton"),
            Content = "Restart to update",
            ToolTip = "Close usagetop, put the new version in and start it again",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        restart.Click += (_, _) =>
        {
            if (updater.RestartToUpdate()) ((App)Application.Current).QuitApp();
        };
        restartRow.Children.Add(restart);
        restartRow.Children.Add(Text("Otherwise it goes in when usagetop next quits or starts.", Caption, FontWeights.Normal, Secondary, new Thickness(0, 6, 0, 0)));
        _restartRow = restartRow;
        stack.Children.Add(restartRow);

        var beta = new CheckBox
        {
            Style = Res("Switch"),
            Content = "Beta versions",
            IsChecked = prefs.BetaUpdates,
            Margin = new Thickness(0, 12, 0, 0),
        };
        // The updater sees the change and looks again with it.
        beta.Checked += (_, _) => prefs.Update(p => p.BetaUpdates = true);
        beta.Unchecked += (_, _) => prefs.Update(p => p.BetaUpdates = false);
        stack.Children.Add(beta);
        stack.Children.Add(Text("Early builds as well as releases, whichever is newer.", Caption, FontWeights.Normal, Secondary, new Thickness(0, 6, 0, 0)));

        _updatesSection = Section("Updates", "\uE896", stack);
        ShowUpdateState();
        return _updatesSection;
    }

    /// <summary>Rewrites the section's lines from the updater; nothing else on the page is touched.</summary>
    private void ShowUpdateState()
    {
        if (_updateVersion == null || _updateStatus == null || _checkNow == null || _restartRow == null) return;
        var updater = Updater.Current;

        _updateVersion.Text = $"usagetop {updater.Version}";
        _updateStatus.Text = UpdateStatus(updater);
        _updateStatus.Foreground = updater.State == Updater.Phase.Ready ? Good : Secondary;
        _updateStatus.ToolTip = updater.State == Updater.Phase.Failed ? updater.FailureDetail
            : updater.SourceOverride != null ? $"Updates from {updater.SourceOverride}"
            : null;

        _checkNow.Visibility = updater.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        _checkNow.IsEnabled = !updater.Busy;
        _restartRow.Visibility = updater.State == Updater.Phase.Ready ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string UpdateStatus(Updater updater) => updater.State switch
    {
        Updater.Phase.Off => "Not an installed copy, so it does not update itself",
        Updater.Phase.Waiting => "Looks for updates shortly",
        Updater.Phase.Checking => "Checking for updates…",
        Updater.Phase.UpToDate when updater.CheckedAt is { } at => $"Up to date · checked {Format.Ago(at)}",
        Updater.Phase.UpToDate => "Up to date",
        Updater.Phase.Downloading when updater.Progress > 0 => $"Downloading {updater.Target}… {updater.Progress}%",
        Updater.Phase.Downloading => $"Downloading {updater.Target}…",
        Updater.Phase.Ready => $"{updater.Target} is ready",
        _ => updater.Failure ?? "Couldn't check for updates",
    };
}
