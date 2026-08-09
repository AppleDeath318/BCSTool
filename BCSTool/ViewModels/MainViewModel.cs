using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BCSTool.Infrastructure;
using BCSTool.Models;
using BCSTool.Services;
using Microsoft.Win32;

namespace BCSTool.ViewModels;

/// <summary>
/// Central coordinator between the WPF UI and the server-management services.
///
/// Think of MainViewModel as the application's traffic controller.
///
/// It does NOT directly know how to:
/// - launch a Windows process,
/// - inspect ports,
/// - write JSON,
/// - calculate schedules,
/// - or write log files.
///
/// Instead it asks specialized services to do those jobs.
///
/// Typical flow:
///
///     Button click
///        ↓
///     ICommand
///        ↓
///     MainViewModel
///        ↓
///     ServerProcessManager / RestartScheduler / PortMonitor
///        ↓
///     Updated properties
///        ↓
///     WPF bindings refresh the UI
///
/// It also contains the high-level server lifecycle orchestration:
/// start → wait for ready → warn → save → stop → restart.
/// </summary>
public sealed class MainViewModel : BindableBase, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly LogService _logService;
    private readonly PortMonitor _portMonitor;
    private readonly ServerProcessManager _processManager;
    private readonly RestartScheduler _restartScheduler;
    private readonly PlayerRosterTracker _playerRosterTracker;
    private readonly ServerExecutableLocator _serverExecutableLocator;
    private readonly SaveBackupService _saveBackupService;

    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private Task? _schedulerTask;
    private bool _initialized;
    private bool _serverReady;
    private bool _applicationClosing;
    private bool _crashRecoveryRunning;

    private DateTime? _nextRestartAt;
    private string? _lastWarningKey;

    private ServerSettings _settings = new();
    private ServerState _serverState = ServerState.Stopped;
    private string _statusMessage = "Starting BCS Tool...";
    private string _serverPidText = "-";
    private string _uptimeText = "-";
    private string _nextRestartText = "Waiting...";
    private string _commandText = "";
    private string _serverExecutableDetectionStatus =
        "Checking server executable...";

    // Raw ConPTY output may split a message across chunks. Keep only enough
    // trailing text to detect the next "Successfully saved" marker across a
    // chunk boundary without retaining unbounded console data.
    private const string SuccessfulSaveMarker = "Successfully saved";
    private readonly object _saveMarkerLock = new();
    private string _saveMarkerTail = "";

    // BannerlordCoopServer.exe is a launcher/terminal host. A fatal game-server
    // failure can terminate the hosted server while leaving this launcher
    // process and ConPTY window alive. In that failure mode Process.Exited does
    // not fire, so crash recovery also watches the authoritative terminal
    // markers emitted by the dedicated server/launcher.
    private const string FatalServerStateMarker = "\"phase\":\"fatal\"";
    private const string LauncherUnexpectedExitMarker =
        "[launcher] the server exited unexpectedly";

    private readonly object _crashMarkerLock = new();
    private string _crashMarkerTail = "";

    // Both terminal markers and Process.Exited can report the same crash.
    // Latch one recovery request per managed server session so a delayed
    // Process.Exited event cannot start a second recovery against the new
    // instance.
    private int _crashRecoverySignalQueued;

    // Bannerlord's native footer exposes a more detailed runtime state, e.g.
    // "SERVING". This value is display-only: the ServerState enum remains the
    // authoritative state machine used by automation.
    private string _nativeServerStatus = "";

    private int _commandCaretIndex;
    private string _terminalText = "";

    private TerminalScreenSnapshot _terminalSnapshot =
        TerminalScreenSnapshot.Empty;

    // Once Bannerlord's full TUI header has been observed, transient redraw
    // frames that temporarily omit it are not allowed to replace the last
    // complete screen. This makes full-screen redraws visually atomic.
    private bool _hasSeenCompleteTerminalHeader;

    // ConsoleLines is retained for internal BCS Tool diagnostic messages and
    // compatibility with the learning walkthrough. The visible server console
    // in v1.2 is TerminalText reconstructed from ConPTY.
    public ObservableCollection<string> ConsoleLines { get; } = new();

    /// <summary>
    /// Text shown in the dedicated Players panel.
    ///
    /// When the server exposes its terminal roster through redirected output,
    /// these lines contain the player/character rows. If only the player count
    /// is available, a helpful fallback message is shown instead.
    /// </summary>
    public ObservableCollection<string> PlayerLines { get; } = new()
    {
        "(no one online)"
    };

    public IReadOnlyList<int> RestartHourOptions { get; } =
        Enumerable.Range(1, 24).ToArray();

    public IReadOnlyList<int> MinuteOptions { get; } =
        Enumerable.Range(0, 60).ToArray();

    public IReadOnlyList<int> WarningMinuteOptions { get; } =
        Enumerable.Range(0, 11).ToArray();

    public IReadOnlyList<int> SaveBackupCountOptions { get; } =
        Enumerable.Range(
            SaveBackupService.MinimumBackupCount,
            SaveBackupService.MaximumBackupCount).ToArray();

    public ServerSettings Settings
    {
        get => _settings;
        private set
        {
            if (SetProperty(ref _settings, value))
            {
                OnPropertyChanged(nameof(ServerExecutableDisplay));
            }
        }
    }

    /// <summary>
    /// User-facing application name and version.
    ///
    /// The version comes from the <Version> value in BCSTool.csproj, so the
    /// project file remains the single source of truth for release numbering.
    /// AssemblyInformationalVersion may contain build metadata after a '+'
    /// character; that metadata is intentionally omitted from the UI.
    /// </summary>
    public string ApplicationVersion =>
        $"BCS Tool v{GetApplicationVersion()}";


    private static string GetApplicationVersion()
    {
        var assembly =
            typeof(MainViewModel).Assembly;

        var informationalVersion =
            assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataSeparator =
                informationalVersion.IndexOf('+');

            return
                metadataSeparator >= 0
                    ? informationalVersion[..metadataSeparator]
                    : informationalVersion;
        }

        // Fallback for unusual builds where informational-version metadata
        // was not generated.
        var assemblyVersion =
            assembly.GetName().Version;

        if (assemblyVersion is null)
            return "0.0.0";

        var build =
            Math.Max(
                0,
                assemblyVersion.Build);

        return
            $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{build}";
    }


    public string ServerExecutableDisplay
    {
        get
        {
            try
            {
                return
                    Settings.ResolveServerExecutablePath();
            }
            catch
            {
                return
                    Settings.ServerExecutable;
            }
        }
    }


    public string ServerExecutableDetectionStatus
    {
        get => _serverExecutableDetectionStatus;
        private set =>
            SetProperty(
                ref _serverExecutableDetectionStatus,
                value);
    }


    public ServerState ServerState
    {
        get => _serverState;
        private set
        {
            if (!SetProperty(ref _serverState, value))
                return;

            OnPropertyChanged(nameof(ServerStateText));
            OnPropertyChanged(nameof(IsServerRunning));
            OnPropertyChanged(nameof(IsServerFullyStopped));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Text shown in the top "Server state" field.
    ///
    /// While a managed server process is running, Bannerlord's native footer
    /// is preferred because it exposes a more detailed runtime status than
    /// BCS Tool's lifecycle enum. Critical BCS Tool states such as Error,
    /// Crashed, PortBlocked, and Restarting still take precedence.
    ///
    /// The internal ServerState enum is deliberately NOT replaced by this
    /// native text because save/restart/crash automation depends on it.
    /// </summary>
    public string ServerStateText
    {
        get
        {
            var useNativeStatus =
                _processManager.IsRunning &&
                !string.IsNullOrWhiteSpace(
                    _nativeServerStatus) &&
                ServerState is not ServerState.Error and
                    not ServerState.Crashed and
                    not ServerState.PortBlocked and
                    not ServerState.Restarting;

            if (useNativeStatus)
                return _nativeServerStatus;

            return ServerState switch
            {
                ServerState.Stopped => "Stopped",
                ServerState.Starting => "Starting",
                ServerState.WaitingForReady => "Loading / waiting for ready",
                ServerState.Ready => "Online / ready",
                ServerState.Saving => "Saving",
                ServerState.Stopping => "Stopping",
                ServerState.Restarting => "Restarting",
                ServerState.Crashed => "Crashed",
                ServerState.PortBlocked => "Startup blocked",
                ServerState.Error => "Error",
                _ => ServerState.ToString()
            };
        }
    }

    /// <summary>
    /// Header displayed above the player roster.
    /// </summary>
    public string PlayersHeaderText =>
        $"Players ({_playerRosterTracker.PlayerCount})";

    public bool IsServerRunning => _processManager.IsRunning;

    /// <summary>
    /// Manual backup restore is intentionally stricter than merely checking
    /// whether a process happens to be absent. The lifecycle state must also
    /// explicitly be Stopped.
    /// </summary>
    public bool IsServerFullyStopped =>
        ServerState == ServerState.Stopped &&
        !_processManager.IsRunning;


    /// <summary>
    /// Called by MainWindow when the visible Server Console viewport changes.
    ///
    /// Pixel-to-character measurement belongs in the WPF view because it
    /// depends on the actual font and DPI. The ViewModel only forwards the
    /// resulting character-grid size to the process manager.
    /// </summary>
    public void ResizeTerminal(
        int columns,
        int rows)
    {
        _processManager.ResizeTerminal(
            columns,
            rows);
    }


    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ServerPidText
    {
        get => _serverPidText;
        private set => SetProperty(ref _serverPidText, value);
    }

    public string UptimeText
    {
        get => _uptimeText;
        private set => SetProperty(ref _uptimeText, value);
    }

    public string NextRestartText
    {
        get => _nextRestartText;
        private set => SetProperty(ref _nextRestartText, value);
    }

    /// <summary>
    /// Styled ConPTY screen used by TerminalDisplayControl.
    /// </summary>
    public TerminalScreenSnapshot TerminalSnapshot
    {
        get => _terminalSnapshot;
        private set => SetProperty(ref _terminalSnapshot, value);
    }


    /// <summary>
    /// Current rendered left/log pane of the ConPTY terminal screen.
    /// </summary>
    public string TerminalText
    {
        get => _terminalText;
        private set => SetProperty(ref _terminalText, value);
    }

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (SetProperty(ref _commandText, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// Caret position mirrored from Bannerlord's native ConPTY prompt.
    ///
    /// WPF TextBox.CaretIndex is not a dependency property, so MainWindow
    /// listens for this property and applies it after the Text binding updates.
    /// </summary>
    public int CommandCaretIndex
    {
        get => _commandCaretIndex;
        private set => SetProperty(ref _commandCaretIndex, value);
    }

    public ICommand StartCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SendCommandCommand { get; }

    public ICommand SaveSettingsCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    public ICommand BrowseServerCommand { get; }
    public ICommand ClearConsoleCommand { get; }
    public ICommand OpenServerLogsCommand { get; }

    /// <summary>
    /// Constructor receives all services the ViewModel depends on.
    /// This makes responsibilities explicit and keeps the class testable.
    /// </summary>
    public MainViewModel(
        SettingsService settingsService,
        LogService logService,
        PortMonitor portMonitor,
        ServerProcessManager processManager,
        RestartScheduler restartScheduler,
        PlayerRosterTracker playerRosterTracker,
        ServerExecutableLocator serverExecutableLocator,
        SaveBackupService saveBackupService)
    {
        _settingsService = settingsService;
        _logService = logService;
        _portMonitor = portMonitor;
        _processManager = processManager;
        _restartScheduler = restartScheduler;
        _playerRosterTracker = playerRosterTracker;
        _serverExecutableLocator = serverExecutableLocator;
        _saveBackupService = saveBackupService;

        _dispatcher = Application.Current.Dispatcher;

        _processManager.OutputReceived += ProcessManager_OutputReceived;
        _processManager.TerminalScreenUpdated +=
            ProcessManager_TerminalScreenUpdated;
        _processManager.UnexpectedExit += ProcessManager_UnexpectedExit;

        StartCommand = new AsyncRelayCommand(
            StartServerAsync,
            () => !_processManager.IsRunning && !_applicationClosing);

        SaveCommand = new AsyncRelayCommand(
            SaveServerAsync,
            () => _processManager.IsRunning &&
                  ServerState == ServerState.Ready &&
                  !_applicationClosing);

        RestartCommand = new AsyncRelayCommand(
            () => RestartServerAsync("Manual restart"),
            () => _processManager.IsRunning &&
                  ServerState == ServerState.Ready &&
                  !_applicationClosing);

        StopCommand = new AsyncRelayCommand(
            () => StopServerAsync(saveFirst: true),
            () => _processManager.IsRunning &&
                  !_applicationClosing);

        SendCommandCommand = new AsyncRelayCommand(
            SubmitNativeCommandLineAsync,
            () => _processManager.IsRunning &&
                  !_applicationClosing);

        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);

        // Reset Settings now resets only the controls owned by the Restart
        // Settings panel. The auto-saved server executable path is preserved.
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync);

        BrowseServerCommand = new AsyncRelayCommand(BrowseServerExecutableAsync);

        // Clears BCS Tool's own informational console only.
        //
        // The live Server Console is the authoritative ConPTY terminal and is
        // deliberately not modified by this command.
        ClearConsoleCommand = new RelayCommand(
            () => ConsoleLines.Clear());

        OpenServerLogsCommand = new RelayCommand(OpenServerLogs);
    }

    /// <summary>
    /// One-time application initialization.
    ///
    /// 1. Load persistent settings.
    /// 2. Start the scheduler loop.
    /// 3. Leave the server stopped until the user presses Start.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        _initialized = true;

        Settings = await _settingsService.LoadAsync();

        AddToolMessage($"{ApplicationVersion} initialized.");
        AddToolMessage($"Settings storage: {_settingsService.StorageLocation}");

        await DetectServerExecutableIfNeededAsync();

        AddToolMessage($"Server executable: {ServerExecutableDisplay}");
        AddToolMessage(
            Settings.SaveBackupsEnabled
                ? $"Save backup rotation enabled; retaining {Settings.SaveBackupCount} generation(s)."
                : "Save backup rotation disabled.");

        _schedulerTask = RunSchedulerLoopAsync(_lifetimeCts.Token);

        // First launch is always manual. Scheduled restarts and optional crash
        // recovery still work after BCS Tool has started a server, but opening
        // the application itself never launches Bannerlord.
        ServerState = ServerState.Stopped;
        StatusMessage = "Server is stopped. Press Start to launch it.";
    }

    /// <summary>
    /// Performs all safety checks and starts the server.
    ///
    /// Notice that "process started" is NOT the same as "server ready".
    /// After startup we move into WaitingForReady and wait until stdout
    /// contains Settings.ReadyText.
    /// </summary>
    private async Task StartServerAsync()
    {
        if (_applicationClosing)
            return;

        await _operationLock.WaitAsync();

        try
        {
            if (_processManager.IsRunning)
                return;

            var validation = Settings.Validate();

            if (validation.Count > 0)
            {
                ServerState = ServerState.Error;
                StatusMessage = string.Join(" ", validation);
                AddToolMessage("Settings validation failed: " + StatusMessage);
                return;
            }

            var executablePath = Settings.ResolveServerExecutablePath();
            var workingDirectory = Settings.ResolveServerDirectory();

            if (!File.Exists(executablePath))
            {
                ServerState = ServerState.Error;
                StatusMessage = $"Server executable not found: {executablePath}";
                AddToolMessage(StatusMessage);
                return;
            }

            // Do not knowingly start a second server executable.
            if (_processManager.HasExternalServerProcess(executablePath))
            {
                ServerState = ServerState.PortBlocked;
                StatusMessage =
                    "Another BannerlordCoopServer process already exists. " +
                    "BCS Tool will not start a duplicate. " +
                    "If you previously stopped a Visual Studio debug session, " +
                    "this may be a leftover process from that run.";
                AddToolMessage(StatusMessage);
                return;
            }

            // Optional network-port guard.
            //
            // ServerPort = 0 disables this check. This is safer than assuming
            // that an arbitrary port shown in server console output is the
            // server's exclusive listening port.
            if (
                Settings.ServerPort > 0 &&
                _portMonitor.IsPortInUse(Settings.ServerPort))
            {
                ServerState = ServerState.PortBlocked;
                StatusMessage =
                    $"Port {Settings.ServerPort} is already in use. " +
                    "BCS Tool will not start another server while that configured port is occupied.";
                AddToolMessage(StatusMessage);
                return;
            }

            _serverReady = false;
            _nextRestartAt = null;
            _lastWarningKey = null;
            ResetCrashOutputDetection();
            ResetPlayerRoster();

            // Do not let the previous process's footer status linger while a
            // new ConPTY screen is being created.
            SetNativeServerStatus("");

            ServerState = ServerState.Starting;
            StatusMessage = "Starting Bannerlord server...";

            var started = await _processManager.StartAsync(
                executablePath,
                workingDirectory,
                _lifetimeCts.Token);

            if (!started)
            {
                ServerState = ServerState.Error;
                StatusMessage = "Server process failed to start.";
                AddToolMessage(StatusMessage);
                return;
            }

            ServerPidText = _processManager.ProcessId?.ToString() ?? "-";
            ServerState = ServerState.WaitingForReady;
            StatusMessage =
                $"Waiting for: {Settings.ReadyText}";

            AddToolMessage(
                $"Server process started. PID {_processManager.ProcessId}.");
            AddToolMessage(
                "Scheduled automation remains disabled until readiness is detected.");
        }
        finally
        {
            _operationLock.Release();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Manual save operation.
    ///
    /// It broadcasts the configured saving message, sends "save", waits the
    /// configured amount of time, then returns the UI to Ready state.
    /// </summary>
    private async Task SaveServerAsync()
    {
        await _operationLock.WaitAsync();

        try
        {
            if (!_processManager.IsRunning)
                return;

            ServerState = ServerState.Saving;
            StatusMessage = "Saving server...";

            await BroadcastAsync(Settings.BroadcastSaving);
            await Task.Delay(1000, _lifetimeCts.Token);

            var sent = await _processManager.SendCommandAsync(
                "save",
                _lifetimeCts.Token);

            if (!sent)
            {
                ServerState = ServerState.Error;
                StatusMessage = "Could not send save command.";
                return;
            }

            AddToolMessage("Save command sent.");

            await Task.Delay(
                TimeSpan.FromSeconds(Settings.SaveWaitSeconds),
                _lifetimeCts.Token);

            ServerState = _serverReady
                ? ServerState.Ready
                : ServerState.WaitingForReady;

            StatusMessage = "Save completed.";
        }
        finally
        {
            _operationLock.Release();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Full controlled restart sequence.
    ///
    /// Sequence:
    ///   broadcast saving
    ///   → save
    ///   → wait
    ///   → broadcast restarting
    ///   → stop
    ///   → wait for process exit
    ///   → verify the port is free
    ///   → delay
    ///   → start a fresh server
    ///
    /// Scheduled and manual restarts share the same method so behavior stays
    /// consistent.
    /// </summary>
    private async Task RestartServerAsync(string reason)
    {
        await _operationLock.WaitAsync();

        try
        {
            if (!_processManager.IsRunning)
                return;

            _nextRestartAt = null;
            _lastWarningKey = null;

            AddToolMessage($"Restart sequence started: {reason}");

            ServerState = ServerState.Saving;
            StatusMessage = "Saving before restart...";

            await BroadcastAsync(Settings.BroadcastSaving);
            await Task.Delay(1000, _lifetimeCts.Token);

            if (!await _processManager.SendCommandAsync(
                    "save",
                    _lifetimeCts.Token))
            {
                ServerState = ServerState.Error;
                StatusMessage = "Could not send save command.";
                return;
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Settings.SaveWaitSeconds),
                _lifetimeCts.Token);

            await BroadcastAsync(Settings.BroadcastRestarting);
            await Task.Delay(2000, _lifetimeCts.Token);

            ServerState = ServerState.Stopping;
            StatusMessage = "Stopping server gracefully...";

            var stopped = await _processManager.StopGracefullyAsync(
                TimeSpan.FromSeconds(Settings.ShutdownTimeoutSeconds),
                _lifetimeCts.Token);

            if (!stopped)
            {
                ServerState = ServerState.Error;
                StatusMessage =
                    "Server did not shut down cleanly. Restart aborted.";
                AddToolMessage(StatusMessage);
                return;
            }

            _serverReady = false;

            ServerPidText = "-";
            UptimeText = "-";

            // If an actual exclusive server port is configured, wait for it
            // to become free before starting the next instance.
            //
            // ServerPort = 0 disables this optional guard.
            if (Settings.ServerPort > 0)
            {
                var portFree = await _portMonitor.WaitForPortFreeAsync(
                    Settings.ServerPort,
                    TimeSpan.FromSeconds(Settings.PortReleaseTimeoutSeconds),
                    _lifetimeCts.Token);

                if (!portFree)
                {
                    AddToolMessage(
                        $"Port {Settings.ServerPort} stayed occupied after graceful stop. " +
                        "Cleaning the managed process tree.");

                    // This is the abnormal fallback. Normal restarts should
                    // never need a force cleanup because "stop" is graceful.
                    _processManager.ForceCleanupManagedTree();

                    portFree = await _portMonitor.WaitForPortFreeAsync(
                        Settings.ServerPort,
                        TimeSpan.FromSeconds(Settings.PortReleaseTimeoutSeconds),
                        _lifetimeCts.Token);
                }

                if (!portFree)
                {
                    ServerState = ServerState.PortBlocked;
                    StatusMessage =
                        $"Port {Settings.ServerPort} is still occupied. Restart paused.";
                    return;
                }
            }

            ServerState = ServerState.Restarting;
            StatusMessage =
                $"Restarting in {Settings.RestartDelaySeconds} seconds...";

            await Task.Delay(
                TimeSpan.FromSeconds(Settings.RestartDelaySeconds),
                _lifetimeCts.Token);

            // Start directly while already holding the operation lock.
            await StartServerWhileLockedAsync();
        }
        finally
        {
            _operationLock.Release();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Internal startup helper used during RestartServerAsync.
    ///
    /// RestartServerAsync already owns _operationLock, so calling the normal
    /// StartServerAsync method would deadlock trying to acquire the same lock
    /// again. This method performs startup while reusing the existing lock.
    /// </summary>
    private async Task StartServerWhileLockedAsync()
    {
        if (_applicationClosing)
            return;

        var executablePath = Settings.ResolveServerExecutablePath();
        var workingDirectory = Settings.ResolveServerDirectory();

        if (
            Settings.ServerPort > 0 &&
            _portMonitor.IsPortInUse(Settings.ServerPort))
        {
            ServerState = ServerState.PortBlocked;
            StatusMessage =
                $"Port {Settings.ServerPort} is still in use. Server not started.";
            return;
        }

        _serverReady = false;
        _nextRestartAt = null;
        _lastWarningKey = null;
        ResetCrashOutputDetection();
        ResetPlayerRoster();

        ServerState = ServerState.Starting;
        StatusMessage = "Starting Bannerlord server...";

        var started = await _processManager.StartAsync(
            executablePath,
            workingDirectory,
            _lifetimeCts.Token);

        if (!started)
        {
            ServerState = ServerState.Error;
            StatusMessage = "Server process failed to start.";
            return;
        }

        ServerPidText = _processManager.ProcessId?.ToString() ?? "-";
        ServerState = ServerState.WaitingForReady;
        StatusMessage = $"Waiting for: {Settings.ReadyText}";

        AddToolMessage(
            $"Server process started. PID {_processManager.ProcessId}.");
    }

    /// <summary>
    /// Public-facing wrapper that serializes stop operations with
    /// _operationLock.
    /// </summary>
    private async Task StopServerAsync(bool saveFirst)
    {
        await _operationLock.WaitAsync();

        try
        {
            await StopServerWhileLockedAsync(saveFirst);
        }
        finally
        {
            _operationLock.Release();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Gracefully stops the managed server while the caller already holds
    /// _operationLock.
    /// </summary>
    private async Task<bool> StopServerWhileLockedAsync(bool saveFirst)
    {
        if (!_processManager.IsRunning)
        {
            ServerState = ServerState.Stopped;
            return true;
        }

        _nextRestartAt = null;
        _lastWarningKey = null;

        if (saveFirst)
        {
            ServerState = ServerState.Saving;
            StatusMessage = "Saving before shutdown...";

            await _processManager.SendCommandAsync(
                "save",
                _lifetimeCts.Token);

            await Task.Delay(
                TimeSpan.FromSeconds(Settings.SaveWaitSeconds),
                _lifetimeCts.Token);
        }

        ServerState = ServerState.Stopping;
        StatusMessage = "Stopping server gracefully...";

        var stopped = await _processManager.StopGracefullyAsync(
            TimeSpan.FromSeconds(Settings.ShutdownTimeoutSeconds),
            _lifetimeCts.Token);

        if (!stopped)
        {
            ServerState = ServerState.Error;
            StatusMessage = "Server did not stop within the shutdown timeout.";
            return false;
        }

        _serverReady = false;
        ResetPlayerRoster();

        ServerState = ServerState.Stopped;
        ServerPidText = "-";
        UptimeText = "-";
        NextRestartText = "Server stopped";
        StatusMessage = "Server stopped.";

        AddToolMessage("Server stopped gracefully.");
        return true;
    }

    /// <summary>
    /// Submits Bannerlord's CURRENT native console input line.
    ///
    /// v1.7 mirrors every editing keystroke into ConPTY as it happens, so the
    /// server already owns the command text. Clicking Send/pressing Enter must
    /// therefore send ONLY the native Enter key rather than re-sending the
    /// complete WPF CommandText string.
    /// </summary>
    private async Task SubmitNativeCommandLineAsync()
    {
        var submittedText =
            CommandText;

        if (await _processManager.SendRawInputAsync(
                "\r",
                _lifetimeCts.Token))
        {
            if (!string.IsNullOrWhiteSpace(submittedText))
            {
                AddToolMessage(
                    $"> {submittedText}");
            }
        }
    }


    /// <summary>
    /// Sends raw interactive terminal input to Bannerlord.
    ///
    /// MainWindow uses this for text input and native editing/autocomplete
    /// keys. No BCS Tool command database is involved.
    /// </summary>
    public Task<bool> SendTerminalInputAsync(
        string input)
    {
        return
            _processManager.SendRawInputAsync(
                input,
                _lifetimeCts.Token);
    }

    /// <summary>
    /// Convenience helper translating a human-readable message into the
    /// server command: "say [message]".
    /// </summary>
    private async Task BroadcastAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        await _processManager.SendCommandAsync(
            $"say {message}",
            _lifetimeCts.Token);
    }

    private bool ConfiguredServerExecutableExists()
    {
        try
        {
            return
                File.Exists(
                    Settings.ResolveServerExecutablePath());
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// Keeps a valid manually-saved server path untouched. If that path is
    /// missing or invalid, attempts a bounded automatic search.
    ///
    /// A successful detection is persisted immediately so later launches do
    /// not need to repeat the search. Detecting a path never starts the server.
    /// </summary>
    private async Task DetectServerExecutableIfNeededAsync()
    {
        if (
            ConfiguredServerExecutableExists())
        {
            ServerExecutableDetectionStatus =
                "Saved executable path loaded — changes are saved automatically.";

            AddToolMessage(
                "Server executable path is valid; auto-detection was not needed.");

            return;
        }

        ServerExecutableDetectionStatus =
            "Auto-detecting BannerlordCoopServer.exe...";

        AddToolMessage(
            "Saved server executable path is missing or invalid. Auto-detecting...");

        ServerExecutableDetectionResult result;

        try
        {
            result =
                await _serverExecutableLocator.DetectAsync(
                    _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ServerExecutableDetectionStatus =
                "Auto-detection failed — use Browse...; selections save automatically.";

            AddToolMessage(
                $"Server executable auto-detection failed: {ex.Message}");

            return;
        }

        if (
            !result.Found ||
            string.IsNullOrWhiteSpace(
                result.Path))
        {
            ServerExecutableDetectionStatus =
                "Not detected automatically — use Browse...; selections save automatically.";

            AddToolMessage(
                "BannerlordCoopServer.exe was not detected automatically. Use Browse...");

            return;
        }

        Settings.ServerDirectory =
            Path.GetDirectoryName(
                result.Path) ??
            "";

        Settings.ServerExecutable =
            Path.GetFileName(
                result.Path);

        OnPropertyChanged(
            nameof(ServerExecutableDisplay));

        if (result.CandidateCount > 1)
        {
            AddToolMessage(
                $"Auto-detection found {result.CandidateCount} candidates; selected: {result.Path}");
        }
        else
        {
            AddToolMessage(
                $"Auto-detected server executable: {result.Path}");
        }

        // Persist only the detected executable location. Restart settings are
        // owned by the Restart Settings panel and are not written here.
        try
        {
            await _settingsService.SaveServerExecutableAsync(
                Settings);

            ServerExecutableDetectionStatus =
                $"Auto-detected from {result.Source} and saved automatically.";

            AddToolMessage(
                "Auto-detected server executable path saved automatically.");
        }
        catch (Exception ex)
        {
            ServerExecutableDetectionStatus =
                $"Auto-detected from {result.Source}, but the path could not be saved.";

            AddToolMessage(
                $"Could not persist auto-detected server executable path: {ex.Message}");
        }
    }


    /// <summary>
    /// Validates and saves only the controls shown in Restart Settings, then
    /// immediately recalculates the next restart time.
    ///
    /// The server executable path is independent and is saved automatically
    /// when detected or selected through Browse.
    /// </summary>
    private async Task SaveSettingsAsync()
    {
        var validation =
            new List<string>();

        if (Settings.RestartEveryHours is < 1 or > 24)
        {
            validation.Add(
                "Restart interval must be between 1 and 24 hours.");
        }

        if (Settings.RestartMinute is < 0 or > 59)
        {
            validation.Add(
                "Restart minute must be between 0 and 59.");
        }

        if (Settings.WarningMinutesBefore is < 0 or > 10)
        {
            validation.Add(
                "Restart warning lead time must be between 0 and 10 minutes.");
        }

        if (validation.Count > 0)
        {
            StatusMessage =
                string.Join(
                    " ",
                    validation);

            MessageBox.Show(
                StatusMessage,
                "Invalid Restart Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        await _settingsService.SaveRestartSettingsAsync(
            Settings);

        RecalculateNextRestart();

        StatusMessage =
            "Restart settings saved.";

        AddToolMessage(
            "Restart settings saved.");
    }

    /// <summary>
    /// Opens a normal Windows file picker so the user can locate
    /// BannerlordCoopServer.exe without editing JSON manually.
    /// </summary>
    /// <summary>
    /// Restores only the Restart Settings panel to its built-in defaults and
    /// persists those restart defaults immediately.
    ///
    /// The independently-saved server executable path is left untouched.
    /// </summary>
    private async Task ResetSettingsAsync()
    {
        var result =
            MessageBox.Show(
                "Reset restart settings to their built-in defaults?" +
                "The saved server executable path will not be changed.",
                "Reset Restart Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        var defaults =
            new ServerSettings();

        Settings.RestartEveryHours =
            defaults.RestartEveryHours;

        Settings.RestartMinute =
            defaults.RestartMinute;

        Settings.WarningMinutesBefore =
            defaults.WarningMinutesBefore;

        Settings.AutoRestartOnCrash =
            defaults.AutoRestartOnCrash;

        // Settings is a nested mutable object, so explicitly refresh the
        // Restart Settings bindings after restoring its values.
        OnPropertyChanged(
            nameof(Settings));

        await _settingsService.SaveRestartSettingsAsync(
            Settings);

        RecalculateNextRestart();

        StatusMessage =
            "Restart settings reset to defaults.";

        AddToolMessage(
            "Restart settings reset to defaults.");
    }


    private async Task BrowseServerExecutableAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select BannerlordCoopServer.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            FileName = Settings.ServerExecutable
        };

        if (dialog.ShowDialog() != true)
            return;

        Settings.ServerDirectory =
            Path.GetDirectoryName(dialog.FileName) ?? "";

        Settings.ServerExecutable =
            Path.GetFileName(dialog.FileName);

        OnPropertyChanged(
            nameof(ServerExecutableDisplay));

        try
        {
            await _settingsService.SaveServerExecutableAsync(
                Settings);

            ServerExecutableDetectionStatus =
                "Selected manually and saved automatically.";

            StatusMessage =
                "Server executable selected and saved.";

            AddToolMessage(
                $"Server executable selected and saved automatically: {ServerExecutableDisplay}");
        }
        catch (Exception ex)
        {
            ServerExecutableDetectionStatus =
                "Selected manually, but the path could not be saved.";

            StatusMessage =
                $"Could not save server executable path: {ex.Message}";

            AddToolMessage(
                StatusMessage);
        }
    }

    private void OpenServerLogs()
    {
        try
        {
            var serverLogDirectory =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord",
                    "CoopData",
                    "DedicatedServer",
                    "logs");

            Directory.CreateDirectory(
                serverLogDirectory);

            Process.Start(
                new ProcessStartInfo
                {
                    FileName =
                        serverLogDirectory,

                    UseShellExecute =
                        true
                });
        }
        catch (Exception ex)
        {
            StatusMessage =
                $"Could not open server logs: {ex.Message}";
        }
    }

    /// <summary>
    /// Resets the visible player panel whenever a server instance ends.
    /// </summary>
    private void ResetPlayerRoster()
    {
        _playerRosterTracker.Reset();

        PlayerLines.Clear();
        PlayerLines.Add("(no one online)");

        _hasSeenCompleteTerminalHeader = false;
        TerminalSnapshot =
            TerminalScreenSnapshot.Empty;
        TerminalText = "";

        CommandText = "";
        CommandCaretIndex = 0;

        OnPropertyChanged(nameof(PlayersHeaderText));
    }


    /// <summary>
    /// Synchronizes the WPF Players panel with PlayerRosterTracker.
    ///
    /// If the server only exposes a count, we still show that count and make
    /// it clear that the detailed terminal roster was not present in the
    /// redirected stdout stream.
    /// </summary>
    private void RefreshPlayerRoster()
    {
        OnPropertyChanged(nameof(PlayersHeaderText));

        PlayerLines.Clear();

        if (_playerRosterTracker.PlayerCount <= 0)
        {
            PlayerLines.Add("(no one online)");
            return;
        }

        if (_playerRosterTracker.RosterLines.Count == 0)
        {
            // Only show pulse-based fallback text before the native ConPTY
            // Players pane has ever been detected.
            //
            // Once a native pane has been seen, transient redraw frames keep
            // the previous valid roster instead of replacing it.
            if (!_playerRosterTracker.HasNativePane)
            {
                PlayerLines.Add(
                    $"{_playerRosterTracker.PlayerCount} player(s) online");

                PlayerLines.Add(
                    "(waiting for native Players pane)");
            }
            else
            {
                PlayerLines.Add(
                    $"{_playerRosterTracker.PlayerCount} player(s) online");
            }

            return;
        }

        foreach (var playerLine in _playerRosterTracker.RosterLines)
        {
            PlayerLines.Add(playerLine);
        }
    }


    /// <summary>
    /// Updates BCS Tool's save-backup settings from the Server Configuration
    /// window. These settings are persisted separately from server-config.json
    /// and take effect immediately.
    /// </summary>
    public async Task UpdateSaveBackupSettingsAsync(
        bool enabled,
        int backupCount)
    {
        backupCount =
            Math.Clamp(
                backupCount,
                SaveBackupService.MinimumBackupCount,
                SaveBackupService.MaximumBackupCount);

        var previousEnabled =
            Settings.SaveBackupsEnabled;

        var previousBackupCount =
            Settings.SaveBackupCount;

        var settingsChanged =
            previousEnabled != enabled ||
            previousBackupCount != backupCount;

        // Server Configuration always calls this method when Save is pressed.
        // If the BCS backup settings themselves did not change, avoid Registry
        // writes and, more importantly, avoid waiting on the backup rotation
        // lock. This keeps ordinary Server Configuration saves responsive.
        if (!settingsChanged)
            return;

        Settings.SaveBackupsEnabled =
            enabled;

        Settings.SaveBackupCount =
            backupCount;

        await _settingsService.SaveBackupSettingsAsync(
            Settings);

        // The filesystem only needs trimming when enabling backup rotation or
        // when reducing the retention count. Increasing the count does not
        // require touching any existing backup files.
        var shouldTrim =
            enabled &&
            (
                !previousEnabled ||
                backupCount < previousBackupCount
            );

        if (shouldTrim)
        {
            try
            {
                await _saveBackupService.TrimBackupsAsync(
                    backupCount,
                    _lifetimeCts.Token);
            }
            catch (FileNotFoundException)
            {
                // No active save exists yet. The selected retention setting is
                // still valid and will be applied when the first backup occurs.
            }
            catch (DirectoryNotFoundException)
            {
                // Same first-run case as above.
            }
        }

        AddToolMessage(
            enabled
                ? $"Save backup rotation enabled; retaining {backupCount} generation(s)."
                : "Save backup rotation disabled; existing backups were preserved.");
    }


    /// <summary>
    /// Returns complete rotating backups for the currently configured save.
    /// </summary>
    public Task<IReadOnlyList<SaveBackupService.SaveBackupInfo>> GetSaveBackupsAsync()
    {
        return
            _saveBackupService.GetBackupsAsync(
                _lifetimeCts.Token);
    }


    /// <summary>
    /// Replaces the current save pair with one selected backup generation.
    ///
    /// This operation shares the same operation lock as Start/Stop/Restart so
    /// a server start cannot race a manual filesystem restore.
    /// </summary>
    public async Task<SaveBackupService.SaveBackupRestoreResult> RestoreSaveBackupAsync(
        int generation)
    {
        await _operationLock.WaitAsync();

        try
        {
            if (!IsServerFullyStopped)
            {
                throw new InvalidOperationException(
                    "The server must be fully stopped before loading a backup save.");
            }

            var result =
                await _saveBackupService.RestoreBackupAsync(
                    generation,
                    _lifetimeCts.Token);

            StatusMessage =
                $"Loaded save backup {result.BackupName}.";

            AddToolMessage(
                $"Loaded save backup {result.BackupName}: " +
                $"{Path.GetFileName(result.ActiveSavPath)} + " +
                $"{Path.GetFileName(result.ActiveJsonPath)} replaced.");

            return
                result;
        }
        finally
        {
            _operationLock.Release();
            CommandManager.InvalidateRequerySuggested();
        }
    }


    /// <summary>
    /// Handles raw ConPTY terminal chunks.
    ///
    /// Raw chunks can contain VT/ANSI control sequences, so they are not
    /// displayed directly. They are used for fast readiness detection while
    /// VirtualTerminalScreen handles the visible terminal rendering.
    /// </summary>
    private void ProcessManager_OutputReceived(
        object? sender,
        string chunk)
    {
        // The launcher can stay alive after the hosted game server fatally
        // exits. Detect that condition from raw ConPTY output instead of
        // relying exclusively on BannerlordCoopServer.exe Process.Exited.
        if (
            ConsumeUnexpectedServerFailureMarker(
                chunk,
                out var failureReason))
        {
            QueueCrashRecovery(
                failureReason);
        }

        // Save completion is independent from server readiness. Bannerlord's
        // engine emits "Successfully saved" only after the save process has
        // completed, so this is the trigger for BCS Tool's rotation.
        if (ConsumeSuccessfulSaveMarker(chunk))
        {
            _ = CreateSaveBackupAfterSuccessfulSaveAsync();
        }

        if (
            _serverReady ||
            !_processManager.IsRunning ||
            !chunk.Contains(
                Settings.ReadyText,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_serverReady)
                return;

            _serverReady = true;

            ServerState = ServerState.Ready;
            StatusMessage = "Server is online and ready.";

            AddToolMessage(
                $"SERVER READY detected: {Settings.ReadyText}");

            RecalculateNextRestart();
        });
    }


    private bool ConsumeUnexpectedServerFailureMarker(
        string chunk,
        out string failureReason)
    {
        failureReason =
            "";

        if (string.IsNullOrEmpty(chunk))
            return false;

        lock (_crashMarkerLock)
        {
            var combined =
                _crashMarkerTail + chunk;

            if (
                combined.Contains(
                    FatalServerStateMarker,
                    StringComparison.OrdinalIgnoreCase))
            {
                _crashMarkerTail =
                    "";

                failureReason =
                    "Dedicated server reported a fatal state while the launcher may still be open.";

                return true;
            }

            if (
                combined.Contains(
                    LauncherUnexpectedExitMarker,
                    StringComparison.OrdinalIgnoreCase))
            {
                _crashMarkerTail =
                    "";

                failureReason =
                    "Launcher reported that the hosted server exited unexpectedly.";

                return true;
            }

            _crashMarkerTail =
                KeepCrashMarkerTail(
                    combined);

            return false;
        }
    }


    private static string KeepCrashMarkerTail(
        string text)
    {
        var maxTailLength =
            Math.Max(
                FatalServerStateMarker.Length,
                LauncherUnexpectedExitMarker.Length) - 1;

        if (text.Length <= maxTailLength)
            return text;

        return
            text[^maxTailLength..];
    }


    private void ResetCrashOutputDetection()
    {
        lock (_crashMarkerLock)
        {
            _crashMarkerTail =
                "";
        }

        Interlocked.Exchange(
            ref _crashRecoverySignalQueued,
            0);
    }


    private void QueueCrashRecovery(
        string reason)
    {
        if (_applicationClosing)
            return;

        if (
            Interlocked.Exchange(
                ref _crashRecoverySignalQueued,
                1) != 0)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(
            new Action(
                async () =>
                {
                    await HandleUnexpectedExitAsync(
                        reason);
                }));
    }


    private bool ConsumeSuccessfulSaveMarker(
        string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return false;

        lock (_saveMarkerLock)
        {
            var combined =
                _saveMarkerTail + chunk;

            var markerIndex =
                combined.IndexOf(
                    SuccessfulSaveMarker,
                    StringComparison.OrdinalIgnoreCase);

            if (markerIndex >= 0)
            {
                // Keep only text after the LAST complete marker. That prevents
                // the marker itself from surviving in the tail and being
                // counted again on the next chunk.
                var lastMarkerIndex =
                    combined.LastIndexOf(
                        SuccessfulSaveMarker,
                        StringComparison.OrdinalIgnoreCase);

                var afterMarker =
                    combined[
                        (lastMarkerIndex + SuccessfulSaveMarker.Length)..];

                _saveMarkerTail =
                    KeepSaveMarkerTail(
                        afterMarker);

                return true;
            }

            _saveMarkerTail =
                KeepSaveMarkerTail(
                    combined);

            return false;
        }
    }


    private static string KeepSaveMarkerTail(
        string text)
    {
        var maxTailLength =
            SuccessfulSaveMarker.Length - 1;

        if (text.Length <= maxTailLength)
            return text;

        return
            text[^maxTailLength..];
    }


    private async Task CreateSaveBackupAfterSuccessfulSaveAsync()
    {
        if (
            !Settings.SaveBackupsEnabled ||
            _applicationClosing)
        {
            return;
        }

        try
        {
            var backup =
                await _saveBackupService.CreateBackupAsync(
                    Settings.SaveBackupCount,
                    _lifetimeCts.Token);

            if (backup is not null)
            {
                AddToolMessage(
                    $"Save backup pair created: {Path.GetFileName(backup.SavPath)} + {Path.GetFileName(backup.JsonPath)}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Backup failure must never affect the running Bannerlord server.
            // Surface the error to the BCS Tool console and continue normally.
            AddToolMessage(
                $"Save backup failed: {ex.Message}");
        }
    }


    /// <summary>
    /// Handles a reconstructed two-dimensional ConPTY terminal screen.
    ///
    /// The native Players pane is parsed first. The left/log pane is then
    /// rendered into TerminalText while the player pane is rendered in the
    /// dedicated WPF Players panel.
    /// </summary>
    private void ProcessManager_TerminalScreenUpdated(
        object? sender,
        TerminalScreenUpdatedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // Render the COMPLETE ConPTY terminal. Bannerlord's own Players
            // pane remains part of this screen.
            var terminalLines =
                e.Lines;

            // Read Bannerlord's authoritative runtime status from the ORIGINAL
            // native snapshot before display-only footer filtering removes it.
            //
            // Example native footer:
            //
            // F10 Stop | SERVING · save saveauto1 · port 4200 · players 0 ...
            //
            // "SERVING" remains hidden in the Server Console, but is surfaced
            // in the top Server state field.
            if (
                TryExtractNativeFooterStatus(
                    e.Snapshot,
                    out var nativeStatus))
            {
                SetNativeServerStatus(
                    nativeStatus);
            }

            // Bannerlord's normal full-screen frame has both labels in the
            // top few terminal rows:
            //
            //     Log ... Players (N)
            //
            // During a terminal redraw those labels can briefly disappear
            // between the "erase old frame" and "draw new frame" VT writes.
            // Once a complete frame has been seen, keep the previous complete
            // screen instead of exposing that intermediate frame to the user.
            var hasCompleteHeader =
                HasCompleteBannerlordTerminalHeader(
                    terminalLines);

            if (hasCompleteHeader)
            {
                _hasSeenCompleteTerminalHeader = true;
            }
            else if (
                _hasSeenCompleteTerminalHeader &&
                _processManager.IsRunning)
            {
                return;
            }

            // Preserve the terminal's row layout while removing trailing
            // completely-empty rows from the WPF TextBox.
            var lastNonEmpty =
                terminalLines.Count - 1;

            while (
                lastNonEmpty >= 0 &&
                string.IsNullOrWhiteSpace(
                    terminalLines[lastNonEmpty]))
            {
                lastNonEmpty--;
            }

            if (lastNonEmpty < 0)
            {
                TerminalSnapshot =
                    TerminalScreenSnapshot.Empty;
                TerminalText = "";
                return;
            }

            // Preserve Bannerlord's ANSI/VT styling, but hide a few native
            // footer fields that are redundant or unusable inside BCS Tool.
            //
            // This is DISPLAY-ONLY filtering. Bannerlord's underlying ConPTY
            // screen is not modified, so native input/autocomplete/history
            // behavior remains untouched.
            var displaySnapshot =
                CreateDisplayTerminalSnapshot(
                    e.Snapshot);

            TerminalSnapshot =
                displaySnapshot;

            // Synchronize the separate WPF command box against the ORIGINAL
            // native screen so footer filtering cannot affect prompt parsing
            // or terminal cursor synchronization.
            SynchronizeCommandInputFromTerminal(
                e.Snapshot);

            // Keep a plain-text copy of exactly what BCS Tool displays.
            var displayLines =
                displaySnapshot.PlainLines;

            var displayLastNonEmpty =
                displayLines.Count - 1;

            while (
                displayLastNonEmpty >= 0 &&
                string.IsNullOrWhiteSpace(
                    displayLines[displayLastNonEmpty]))
            {
                displayLastNonEmpty--;
            }

            TerminalText =
                displayLastNonEmpty < 0
                    ? ""
                    : string.Join(
                        Environment.NewLine,
                        displayLines.Take(
                            displayLastNonEmpty + 1));
        });
    }


    // Bannerlord's native footer currently resembles:
    //
    // F10 Stop | SERVING · save saveauto1 · port 4200 · players 0 · up 0:02:58
    //
    // The native runtime state ("SERVING" in this example) is parsed from the
    // ORIGINAL snapshot and shown in BCS Tool's top Server state field.
    //
    // The embedded Server Console still hides the native status and displays:
    //
    // | save saveauto1 · port 4200 · players 0
    //
    // Footer filtering is display-only and does not alter ConPTY state.
    /// <summary>
    /// Updates the display-only native server status and refreshes the bound
    /// ServerStateText property only when the text actually changes.
    /// </summary>
    private void SetNativeServerStatus(
        string status)
    {
        status =
            status.Trim();

        // Bannerlord can report descriptive native states with a lowercase
        // first letter, such as "loading campaign". Match BCS Tool's other
        // state labels by capitalizing only that first character.
        //
        // Already-capitalized/all-uppercase states such as "SERVING" are left
        // unchanged.
        if (
            status.Length > 0 &&
            char.IsLower(
                status[0]))
        {
            status =
                char.ToUpperInvariant(
                    status[0]) +
                status[1..];
        }

        if (
            string.Equals(
                _nativeServerStatus,
                status,
                StringComparison.Ordinal))
        {
            return;
        }

        _nativeServerStatus =
            status;

        OnPropertyChanged(
            nameof(ServerStateText));
    }


    /// <summary>
    /// Finds Bannerlord's native footer near the bottom of the ORIGINAL ConPTY
    /// snapshot and extracts the status between the F10 control field and the
    /// first normal data field ("save ...").
    ///
    /// Example:
    ///
    /// F10 Stop | SERVING · save saveauto1 · port 4200 ...
    ///            ^^^^^^^
    ///
    /// No specific status word is hardcoded. If Bannerlord changes SERVING to
    /// another detailed runtime state, that new text is surfaced automatically.
    /// </summary>
    private static bool TryExtractNativeFooterStatus(
        TerminalScreenSnapshot snapshot,
        out string status)
    {
        status = "";

        if (snapshot.Lines.Count == 0)
            return false;

        var firstRow =
            Math.Max(
                0,
                snapshot.Lines.Count - 8);

        for (
            var row = snapshot.Lines.Count - 1;
            row >= firstRow;
            row--)
        {
            var plain =
                snapshot.Lines[row].PlainText;

            if (
                TryParseNativeFooter(
                    plain,
                    out status,
                    out _))
            {
                return true;
            }
        }

        status = "";
        return false;
    }


    /// <summary>
    /// Parses the semantic pieces needed from Bannerlord's native footer.
    ///
    /// The parser intentionally does not depend on SERVING being the status.
    /// It validates the row through the F10/save/port fields, then treats the
    /// text between the F10 action and the save field as the native state.
    /// </summary>
    private static bool TryParseNativeFooter(
        string plain,
        out string status,
        out int saveIndex)
    {
        status = "";
        saveIndex = -1;

        if (string.IsNullOrWhiteSpace(plain))
            return false;

        saveIndex =
            plain.IndexOf(
                "save ",
                StringComparison.OrdinalIgnoreCase);

        var portIndex =
            plain.IndexOf(
                "port ",
                StringComparison.OrdinalIgnoreCase);

        var f10Index =
            plain.IndexOf(
                "F10",
                StringComparison.OrdinalIgnoreCase);

        if (
            f10Index < 0 ||
            saveIndex < 0 ||
            portIndex < 0 ||
            portIndex <= saveIndex ||
            saveIndex <= f10Index)
        {
            saveIndex = -1;
            return false;
        }

        // Prefer the explicit "|" divider after "F10 Stop". If Bannerlord
        // changes that glyph, fall back to the text after the word "Stop".
        var statusStart =
            plain.IndexOf(
                '|',
                f10Index);

        if (
            statusStart >= 0 &&
            statusStart < saveIndex)
        {
            statusStart++;
        }
        else
        {
            var stopIndex =
                plain.IndexOf(
                    "Stop",
                    f10Index,
                    StringComparison.OrdinalIgnoreCase);

            statusStart =
                stopIndex >= 0 &&
                stopIndex < saveIndex
                    ? stopIndex + "Stop".Length
                    : f10Index + "F10".Length;
        }

        if (
            statusStart < 0 ||
            statusStart >= saveIndex)
        {
            return false;
        }

        var statusSegment =
            plain[
                statusStart..
                saveIndex];

        status =
            TrimFooterSeparators(
                statusSegment);

        // The native footer occasionally uses a box-drawing/broken/fullwidth
        // vertical bar that visually resembles "|". Never expose those
        // decorative separators as part of the Server state.
        status =
            new string(
                status
                    .Where(
                        ch =>
                            !IsVerticalFooterBar(
                                ch))
                    .ToArray())
                .Trim();

        return
            !string.IsNullOrWhiteSpace(
                status);
    }


    /// <summary>
    /// Removes footer punctuation from both ends of a semantic field while
    /// preserving spaces/punctuation inside a multi-word native status.
    /// </summary>
    private static string TrimFooterSeparators(
        string text)
    {
        var start = 0;
        var end = text.Length;

        while (
            start < end &&
            IsFooterSeparatorCharacter(
                text[start]))
        {
            start++;
        }

        while (
            end > start &&
            IsFooterSeparatorCharacter(
                text[end - 1]))
        {
            end--;
        }

        return
            text[start..end]
                .Trim();
    }


    /// <summary>
    /// Creates the terminal snapshot shown to the user.
    ///
    /// Only the displayed snapshot is changed. The original ConPTY snapshot
    /// remains authoritative for prompt synchronization, autocomplete,
    /// history, cursor tracking, and native server-state extraction.
    /// </summary>
    private static TerminalScreenSnapshot CreateDisplayTerminalSnapshot(
        TerminalScreenSnapshot source)
    {
        if (source.Lines.Count == 0)
            return source;

        var lines =
            source.Lines.ToArray();

        var firstRow =
            Math.Max(
                0,
                lines.Length - 8);

        for (
            var row = lines.Length - 1;
            row >= firstRow;
            row--)
        {
            var plain =
                lines[row].PlainText;

            if (string.IsNullOrWhiteSpace(plain))
                continue;

            // Identify the native footer through the same generic parser
            // used for Server state extraction. The status itself remains
            // hidden because the visible row begins at "save ...".
            if (
                !TryParseNativeFooter(
                    plain,
                    out _,
                    out var saveIndex))
            {
                continue;
            }

            lines[row] =
                SliceNativeFooterLine(
                    lines[row],
                    saveIndex);

            break;
        }

        return
            new TerminalScreenSnapshot(
                lines,
                source.CursorRow,
                source.CursorColumn);
    }


    /// <summary>
    /// Keeps the styled row starting at `save ...` and ending before the final
    /// native uptime field (`up H:MM:SS`).
    /// </summary>
    private static TerminalStyledLine SliceNativeFooterLine(
        TerminalStyledLine line,
        int saveIndex)
    {
        var styledCharacters =
            new List<(char Character, TerminalCellStyle Style)>();

        foreach (var run in line.Runs)
        {
            foreach (var ch in run.Text)
            {
                styledCharacters.Add(
                    (ch, run.Style));
            }
        }

        if (
            saveIndex < 0 ||
            saveIndex >= styledCharacters.Count)
        {
            return line;
        }

        var uptimeIndex =
            FindNativeUptimeFieldStart(
                line.PlainText,
                saveIndex);

        var endExclusive =
            uptimeIndex >= 0
                ? uptimeIndex
                : styledCharacters.Count;

        // Remove punctuation/spacing immediately before the uptime field.
        while (
            endExclusive > saveIndex &&
            IsFooterSeparatorCharacter(
                styledCharacters[endExclusive - 1].Character))
        {
            endExclusive--;
        }

        var kept =
            styledCharacters
                .Skip(saveIndex)
                .Take(
                    Math.Max(
                        0,
                        endExclusive - saveIndex))
                .ToList();

        while (
            kept.Count > 0 &&
            char.IsWhiteSpace(
                kept[0].Character))
        {
            kept.RemoveAt(0);
        }

        while (
            kept.Count > 0 &&
            IsFooterSeparatorCharacter(
                kept[^1].Character))
        {
            kept.RemoveAt(
                kept.Count - 1);
        }

        if (kept.Count == 0)
        {
            return
                new TerminalStyledLine(
                    "",
                    Array.Empty<TerminalTextRun>());
        }

        // Keep the native status itself hidden, but restore a simple divider
        // before the remaining footer fields:
        //
        //     | save saveauto1 · port 4200 · players 0
        //
        // Using the first visible field's style keeps the divider visually
        // consistent without exposing the hidden F10/status cells.
        var footerStyle =
            kept[0].Style;

        kept.Insert(
            0,
            (' ', footerStyle));

        kept.Insert(
            0,
            ('|', footerStyle));

        var runs =
            new List<TerminalTextRun>();

        var runText =
            new System.Text.StringBuilder();

        var runStyle =
            kept[0].Style;

        foreach (var item in kept)
        {
            if (
                runText.Length > 0 &&
                item.Style != runStyle)
            {
                runs.Add(
                    new TerminalTextRun(
                        runText.ToString(),
                        runStyle));

                runText.Clear();
                runStyle =
                    item.Style;
            }

            runText.Append(
                item.Character);
        }

        if (runText.Length > 0)
        {
            runs.Add(
                new TerminalTextRun(
                    runText.ToString(),
                    runStyle));
        }

        var filteredPlain =
            new string(
                kept
                    .Select(item => item.Character)
                    .ToArray());

        return
            new TerminalStyledLine(
                filteredPlain,
                runs);
    }


    /// <summary>
    /// Finds a trailing uptime field shaped like `up 0:02:58`.
    /// </summary>
    private static int FindNativeUptimeFieldStart(
        string text,
        int searchFrom)
    {
        var searchIndex =
            text.Length;

        while (searchIndex > searchFrom)
        {
            var candidate =
                text.LastIndexOf(
                    "up ",
                    searchIndex - 1,
                    StringComparison.OrdinalIgnoreCase);

            if (candidate < searchFrom)
                return -1;

            var value =
                text[(candidate + 3)..]
                    .Trim();

            if (
                TimeSpan.TryParse(
                    value,
                    out _))
            {
                return candidate;
            }

            searchIndex =
                candidate;
        }

        return -1;
    }


    private static bool IsFooterSeparatorCharacter(char ch)
    {
        return
            char.IsWhiteSpace(ch) ||
            IsVerticalFooterBar(ch) ||
            ch == '·' ||
            ch == '•' ||
            ch == '∙' ||
            ch == '⋅' ||
            ch == '-' ||
            ch == '—' ||
            ch == '–' ||
            ch == 'â';
    }


    /// <summary>
    /// Bannerlord/terminal fonts may use several visually similar vertical
    /// separators. Treat all of them as footer punctuation so none leak into
    /// the Server state text.
    /// </summary>
    private static bool IsVerticalFooterBar(char ch)
    {
        return
            ch == '|' ||   // ASCII vertical line
            ch == '│' ||   // box drawings light vertical
            ch == '┃' ||   // box drawings heavy vertical
            ch == '¦' ||   // broken bar
            ch == '｜';    // fullwidth vertical line
    }


    /// <summary>
    /// Finds Bannerlord's native prompt near the bottom of the terminal:
    ///
    ///     > st
    ///
    /// and mirrors its content/cursor into the WPF command input.
    ///
    /// Bannerlord remains authoritative. If Tab changes `st` to `status`, the
    /// next complete terminal snapshot changes CommandText to `status`.
    /// </summary>
    private void SynchronizeCommandInputFromTerminal(
        TerminalScreenSnapshot snapshot)
    {
        var lines =
            snapshot.Lines;

        if (lines.Count == 0)
            return;

        // The prompt is normally directly above the F10/status footer. Search
        // only the lower portion of the screen to avoid interpreting a log
        // line that happens to contain a `>` character.
        var firstRow =
            Math.Max(
                0,
                lines.Count - 12);

        for (
            var row = lines.Count - 1;
            row >= firstRow;
            row--)
        {
            var line =
                lines[row].PlainText;

            if (string.IsNullOrEmpty(line))
                continue;

            var promptColumn = 0;

            while (
                promptColumn < line.Length &&
                char.IsWhiteSpace(
                    line[promptColumn]))
            {
                promptColumn++;
            }

            if (
                promptColumn >= line.Length ||
                line[promptColumn] != '>')
            {
                continue;
            }

            var inputStart =
                promptColumn + 1;

            // Native console normally renders "> command". Do not require the
            // space so this keeps working if its prompt style changes slightly.
            if (
                inputStart < line.Length &&
                line[inputStart] == ' ')
            {
                inputStart++;
            }

            // Terminal rows contain blank padding to the right edge of the
            // screen, so we cannot simply keep every trailing space. At the
            // same time, TrimEnd() is too aggressive because a space the user
            // just typed at the native prompt is meaningful even when it is
            // currently the last character.
            //
            // Keep content through the final non-whitespace cell, and when the
            // native cursor is on this prompt row, also keep cells through the
            // cursor position. This preserves typed trailing spaces while still
            // discarding unused terminal-row padding.
            var commandEnd =
                line.TrimEnd().Length;

            if (snapshot.CursorRow == row)
            {
                commandEnd =
                    Math.Max(
                        commandEnd,
                        snapshot.CursorColumn);
            }

            commandEnd =
                Math.Clamp(
                    commandEnd,
                    inputStart,
                    line.Length);

            var command =
                inputStart < commandEnd
                    ? line[inputStart..commandEnd]
                    : "";

            CommandText =
                command;

            var caret =
                command.Length;

            if (snapshot.CursorRow == row)
            {
                caret =
                    Math.Clamp(
                        snapshot.CursorColumn - inputStart,
                        0,
                        command.Length);
            }

            CommandCaretIndex =
                caret;

            return;
        }
    }


    /// <summary>
    /// Detects Bannerlord Coop's native complete top frame.
    ///
    /// We intentionally only inspect the first few terminal rows so ordinary
    /// log text containing the words "Log" or "Players" cannot satisfy it.
    /// </summary>
    private static bool HasCompleteBannerlordTerminalHeader(
        IReadOnlyList<string> terminalLines)
    {
        var rowsToInspect =
            Math.Min(
                6,
                terminalLines.Count);

        for (
            var row = 0;
            row < rowsToInspect;
            row++)
        {
            var line =
                terminalLines[row];

            if (
                line.Contains(
                    "Log",
                    StringComparison.OrdinalIgnoreCase) &&
                line.Contains(
                    "Players (",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


    private void ProcessManager_UnexpectedExit(
        object? sender,
        EventArgs e)
    {
        QueueCrashRecovery(
            "Managed BannerlordCoopServer process exited unexpectedly.");
    }


    /// <summary>
    /// Crash-recovery state machine.
    ///
    /// Recovery can be triggered by either:
    ///   - the managed launcher process actually exiting, or
    ///   - terminal output proving that the hosted game server died while the
    ///     launcher/console itself remained open.
    ///
    /// Recovery:
    ///   mark crashed
    ///   → settle briefly
    ///   → terminate the stale launcher + managed child tree
    ///   → wait for the launcher process to actually disappear
    ///   → freeze the newest retained backup as a manual crash snapshot
    ///   → wait for the server port to become free
    ///   → restart using the current active save after the configured delay
    /// </summary>
    private async Task HandleUnexpectedExitAsync(
        string reason)
    {
        if (_applicationClosing || _crashRecoveryRunning)
            return;

        _crashRecoveryRunning = true;

        try
        {
            _serverReady = false;
            _nextRestartAt = null;
            _lastWarningKey = null;
            ResetPlayerRoster();

            ServerPidText = "-";
            UptimeText = "-";
            NextRestartText = "Crash recovery";

            ServerState = ServerState.Crashed;
            StatusMessage = "Server failure detected.";

            AddToolMessage(
                $"Unexpected server failure detected: {reason}");

            await Task.Delay(
                TimeSpan.FromSeconds(Settings.CrashRecoverySettleSeconds),
                _lifetimeCts.Token);

            // A fatal hosted-server crash can leave BannerlordCoopServer.exe
            // sitting at a dead launcher prompt. Terminate the entire managed
            // job even when the launcher process itself has not exited.
            AddToolMessage(
                "Cleaning the crashed server launcher and managed child processes.");

            _processManager.ForceCleanupManagedTree();

            // The old crash path was entered only after Process.Exited, so it
            // never had to wait for the root launcher. Terminal-marker recovery
            // can arrive while that launcher is still alive. Starting before
            // it actually exits can make StartServerAsync silently return or
            // trip duplicate-process protection.
            var launcherExitDeadline =
                DateTime.UtcNow +
                TimeSpan.FromSeconds(
                    Math.Max(
                        5,
                        Settings.PortReleaseTimeoutSeconds));

            while (
                _processManager.IsRunning &&
                !_applicationClosing &&
                DateTime.UtcNow < launcherExitDeadline)
            {
                StatusMessage =
                    "Waiting for crashed server launcher to close...";

                await Task.Delay(
                    250,
                    _lifetimeCts.Token);
            }

            if (_applicationClosing)
                return;

            if (_processManager.IsRunning)
            {
                ServerState = ServerState.Error;
                StatusMessage =
                    "Crashed server launcher could not be terminated. Automatic recovery paused.";

                AddToolMessage(
                    StatusMessage);

                return;
            }

            // Freeze the newest complete retained backup as a dedicated
            // crash recovery point. This NEVER changes the active save:
            // Bannerlord still restarts from the current configured save pair.
            //
            // Capture it even when automatic restart is disabled, because the
            // manual recovery point is still useful after any detected crash.
            StatusMessage =
                "Creating crash backup from newest retained save...";

            try
            {
                var crashBackup =
                    await _saveBackupService.CreateCrashBackupFromNewestBackupAsync(
                        _lifetimeCts.Token);

                if (crashBackup is null)
                {
                    AddToolMessage(
                        "No complete retained save backup is available for a crash snapshot. " +
                        "The current active save was left unchanged.");
                }
                else
                {
                    AddToolMessage(
                        $"Crash backup created from {crashBackup.SourceBackupName}: " +
                        $"{Path.GetFileName(crashBackup.SavPath)} + " +
                        $"{Path.GetFileName(crashBackup.JsonPath)}.");

                    AddToolMessage(
                        "The active save was not rolled back. If it later proves corrupted, " +
                        $"stop the server and load {crashBackup.CrashBackupName} manually.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Snapshot failure must not replace or block the current save.
                AddToolMessage(
                    $"Warning: crash backup could not be created: {ex.Message}");
                AddToolMessage(
                    "The current active save was left unchanged.");
            }

            if (!Settings.AutoRestartOnCrash)
            {
                _processManager.MarkUnexpectedExitHandlingComplete();

                ServerState = ServerState.Stopped;
                StatusMessage =
                    "Crash recovery is disabled. Server remains stopped.";

                AddToolMessage(
                    "Crashed server process tree was cleaned. Automatic restart is disabled.");

                return;
            }

            while (
                Settings.ServerPort > 0 &&
                _portMonitor.IsPortInUse(Settings.ServerPort) &&
                !_applicationClosing)
            {
                ServerState = ServerState.PortBlocked;
                StatusMessage =
                    $"Waiting for port {Settings.ServerPort} to become free...";

                await Task.Delay(
                    2000,
                    _lifetimeCts.Token);
            }

            if (_applicationClosing)
                return;

            ServerState = ServerState.Restarting;
            StatusMessage =
                $"Restarting after crash in {Settings.RestartDelaySeconds} seconds...";

            await Task.Delay(
                TimeSpan.FromSeconds(Settings.RestartDelaySeconds),
                _lifetimeCts.Token);

            _processManager.MarkUnexpectedExitHandlingComplete();

            await StartServerAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _crashRecoveryRunning = false;
        }
    }

    /// <summary>
    /// Background scheduler loop.
    ///
    /// It ticks once per second but performs automation only when:
    ///   - the server is Ready,
    ///   - a next-restart timestamp exists,
    ///   - the application is not closing.
    ///
    /// It also ensures each minute warning is sent only once.
    /// </summary>
    private async Task RunSchedulerLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _dispatcher.InvokeAsync(
                    () => UpdateRuntimeDisplay());

                if (
                    !_serverReady ||
                    ServerState != ServerState.Ready ||
                    _nextRestartAt is null ||
                    _applicationClosing)
                {
                    continue;
                }

                var now = DateTime.Now;
                var remaining = _nextRestartAt.Value - now;

                if (remaining <= TimeSpan.Zero)
                {
                    var restartTime = _nextRestartAt.Value;
                    _nextRestartAt = null;
                    _lastWarningKey = null;

                    await _dispatcher.InvokeAsync(() =>
                    {
                        AddToolMessage(
                            $"Scheduled restart time reached: {restartTime:yyyy-MM-dd HH:mm:ss}");

                        _ = RestartServerAsync("Scheduled restart");
                    });

                    continue;
                }

                if (Settings.WarningMinutesBefore <= 0)
                    continue;

                var minutesRemaining =
                    (int)Math.Ceiling(remaining.TotalMinutes);

                if (
                    minutesRemaining < 1 ||
                    minutesRemaining > Settings.WarningMinutesBefore)
                {
                    continue;
                }

                var warningKey =
                    $"{_nextRestartAt:yyyyMMddHHmm}-{minutesRemaining}";

                if (warningKey == _lastWarningKey)
                    continue;

                _lastWarningKey = warningKey;

                var message = minutesRemaining == 1
                    ? "Server restart in 1 minute."
                    : $"Server restart in {minutesRemaining} minutes.";

                await _dispatcher.InvokeAsync(() =>
                {
                    AddToolMessage(message);
                    _ = BroadcastAsync(message);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateRuntimeDisplay()
    {
        ServerPidText =
            _processManager.ProcessId?.ToString() ?? "-";

        if (_processManager.StartedAt is { } startedAt &&
            _processManager.IsRunning)
        {
            var uptime = DateTime.Now - startedAt;

            UptimeText =
                $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
        }
        else
        {
            UptimeText = "-";
        }

        if (_nextRestartAt is { } next)
        {
            NextRestartText = next.ToString("yyyy-MM-dd HH:mm:ss");
        }

        OnPropertyChanged(nameof(IsServerRunning));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Recomputes the next restart after the server becomes ready or the user
    /// saves new scheduling settings.
    ///
    /// Missed restart times are intentionally not replayed.
    /// </summary>
    private void RecalculateNextRestart()
    {
        if (!_serverReady || ServerState != ServerState.Ready)
        {
            _nextRestartAt = null;
            NextRestartText = "Waiting for server ready";
            return;
        }

        _nextRestartAt =
            _restartScheduler.CalculateNextRestart(
                DateTime.Now,
                Settings);

        _lastWarningKey = null;

        NextRestartText =
            _nextRestartAt.Value.ToString("yyyy-MM-dd HH:mm:ss");

        AddToolMessage(
            $"Next scheduled restart: {_nextRestartAt:yyyy-MM-dd HH:mm:ss}");
    }

    private void AddToolMessage(string message)
    {
        _logService.Write(message);
        AddConsoleLine($"[BCS Tool] {message}");
    }

    /// <summary>
    /// Adds one line to the visible in-app console.
    ///
    /// ObservableCollection requires updates on WPF's UI thread. The
    /// Dispatcher branch safely hops back to that thread when needed.
    ///
    /// The collection is capped at 5,000 lines so a server running for days
    /// does not grow UI memory without limit.
    /// </summary>
    private void AddConsoleLine(string line)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AddConsoleLine(line));
            return;
        }

        ConsoleLines.Add(line);

        const int maxLines = 5000;

        while (ConsoleLines.Count > maxLines)
        {
            ConsoleLines.RemoveAt(0);
        }
    }

    /// <summary>
    /// Called when the user closes BCS Tool while the server is still running.
    /// Attempts a final save + graceful stop before the application exits.
    /// </summary>
    public async Task<bool> PrepareForApplicationExitAsync()
    {
        _applicationClosing = true;
        _nextRestartAt = null;

        if (!_processManager.IsRunning)
            return true;

        await _operationLock.WaitAsync();

        try
        {
            return await StopServerWhileLockedAsync(saveFirst: true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void ForceCleanupManagedServerTree()
    {
        _processManager.ForceCleanupManagedTree();
    }

    public void Dispose()
    {
        _applicationClosing = true;

        _lifetimeCts.Cancel();

        try
        {
            _schedulerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _processManager.OutputReceived -= ProcessManager_OutputReceived;
        _processManager.TerminalScreenUpdated -=
            ProcessManager_TerminalScreenUpdated;
        _processManager.UnexpectedExit -= ProcessManager_UnexpectedExit;

        _processManager.Dispose();
        _operationLock.Dispose();
        _lifetimeCts.Dispose();
    }
}
