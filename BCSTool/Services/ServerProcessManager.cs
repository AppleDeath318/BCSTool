using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BCSTool.Services;

/// <summary>
/// Owns the BannerlordCoopServer.exe process through Windows ConPTY.
///
/// v1.2 replaces normal RedirectStandardOutput with a pseudo console.
///
/// Benefits:
///
/// - Commands are still sent directly; no SendKeys/focus required.
/// - Bannerlord believes it is connected to a real terminal.
/// - BCS Tool receives VT/ANSI cursor and screen-control sequences.
/// - VirtualTerminalScreen reconstructs the full two-dimensional terminal UI.
/// - The native Players pane can therefore be inspected instead of relying
///   only on delayed `players=N` pulse messages.
///
/// The existing Windows Job Object is retained for orphan-process cleanup.
/// </summary>
public sealed class ServerProcessManager : IDisposable
{
    // Initial fallback dimensions. As soon as the WPF Server Console has a
    // real viewport size, MainWindow replaces these with dimensions measured
    // from the visible console area.
    private const short DefaultTerminalColumns = 160;
    private const short DefaultTerminalRows = 50;

    // Keep practical limits so an accidentally tiny/huge WPF layout cannot
    // request unusable ConPTY dimensions.
    private const short MinimumTerminalColumns = 80;
    private const short MinimumTerminalRows = 12;
    private const short MaximumTerminalColumns = 300;
    private const short MaximumTerminalRows = 120;

    private readonly LogService _logService;
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly object _resizeSync = new();

    private readonly VirtualTerminalScreen _terminal;

    private short _terminalColumns =
        DefaultTerminalColumns;

    private short _terminalRows =
        DefaultTerminalRows;

    private ConPtySession? _session;
    private Process? _process;
    private ManagedJobObject? _job;

    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    private volatile bool _expectedExit;

    // Full-screen terminal redraws arrive as a burst of many VT writes.
    //
    // v1.4 published on a fixed ~80 ms cadence, which could expose a frame
    // after the old header had been erased but before the new header had been
    // drawn. v1.4.1 tracks a monotonically increasing change version and
    // prefers to publish after a brief quiet period.
    private int _snapshotPublisherRunning;
    private long _terminalChangeVersion;
    private long _lastPublishedTerminalVersion;


    /// <summary>
    /// Raw ConPTY text chunks.
    ///
    /// MainViewModel uses these mainly for fast readiness detection. The
    /// visible console itself comes from TerminalScreenUpdated.
    /// </summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>
    /// Raised after VT/ANSI data has been rendered into a stable screen.
    /// </summary>
    public event EventHandler<TerminalScreenUpdatedEventArgs>?
        TerminalScreenUpdated;

    public event EventHandler? UnexpectedExit;


    public bool IsRunning =>
        _process is { HasExited: false };


    public int? ProcessId =>
        IsRunning ? _process?.Id : null;


    public DateTime? StartedAt { get; private set; }


    public ServerProcessManager(LogService logService)
    {
        _logService = logService;

        _terminal =
            new VirtualTerminalScreen(
                _terminalColumns,
                _terminalRows);
    }


