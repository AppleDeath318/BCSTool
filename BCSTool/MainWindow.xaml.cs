using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BCSTool.Services;
using BCSTool.ViewModels;

namespace BCSTool;

/// <summary>
/// Thin code-behind for UI-only behavior.
///
/// Business logic belongs in MainViewModel. This file only handles things
/// that are naturally Window/control-specific:
///
/// - Initialize the ViewModel after the window loads.
/// - Treat Enter in the command box as Send.
/// - Ask for confirmation before closing while the server is running.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CoopConfigService _coopConfigService =
        new();
    private readonly DispatcherTimer _terminalResizeTimer;

    private bool _allowClose;

    private int _lastTerminalColumns = -1;
    private int _lastTerminalRows = -1;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        // Window resizing can generate dozens of SizeChanged events per
        // second. A short debounce avoids flooding ResizePseudoConsole while
        // still making the terminal feel immediately responsive.
        _terminalResizeTimer =
            new DispatcherTimer
            {
                Interval =
                    TimeSpan.FromMilliseconds(
                        120)
            };

        _terminalResizeTimer.Tick +=
            TerminalResizeTimer_Tick;

        TerminalDisplay.SizeChanged +=
            TerminalDisplay_SizeChanged;

        // Keep the newest BCS Tool information visible.
        _viewModel.ConsoleLines.CollectionChanged +=
            BcsToolConsoleLines_CollectionChanged;

        // CommandText and CommandCaretIndex are driven by Bannerlord's native
        // ConPTY prompt rather than WPF's local line editor.
        _viewModel.PropertyChanged +=
            ViewModel_PropertyChanged;
    }

    // WPF calls this after the window is fully created.
    // InitializeAsync loads settings and deliberately leaves the server stopped.
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded occurs after WPF has performed its first layout pass, so the
        // terminal now has meaningful ActualWidth / ActualHeight values.
        //
        // Measure before InitializeAsync so the first manually-started ConPTY
        // instance already has the correct viewport dimensions.
        ResizeTerminalToViewport();

        await _viewModel.InitializeAsync();

        // Settings/status changes during initialization can slightly alter the
        // remaining console height, so measure once more afterward.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(
                ResizeTerminalToViewport));
    }


    /// <summary>
    /// Restarts the short resize debounce whenever WPF changes the visible
    /// terminal viewport.
    /// </summary>
    private void TerminalDisplay_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        _terminalResizeTimer.Stop();
        _terminalResizeTimer.Start();
    }


    private void TerminalResizeTimer_Tick(
        object? sender,
        EventArgs e)
    {
        _terminalResizeTimer.Stop();

        ResizeTerminalToViewport();
    }


    /// <summary>
    /// Converts the terminal's visible WPF pixel area into a monospace
    /// character grid and forwards that grid size to ConPTY.
    ///
    /// Because Consolas is monospace, measuring one "M" gives us a reliable
    /// cell width. FormattedText also respects the current Windows DPI scale.
    ///
    /// A small safety margin prevents fractional-pixel rounding from creating
    /// one extra column/row that would otherwise cause an internal scrollbar.
    /// </summary>
    private void ResizeTerminalToViewport()
    {
        if (
            TerminalDisplay.ActualWidth <= 1 ||
            TerminalDisplay.ActualHeight <= 1)
        {
            return;
        }

        var dpi =
            VisualTreeHelper.GetDpi(
                TerminalDisplay);

        var typeface =
            new Typeface(
                TerminalDisplay.FontFamily,
                TerminalDisplay.FontStyle,
                TerminalDisplay.FontWeight,
                TerminalDisplay.FontStretch);

        var sample =
            new FormattedText(
                "M",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                TerminalDisplay.FontSize,
                Brushes.Black,
                dpi.PixelsPerDip);

        var cellWidth =
            Math.Max(
                1.0,
                sample.WidthIncludingTrailingWhitespace);

        var cellHeight =
            Math.Max(
                1.0,
                sample.Height);

        // Account for TextBox padding/border plus a few pixels of tolerance
        // for WPF's device-pixel rounding.
        var usableWidth =
            Math.Max(
                1.0,
                TerminalDisplay.ActualWidth
                - TerminalDisplay.Padding.Left
                - TerminalDisplay.Padding.Right
                - TerminalDisplay.BorderThickness.Left
                - TerminalDisplay.BorderThickness.Right
                - 8.0);

        var usableHeight =
            Math.Max(
                1.0,
                TerminalDisplay.ActualHeight
                - TerminalDisplay.Padding.Top
                - TerminalDisplay.Padding.Bottom
                - TerminalDisplay.BorderThickness.Top
                - TerminalDisplay.BorderThickness.Bottom
                - 8.0);

        // Subtract one final cell in each direction as a conservative guard
        // against font/rendering rounding. The server receives exactly the
        // number of rows/columns that can be shown without internal scrolling.
        var columns =
            Math.Max(
                1,
                (int)Math.Floor(
                    usableWidth / cellWidth) - 1);

        var rows =
            Math.Max(
                1,
                (int)Math.Floor(
                    usableHeight / cellHeight) - 1);

        if (
            columns == _lastTerminalColumns &&
            rows == _lastTerminalRows)
        {
            return;
        }

        _lastTerminalColumns =
            columns;

        _lastTerminalRows =
            rows;

        _viewModel.ResizeTerminal(
            columns,
            rows);
    }

    /// <summary>
    /// Auto-scrolls the BCS Tool Console to its newest message.
    ///
    /// ScrollIntoView is deferred until after WPF finishes processing the
    /// ObservableCollection notification. This avoids the ItemsControl
    /// consistency exception that can occur with synchronous auto-scroll.
    /// </summary>
    private void BcsToolConsoleLines_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (BcsToolConsoleList.Items.Count == 0)
                    return;

                var lastItem =
                    BcsToolConsoleList.Items[
                        BcsToolConsoleList.Items.Count - 1];

                BcsToolConsoleList.ScrollIntoView(
                    lastItem);
            }));
    }


    /// <summary>
    /// Opens the dedicated server configuration editor.
    ///
    /// Bannerlord Coop creates server-config.json on first server startup.
    /// If the file does not exist yet, explain that requirement and offer to
    /// start the server instead of opening an empty/broken editor.
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
                _coopConfigService)
            {
                Owner = this
            };

        window.ShowDialog();
    }


    /// <summary>
    /// Opens the Bannerlord Coop mod configuration editor.
    ///
    /// Bannerlord Coop creates mod-config.json on first server startup.
    /// If the file does not exist yet, explain that requirement and offer to
    /// start the server instead of opening an empty/broken editor.
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


    /// <summary>
    /// Ensures a Bannerlord Coop generated configuration file exists before
    /// opening its editor.
    ///
    /// If the file is missing:
    /// - while the server is already running, tell the user to wait for first
    ///   initialization to finish and try the editor again;
    /// - while the server is stopped, offer to start it now so Bannerlord Coop
    ///   can generate the file.
    ///
    /// Starting from this prompt uses the same StartCommand as the main Start
    /// button, so all normal validation and duplicate-process safeguards still
    /// apply.
    /// </summary>
    private bool EnsureConfigurationFileExists(
        string configurationPath,
        string configurationName)
    {
        if (
            File.Exists(
                configurationPath))
        {
            return true;
        }

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


    /// <summary>
    /// Normal printable text is sent directly to ConPTY.
    ///
    /// The WPF TextBox does not edit itself locally. It is a mirror of the
    /// native Bannerlord prompt, which comes back through terminal snapshots.
    /// </summary>
    private async void CommandInput_PreviewTextInput(
        object sender,
        TextCompositionEventArgs e)
    {
        if (
            string.IsNullOrEmpty(e.Text) ||
            !_viewModel.IsServerRunning)
        {
            return;
        }

        e.Handled = true;

        await _viewModel.SendTerminalInputAsync(
            e.Text);
    }


    /// <summary>
    /// Proxies terminal editing/autocomplete/history keys to Bannerlord.
    ///
    /// VT sequences used:
    ///
    /// Tab         HT
    /// Shift+Tab   CSI Z
    /// Up          CSI A
    /// Down        CSI B
    /// Right       CSI C
    /// Left        CSI D
    /// Home        CSI H
    /// End         CSI F
    /// Delete      CSI 3 ~
    /// Backspace   DEL
    /// Escape      ESC
    ///
    /// Enter uses the same ViewModel command as the Send button and submits
    /// only CR because the native terminal already owns the command text.
    /// </summary>
    private async void CommandInput_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (!_viewModel.IsServerRunning)
            return;

        // Ctrl+V: emulate terminal paste instead of allowing WPF to modify its
        // local mirror independently from the native prompt.
        if (
            e.Key == Key.V &&
            (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;

            if (Clipboard.ContainsText())
            {
                await _viewModel.SendTerminalInputAsync(
                    Clipboard.GetText());
            }

            return;
        }

        if (
            e.Key == Key.Enter ||
            e.Key == Key.Return)
        {
            e.Handled = true;

            if (
                _viewModel.SendCommandCommand.CanExecute(
                    null))
            {
                _viewModel.SendCommandCommand.Execute(
                    null);
            }

            return;
        }

        string? sequence =
            e.Key switch
            {
                Key.Tab
                    when
                        (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    => "\x1B[Z",

                Key.Tab
                    => "\t",

                Key.Up
                    => "\x1B[A",

                Key.Down
                    => "\x1B[B",

                Key.Right
                    => "\x1B[C",

                Key.Left
                    => "\x1B[D",

                Key.Home
                    => "\x1B[H",

                Key.End
                    => "\x1B[F",

                Key.Delete
                    => "\x1B[3~",

                // DEL is the conventional VT Backspace input byte.
                Key.Back
                    => "\x7F",

                Key.Escape
                    => "\x1B",

                _ => null
            };

        if (sequence is null)
            return;

        e.Handled = true;

        await _viewModel.SendTerminalInputAsync(
            sequence);
    }


    /// <summary>
    /// Applies Bannerlord's native prompt cursor to the WPF command mirror.
    ///
    /// TextBox.CaretIndex is not bindable, so this small view-only bridge is
    /// required after the Text binding has updated.
    /// </summary>
    private void ViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName != nameof(MainViewModel.CommandText) &&
            e.PropertyName != nameof(MainViewModel.CommandCaretIndex))
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                var caret =
                    Math.Clamp(
                        _viewModel.CommandCaretIndex,
                        0,
                        CommandInput.Text.Length);

                CommandInput.CaretIndex =
                    caret;

                CommandInput.SelectionLength =
                    0;
            }));
    }


    // If BCS Tool owns a running server, closing the application should not
    // simply abandon it. We first offer a graceful save + stop sequence.
    private async void Window_Closing(object? sender, CancelEventArgs e)
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

        var stopped = await _viewModel.PrepareForApplicationExitAsync();

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
        Close();
    }
}
