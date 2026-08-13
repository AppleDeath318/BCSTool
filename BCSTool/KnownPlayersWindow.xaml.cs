using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BCSTool.Infrastructure;
using BCSTool.Models;
using BCSTool.Services;
using BCSTool.ViewModels;

namespace BCSTool;

public partial class KnownPlayersWindow : Window
{
    private readonly MainViewModel _viewModel;
    private List<PlayerAccessEntry> _banlist = new();
    private List<PlayerAccessEntry> _whitelist = new();
    private ICollectionView? _playersView;
    private bool _busy;

    public ObservableCollection<KnownPlayerIdentityRow> Players { get; } = new();

    public KnownPlayersWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = this;

        _playersView =
            CollectionViewSource.GetDefaultView(Players);

        _playersView.Filter = MatchesSearch;
    }

    private async void Window_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        await LoadPlayersAsync();
    }

    private async void Refresh_Click(
        object sender,
        RoutedEventArgs e)
    {
        await LoadPlayersAsync();
    }

    private async Task LoadPlayersAsync()
    {
        if (_busy)
            return;

        SetBusy(true);
        StatusText.Text = "Loading player list...";

        try
        {
            var identitiesTask =
                _viewModel.GetPlayerIdentityCacheAsync();

            var banlistTask =
                _viewModel.GetBanlistAsync();

            var whitelistTask =
                _viewModel.GetWhitelistAsync();

            await Task.WhenAll(
                identitiesTask,
                banlistTask,
                whitelistTask);

            _banlist =
                banlistTask.Result
                    .Select(Clone)
                    .ToList();

            _whitelist =
                whitelistTask.Result
                    .Select(Clone)
                    .ToList();

            var bannedSteamIds =
                _banlist
                    .Select(entry => entry.SteamId)
                    .ToHashSet(StringComparer.Ordinal);

            var whitelistedSteamIds =
                _whitelist
                    .Select(entry => entry.SteamId)
                    .ToHashSet(StringComparer.Ordinal);

            var identities =
                identitiesTask.Result
                    .Where(
                        identity =>
                            PlayerAccessService.IsValidSteamId64(
                                identity.SteamId))
                    .GroupBy(
                        identity => identity.SteamId,
                        StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .OrderBy(
                        identity => identity.LastKnownCharacterName,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(
                        identity => identity.SteamId,
                        StringComparer.Ordinal)
                    .ToArray();

            Players.Clear();

            foreach (var identity in identities)
            {
                Players.Add(
                    new KnownPlayerIdentityRow(
                        identity.SteamId,
                        identity.LastKnownCharacterName?.Trim() ?? "",
                        identity.HeroId?.Trim() ?? "",
                        bannedSteamIds.Contains(identity.SteamId),
                        whitelistedSteamIds.Contains(identity.SteamId)));
            }

            _playersView.Refresh();
            UpdatePlayerCountText();

            StatusText.Text =
                Players.Count == 0
                    ? "No player identities have been learned yet."
                    : "Loaded from player-identities.json.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not load the player list.";

            MessageBox.Show(
                this,
                $"Could not load the player list.\n\n{ex.Message}",
                "Player List",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Ban_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            GetRow(sender) is not { } row ||
            row.IsBanned)
        {
            return;
        }

        var updatedBanlist =
            _banlist
                .Select(Clone)
                .ToList();

        updatedBanlist.Add(
            CreateAccessEntry(
                row,
                "Added from Player List"));

        if (await SaveListsAsync(
                updatedBanlist,
                _whitelist,
                $"Added {row.CharacterNameDisplay} to the banlist."))
        {
            row.IsBanned = true;
        }
    }

    private async void AddToWhitelist_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            GetRow(sender) is not { } row ||
            row.IsWhitelisted)
        {
            return;
        }

        var updatedWhitelist =
            _whitelist
                .Select(Clone)
                .ToList();

        updatedWhitelist.Add(
            CreateAccessEntry(
                row,
                "Added from Player List"));

        if (await SaveListsAsync(
                _banlist,
                updatedWhitelist,
                $"Added {row.CharacterNameDisplay} to the whitelist."))
        {
            row.IsWhitelisted = true;
        }
    }

    private async void Unban_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            GetRow(sender) is not { } row ||
            !row.IsBanned)
        {
            return;
        }

        var updatedBanlist =
            _banlist
                .Where(
                    entry =>
                        !string.Equals(
                            entry.SteamId,
                            row.SteamId,
                            StringComparison.Ordinal))
                .Select(Clone)
                .ToList();

        if (await SaveListsAsync(
                updatedBanlist,
                _whitelist,
                $"Removed {row.CharacterNameDisplay} from the banlist."))
        {
            row.IsBanned = false;
        }
    }

    private async void Unwhitelist_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            GetRow(sender) is not { } row ||
            !row.IsWhitelisted)
        {
            return;
        }

        var updatedWhitelist =
            _whitelist
                .Where(
                    entry =>
                        !string.Equals(
                            entry.SteamId,
                            row.SteamId,
                            StringComparison.Ordinal))
                .Select(Clone)
                .ToList();

        if (await SaveListsAsync(
                _banlist,
                updatedWhitelist,
                $"Removed {row.CharacterNameDisplay} from the whitelist."))
        {
            row.IsWhitelisted = false;
        }
    }

    private void SearchTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        _playersView?.Refresh();
        UpdatePlayerCountText();
    }

    private bool MatchesSearch(object item)
    {
        if (item is not KnownPlayerIdentityRow row)
            return false;

        var query =
            SearchTextBox.Text.Trim();

        return
            query.Length == 0 ||
            row.CharacterNameDisplay.Contains(
                query,
                StringComparison.OrdinalIgnoreCase) ||
            row.SteamId.Contains(
                query,
                StringComparison.OrdinalIgnoreCase);
    }

    private void UpdatePlayerCountText()
    {
        var visibleCount =
            _playersView?.Cast<object>().Count() ??
            Players.Count;

        PlayerCountText.Text =
            string.IsNullOrWhiteSpace(SearchTextBox.Text)
                ? $"{Players.Count} player(s)"
                : $"{visibleCount} of {Players.Count} player(s)";
    }

    private void PlayerGrid_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        var row =
            ItemsControl.ContainerFromElement(
                PlayerGrid,
                e.OriginalSource as DependencyObject)
            as DataGridRow;

        PlayerGrid.SelectedItem =
            row?.Item;
    }

    private void PlayerGrid_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        if (
            PlayerGrid.SelectedItem is not
                KnownPlayerIdentityRow row ||
            !PlayerAccessService.IsValidSteamId64(
                row.SteamId))
        {
            e.Handled = true;
        }
    }

    private void CopySelectedSteamId_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            PlayerGrid.SelectedItem is not
                KnownPlayerIdentityRow row ||
            !PlayerAccessService.IsValidSteamId64(
                row.SteamId))
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.SteamId);
            StatusText.Text =
                $"Copied Steam ID for {row.CharacterNameDisplay}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not copy the Steam ID.\n\n{ex.Message}",
                "Player List",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<bool> SaveListsAsync(
        IEnumerable<PlayerAccessEntry> banlist,
        IEnumerable<PlayerAccessEntry> whitelist,
        string successMessage)
    {
        if (_busy)
            return false;

        var savedBanlist =
            banlist
                .Select(Clone)
                .ToList();

        var savedWhitelist =
            whitelist
                .Select(Clone)
                .ToList();

        SetBusy(true);
        StatusText.Text = "Applying player access change...";

        try
        {
            await _viewModel.ApplyPlayerAccessListsAsync(
                savedBanlist,
                savedWhitelist);

            _banlist = savedBanlist;
            _whitelist = savedWhitelist;
            StatusText.Text = successMessage;
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not apply the player access change.";

            MessageBox.Show(
                this,
                $"Could not apply the player access change.\n\n{ex.Message}",
                "Player List",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PlayerGrid.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
    }

    private void Close_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }

    private static KnownPlayerIdentityRow? GetRow(object sender) =>
        (sender as FrameworkElement)?.DataContext as KnownPlayerIdentityRow;

    private static PlayerAccessEntry CreateAccessEntry(
        KnownPlayerIdentityRow row,
        string note) =>
        new()
        {
            SteamId = row.SteamId,
            LastKnownCharacterName = row.CharacterName,
            HeroId = row.HeroId,
            Note = note
        };

    private static PlayerAccessEntry Clone(PlayerAccessEntry entry) =>
        new()
        {
            SteamId = entry.SteamId,
            LastKnownCharacterName = entry.LastKnownCharacterName,
            HeroId = entry.HeroId,
            Note = entry.Note
        };
}