    /// <summary>
    /// Looks for an already-running BannerlordCoopServer process outside the
    /// current managed session.
    /// </summary>
    public bool HasExternalServerProcess(string executablePath)
    {
        var processName =
            Path.GetFileNameWithoutExtension(
                executablePath);

        foreach (
            var process in
            Process.GetProcessesByName(processName))
        {
            try
            {
                if (
                    _process is not null &&
                    process.Id == _process.Id)
                {
                    continue;
                }

                return true;
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }


    /// <summary>
    /// Creates ConPTY and launches Bannerlord inside it.
    ///
    /// The returned true value means the Windows process exists. The game
    /// server is not considered Ready until MainViewModel sees ReadyText in
    /// the terminal stream.
    /// </summary>
    public Task<bool> StartAsync(
        string executablePath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (IsRunning)
            return Task.FromResult(true);

        if (!File.Exists(executablePath))
            return Task.FromResult(false);

        CleanupPreviousSession();

        _terminal.Clear();

        _expectedExit = false;

        try
        {
            _session =
                ConPtySession.Start(
                    executablePath,
                    workingDirectory,
                    _terminalColumns,
                    _terminalRows);

            _process = _session.Process;
            _process.EnableRaisingEvents = true;

            _process.Exited += Process_Exited;

            StartedAt = DateTime.Now;

            // Put the complete server tree in our Windows Job Object.
            _job = new ManagedJobObject();

            if (!_job.TryAssign(_process))
            {
                _logService.Write(
                    "Warning: server process could not be assigned to the managed job object.");
            }

            // Read the synchronous anonymous-pipe handle on a dedicated
            // background Task so the WPF UI thread never blocks.
            _readerCts = new CancellationTokenSource();

            _readerTask =
                Task.Run(
                    () => ReadTerminalLoop(
                        _readerCts.Token),
                    CancellationToken.None);

            _logService.Write(
                $"Started ConPTY server PID {_process.Id}: {executablePath}");

            PublishTerminalSnapshot();

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logService.Write(
                $"ConPTY server start failed: {ex}");

            CleanupPreviousSession();

            return Task.FromResult(false);
        }
    }



    /// <summary>
    /// Resizes both the reconstructed terminal and the live Windows ConPTY
    /// session.
    ///
    /// This method also works before the server is started. In that case the
    /// calculated dimensions are remembered and used by the next StartAsync.
    /// </summary>
    public bool ResizeTerminal(
        int columns,
        int rows)
    {
        var newColumns =
            (short)Math.Clamp(
                columns,
                MinimumTerminalColumns,
                MaximumTerminalColumns);

        var newRows =
            (short)Math.Clamp(
                rows,
                MinimumTerminalRows,
                MaximumTerminalRows);

        lock (_resizeSync)
        {
            if (
                newColumns == _terminalColumns &&
                newRows == _terminalRows)
            {
                return true;
            }

            var oldColumns =
                _terminalColumns;

            var oldRows =
                _terminalRows;

            try
            {
                // Resize our local screen first so any VT output produced
                // immediately by ResizePseudoConsole already has the correct
                // target geometry.
                _terminal.Resize(
                    newColumns,
                    newRows);

                if (_session is not null)
                {
                    _session.Resize(
                        newColumns,
                        newRows);
                }

                _terminalColumns =
                    newColumns;

                _terminalRows =
                    newRows;

                PublishTerminalSnapshot();

                _logService.Write(
                    $"ConPTY resized to {_terminalColumns}x{_terminalRows}.");

                return true;
            }
            catch (Exception ex)
            {
                // Restore the previous emulator geometry if Windows rejected
                // the pseudo-console resize.
                _terminal.Resize(
                    oldColumns,
                    oldRows);

                _logService.Write(
                    $"ConPTY resize failed: {ex.Message}");

                return false;
            }
        }
    }



    /// <summary>
    /// Sends raw terminal input through ConPTY without automatically adding
    /// Enter and without logging every keystroke.
    ///
    /// This is used by the interactive v1.7 command proxy:
    ///
    ///     normal text
    ///     Tab
    ///     arrows
    ///     Backspace/Delete
    ///     Home/End
    ///     Escape
    ///
    /// Bannerlord's own console line editor remains the source of truth for
    /// autocomplete and command history.
    /// </summary>
    public async Task<bool> SendRawInputAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        if (
            string.IsNullOrEmpty(input) ||
            !IsRunning ||
            _session is null)
        {
            return false;
        }

        await _inputLock.WaitAsync(
            cancellationToken);

        try
        {
            _session.InputWriter.Write(input);
            _session.InputWriter.Flush();

            return true;
        }
        catch (Exception ex)
        {
            _logService.Write(
                $"Could not send raw ConPTY input: {ex.Message}");

            return false;
        }
        finally
        {
            _inputLock.Release();
        }
    }


    /// <summary>
    /// Sends one command through ConPTY input.
    ///
    /// Enter is represented by carriage return (`\r`) in a terminal.
    /// </summary>
    public async Task<bool> SendCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        if (
            !IsRunning ||
            _session is null)
        {
            return false;
        }

        await _inputLock.WaitAsync(
            cancellationToken);

        try
        {
            // Do not use WriteLine here. A terminal Enter key is CR.
            _session.InputWriter.Write(command);
            _session.InputWriter.Write('\r');
            _session.InputWriter.Flush();

            _logService.Write(
                $"Command sent: {command}");

            return true;
        }
        catch (Exception ex)
        {
            _logService.Write(
                $"Could not send command '{command}': {ex.Message}");

            return false;
        }
        finally
        {
            _inputLock.Release();
        }
    }


    /// <summary>
    /// Sends the native "stop" command and waits for the managed process.
    /// </summary>
    public async Task<bool> StopGracefullyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (
            !IsRunning ||
            _process is null)
        {
            return true;
        }

        _expectedExit = true;

        if (!await SendCommandAsync(
                "stop",
                cancellationToken))
        {
            _expectedExit = false;
            return false;
        }

