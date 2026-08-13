using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BCSTool.Models;
using BCSTool.Services;
using BCSTool.ViewModels;

namespace BCSTool;

/// <summary>
/// Thin code-behind for UI-only behavior.
///
/// The command field is now a normal local WPF TextBox. Only Enter submits a
/// complete command to the server; editing, selection, caret movement, history,
/// and Tab completion no longer mirror Bannerlord's native terminal cursor.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CoopConfigService _coopConfigService;

    private readonly LiveConsoleScroller _serverConsoleScroller;
    private readonly LiveConsoleScroller _bcsToolConsoleScroller;

    private bool _allowClose;

    private string? _autocompletePrefix;
    private string _autocompleteSuffix = "";
    private IReadOnlyList<string> _autocompleteMatches =
        Array.Empty<string>();
    private int _autocompleteIndex = -1;
    private bool _applyingAutocomplete;
    private bool _suppressSuggestionRefresh;
    private bool _commandSuggestionUiReady;
    private bool _playerAccessModeUiReady;

    public MainWindow(
        MainViewModel viewModel,
        CoopConfigService coopConfigService)
    {
        InitializeComponent();
        _commandSuggestionUiReady = true;

        _viewModel = viewModel;
        _coopConfigService = coopConfigService;
        DataContext = _viewModel;

        _serverConsoleScroller =
            new LiveConsoleScroller(
                ServerConsoleList,
                _viewModel.ServerConsoleLines,
                Dispatcher);

        _bcsToolConsoleScroller =
            new LiveConsoleScroller(
                BcsToolConsoleList,
                _viewModel.ConsoleLines,
                Dispatcher);
    }

    private async void Window_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        _serverConsoleScroller.Initialize();
        _bcsToolConsoleScroller.Initialize();

        await _viewModel.InitializeAsync();

        // Initialization replaces the default Settings instance with the
        // persisted one. Enable user-driven mode changes only after that load
        // is complete so the ComboBox cannot write the temporary default.
        PlayerAccessModeComboBox.SelectedItem =
            _viewModel.PlayerAccessMode;

        _playerAccessModeUiReady = true;
    }

    private async void PlayerAccessMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (
            !_playerAccessModeUiReady ||
            PlayerAccessModeComboBox.SelectedItem is not
                PlayerAccessMode selectedMode ||
            selectedMode == _viewModel.PlayerAccessMode)
        {
            return;
        }

        PlayerAccessModeComboBox.IsEnabled = false;

        try
        {
            await _viewModel.UpdatePlayerAccessModeAsync(
                selectedMode);
        }
        catch (Exception ex)
        {
            PlayerAccessModeComboBox.SelectedItem =
                _viewModel.PlayerAccessMode;

            MessageBox.Show(
                this,
                $"Could not apply the player access mode.\n\n{ex.Message}",
                "Access Control",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            PlayerAccessModeComboBox.IsEnabled = true;
        }
    }

    private void PlayerInformation_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        var item =
            ItemsControl.ContainerFromElement(
                PlayerInformationList,
                e.OriginalSource as DependencyObject)
            as ListBoxItem;

        PlayerInformationList.SelectedItem =
            item?.DataContext;
    }

    private void PlayerInformation_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        if (
            PlayerInformationList.SelectedItem is not
                PlayerInformationLine line ||
            (!line.IsPlayerLine &&
             !line.CanCopySteamId))
        {
            e.Handled = true;
            return;
        }

        var playerActionVisibility =
            line.IsPlayerLine
                ? Visibility.Visible
                : Visibility.Collapsed;

        KickPlayerMenuItem.Visibility =
            playerActionVisibility;

        BanPlayerMenuItem.Visibility =
            playerActionVisibility;

        KickPlayerMenuItem.IsEnabled =
            _viewModel.CanKickPlayer(line);

        BanPlayerMenuItem.IsEnabled =
            _viewModel.CanBanPlayer(line);

        BanPlayerMenuItem.ToolTip =
            BanPlayerMenuItem.IsEnabled
                ? null
                : "SteamID64 has not been resolved for this player yet.";

        CopyPlayerSteamIdMenuItem.Visibility =
            line.CanCopySteamId
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void KickPlayer_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            PlayerInformationList.SelectedItem is not
                PlayerInformationLine line)
        {
            return;
        }

        try
        {
            await _viewModel.KickPlayerAsync(line);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not kick the player.\n\n{ex.Message}",
                "Access Control",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void BanPlayer_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            PlayerInformationList.SelectedItem is not
                PlayerInformationLine line)
        {
            return;
        }

        try
        {
            await _viewModel.BanPlayerAsync(line);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not add the player to the banlist.\n\n{ex.Message}",
                "Access Control",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CopyPlayerSteamId_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            PlayerInformationList.SelectedItem is not
                PlayerInformationLine line ||
            !line.CanCopySteamId ||
            !PlayerAccessService.IsValidSteamId64(line.SteamId))
        {
            return;
        }

        try
        {
            Clipboard.SetText(line.SteamId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not copy the SteamID64.\n\n{ex.Message}",
                "Access Control",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Keeps Enter/Tab/Up/Down as command-console conveniences while allowing
    /// every normal WPF editing gesture (mouse caret, Ctrl+A/C/X/V, Home/End,
    /// selections, etc.) to work without interception.
    /// </summary>
    private void CommandInput_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ResetAutocomplete();

            if (
                _viewModel.SendCommandCommand.CanExecute(
                    null))
            {
                _viewModel.SendCommandCommand.Execute(
                    null);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            var reverse =
                (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // Keep focus in the command box even when there is no match.
            e.Handled = true;

            ApplyAutocomplete(
                reverse);

            return;
        }

        if (e.Key == Key.Escape)
        {
            ResetAutocomplete();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            ResetAutocomplete();

            _suppressSuggestionRefresh = true;

            try
            {
                _viewModel.NavigateCommandHistory(-1);
            }
            finally
            {
                _suppressSuggestionRefresh = false;
            }

            MoveCommandCaretToEnd();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            ResetAutocomplete();

            _suppressSuggestionRefresh = true;

            try
            {
                _viewModel.NavigateCommandHistory(1);
            }
            finally
            {
                _suppressSuggestionRefresh = false;
            }

            MoveCommandCaretToEnd();
            e.Handled = true;
        }
    }

    private void CommandInput_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (
            !_commandSuggestionUiReady ||
            _applyingAutocomplete)
        {
            return;
        }

        ResetAutocompleteCycle();

        if (_suppressSuggestionRefresh)
        {
            HideAutocompleteSuggestions();
            return;
        }

        RefreshAutocompleteSuggestions();
    }


    private void CommandInput_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (
                    !CommandInput.IsKeyboardFocusWithin &&
                    !CommandSuggestionsList.IsKeyboardFocusWithin)
                {
                    HideAutocompleteSuggestions();
                }
            }));
    }

    private void Window_Deactivated(
        object? sender,
        EventArgs e)
    {
        if (_commandSuggestionUiReady)
        {
            HideAutocompleteSuggestions();
        }
    }

    /// <summary>
    /// Completes only the first command token. Arguments after that token are
    /// preserved. Repeated Tab cycles forward; Shift+Tab cycles backward.
    /// </summary>
    private bool ApplyAutocomplete(
        bool reverse)
    {
        if (_autocompleteMatches.Count == 0)
        {
            if (
                !TryGetAutocompleteContext(
                    out var prefix,
                    out var suffix))
            {
                HideAutocompleteSuggestions();
                return false;
            }

            _autocompletePrefix =
                prefix;

            _autocompleteSuffix =
                suffix;

            _autocompleteMatches =
                _viewModel.GetCommandCompletions(
                    _autocompletePrefix);

            if (_autocompleteMatches.Count == 0)
            {
                ResetAutocomplete();
                return false;
            }

            _autocompleteIndex =
                reverse
                    ? _autocompleteMatches.Count - 1
                    : 0;
        }
        else
        {
            _autocompleteIndex =
                reverse
                    ? (_autocompleteIndex - 1 + _autocompleteMatches.Count) %
                        _autocompleteMatches.Count
                    : (_autocompleteIndex + 1) %
                        _autocompleteMatches.Count;
        }

        var completion =
            _autocompleteMatches[
                _autocompleteIndex];

        ApplyCompletionText(
            completion,
            _autocompleteSuffix,
            keepSuggestionsOpen: true);

        CommandSuggestionsList.ItemsSource =
            _autocompleteMatches;

        CommandSuggestionsList.SelectedIndex =
            _autocompleteIndex;

        CommandSuggestionsList.ScrollIntoView(
            completion);

        ShowAutocompleteSuggestions(
            _autocompleteMatches);

        return true;
    }

    /// <summary>
    /// Shows the same potential command-name matches that Tab can cycle through.
    /// The list is passive while typing; clicking one match completes it.
    /// </summary>
    private void RefreshAutocompleteSuggestions()
    {
        if (
            !TryGetAutocompleteContext(
                out var prefix,
                out _))
        {
            HideAutocompleteSuggestions();
            return;
        }

        var matches =
            _viewModel.GetCommandCompletions(
                prefix);

        if (matches.Count == 0)
        {
            HideAutocompleteSuggestions();
            return;
        }

        CommandSuggestionsList.ItemsSource =
            matches;

        CommandSuggestionsList.SelectedIndex =
            -1;

        ShowAutocompleteSuggestions(
            matches);
    }

    private bool TryGetAutocompleteContext(
        out string prefix,
        out string suffix)
    {
        prefix = "";
        suffix = "";

        var text =
            CommandInput.Text ?? "";

        var caret =
            Math.Clamp(
                CommandInput.CaretIndex,
                0,
                text.Length);

        if (caret <= 0)
            return false;

        // Command-name completion applies only to the first token.
        for (
            var index = 0;
            index < caret;
            index++)
        {
            if (char.IsWhiteSpace(text[index]))
                return false;
        }

        var tokenEnd =
            caret;

        while (
            tokenEnd < text.Length &&
            !char.IsWhiteSpace(text[tokenEnd]))
        {
            tokenEnd++;
        }

        prefix =
            text[..caret];

        suffix =
            text[tokenEnd..];

        return prefix.Length > 0;
    }

    private void CommandSuggestionsList_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (
            CommandSuggestionsList.SelectedItem is not string completion ||
            !TryGetAutocompleteContext(
                out _,
                out var suffix))
        {
            return;
        }

        ResetAutocompleteCycle();

        ApplyCompletionText(
            completion,
            suffix,
            keepSuggestionsOpen: false);

        CommandInput.Focus();
        e.Handled = true;
    }

    private void ApplyCompletionText(
        string completion,
        string suffix,
        bool keepSuggestionsOpen)
    {
        _applyingAutocomplete = true;

        try
        {
            CommandInput.Text =
                completion +
                suffix;

            CommandInput.CaretIndex =
                completion.Length;

            CommandInput.SelectionLength =
                0;
        }
        finally
        {
            _applyingAutocomplete = false;
        }

        if (!keepSuggestionsOpen)
        {
            HideAutocompleteSuggestions();
        }
    }

    private void ShowAutocompleteSuggestions(
        IReadOnlyList<string> matches)
    {
        CommandSuggestionsStatus.Text =
            matches.Count == 1
                ? "1 matching command · Tab to complete"
                : $"{matches.Count} matching commands · Tab / Shift+Tab to cycle";

        CommandSuggestionsBorder.Width =
            Math.Max(
                320,
                CommandInput.ActualWidth);

        CommandSuggestionsPopup.IsOpen =
            true;
    }

    private void HideAutocompleteSuggestions()
    {
        CommandSuggestionsPopup.IsOpen =
            false;

        CommandSuggestionsList.SelectedIndex =
            -1;
    }

    private void ResetAutocompleteCycle()
    {
        _autocompletePrefix = null;
        _autocompleteSuffix = "";
        _autocompleteMatches =
            Array.Empty<string>();
        _autocompleteIndex = -1;
    }

    private void ResetAutocomplete()
    {
        ResetAutocompleteCycle();
        HideAutocompleteSuggestions();
    }

    private void MoveCommandCaretToEnd()
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                CommandInput.CaretIndex =
                    CommandInput.Text.Length;

                CommandInput.SelectionLength =
                    0;
            }));
    }

    /// <summary>
    /// Opens the dedicated server configuration editor.
    /// </summary>
    private void ServerConfiguration_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            !EnsureConfigurationFileExists(
                _coopConfigService.ServerConfigPath,
                "Server Configuration"))
        {
            return;
        }

        var window =
            new ServerConfigurationWindow(
                _coopConfigService,
                _viewModel)
            {
                Owner = this
            };

        window.ShowDialog();
    }

    private void PlayerAccess_Click(
        object sender,
        RoutedEventArgs e)
    {
        var window =
            new PlayerAccessWindow(_viewModel)
            {
                Owner = this
            };

        window.ShowDialog();
    }


    /// <summary>
    /// Opens the Bannerlord Coop mod configuration editor.
    /// </summary>
    private void ModConfiguration_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            !EnsureConfigurationFileExists(
                _coopConfigService.ModConfigPath,
                "Mod Configuration"))
        {
            return;
        }

        var window =
            new ModConfigurationWindow(
                _coopConfigService)
            {
                Owner = this
            };

        window.ShowDialog();
    }

    private bool EnsureConfigurationFileExists(
        string configurationPath,
        string configurationName)
    {
        if (File.Exists(configurationPath))
            return true;

        if (_viewModel.IsServerRunning)
        {
            MessageBox.Show(
                this,
                $"{configurationName} has not been generated yet.\n\n" +
                $"Expected file:\n{configurationPath}\n\n" +
                "Bannerlord Coop generates this configuration file when the " +
                "server is started for the first time.\n\n" +
                "The server is already running. Allow it to finish its first " +
                "startup, then open this configuration again.",
                $"{configurationName} Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return false;
        }

        var result =
            MessageBox.Show(
                this,
                $"{configurationName} has not been generated yet.\n\n" +
                $"Expected file:\n{configurationPath}\n\n" +
                "Bannerlord Coop generates this configuration file when the " +
                "server is started for the first time.\n\n" +
                "Would you like BCS Tool to start the server now?\n\n" +
                "After the server finishes its first startup, open this " +
                "configuration again.",
                $"{configurationName} Not Found",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.Yes);

        if (result != MessageBoxResult.Yes)
            return false;

        if (
            _viewModel.StartCommand.CanExecute(
                null))
        {
            _viewModel.StartCommand.Execute(
                null);

            return false;
        }

        MessageBox.Show(
            this,
            "BCS Tool cannot start the server right now. Check the server " +
            "executable path and current server status, then press Start.",
            "Unable to Start Server",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        return false;
    }

    private async void Window_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (_allowClose)
            return;

        if (!_viewModel.IsServerRunning)
        {
            _allowClose = true;
            return;
        }

        e.Cancel = true;

        var result = MessageBox.Show(
            "BCS Tool currently owns the server process.\n\n" +
            "Closing BCS Tool will save and stop the server gracefully.\n\n" +
            "Continue?",
            "Exit BCS Tool",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        IsEnabled = false;

        var stopped =
            await _viewModel.PrepareForApplicationExitAsync();

        if (!stopped)
        {
            var force = MessageBox.Show(
                "The server did not stop cleanly.\n\n" +
                "Force-clean the managed server process tree and exit?",
                "Server Still Running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (force == MessageBoxResult.Yes)
            {
                _viewModel.ForceCleanupManagedServerTree();
            }
            else
            {
                IsEnabled = true;
                return;
            }
        }

        _allowClose = true;

        // Closing is an async event handler. If the shutdown task happens to
        // complete synchronously, calling Close() here would re-enter WPF's
        // active close operation and throw InvalidOperationException. Queue the
        // final close so it runs only after the current Closing event has fully
        // unwound.
        _ = Dispatcher.BeginInvoke(
            new Action(Close));
    }

    /// <summary>
    /// Auto-follow helper shared by both live console ListBoxes.
    /// </summary>
    private sealed class LiveConsoleScroller
    {
        private readonly ListBox _listBox;
        private readonly INotifyCollectionChanged _collection;
        private readonly Dispatcher _dispatcher;

        private ScrollViewer? _scrollViewer;
        private bool _autoFollow = true;
        private bool _programmaticScroll;
        private bool _scrollPending;

        public LiveConsoleScroller(
            ListBox listBox,
            INotifyCollectionChanged collection,
            Dispatcher dispatcher)
        {
            _listBox = listBox;
            _collection = collection;
            _dispatcher = dispatcher;

            _collection.CollectionChanged +=
                Collection_CollectionChanged;
        }

        public void Initialize()
        {
            if (_scrollViewer is not null)
                return;

            _listBox.ApplyTemplate();
            _listBox.UpdateLayout();

            _scrollViewer =
                FindVisualChild<ScrollViewer>(
                    _listBox);

            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged +=
                    ScrollViewer_ScrollChanged;
            }

            ScrollToEnd();
        }

        private void ScrollViewer_ScrollChanged(
            object sender,
            ScrollChangedEventArgs e)
        {
            if (_programmaticScroll)
                return;

            if (Math.Abs(e.VerticalChange) < 0.001)
                return;

            _autoFollow =
                IsAtBottom();
        }

        private bool IsAtBottom()
        {
            if (_scrollViewer is null)
                return true;

            const double tolerance = 0.5;

            return
                _scrollViewer.ScrollableHeight <= tolerance ||
                _scrollViewer.VerticalOffset >=
                    _scrollViewer.ScrollableHeight - tolerance;
        }

        private void Collection_CollectionChanged(
            object? sender,
            NotifyCollectionChangedEventArgs e)
        {
            if (
                !_autoFollow ||
                _scrollPending)
            {
                return;
            }

            _scrollPending = true;

            _ = _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    _scrollPending = false;

                    if (_autoFollow)
                        ScrollToEnd();
                }));
        }

        private void ScrollToEnd()
        {
            if (_listBox.Items.Count == 0)
                return;

            if (_scrollViewer is null)
            {
                _listBox.ScrollIntoView(
                    _listBox.Items[
                        _listBox.Items.Count - 1]);

                return;
            }

            _programmaticScroll = true;

            try
            {
                _scrollViewer.ScrollToEnd();
            }
            finally
            {
                _programmaticScroll = false;
            }
        }

        private static T? FindVisualChild<T>(
            DependencyObject parent)
            where T : DependencyObject
        {
            var childCount =
                VisualTreeHelper.GetChildrenCount(
                    parent);

            for (
                var index = 0;
                index < childCount;
                index++)
            {
                var child =
                    VisualTreeHelper.GetChild(
                        parent,
                        index);

                if (child is T match)
                    return match;

                var nested =
                    FindVisualChild<T>(
                        child);

                if (nested is not null)
                    return nested;
            }

            return null;
        }
    }
}
