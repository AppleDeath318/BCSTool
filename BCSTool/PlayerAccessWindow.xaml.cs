using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BCSTool.Models;
using BCSTool.Services;
using BCSTool.ViewModels;

namespace BCSTool;

public partial class PlayerAccessWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private bool _loaded;
    private string _savedSnapshot = "";
    private IReadOnlyDictionary<string, PlayerIdentityEntry> _identityBySteamId =
        new Dictionary<string, PlayerIdentityEntry>(StringComparer.Ordinal);

    public ObservableCollection<PlayerAccessEntry> BanlistEntries { get; } = new();
    public ObservableCollection<PlayerAccessEntry> WhitelistEntries { get; } = new();

    public PlayerAccessWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = this;

        ListTabControl.SelectedIndex =
            _viewModel.PlayerAccessMode == PlayerAccessMode.Whitelist
                ? 1
                : 0;

        StoragePathText.Text = _viewModel.PlayerAccessDataDirectory;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var identities = await _viewModel.GetPlayerIdentityCacheAsync();
        _identityBySteamId =
            identities
                .GroupBy(identity => identity.SteamId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last(),
                    StringComparer.Ordinal);

        Populate(
            BanlistEntries,
            await _viewModel.GetBanlistAsync());

        Populate(
            WhitelistEntries,
            await _viewModel.GetWhitelistAsync());

        _loaded = true;
        _savedSnapshot = CaptureSnapshot();
    }

    private void Populate(
        ObservableCollection<PlayerAccessEntry> destination,
        IEnumerable<PlayerAccessEntry> source)
    {
        destination.Clear();

        foreach (var entry in source)
        {
            var copy = Clone(entry);

            if (_identityBySteamId.TryGetValue(copy.SteamId, out var identity))
            {
                if (!string.IsNullOrWhiteSpace(identity.LastKnownCharacterName))
                    copy.LastKnownCharacterName = identity.LastKnownCharacterName;

                if (!string.IsNullOrWhiteSpace(identity.HeroId))
                    copy.HeroId = identity.HeroId;
            }

            destination.Add(copy);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var steamId = SteamIdTextBox.Text.Trim();

        if (!PlayerAccessService.IsValidSteamId64(steamId))
        {
            MessageBox.Show(
                this,
                "Enter a valid 17-digit SteamID64.",
                "Player Access",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var list = CurrentList;
        var existing =
            list.FirstOrDefault(
                entry =>
                    string.Equals(
                        entry.SteamId,
                        steamId,
                        StringComparison.Ordinal));

        var note = NoteTextBox.Text.Trim();

        if (existing is not null)
        {
            existing.Note = note;
            RefreshCurrentGrid();
        }
        else
        {
            _identityBySteamId.TryGetValue(steamId, out var identity);

            list.Add(
                new PlayerAccessEntry
                {
                    SteamId = steamId,
                    LastKnownCharacterName = identity?.LastKnownCharacterName ?? "",
                    HeroId = identity?.HeroId ?? "",
                    Note = note
                });
        }

        SteamIdTextBox.Clear();
        NoteTextBox.Clear();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = CurrentGrid.SelectedItem as PlayerAccessEntry;

        if (selected is null)
            return;

        CurrentList.Remove(selected);
    }

    private void AccessGrid_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        var row =
            ItemsControl.ContainerFromElement(
                grid,
                e.OriginalSource as DependencyObject)
            as DataGridRow;

        grid.SelectedItem =
            row?.Item;
    }

    private void AccessGrid_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        if (
            sender is not DataGrid grid ||
            grid.SelectedItem is not PlayerAccessEntry entry ||
            !PlayerAccessService.IsValidSteamId64(entry.SteamId))
        {
            e.Handled = true;
        }
    }

    private void CopyBanlistSteamId_Click(
        object sender,
        RoutedEventArgs e)
    {
        CopySelectedSteamId(BanlistGrid);
    }

    private void CopyWhitelistSteamId_Click(
        object sender,
        RoutedEventArgs e)
    {
        CopySelectedSteamId(WhitelistGrid);
    }

    private void CopySelectedSteamId(DataGrid grid)
    {
        if (
            grid.SelectedItem is not PlayerAccessEntry entry ||
            !PlayerAccessService.IsValidSteamId64(entry.SteamId))
        {
            return;
        }

        try
        {
            Clipboard.SetText(entry.SteamId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not copy the SteamID64.\n\n{ex.Message}",
                "Player Access",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAsync();
    }

    private async void ApplyAndClose_Click(object sender, RoutedEventArgs e)
    {
        if (!await ApplyAsync())
            return;

        _allowClose = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = _viewModel.PlayerAccessDataDirectory,
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not open the player access folder.\n\n{ex.Message}",
                "Player Access",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<bool> ApplyAsync()
    {
        try
        {
            await _viewModel.ApplyPlayerAccessListsAsync(
                BanlistEntries,
                WhitelistEntries);

            _savedSnapshot = CaptureSnapshot();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not apply player access changes.\n\n{ex.Message}",
                "Player Access",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_loaded || CaptureSnapshot() == _savedSnapshot)
            return;

        var result =
            MessageBox.Show(
                this,
                "You have unsaved Player Access changes.\n\nClose without saving?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private ObservableCollection<PlayerAccessEntry> CurrentList =>
        ListTabControl.SelectedIndex == 1
            ? WhitelistEntries
            : BanlistEntries;

    private DataGrid CurrentGrid =>
        ListTabControl.SelectedIndex == 1
            ? WhitelistGrid
            : BanlistGrid;

    private void RefreshCurrentGrid()
    {
        CurrentGrid.Items.Refresh();
    }

    private string CaptureSnapshot()
    {
        return JsonSerializer.Serialize(
            new
            {
                Banlist = BanlistEntries.Select(Clone).ToArray(),
                Whitelist = WhitelistEntries.Select(Clone).ToArray()
            });
    }

    private static PlayerAccessEntry Clone(PlayerAccessEntry entry) =>
        new()
        {
            SteamId = entry.SteamId,
            LastKnownCharacterName = entry.LastKnownCharacterName,
            HeroId = entry.HeroId,
            Note = entry.Note
        };
}