        try
        {
            await _process
                .WaitForExitAsync(cancellationToken)
                .WaitAsync(
                    timeout,
                    cancellationToken);

            return true;
        }
        catch (TimeoutException)
        {
            _logService.Write(
                $"Server did not exit within {timeout.TotalSeconds:0} seconds after stop.");

            _expectedExit = false;

            return false;
        }
        catch (OperationCanceledException)
        {
            _expectedExit = false;
            throw;
        }
    }


    /// <summary>
    /// Clears BCS Tool's reconstructed terminal display.
    ///
    /// This does not send a clear command to Bannerlord. The server may redraw
    /// its terminal UI immediately afterward.
    /// </summary>
    public void ClearTerminalScreen()
    {
        _terminal.Clear();
        PublishTerminalSnapshot();
    }


    /// <summary>
    /// Emergency cleanup used only after abnormal process failure.
    /// </summary>
    public void ForceCleanupManagedTree()
    {
        try
        {
            _expectedExit = true;

            _job?.Terminate();

            _logService.Write(
                "Managed server process tree force-cleaned.");
        }
        catch (Exception ex)
        {
            _logService.Write(
                $"Managed process-tree cleanup failed: {ex.Message}");
        }
    }


    public void MarkUnexpectedExitHandlingComplete()
    {
        _expectedExit = false;
    }


    private void Process_Exited(
        object? sender,
        EventArgs e)
    {
        _logService.Write(
            $"Server process exited. Expected={_expectedExit}.");

        if (!_expectedExit)
        {
            UnexpectedExit?.Invoke(
                this,
                EventArgs.Empty);
        }
    }


    /// <summary>
    /// Reads ConPTY output, feeds the VT parser, and schedules screen updates.
    ///
    /// The raw chunk is also raised through OutputReceived so readiness text
    /// can be detected without waiting for the next screen snapshot.
    /// </summary>
    private void ReadTerminalLoop(
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_session is null)
                    return;

                int count;

                try
                {
                    count =
                        _session.OutputReader.Read(
                            buffer,
                            0,
                            buffer.Length);
                }
                catch (
                    ObjectDisposedException)
                {
                    return;
                }

                if (count <= 0)
                    return;

                var chunk =
                    new string(
                        buffer,
                        0,
                        count);

                // Fast text-level consumers such as readiness detection.
                OutputReceived?.Invoke(
                    this,
                    chunk);

                // Reconstruct the visible terminal screen.
                _terminal.Feed(chunk);

                Interlocked.Increment(
                    ref _terminalChangeVersion);

                ScheduleSnapshotPublisher();
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logService.Write(
                    $"ConPTY terminal reader stopped unexpectedly: {ex}");
            }
        }
        finally
        {
            PublishTerminalSnapshot();
        }
    }


    /// <summary>
    /// Coalesces a burst of terminal writes and tries to publish after the
    /// stream has been quiet for a short interval.
    ///
    /// A maximum latency is retained so very busy server output still updates
    /// regularly.
    /// </summary>
    private void ScheduleSnapshotPublisher()
    {
        if (
            Interlocked.CompareExchange(
                ref _snapshotPublisherRunning,
                1,
                0) != 0)
        {
            return;
        }

        _ =
            Task.Run(
                async () =>
                {
                    try
                    {
                        var burstStarted =
                            Stopwatch.GetTimestamp();

                        var observedVersion =
                            Volatile.Read(
                                ref _terminalChangeVersion);

                        while (true)
                        {
                            // Most Bannerlord terminal redraw bursts finish
                            // well inside this interval.
                            await Task.Delay(45);

                            var currentVersion =
                                Volatile.Read(
                                    ref _terminalChangeVersion);

                            var quiet =
                                currentVersion ==
                                observedVersion;

                            var elapsed =
                                Stopwatch.GetElapsedTime(
                                    burstStarted);

                            // Prefer a complete/quiescent frame, but never
                            // withhold a continuously busy console for more
                            // than about a quarter of a second.
                            if (
                                quiet ||
                                elapsed >=
                                    TimeSpan.FromMilliseconds(250))
                            {
                                PublishTerminalSnapshot();

                                burstStarted =
                                    Stopwatch.GetTimestamp();

                                observedVersion =
                                    currentVersion;

                                // If nothing arrived while we published,
                                // this burst is complete.
                                await Task.Delay(1);

                                if (
                                    Volatile.Read(
                                        ref _terminalChangeVersion) ==
                                    observedVersion)
                                {
                                    break;
                                }

                                continue;
                            }

                            observedVersion =
                                currentVersion;
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(
                            ref _snapshotPublisherRunning,
                            0);

                        // Handle the small race where terminal data arrives
                        // just as the publisher exits.
                        ScheduleSnapshotPublisherIfChanged();
                    }
                });
    }


    private void ScheduleSnapshotPublisherIfChanged()
    {
        if (
            IsRunning &&
            Volatile.Read(
                ref _terminalChangeVersion) !=
            Volatile.Read(
                ref _lastPublishedTerminalVersion))
        {
            ScheduleSnapshotPublisher();
        }
    }


    private void PublishTerminalSnapshot()
    {
        var snapshot =
            _terminal.Snapshot();

        Interlocked.Exchange(
            ref _lastPublishedTerminalVersion,
            Volatile.Read(
                ref _terminalChangeVersion));

        TerminalScreenUpdated?.Invoke(
            this,
            new TerminalScreenUpdatedEventArgs(
                snapshot));
    }


    /// <summary>
    /// Disposes an old ConPTY session before a fresh server is created.
    ///
    /// ManagedJobObject uses KILL_ON_JOB_CLOSE, so disposing the job is also
    /// a final orphan-process safety net.
    /// </summary>
    private void CleanupPreviousSession()
    {
        try
        {
            _readerCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _session?.Dispose();
        }
        catch
        {
        }

        try
        {
            _job?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (_process is not null)
            {
                _process.Exited -= Process_Exited;
                _process.Dispose();
            }
        }
        catch
        {
        }

        _readerCts?.Dispose();

        _readerCts = null;
        _readerTask = null;
        _session = null;
        _job = null;
        _process = null;
    }


    public void Dispose()
    {
        _inputLock.Dispose();

        CleanupPreviousSession();
    }
}
