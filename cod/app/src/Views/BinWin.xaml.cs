using System.Globalization;
using System.Windows;
using ShouTech.App;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Company recycle bin for soft-deleted master data.
/// Restore returns rows to working sets; permanent delete is irreversible.
/// </summary>
public partial class BinWin : Window
{
    private bool _busy;

    public BinWin()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        try
        {
            LangDirSv.ApplyToWindow(this);
        }
        catch
        {
            // Direction helpers must not block the window.
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        if (_busy)
            return;

        SetBusy(true);
        try
        {
            StatusText.Text = "Loading…";
            var companyCode = AppState.CurrentCompanyCode ?? string.Empty;
            if (string.IsNullOrWhiteSpace(companyCode))
            {
                ItemsGrid.ItemsSource = Array.Empty<RecycleBinItem>();
                StatusText.Text = "No active company. Open a company session first.";
                return;
            }

            var items = await RecycleBinSvc.ListAsync(companyCode).ConfigureAwait(true);
            ItemsGrid.ItemsSource = items;
            StatusText.Text = items.Count == 0
                ? "The recycle bin is empty for this company."
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "{0:N0} item(s) in the recycle bin for company {1}.",
                    items.Count,
                    companyCode);
        }
        catch (Exception ex)
        {
            ItemsGrid.ItemsSource = Array.Empty<RecycleBinItem>();
            StatusText.Text = "Failed to load the recycle bin.";
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await ReloadAsync().ConfigureAwait(true);

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select one or more rows to restore.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        try
        {
            var results = await RecycleBinSvc.RestoreManyAsync(selected).ConfigureAwait(true);
            var restored = results.Count(result => result.Succeeded);
            var failed = results.Count - restored;

            if (failed == 0)
            {
                MessageBox.Show(this,
                    string.Format(CultureInfo.CurrentCulture, "Restored {0:N0} item(s).", restored),
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var firstError = results.FirstOrDefault(result => !result.Succeeded)?.ErrorMessage;
                MessageBox.Show(this,
                    string.Format(CultureInfo.CurrentCulture,
                        "Restored {0:N0} item(s). {1:N0} failed.{2}{3}",
                        restored, failed, Environment.NewLine, firstError ?? string.Empty),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    private async void Purge_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select one or more rows to delete permanently.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            "Selected items will be permanently deleted and cannot be restored.\n\nContinue?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var purged = 0;
            var failed = 0;
            string? firstError = null;
            foreach (var item in selected)
            {
                var result = await RecycleBinSvc.PurgeAsync(item).ConfigureAwait(true);
                if (result.Succeeded)
                    purged++;
                else
                {
                    failed++;
                    firstError ??= result.ErrorMessage;
                }
            }

            if (failed == 0)
            {
                MessageBox.Show(this,
                    string.Format(CultureInfo.CurrentCulture, "Permanently deleted {0:N0} item(s).", purged),
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    string.Format(CultureInfo.CurrentCulture,
                        "Permanently deleted {0:N0} item(s). {1:N0} failed.{2}{3}",
                        purged, failed, Environment.NewLine, firstError ?? string.Empty),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private List<RecycleBinItem> GetSelectedItems()
        => ItemsGrid.SelectedItems.OfType<RecycleBinItem>().ToList();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;
        PurgeButton.IsEnabled = !busy;
        ItemsGrid.IsEnabled = !busy;
    }
}
