using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BCSTool.Models;
using BCSTool.Services;
using BCSTool.ViewModels;

namespace BCSTool;

/// <summary>
/// Dedicated server configuration editor.
/// </summary>
public partial class ServerConfigurationWindow : Window
{
    private readonly CoopConfigService _configService;
    private readonly MainViewModel _viewModel;

    private DedicatedServerConfig _config =
        new();

    private bool _updatingPasswordControls;

    private ServerConfigurationSnapshot? _savedConfigurationSnapshot;
    private bool _configurationLoaded;


    public ServerConfigurationWindow(
        CoopConfigService configService,
        MainViewModel viewModel)
    {
        InitializeComponent();

        _configService =
            configService;

        _viewModel =
            viewModel;

        ConfigPathText.Text =
            _configService.ServerConfigPath;

        LoadConfiguration();
    }


    private void LoadConfiguration()
    {
        try
        {
            _config =
                _configService.LoadServerConfig();

            DataContext =
                _config;

            SetPasswordControls(
                _config.Password);

            SaveBackupsEnabledCheckBox.IsChecked =
                _viewModel.Settings.SaveBackupsEnabled;

            SaveBackupCountComboBox.ItemsSource =
                _viewModel.SaveBackupCountOptions;

            SaveBackupCountComboBox.SelectedItem =
                _viewModel.Settings.SaveBackupCount;

            _savedConfigurationSnapshot =
                CreateConfigurationSnapshot();

            _configurationLoaded =
                true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Server Configuration",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    /// <summary>
    /// Saves Bannerlord's server configuration and BCS Tool's backup-rotation
    /// settings. The two setting groups intentionally use different storage:
    /// server-config.json for Bannerlord and the BCS Tool Registry key for the
    /// backup feature.
    /// </summary>
    private async Task<bool> SaveConfigurationAsync()
    {
        if (HasValidationErrors(this))
        {
            MessageBox.Show(
                this,
                "One or more values are not valid. Correct the highlighted fields before saving.",
                "Server Configuration",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        try
        {
            _configService.SaveServerConfig(
                _config);

            var backupCount =
                SaveBackupCountComboBox.SelectedItem is int selectedCount
                    ? selectedCount
                    : _viewModel.Settings.SaveBackupCount;

            await _viewModel.UpdateSaveBackupSettingsAsync(
                SaveBackupsEnabledCheckBox.IsChecked == true,
                backupCount);

            _savedConfigurationSnapshot =
                CreateConfigurationSnapshot();

            _configurationLoaded =
                true;

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Server Configuration",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return false;
        }
    }


    private ServerConfigurationSnapshot CreateConfigurationSnapshot()
    {
        var backupCount =
            SaveBackupCountComboBox.SelectedItem is int selectedCount
                ? selectedCount
                : _viewModel.Settings.SaveBackupCount;

        return
            new ServerConfigurationSnapshot(
                _config.SaveName,
                _config.AutosaveMinutes,
                _config.Password,
                _config.LogFile,
                _config.Steam,
                _config.TraceTick,
                _config.TracePublish,
                _config.TraceBandits,
                SaveBackupsEnabledCheckBox.IsChecked == true,
                backupCount);
    }


    private bool HasUnsavedChanges()
    {
        if (
            !_configurationLoaded ||
            _savedConfigurationSnapshot is null)
        {
            return false;
        }

        return
            _savedConfigurationSnapshot !=
            CreateConfigurationSnapshot();
    }


    /// <summary>
    /// Runs for both the Close button and the title-bar X. A successful Save
    /// refreshes the snapshot, so Save & Close exits without a second prompt.
    /// </summary>
    private void Window_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!HasUnsavedChanges())
            return;

        var result =
            MessageBox.Show(
                this,
                "You have unsaved Server Configuration changes.\n\n" +
                "Close without saving them?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            e.Cancel =
                true;
        }
    }


    private async void Save_Click(
        object sender,
        RoutedEventArgs e)
    {
        await SaveConfigurationAsync();
    }


    private async void SaveAndClose_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (await SaveConfigurationAsync())
        {
            Close();
        }
    }


    private void Close_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }


    /// <summary>
    /// Opens Bannerlord Coop's dedicated-server save directory:
    ///
    /// Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\Game Saves
    ///
    /// The base directory is derived from ServerConfigPath so it follows the
    /// same Documents-folder resolution already used by CoopConfigService.
    /// </summary>
    private void OpenSaveFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var dedicatedServerDirectory =
                Path.GetDirectoryName(
                    _configService.ServerConfigPath);