public sealed class KnownPlayerIdentityRow : BindableBase
{
    private bool _isBanned;
    private bool _isWhitelisted;

    public KnownPlayerIdentityRow(
        string steamId,
        string characterName,
        string heroId,
        bool isBanned,
        bool isWhitelisted)
    {
        SteamId = steamId;
        CharacterName = characterName;
        HeroId = heroId;
        _isBanned = isBanned;
        _isWhitelisted = isWhitelisted;
    }

    public string SteamId { get; }
    public string CharacterName { get; }
    public string CharacterNameDisplay =>
        string.IsNullOrWhiteSpace(CharacterName)
            ? "(unknown character)"
            : CharacterName;

    public string HeroId { get; }

    public bool IsBanned
    {
        get => _isBanned;
        set
        {
            if (!SetProperty(ref _isBanned, value))
                return;

            OnPropertyChanged(nameof(CanBan));
            OnPropertyChanged(nameof(CanUnban));
            OnPropertyChanged(nameof(BanButtonText));
        }
    }

    public bool IsWhitelisted
    {
        get => _isWhitelisted;
        set
        {
            if (!SetProperty(ref _isWhitelisted, value))
                return;

            OnPropertyChanged(nameof(CanAddToWhitelist));
            OnPropertyChanged(nameof(CanUnwhitelist));
            OnPropertyChanged(nameof(AddWhitelistButtonText));
        }
    }

    public bool CanBan => !IsBanned;
    public bool CanUnban => IsBanned;
    public bool CanAddToWhitelist => !IsWhitelisted;
    public bool CanUnwhitelist => IsWhitelisted;

    public string BanButtonText =>
        IsBanned
            ? "Banned"
            : "Ban";

    public string AddWhitelistButtonText =>
        IsWhitelisted
            ? "Whitelisted"
            : "Add to Whitelist";
}
