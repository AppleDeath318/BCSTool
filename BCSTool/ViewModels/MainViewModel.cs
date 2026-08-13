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
using System.Text.Json;
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
    private readonly ServerLogMonitor _serverLogMonitor;
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

    // The live dedicated-server log is the authoritative source for
    // readiness, successful-save, hosted-server crash, player, command, and
    // native runtime-state events. Commands are sent through redirected stdin.
    private const string SuccessfulSaveMarker = "Successfully saved";
    private const string LauncherUnexpectedExitMarker =
        "[launcher] the server exited unexpectedly";

    // Both server-log markers and Process.Exited can report the same crash.
    // Latch one recovery request per managed server session so a delayed
    // Process.Exited event cannot start a second recovery against the new
    // instance.
    private int _crashRecoverySignalQueued;

    // Bannerlord's structured server-log state exposes a more detailed runtime
    // status, e.g. "SERVING". This value is display-only: ServerState remains the
    // authoritative state machine used by automation.
    private string _reportedServerStatus = "";

    // BCS Tool lifecycle/scheduler messages.
    public ObservableCollection<string> ConsoleLines { get; } = new();

    // The visible Server Console now follows the dedicated server's .log file.
    // Keep a bounded live window so a long-running 24/7 server cannot grow the
    // WPF collection without limit. The full log always remains on disk.
    private const int MaximumServerConsoleLines = 10000;
    public ObservableCollection<ServerConsoleLine> ServerConsoleLines
    {
        get;
    } = new();

    // Populated from the log-only @DS@ {"ev":"commands"} event. Tab completion
    // is therefore local to BCS Tool and no longer depends on the native prompt.
    private readonly List<string> _availableCommands = new();

    /// <summary>
    /// Text shown in the dedicated Players panel. The source is the complete
    /// log-only @DS@ players snapshot, so states such as "creating character"
    /// are not limited by the native terminal's visual column width.
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
    /// While a managed server process is running, Bannerlord's structured
    /// log state is preferred because it exposes a more detailed runtime status
    /// than BCS Tool's lifecycle enum. Critical BCS Tool states such as Error,
    /// Crashed, PortBlocked, and Restarting still take precedence.
    ///
    /// The internal ServerState enum is deliberately NOT replaced by this
    /// reported text because save/restart/crash automation depends on it.
    /// </summary>
    public string ServerStateText
    {
        get
        {
            var useReportedStatus =
                _processManager.IsRunning &&
                !string.IsNullOrWhiteSpace(
                    _reportedServerStatus) &&
                ServerState is not ServerState.Error and
                    not ServerState.Crashed and
                    not ServerState.PortBlocked and
                    not ServerState.Restarting;

            if (useReportedStatus)
                return _reportedServerStatus;

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
        ServerLogMonitor serverLogMonitor,
        ServerExecutableLocator serverExecutableLocator,
        SaveBackupService saveBackupService)
    {
        _settingsService = settingsService;
        _logService = logService;
        _portMonitor = portMonitor;
        _processManager = processManager;
        _restartScheduler = restartScheduler;
        _playerRosterTracker = playerRosterTracker;
        _serverLogMonitor = serverLogMonitor;
        _serverExecutableLocator = serverExecutableLocator;
        _saveBackupService = saveBackupService;

        _dispatcher = Application.Current.Dispatcher;

        // The dedicated-server log is authoritative for runtime output/state,
        // while redirected stdin is used only for completed commands.
        // Process.Exited remains an independent crash fallback if the managed
        // launcher itself terminates before a log marker is observed.
        _processManager.UnexpectedExit += ProcessManager_UnexpectedExit;

        _serverLogMonitor.LinesReceived +=
            ServerLogMonitor_LinesReceived;

        StartCommand = new AsyncRelayCommand(
            StartServerAsync,
            CanStartServer);

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
            StopServerAsync,
            () => _processManager.IsRunning &&
                  !_applicationClosing);

        SendCommandCommand = new AsyncRelayCommand(
            SubmitCommandLineAsync,
            () => _processManager.IsRunning &&
                  !_applicationClosing);

        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);

        // Reset Settings now resets only the controls owned by the Restart
        // Settings panel. The auto-saved server executable path is preserved.
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync);

        BrowseServerCommand = new AsyncRelayCommand(BrowseServerExecutableAsync);

        // Clears BCS Tool's own informational console only. The live
        // Server Console follows the current server .log independently.
        ClearConsoleCommand = new RelayCommand(
            () => ConsoleLines.Clear());

        OpenServerLogsCommand = new RelayCommand(OpenServerLogs);
    }

    private bool CanStartServer()
    {
        if (
            _applicationClosing ||
            _processManager.IsRunning)
        {
            return false;
        }

        // During a controlled restart there is intentionally a short period
        // where the old process has exited and the replacement has not started
        // yet. Process absence alone must not make Start clickable during that
        // lifecycle gap. Only explicit manual-start states allow a fresh start.
        return ServerState is
            ServerState.Stopped or
            ServerState.Crashed or
            ServerState.PortBlocked or
            ServerState.Error;
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
    /// After startup we move into WaitingForReady and wait until the live
    /// dedicated-server log reports the configured ready marker/state.
    /// </summary>
    private async Task StartServerAsync()
    {
        if (_applicationClosing)
            return;

        await _operationLock.WaitAsync();

        try
        {
            // Re-check after acquiring the lifecycle lock. This also prevents a
            // queued/manual Start from slipping through while a restart is in
            // progress and temporarily has no running process.
            if (!CanStartServer())
                return;

            await StartServerWhileLockedAsync(
                logAutomationDisabledMessage: true);
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

            if (!await TrySaveServerAsync(
                    "Saving server...",
                    broadcastSavingMessage: true,
                    logCommandSent: true))
            {
                return;
            }

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
    /// Sends the explicit manual-save sequence.
    /// The caller must already hold _operationLock.
    /// </summary>
    private async Task<bool> TrySaveServerAsync(
        string statusMessage,
        bool broadcastSavingMessage,
        bool logCommandSent)
    {
        ServerState = ServerState.Saving;
        StatusMessage = statusMessage;

        if (broadcastSavingMessage)
        {
            await BroadcastAsync(Settings.BroadcastSaving);
            await Task.Delay(
                TimeSpan.FromSeconds(1),
                _lifetimeCts.Token);
        }

        if (!await _processManager.SendCommandAsync(
                "save",
                _lifetimeCts.Token))
        {
            ServerState = ServerState.Error;
            StatusMessage = "Could not send save command.";
            return false;
        }

        if (logCommandSent)
            AddToolMessage("Save command sent.");

        await Task.Delay(
            TimeSpan.FromSeconds(Settings.SaveWaitSeconds),
            _lifetimeCts.Token);

        return true;
    }

    /// <summary>
    /// Full controlled restart sequence.
    ///
    /// Bannerlord Coop's native "stop" command saves the world before it
    /// exits, so BCS Tool deliberately does not send a separate "save" first.
    /// This avoids writing the same save twice during every restart.
    ///
    /// Sequence:
    ///   broadcast restarting
    ///   → stop (the server saves as part of stop)
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

            await BroadcastAsync(Settings.BroadcastRestarting);
            await Task.Delay(
                TimeSpan.FromSeconds(2),
                _lifetimeCts.Token);

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
    private async Task StartServerWhileLockedAsync(
        bool logAutomationDisabledMessage = false)
    {
        if (_applicationClosing)
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
            StatusMessage =
                $"Server executable not found: {executablePath}";
            AddToolMessage(StatusMessage);
            return;
        }

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
        ResetCrashRecoverySignal();
        ResetPlayerRoster();

        // Prevent the previous server session's reported status from surviving
        // until the new log emits its first structured state event.
        SetReportedServerStatus("");

        ServerState = ServerState.Starting;
        StatusMessage = "Starting Bannerlord server...";

        // Snapshot existing server logs BEFORE launch so the monitor can bind
        // unambiguously to the new coop-server-*.log created by this session.
        _serverLogMonitor.PrepareForServerStart();
        ServerConsoleLines.Clear();

        if (!await _processManager.StartAsync(
                executablePath,
                workingDirectory,
                _lifetimeCts.Token))
        {
            _serverLogMonitor.StopMonitoring();
            ServerState = ServerState.Error;
            StatusMessage = "Server process failed to start.";
            AddToolMessage(StatusMessage);
            return;
        }

        _serverLogMonitor.StartMonitoring(
            _lifetimeCts.Token);

        ServerPidText =
            _processManager.ProcessId?.ToString() ?? "-";

        ServerState = ServerState.WaitingForReady;
        StatusMessage =
            $"Waiting for: {Settings.ReadyText}";

        AddToolMessage(
            $"Server process started. PID {_processManager.ProcessId}.");

        if (logAutomationDisabledMessage)
        {
            AddToolMessage(
                "Scheduled automation remains disabled until readiness is detected.");
        }
    }

    /// <summary>
    /// Public-facing wrapper that serializes stop operations with
    /// _operationLock. The native server "stop" command performs its own save,
    /// so no separate save command is sent first.
    /// </summary>
    private async Task StopServerAsync()
    {
        await _operationLock.WaitAsync();

        try
        {
            await StopServerWhileLockedAsync();
        }
        finally
        {
            _operationLock.Release();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Gracefully stops the managed server while the caller already holds
    /// _operationLock. Bannerlord Coop saves the world as part of "stop".
    /// </summary>
    private async Task<bool> StopServerWhileLockedAsync()
    {
        if (!_processManager.IsRunning)
        {
            ServerState = ServerState.Stopped;
            return true;
        }

        _nextRestartAt = null;
        _lastWarningKey = null;

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

        // Leave the completed session log monitor alive until the next Start
        // (or application disposal). The native stop command saves immediately
        // before exit, and the log tailer must be allowed to consume those final
        // "Successfully saved" lines so normal backup rotation still occurs.
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
    /// Sends the complete local WPF command line to the server.
    ///
    /// BCS Tool now owns command editing, caret movement, history, and Tab
    /// completion. Nothing is mirrored into Bannerlord until Send/Enter.
    /// </summary>
    private async Task SubmitCommandLineAsync()
    {
        var command =
            CommandText.Trim();

        if (command.Length == 0)
            return;

        // `stop` is not an ordinary fire-and-forget server command. It ends
        // the managed launcher process, so route it through BCS Tool's normal
        // graceful-stop lifecycle. That marks the exit as expected and keeps
        // the UI/log monitor in sync instead of reporting a crash.
        if (string.Equals(
                command,
                "stop",
                StringComparison.OrdinalIgnoreCase))
        {
            RememberSubmittedCommand(
                command);

            AddToolMessage(
                $"> {command}");

            CommandText = "";

            await StopServerAsync();

            return;
        }

        if (!await _processManager.SendCommandAsync(
                command,
                _lifetimeCts.Token))
        {
            return;
        }

        RememberSubmittedCommand(
            command);

        AddToolMessage(
            $"> {command}");

        CommandText = "";
    }


    /// <summary>
    /// Returns command-name completions loaded from the server log's structured
    /// @DS@ commands event.
    /// </summary>
    public IReadOnlyList<string> GetCommandCompletions(
        string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return Array.Empty<string>();

        return
            _availableCommands
                .Where(
                    command =>
                        command.StartsWith(
                            prefix,
                            StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }


    private readonly List<string> _commandHistory = new();
    private int _commandHistoryIndex = -1;
    private string _commandHistoryDraft = "";


    public void NavigateCommandHistory(
        int direction)
    {
        if (
            direction == 0 ||
            _commandHistory.Count == 0)
        {
            return;
        }

        if (_commandHistoryIndex < 0)
        {
            _commandHistoryDraft =
                CommandText;

            _commandHistoryIndex =
                _commandHistory.Count;
        }

        _commandHistoryIndex =
            Math.Clamp(
                _commandHistoryIndex + direction,
                0,
                _commandHistory.Count);

        CommandText =
            _commandHistoryIndex == _commandHistory.Count
                ? _commandHistoryDraft
                : _commandHistory[_commandHistoryIndex];
    }


    private void RememberSubmittedCommand(
        string command)
    {
        if (
            _commandHistory.Count == 0 ||
            !string.Equals(
                _commandHistory[^1],
                command,
                StringComparison.Ordinal))
        {
            _commandHistory.Add(
                command);
        }

        _commandHistoryIndex = -1;
        _commandHistoryDraft = "";
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


    /// <summary>
    /// Lets the user select BannerlordCoopServer.exe and persists that path
    /// independently from the restart settings.
    /// </summary>
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
                _serverLogMonitor.LogDirectory;

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

        CommandText = "";
        _availableCommands.Clear();

        OnPropertyChanged(nameof(PlayersHeaderText));
    }


    /// <summary>
    /// Synchronizes the WPF Players panel with the latest structured @DS@
    /// players snapshot from the server log.
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
    /// Receives live lines from the active coop-server-*.log. This is the
    /// authoritative runtime feed for visible console output and structured
    /// server state. Redirected stdout/stderr are intentionally not consulted here.
    /// </summary>
    private void ServerLogMonitor_LinesReceived(
        object? sender,
        ServerLogLinesEventArgs e)
    {
        var visibleLines =
            new List<string>(
                e.Lines.Count);

        var structuredEvents =
            new List<string>();

        var readyTextDetected =
            false;

        var successfulSaveCount =
            0;

        var launcherUnexpectedExitDetected =
            false;

        foreach (var line in e.Lines)
        {
            if (
                !readyTextDetected &&
                !string.IsNullOrWhiteSpace(
                    Settings.ReadyText) &&
                line.Contains(
                    Settings.ReadyText,
                    StringComparison.OrdinalIgnoreCase))
            {
                readyTextDetected = true;
            }

            if (
                line.Contains(
                    SuccessfulSaveMarker,
                    StringComparison.OrdinalIgnoreCase))
            {
                successfulSaveCount++;
            }

            if (
                !launcherUnexpectedExitDetected &&
                line.Contains(
                    LauncherUnexpectedExitMarker,
                    StringComparison.OrdinalIgnoreCase))
            {
                launcherUnexpectedExitDetected = true;
            }

            var markerIndex =
                line.IndexOf(
                    "@DS@",
                    StringComparison.Ordinal);

            if (markerIndex >= 0)
            {
                structuredEvents.Add(
                    line[(markerIndex + 4)..].Trim());
            }
            else
            {
                visibleLines.Add(line);
            }
        }

        _ = _dispatcher.BeginInvoke(
            new Action(() =>
            {
                AppendServerConsoleLines(
                    visibleLines);

                foreach (var json in structuredEvents)
                {
                    ProcessDedicatedServerEvent(
                        json);
                }

                for (
                    var saveIndex = 0;
                    saveIndex < successfulSaveCount;
                    saveIndex++)
                {
                    _ = CreateSaveBackupAfterSuccessfulSaveAsync();
                }

                if (readyTextDetected)
                {
                    MarkServerReadyFromLog();
                }

                if (launcherUnexpectedExitDetected)
                {
                    QueueCrashRecovery(
                        "Launcher reported that the hosted server exited unexpectedly.");
                }
            }));
    }


    private void AppendServerConsoleLines(
        IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            // The persisted server log keeps its original timestamp. The
            // in-app console omits the leading HH:mm:ss.fff field because the
            // session already presents entries in chronological order.
            var displayLine =
                RemoveServerLogTimestamp(line);

            ServerConsoleLines.Add(
                ServerConsoleLine.FromText(displayLine));
        }

        while (
            ServerConsoleLines.Count >
            MaximumServerConsoleLines)
        {
            ServerConsoleLines.RemoveAt(0);
        }
    }


    private static string RemoveServerLogTimestamp(
        string line)
    {
        // coop-server logs use a fixed-width prefix such as:
        //     01:01:03.015  [DedicatedServer] ...
        // Only strip it when the exact timestamp shape is present so ordinary
        // server messages that happen to begin with digits are left untouched.
        if (
            line.Length <= 12 ||
            line[2] != ':' ||
            line[5] != ':' ||
            line[8] != '.' ||
            !char.IsDigit(line[0]) ||
            !char.IsDigit(line[1]) ||
            !char.IsDigit(line[3]) ||
            !char.IsDigit(line[4]) ||
            !char.IsDigit(line[6]) ||
            !char.IsDigit(line[7]) ||
            !char.IsDigit(line[9]) ||
            !char.IsDigit(line[10]) ||
            !char.IsDigit(line[11]) ||
            !char.IsWhiteSpace(line[12]))
        {
            return line;
        }

        var contentStart = 12;

        while (
            contentStart < line.Length &&
            char.IsWhiteSpace(line[contentStart]))
        {
            contentStart++;
        }

        return line[contentStart..];
    }


    private void ProcessDedicatedServerEvent(
        string json)
    {
        try
        {
            using var document =
                JsonDocument.Parse(json);

            var root =
                document.RootElement;

            if (
                !root.TryGetProperty(
                    "ev",
                    out var eventProperty) ||
                eventProperty.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var eventName =
                eventProperty.GetString();

            switch (eventName)
            {
                case "players":
                    if (
                        root.TryGetProperty(
                            "list",
                            out var players) &&
                        _playerRosterTracker.ProcessPlayersList(
                            players))
                    {
                        RefreshPlayerRoster();
                    }
                    break;

                case "commands":
                    UpdateAvailableCommands(
                        root);
                    break;

                case "state":
                    ProcessDedicatedServerStateEvent(
                        root);
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore one malformed structured line. ServerLogMonitor emits only
            // newline-terminated records, and the human log remains on disk.
        }
    }


    private void ProcessDedicatedServerStateEvent(
        JsonElement root)
    {
        if (
            !root.TryGetProperty(
                "phase",
                out var phaseProperty) ||
            phaseProperty.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var phase =
            phaseProperty.GetString();

        SetReportedServerStatusFromLogPhase(
            phase);

        if (string.IsNullOrWhiteSpace(phase))
            return;

        if (
            string.Equals(
                phase,
                "serving",
                StringComparison.OrdinalIgnoreCase))
        {
            MarkServerReadyFromLog();
            return;
        }

        if (
            string.Equals(
                phase,
                "fatal",
                StringComparison.OrdinalIgnoreCase))
        {
            QueueCrashRecovery(
                "Dedicated server reported a fatal state while the launcher may still be open.");
        }
    }


    private void MarkServerReadyFromLog()
    {
        if (
            _serverReady ||
            _applicationClosing ||
            !_processManager.IsRunning)
        {
            return;
        }

        _serverReady = true;

        ServerState = ServerState.Ready;
        StatusMessage = "Server is online and ready.";

        AddToolMessage(
            $"SERVER READY detected: {Settings.ReadyText}");

        RecalculateNextRestart();
    }


    private void UpdateAvailableCommands(
        JsonElement root)
    {
        var commands =
            new List<string>();

        AddCommandArray(
            root,
            "builtin",
            commands);

        AddCommandArray(
            root,
            "game",
            commands);

        var normalized =
            commands
                .Where(
                    command =>
                        !string.IsNullOrWhiteSpace(command))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    command => command,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (_availableCommands.SequenceEqual(
                normalized,
                StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _availableCommands.Clear();
        _availableCommands.AddRange(
            normalized);

        AddToolMessage(
            $"Command autocomplete loaded: {_availableCommands.Count} commands.");
    }


    private static void AddCommandArray(
        JsonElement root,
        string propertyName,
        List<string> destination)
    {
        if (
            !root.TryGetProperty(
                propertyName,
                out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var command =
                item.GetString();

            if (!string.IsNullOrWhiteSpace(command))
            {
                destination.Add(command);
            }
        }
    }


    private void SetReportedServerStatusFromLogPhase(
        string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
            return;

        var status =
            phase.Trim().ToLowerInvariant() switch
            {
                "boot" => "Starting",
                "loading" => "Loading campaign",
                "serving" => "SERVING",
                "stopping" => "Stopping",
                _ => phase.Trim()
            };

        SetReportedServerStatus(
            status);
    }


    private void ResetCrashRecoverySignal()
    {
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
    /// Updates the display-only server-reported status and refreshes the bound
    /// ServerStateText property only when the text actually changes.
    /// </summary>
    private void SetReportedServerStatus(
        string status)
    {
        status =
            status.Trim();

        // Bannerlord can report descriptive states with a lowercase
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
                _reportedServerStatus,
                status,
                StringComparison.Ordinal))
        {
            return;
        }

        _reportedServerStatus =
            status;

        OnPropertyChanged(
            nameof(ServerStateText));
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
    ///   - the dedicated-server log proving that the hosted game server died
    ///     while the launcher process itself remained open.
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
            // never had to wait for the root launcher. Log-marker recovery
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

            await CreateCrashBackupAfterFailureAsync();

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
    /// Freezes the newest retained backup as a manual recovery point after a
    /// crash. Failure to create the snapshot never blocks normal crash restart.
    /// </summary>
    private async Task CreateCrashBackupAfterFailureAsync()
    {
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
                return;
            }

            AddToolMessage(
                $"Crash backup created from {crashBackup.SourceBackupName}: " +
                $"{Path.GetFileName(crashBackup.SavPath)} + " +
                $"{Path.GetFileName(crashBackup.JsonPath)}.");

            AddToolMessage(
                "The active save was not rolled back. If it later proves corrupted, " +
                $"stop the server and load {crashBackup.CrashBackupName} manually.");
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
    /// Uses the native graceful stop command, which saves the world before exit.
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
            return await StopServerWhileLockedAsync();
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

        _processManager.UnexpectedExit -= ProcessManager_UnexpectedExit;
        _serverLogMonitor.LinesReceived -= ServerLogMonitor_LinesReceived;

        _serverLogMonitor.Dispose();
        _processManager.Dispose();
        _operationLock.Dispose();
        _lifetimeCts.Dispose();
    }
}
