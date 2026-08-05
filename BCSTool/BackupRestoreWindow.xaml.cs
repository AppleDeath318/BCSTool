using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using BCSTool.Services;
using BCSTool.ViewModels;

namespace BCSTool;

/// <summary>
/// Manual selector for restoring one complete .sav/.json backup generation.
/// </summary>
public partial class BackupRestoreWindow : Window
{
    private readonly MainViewModel _viewModel;

    private readonly ObservableCollection<SaveBackupService.SaveBackupInfo> _backups =
        new();


    public BackupRestoreWindow(
        MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel =
            viewModel;

        BackupListView.ItemsSource =
            _backups;

        Loaded +=
            BackupRestoreWindow_Loaded;
    }


    private async void BackupRestoreWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var backups =
                await _viewModel.GetSaveBackupsAsync();

            _backups.Clear();

            foreach (var backup in backups)
            {
                _backups.Add(
                    backup);
            }

            EmptyBackupsText.Visibility =
                _backups.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not load the save-backup list.\n\n{ex.Message}",
                "Load Save Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    private void BackupListView_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ApplyButton.IsEnabled =
            BackupListView.SelectedItem is SaveBackupService.SaveBackupInfo;
    }


    private async void Apply_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            BackupListView.SelectedItem
                is not SaveBackupService.SaveBackupInfo selectedBackup)
        {
            return;
        }

        // Re-check immediately before the destructive operation.
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

        var confirmation =
            MessageBox.Show(
                this,
                $"Replace the current save with {selectedBackup.Name}?\n\n" +
                $"Backup date: {selectedBackup.DateModified:G}\n\n" +
                "Both the .sav and companion .json files will be replaced.",
                "Load Save Backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
            return;

        ApplyButton.IsEnabled =
            false;

        try
        {
            var result =
                await _viewModel.RestoreSaveBackupAsync(
                    selectedBackup.Generation);

            MessageBox.Show(
                this,
                $"Backup loaded successfully.\n\n{result.BackupName}\n\n" +
                "Both current save files were replaced. You can now start the server.",
                "Load Save Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult =
                true;

            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not load the selected backup.\n\n{ex.Message}",
                "Load Save Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            ApplyButton.IsEnabled =
                BackupListView.SelectedItem is SaveBackupService.SaveBackupInfo;
        }
    }


    private void Close_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }
}
