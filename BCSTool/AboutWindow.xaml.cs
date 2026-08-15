using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using BCSTool.Infrastructure;
using BCSTool.Models;
using BCSTool.Services;

namespace BCSTool;

public partial class AboutWindow : Window
{
    private readonly UpdateService _updateService;
    private readonly Func<bool> _isServerFullyStopped;

    public AboutWindow(
        UpdateService updateService,
        Func<bool> isServerFullyStopped)
    {
        InitializeComponent();

        _updateService = updateService;
        _isServerFullyStopped = isServerFullyStopped;

        VersionText.Text = $"Version {AppVersion.DisplayVersion}";
        _updateService.StatusChanged += UpdateService_StatusChanged;
        Closed += AboutWindow_Closed;

        RefreshUpdateUi();
    }

    private async void UpdateButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            _updateService.State !=
                UpdateCheckState.UpdateAvailable)
        {
            await _updateService.CheckForUpdatesAsync();
            return;
        }

        var release = _updateService.AvailableRelease;

        if (release is null)
            return;

        if (!_isServerFullyStopped())
        {
            MessageBox.Show(
                this,
                "The Bannerlord Coop server must be fully stopped before " +
                "installing the BCS Tool update.",
                "Server Must Be Stopped",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Download and install BCS Tool v{release.Version}?\n\n" +
            "BCS Tool will close, replace its executable, and reopen.",
            "Install BCS Tool Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (result != MessageBoxResult.Yes)
            return;

        if (await _updateService.PrepareAndLaunchInstallerAsync())
            Application.Current.Shutdown();
    }

    private void GitHubRepository_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenWebPage(
            UpdateService.RepositoryUrl,
            "GitHub repository");
    }

    private void AuthorProfile_RequestNavigate(
        object sender,
        RequestNavigateEventArgs e)
    {
        e.Handled = true;

        OpenWebPage(
            e.Uri.AbsoluteUri,
            "author's GitHub profile");
    }

    private void OpenWebPage(
        string url,
        string description)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not open the {description}.\n\n{ex.Message}",
                "About BCS Tool",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UpdateService_StatusChanged(
        object? sender,
        EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(RefreshUpdateUi));
            return;
        }

        RefreshUpdateUi();
    }

    private void RefreshUpdateUi()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";
        DownloadProgressPanel.Visibility = Visibility.Collapsed;

        switch (_updateService.State)
        {
            case UpdateCheckState.NotChecked:
                SetUpdateButton("Check for Updates", true);
                break;

            case UpdateCheckState.Checking:
                SetUpdateButton("Checking...", false);
                break;

            case UpdateCheckState.UpToDate:
                SetUpdateButton("Already Up to Date", false);
                break;

            case UpdateCheckState.UpdateAvailable:
                var latestVersion =
                    _updateService.AvailableRelease?.Version;
                SetUpdateButton(
                    latestVersion is null
                        ? "Download Update"
                        : $"Download v{latestVersion}",
                    true);
                break;

            case UpdateCheckState.Downloading:
                ShowDownloadProgress();
                break;

            case UpdateCheckState.Installing:
                SetUpdateButton("Installing...", false);
                break;

            case UpdateCheckState.Failed:
                SetUpdateButton("Try Again", true);
                ErrorText.Text = _updateService.ErrorMessage;
                ErrorText.Visibility = Visibility.Visible;
                break;
        }
    }

    private void ShowDownloadProgress()
    {
        var progress = _updateService.DownloadProgressPercent;

        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = progress is null;
        DownloadProgressBar.Value = progress ?? 0;
        DownloadProgressText.Text = progress is null
            ? "Downloading update..."
            : $"Downloading update... {progress.Value}%";

        SetUpdateButton(
            progress is null
                ? "Downloading..."
                : $"Downloading... {progress.Value}%",
            false);
    }

    private void SetUpdateButton(
        string content,
        bool enabled)
    {
        UpdateButton.Content = content;
        UpdateButton.IsEnabled = enabled;
    }

    private void AboutWindow_Closed(
        object? sender,
        EventArgs e)
    {
        _updateService.StatusChanged -= UpdateService_StatusChanged;
    }
}
