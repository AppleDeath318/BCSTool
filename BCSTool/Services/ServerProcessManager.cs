using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BCSTool.Services;

/// <summary>
/// Owns the BannerlordCoopServer.exe process.
///
/// Server output/state is sourced from the dedicated-server .log by
/// ServerLogMonitor. This class launches the server with redirected standard
/// input and drains stdout/stderr only to prevent pipe back-pressure.
///
/// The existing Windows Job Object is retained for orphan-process cleanup and
/// Process.Exited remains an independent crash fallback.
/// </summary>
public sealed class ServerProcessManager : IDisposable
{
    private readonly LogService _logService;
    private readonly SemaphoreSlim _inputLock = new(1, 1);

    private Process? _process;
    private ManagedJobObject? _job;

    private CancellationTokenSource? _outputDrainCts;

    private volatile bool _expectedExit;

    public event EventHandler? UnexpectedExit;

    public bool IsRunning =>
        _process is { HasExited: false };

    public int? ProcessId =>
        IsRunning ? _process?.Id : null;

    public DateTime? StartedAt { get; private set; }

    public ServerProcessManager(LogService logService)
    {
        _logService = logService;
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
    /// Launches Bannerlord with redirected standard input.
    ///
    /// stdout/stderr are also redirected and continuously drained because the
    /// visible Server Console is sourced from the dedicated-server .log. This
    /// prevents a full output pipe from blocking the server while keeping the
    /// process window hidden.
    ///
    /// A true result means only that the Windows process was created. Runtime
    /// readiness remains authoritative from ServerLogMonitor/MainViewModel.
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

        cancellationToken.ThrowIfCancellationRequested();

        CleanupPreviousSession();
        _expectedExit = false;

        try
        {
            var startInfo =
                new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            _process =
                new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

            _process.Exited += Process_Exited;

            if (!_process.Start())
            {
                _process.Exited -= Process_Exited;
                _process.Dispose();
                _process = null;
                return Task.FromResult(false);
            }

            // Commands are line-oriented in redirected-stdin mode. AutoFlush
            // makes each completed command immediately visible to the launcher.
            _process.StandardInput.AutoFlush = true;

            StartedAt = DateTime.Now;

            // Put the complete server tree in our Windows Job Object.
            _job = new ManagedJobObject();

            if (!_job.TryAssign(_process))
            {
                _logService.Write(
                    "Warning: server process could not be assigned to the managed job object.");
            }

            // The .log file is the authoritative output channel now. Drain
            // redirected stdout/stderr in the background so neither pipe can
            // fill and stall the launcher/server process tree.
            _outputDrainCts = new CancellationTokenSource();

            _ = DrainOutputAsync(
                _process.StandardOutput,
                "stdout",
                _outputDrainCts.Token);

            _ = DrainOutputAsync(
                _process.StandardError,
                "stderr",
                _outputDrainCts.Token);

            _logService.Write(
                $"Started redirected-stdin server PID {_process.Id}: {executablePath}");

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logService.Write(
                $"Redirected-stdin server start failed: {ex}");

            CleanupPreviousSession();

            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Sends one complete command through redirected stdin.
    ///
    /// BCS Tool owns command-line editing and autocomplete locally. Only the
    /// finished command is written here.
    /// </summary>
    public async Task<bool> SendCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        if (
            !IsRunning ||
            _process is null)
        {
            return false;
        }

        await _inputLock.WaitAsync(
            cancellationToken);

        try
        {
            await _process.StandardInput.WriteLineAsync(
                command.AsMemory(),
                cancellationToken);

            await _process.StandardInput.FlushAsync(
                cancellationToken);

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

    private async Task DrainOutputAsync(
        StreamReader reader,
        string streamName,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count =
                    await reader.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken);

                if (count <= 0)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logService.Write(
                    $"Redirected {streamName} drain stopped unexpectedly: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Disposes the previous managed server session before a fresh server is
    /// created. ManagedJobObject uses KILL_ON_JOB_CLOSE, so disposing the job
    /// remains the final orphan-process safety net.
    /// </summary>
    private void CleanupPreviousSession()
    {
        try
        {
            _outputDrainCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            if (_process is not null)
            {
                try
                {
                    _process.StandardInput.Close();
                }
                catch
                {
                }
            }
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

        _outputDrainCts?.Dispose();

        _outputDrainCts = null;
        _job = null;
        _process = null;
    }

    public void Dispose()
    {
        _inputLock.Dispose();

        CleanupPreviousSession();
    }
}
