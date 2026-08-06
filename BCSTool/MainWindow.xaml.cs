using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
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
    private readonly CoopConfigService _coopConfigService;
    private readonly DispatcherTimer _terminalResizeTimer;

    private bool _allowClose;

    private int _lastTerminalColumns = -1;
    private int _lastTerminalRows = -1;

    // The command TextBox mirrors Bannerlord's native ConPTY line editor.
    // Preserve an explicit Ctrl+A selection across terminal redraws until the
    // user performs an operation that consumes/cancels the selection.
    private bool _commandSelectAllActive;

    // Space is forwarded explicitly from PreviewKeyDown because WPF does not
    // always surface it reliably through PreviewTextInput for this proxy-style
    // read-only/mirrored command field. If a TextInput event also follows, it
    // is suppressed so the native prompt receives exactly one space.
    private bool _spaceForwardedFromKeyDown;

    // Local optimistic command state. ConPTY echo/redraw can lag behind input
    // by one or more keystrokes, especially for trailing spaces. Keep the WPF
    // command box immediately responsive and ignore stale native snapshots
    // until Bannerlord catches up to the text we have already sent.
    private string? _pendingCommandText;
    private int _pendingCommandCaretIndex;

    public MainWindow(
        MainViewModel viewModel,
        CoopConfigService coopConfigService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _coopConfigService = coopConfigService;
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
        _ = Dispatcher.BeginInvoke(
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
                _coopConfigService,
                _viewModel)
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

        // Space is sent explicitly from PreviewKeyDown. Some WPF input paths
        // still raise a TextInput event afterward; suppress only that duplicate.
        if (_spaceForwardedFromKeyDown)
        {
            var isForwardedSpace =
                e.Text == " ";

            _spaceForwardedFromKeyDown =
                false;

            if (isForwardedSpace)
            {
                e.Handled =
                    true;

                return;
            }
        }

        e.Handled =
            true;

        if (CommandInput.SelectionLength > 0)
        {
            await ReplaceNativeCommandSelectionAsync(
                e.Text);

            return;
        }

        _commandSelectAllActive =
            false;

        InsertOptimisticCommandText(
            e.Text);

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

        var modifiers =
            Keyboard.Modifiers;

        var controlPressed =
            (modifiers & ModifierKeys.Control) != 0;

        // Ctrl+A is a WPF selection operation, not a native terminal editing
        // command. Keep Bannerlord's command line unchanged and select the
        // mirrored line locally. Later typing, Backspace/Delete, Cut, or Paste
        // translates that selection back into native cursor/delete sequences.
        if (
            controlPressed &&
            e.Key == Key.A)
        {
            e.Handled =
                true;

            _commandSelectAllActive =
                true;

            CommandInput.SelectAll();

            return;
        }

        // Ctrl+C copies the mirrored selection without sending the terminal's
        // ETX (Ctrl+C) byte, which could have unrelated process semantics.
        if (
            controlPressed &&
            e.Key == Key.C &&
            CommandInput.SelectionLength > 0)
        {
            e.Handled =
                true;

            Clipboard.SetText(
                CommandInput.SelectedText);

            return;
        }

        // Ctrl+X must remove the same characters from Bannerlord's native line
        // rather than only cutting the WPF mirror.
        if (
            controlPressed &&
            e.Key == Key.X &&
            CommandInput.SelectionLength > 0)
        {
            e.Handled =
                true;

            Clipboard.SetText(
                CommandInput.SelectedText);

            await ReplaceNativeCommandSelectionAsync(
                "");

            return;
        }

        // Ctrl+V: emulate terminal paste instead of allowing WPF to modify its
        // local mirror independently from the native prompt. If text is
        // selected, paste replaces the corresponding native selection.
        if (
            controlPressed &&
            e.Key == Key.V)
        {
            e.Handled =
                true;

            if (Clipboard.ContainsText())
            {
                var clipboardText =
                    Clipboard.GetText();

                if (CommandInput.SelectionLength > 0)
                {
                    await ReplaceNativeCommandSelectionAsync(
                        clipboardText);
                }
                else
                {
                    _commandSelectAllActive =
                        false;

                    InsertOptimisticCommandText(
                        clipboardText);

                    await _viewModel.SendTerminalInputAsync(
                        clipboardText);
                }
            }

            return;
        }

        // Forward Space explicitly. This avoids relying on WPF's text
        // composition path for a key that can otherwise disappear while the
        // TextBox is acting as a mirror of a native line editor.
        if (
            e.Key == Key.Space &&
            (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            e.Handled =
                true;

            _spaceForwardedFromKeyDown =
                true;

            if (CommandInput.SelectionLength > 0)
            {
                await ReplaceNativeCommandSelectionAsync(
                    " ");
            }
            else
            {
                _commandSelectAllActive =
                    false;

                InsertOptimisticCommandText(
                    " ");

                await _viewModel.SendTerminalInputAsync(
                    " ");
            }

            return;
        }

        if (
            e.Key == Key.Enter ||
            e.Key == Key.Return)
        {
            e.Handled = true;

            _commandSelectAllActive =
                false;

            ClearOptimisticCommandState();

            if (
                _viewModel.SendCommandCommand.CanExecute(
                    null))
            {
                _viewModel.SendCommandCommand.Execute(
                    null);
            }

            return;
        }

        // Normal WPF semantics: Backspace/Delete removes the entire selected
        // range, not just one character from Bannerlord's current caret.
        if (
            CommandInput.SelectionLength > 0 &&
            (e.Key == Key.Back || e.Key == Key.Delete))
        {
            e.Handled =
                true;

            await ReplaceNativeCommandSelectionAsync(
                "");

            return;
        }

        // Standard TextBox behavior with an active selection:
        //
        // Left  -> collapse selection to its beginning.
        // Right -> collapse selection to its end.
        //
        // Bannerlord's native line editor does not know about the WPF
        // selection, so sending a single Left/Right would move from the native
        // caret instead. For Ctrl+A that native caret is normally still at the
        // end of the command, which causes an immediate desync. Reposition the
        // native caret absolutely from HOME so both editors collapse to the
        // same location.
        if (
            CommandInput.SelectionLength > 0 &&
            (e.Key == Key.Left || e.Key == Key.Right) &&
            (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            e.Handled =
                true;

            var selectionStart =
                Math.Clamp(
                    CommandInput.SelectionStart,
                    0,
                    CommandInput.Text.Length);

            var selectionEnd =
                Math.Clamp(
                    selectionStart + CommandInput.SelectionLength,
                    0,
                    CommandInput.Text.Length);

            var targetCaret =
                e.Key == Key.Left
                    ? selectionStart
                    : selectionEnd;

            _commandSelectAllActive =
                false;

            SetOptimisticCaret(
                targetCaret);

            var selectionNavigationSequence =
                new StringBuilder();

            selectionNavigationSequence.Append(
                "\x1B[H");

            for (
                var index = 0;
                index < targetCaret;
                index++)
            {
                selectionNavigationSequence.Append(
                    "\x1B[C");
            }

            await _viewModel.SendTerminalInputAsync(
                selectionNavigationSequence.ToString());

            return;
        }

        // Any explicit navigation/history/autocomplete key cancels a local
        // Ctrl+A selection. Deterministic caret movement (Left/Right/Home/End)
        // stays optimistic so stale ConPTY redraws cannot snap the WPF caret
        // back to its previous position. Tab/history are native operations
        // whose resulting text is not predictable locally, so those return
        // authority to Bannerlord immediately.
        if (
            e.Key is Key.Tab or
                Key.Up or
                Key.Down or
                Key.Right or
                Key.Left or
                Key.Home or
                Key.End or
                Key.Escape)
        {
            _commandSelectAllActive =
                false;
        }

        if (
            e.Key is Key.Tab or
                Key.Up or
                Key.Down or
                Key.Escape)
        {
            ClearOptimisticCommandState();
        }
        else if (
            e.Key == Key.Left)
        {
            ApplyOptimisticCaretMove(
                -1);
        }
        else if (
            e.Key == Key.Right)
        {
            ApplyOptimisticCaretMove(
                1);
        }
        else if (
            e.Key == Key.Home)
        {
            SetOptimisticCaret(
                0);
        }
        else if (
            e.Key == Key.End)
        {
            SetOptimisticCaret(
                CommandInput.Text.Length);
        }

        if (
            CommandInput.SelectionLength == 0 &&
            e.Key == Key.Back)
        {
            ApplyOptimisticBackspace();
        }
        else if (
            CommandInput.SelectionLength == 0 &&
            e.Key == Key.Delete)
        {
            ApplyOptimisticDelete();
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
    /// Replaces the current WPF command selection in Bannerlord's native line
    /// editor using cursor movement + Delete sequences.
    ///
    /// The WPF TextBox is only a mirror, so changing SelectedText locally would
    /// immediately be overwritten by the next terminal snapshot. This method
    /// performs the equivalent edit against the actual ConPTY prompt.
    /// </summary>
    private async Task ReplaceNativeCommandSelectionAsync(
        string replacement)
    {
        var currentText =
            CommandInput.Text;

        var selectionStart =
            Math.Clamp(
                CommandInput.SelectionStart,
                0,
                currentText.Length);

        var selectionLength =
            Math.Clamp(
                CommandInput.SelectionLength,
                0,
                currentText.Length - selectionStart);

        if (selectionLength <= 0)
        {
            _commandSelectAllActive =
                false;

            if (!string.IsNullOrEmpty(replacement))
            {
                InsertOptimisticCommandText(
                    replacement);

                await _viewModel.SendTerminalInputAsync(
                    replacement);
            }

            return;
        }

        var replacementText =
            currentText.Remove(
                selectionStart,
                selectionLength)
            .Insert(
                selectionStart,
                replacement);

        var replacementCaret =
            selectionStart +
            replacement.Length;

        // Anchor editing from native HOME rather than from the last mirrored
        // caret. ConPTY snapshots can lag behind (most visibly after a trailing
        // space), so relative movement from CommandCaretIndex can be off by one.
        //
        // HOME -> move right to selection start -> Delete selected range ->
        // insert replacement.
        var sequence =
            new StringBuilder();

        sequence.Append(
            "\x1B[H");

        for (
            var index = 0;
            index < selectionStart;
            index++)
        {
            sequence.Append(
                "\x1B[C");
        }

        for (
            var index = 0;
            index < selectionLength;
            index++)
        {
            sequence.Append(
                "\x1B[3~");
        }

        sequence.Append(
            replacement);

        _commandSelectAllActive =
            false;

        SetOptimisticCommandState(
            replacementText,
            replacementCaret);

        await _viewModel.SendTerminalInputAsync(
            sequence.ToString());
    }


    /// <summary>
    /// Inserts text into the local command mirror immediately. Bannerlord still
    /// receives the same raw input; this only avoids waiting for terminal echo
    /// before the WPF textbox reflects what the user typed.
    /// </summary>
    private void InsertOptimisticCommandText(
        string insertedText)
    {
        if (string.IsNullOrEmpty(insertedText))
            return;

        var currentText =
            CommandInput.Text;

        var caret =
            Math.Clamp(
                CommandInput.CaretIndex,
                0,
                currentText.Length);

        var updatedText =
            currentText.Insert(
                caret,
                insertedText);

        SetOptimisticCommandState(
            updatedText,
            caret + insertedText.Length);
    }


    private void ApplyOptimisticBackspace()
    {
        var currentText =
            CommandInput.Text;

        var caret =
            Math.Clamp(
                CommandInput.CaretIndex,
                0,
                currentText.Length);

        if (caret <= 0)
            return;

        var updatedText =
            currentText.Remove(
                caret - 1,
                1);

        SetOptimisticCommandState(
            updatedText,
            caret - 1);
    }


    private void ApplyOptimisticDelete()
    {
        var currentText =
            CommandInput.Text;

        var caret =
            Math.Clamp(
                CommandInput.CaretIndex,
                0,
                currentText.Length);

        if (caret >= currentText.Length)
            return;

        var updatedText =
            currentText.Remove(
                caret,
                1);

        SetOptimisticCommandState(
            updatedText,
            caret);
    }


    private void ApplyOptimisticCaretMove(
        int delta)
    {
        var currentText =
            CommandInput.Text;

        var currentCaret =
            Math.Clamp(
                CommandInput.CaretIndex,
                0,
                currentText.Length);

        SetOptimisticCommandState(
            currentText,
            Math.Clamp(
                currentCaret + delta,
                0,
                currentText.Length));
    }


    private void SetOptimisticCaret(
        int caret)
    {
        var currentText =
            CommandInput.Text;

        SetOptimisticCommandState(
            currentText,
            Math.Clamp(
                caret,
                0,
                currentText.Length));
    }


    private void SetOptimisticCommandState(
        string text,
        int caret)
    {
        _pendingCommandText =
            text;

        _pendingCommandCaretIndex =
            Math.Clamp(
                caret,
                0,
                text.Length);

        // SetCurrentValue preserves the existing WPF binding while updating the
        // displayed value immediately.
        CommandInput.SetCurrentValue(
            System.Windows.Controls.TextBox.TextProperty,
            text);

        CommandInput.CaretIndex =
            _pendingCommandCaretIndex;

        CommandInput.SelectionLength =
            0;
    }


    private void ClearOptimisticCommandState()
    {
        _pendingCommandText =
            null;

        _pendingCommandCaretIndex =
            0;
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

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                // If we already sent newer local input, a ConPTY redraw may
                // still contain an older prompt or an older cursor position.
                // Do not let that stale snapshot make the textbox/caret jump
                // backward. Bannerlord is considered caught up only when BOTH
                // its text and native caret match the optimistic local state.
                if (_pendingCommandText is not null)
                {
                    var nativeCaughtUp =
                        string.Equals(
                            _viewModel.CommandText,
                            _pendingCommandText,
                            StringComparison.Ordinal) &&
                        _viewModel.CommandCaretIndex ==
                            _pendingCommandCaretIndex;

                    if (!nativeCaughtUp)
                    {
                        CommandInput.SetCurrentValue(
                            System.Windows.Controls.TextBox.TextProperty,
                            _pendingCommandText);

                        CommandInput.CaretIndex =
                            Math.Clamp(
                                _pendingCommandCaretIndex,
                                0,
                                CommandInput.Text.Length);

                        if (_commandSelectAllActive)
                        {
                            CommandInput.SelectAll();
                        }
                        else
                        {
                            CommandInput.SelectionLength =
                                0;
                        }

                        return;
                    }

                    ClearOptimisticCommandState();
                }

                var caret =
                    Math.Clamp(
                        _viewModel.CommandCaretIndex,
                        0,
                        CommandInput.Text.Length);

                if (_commandSelectAllActive)
                {
                    CommandInput.SelectAll();

                    return;
                }

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