            if (string.IsNullOrWhiteSpace(dedicatedServerDirectory))
            {
                throw new InvalidOperationException(
                    "Could not determine the DedicatedServer directory.");
            }

            var saveFolder =
                Path.Combine(
                    dedicatedServerDirectory,
                    "Game Saves");

            if (!Directory.Exists(saveFolder))
            {
                MessageBox.Show(
                    this,
                    $"The save folder does not exist yet:\n\n{saveFolder}\n\n" +
                    "Start the server and create or save a game first, then try again.",
                    "Game Saves",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName =
                        saveFolder,
                    UseShellExecute =
                        true
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not open the save folder.\n\n{ex.Message}",
                "Game Saves",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    /// <summary>
    /// Opens BCS Tool's rotating-save backup directory:
    ///
    /// Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\
    /// Game Saves\BCS Backups
    ///
    /// The directory is intentionally not created by this button. It appears
    /// only after SaveBackupService successfully creates the first backup.
    /// </summary>
    private void OpenBackupFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var dedicatedServerDirectory =
                Path.GetDirectoryName(
                    _configService.ServerConfigPath);

            if (string.IsNullOrWhiteSpace(dedicatedServerDirectory))
            {
                throw new InvalidOperationException(
                    "Could not determine the DedicatedServer directory.");
            }

            var backupFolder =
                Path.Combine(
                    dedicatedServerDirectory,
                    "Game Saves",
                    "BCS Backups");

            if (!Directory.Exists(backupFolder))
            {
                MessageBox.Show(
                    this,
                    "The backup folder does not exist yet.\n\n" +
                    "BCS Tool creates the backup folder after it has made at least one save backup.",
                    "Save Backups",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName =
                        backupFolder,
                    UseShellExecute =
                        true
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not open the backup folder.\n\n{ex.Message}",
                "Save Backups",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    /// <summary>
    /// Opens the manual backup selector only when the managed Bannerlord
    /// server is completely stopped.
    /// </summary>
    private void LoadBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_viewModel.IsServerFullyStopped)
        {
            MessageBox.Show(
                this,
                "The server must be fully stopped before loading a backup save.\n\n" +
                "Stop the server and wait until Server state shows Stopped, then try again.",
                "Load Save Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var window =
            new BackupRestoreWindow(
                _viewModel)
            {
                Owner = this
            };

        window.ShowDialog();
    }


    private void ServerPasswordBox_PasswordChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (_updatingPasswordControls)
            return;

        _updatingPasswordControls = true;

        try
        {
            _config.Password =
                ServerPasswordBox.Password;

            VisibleServerPasswordTextBox.Text =
                ServerPasswordBox.Password;
        }
        finally
        {
            _updatingPasswordControls = false;
        }
    }


    private void VisibleServerPasswordTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_updatingPasswordControls)
            return;

        _updatingPasswordControls = true;

        try
        {
            _config.Password =
                VisibleServerPasswordTextBox.Text;

            ServerPasswordBox.Password =
                VisibleServerPasswordTextBox.Text;
        }
        finally
        {
            _updatingPasswordControls = false;
        }
    }


    private void ShowPasswordCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        var showPassword =
            ShowPasswordCheckBox.IsChecked == true;

        _updatingPasswordControls = true;

        try
        {
            if (showPassword)
            {
                VisibleServerPasswordTextBox.Text =
                    ServerPasswordBox.Password;
            }
            else
            {
                ServerPasswordBox.Password =
                    VisibleServerPasswordTextBox.Text;
            }

            ServerPasswordBox.Visibility =
                showPassword
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            VisibleServerPasswordTextBox.Visibility =
                showPassword
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        finally
        {
            _updatingPasswordControls = false;
        }
    }


    private void SetPasswordControls(
        string password)
    {
        _updatingPasswordControls = true;

        try
        {
            ServerPasswordBox.Password =
                password;

            VisibleServerPasswordTextBox.Text =
                password;
        }
        finally
        {
            _updatingPasswordControls = false;
        }
    }


    private sealed record ServerConfigurationSnapshot(
        string SaveName,
        int AutosaveMinutes,
        string Password,
        bool LogFile,
        bool Steam,
        bool TraceTick,
        bool TracePublish,
        bool TraceBandits,
        bool SaveBackupsEnabled,
        int SaveBackupCount);


    private static bool HasValidationErrors(
        DependencyObject root)
    {
        if (Validation.GetHasError(root))
            return true;

        for (
            var i = 0;
            i < VisualTreeHelper.GetChildrenCount(root);
            i++)
        {
            if (
                HasValidationErrors(
                    VisualTreeHelper.GetChild(
                        root,
                        i)))
            {
                return true;
            }
        }

        return false;
    }
}
