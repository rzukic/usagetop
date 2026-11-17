using System.Windows;
using System.Windows.Media.Animation;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// One provider in full, or every subscription averaged into one drawdown, in its
/// own window. The page itself is <see cref="DetailView"/>, which the tray popup
/// shows too.
/// </summary>
public partial class DetailWindow : Window
{
    private Storyboard? _spin;

    /// <summary>Null opens the combined view.</summary>
    public DetailWindow(string? account)
    {
        InitializeComponent();

        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        if (account != null && Account.Parse(account) is { } parsed)
        {
            var name = App.Repository.Cached()?.Usages.FirstOrDefault(u => u.AccountKey == account)?.DisplayName
                ?? ProviderCatalog.Of(parsed.Provider).DisplayName;
            Title = $"{name} — usagetop";
            HeaderTitle.Text = name;
            HeaderLogo.Data = ProviderArt.Logo(parsed.Provider);
        }
        else
        {
            Title = "All usage — usagetop";
            HeaderTitle.Text = "All usage";
            HeaderLogo.Visibility = Visibility.Collapsed;
        }

        Detail.Show(account);

        // A provider picked inside the combined view gets its own window here.
        Detail.Navigate += id => ((App)Application.Current).ShowDetail(id);

        App.Repository.Changed += OnChanged;

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

        StopSpin();
    }

    // ---- Refresh ----------------------------------------------------------

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync(force: true);

    private async Task RefreshAsync(bool force = false)
    {
        StartSpin();
        await ((App)Application.Current).RefreshAsync(force);
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
}
