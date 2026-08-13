using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BCSTool.Services;

/// <summary>
/// Follows the active Bannerlord Coop dedicated-server log in real time.
///
/// The server writes information to the .log file that is not present in the
/// console-style output, including the complete @DS@ player and command snapshots.
/// BCS Tool therefore treats this file as its authoritative server-console and
/// structured-state feed.
/// </summary>
public sealed class ServerLogMonitor : IDisposable
{
    private const string ServerLogPattern = "coop-server-*.log";
    private const int MaximumBatchSize = 250;

    private readonly LogService _logService;
    private readonly object _sync = new();

    private HashSet<string> _logsBeforeStart =
        new(StringComparer.OrdinalIgnoreCase);

    private DateTime _preparedAtUtc;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public ServerLogMonitor(
        LogService logService,
        CoopConfigService coopConfigService)
    {
        _logService = logService;

        LogDirectory =
            Path.Combine(
                coopConfigService.CoopDataDirectory,
                "DedicatedServer",
                "logs");
    }

    public string LogDirectory { get; }

    public string? ActiveLogPath { get; private set; }

    public event EventHandler<ServerLogLinesEventArgs>? LinesReceived;

    /// <summary>
    /// Captures the existing log files immediately before a new server process
    /// is launched. The next file created by Bannerlord is then unambiguous.
    /// </summary>
    public void PrepareForServerStart()
    {
        StopMonitoring();

        _preparedAtUtc = DateTime.UtcNow;
        ActiveLogPath = null;

        try
        {
            _logsBeforeStart =
                Directory.Exists(LogDirectory)
                    ? Directory
                        .EnumerateFiles(
                            LogDirectory,
                            ServerLogPattern,
                            SearchOption.TopDirectoryOnly)
                        .ToHashSet(
                            StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logsBeforeStart =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            _logService.Write(
                $"Could not snapshot existing server logs: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts following the log created by the server process that was just
    /// launched. The monitor reads from the beginning of that file so the BCS
    /// Tool Server Console contains the complete current startup/session log.
    /// </summary>
    public void StartMonitoring(
        CancellationToken lifetimeToken)
    {
        lock (_sync)
        {
            StopMonitoringCore();

            _monitorCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    lifetimeToken);

            var token =
                _monitorCts.Token;

            _monitorTask =
                Task.Run(
                    () => MonitorLoopAsync(token),
                    CancellationToken.None);
        }
    }

    public void StopMonitoring()
    {
        lock (_sync)
        {
            StopMonitoringCore();
        }
    }

    private void StopMonitoringCore()
    {
        try
        {
            _monitorCts?.Cancel();
        }
        catch
        {
        }

        _monitorCts?.Dispose();
        _monitorCts = null;
        _monitorTask = null;
        ActiveLogPath = null;
    }

    private async Task MonitorLoopAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var logPath =
                await WaitForCurrentServerLogAsync(
                    cancellationToken);

            if (logPath is null)
                return;

            ActiveLogPath = logPath;

            _logService.Write(
                $"Following server log: {logPath}");

            await TailLogAsync(
                logPath,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logService.Write(
                    $"Server log monitor stopped unexpectedly: {ex}");
            }
        }
    }

    private async Task<string?> WaitForCurrentServerLogAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (Directory.Exists(LogDirectory))
                {
                    var logs =
                        Directory
                            .EnumerateFiles(
                                LogDirectory,
                                ServerLogPattern,
                                SearchOption.TopDirectoryOnly)
                            .Select(
                                path =>
                                    new FileInfo(path))
                            .OrderByDescending(
                                file => file.CreationTimeUtc)
                            .ThenByDescending(
                                file => file.LastWriteTimeUtc)
                            .ToArray();

                    // Normal path: Bannerlord creates one fresh log per server
                    // launch, so prefer a file that did not exist at Prepare.
                    var newLog =
                        logs.FirstOrDefault(
                            file =>
                                !_logsBeforeStart.Contains(
                                    file.FullName));

                    if (newLog is not null)
                        return newLog.FullName;

                    // Filesystems can report unusual creation metadata. After a
                    // short grace period, accept the newest file that has been
                    // written since this launch was prepared.
                    if (
                        DateTime.UtcNow - _preparedAtUtc >=
                            TimeSpan.FromSeconds(3))
                    {
                        var recentLog =
                            logs.FirstOrDefault(
                                file =>
                                    file.LastWriteTimeUtc >=
                                    _preparedAtUtc.AddSeconds(-1));

                        if (recentLog is not null)
                            return recentLog.FullName;
                    }
                }
            }
            catch (IOException)
            {
                // The server may be in the middle of creating/rotating the file.
            }
            catch (UnauthorizedAccessException ex)
            {
                _logService.Write(
                    $"Cannot access server log directory: {ex.Message}");

                return null;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                cancellationToken);
        }

        return null;
    }

    private async Task TailLogAsync(
        string logPath,
        CancellationToken cancellationToken)
    {
        await using var stream =
            new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                options:
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);

        var byteBuffer =
            new byte[64 * 1024];

        var decoder =
            Encoding.UTF8.GetDecoder();

        var charBuffer =
            new char[
                Encoding.UTF8.GetMaxCharCount(
                    byteBuffer.Length)];

        var pending =
            new StringBuilder();

        var firstText = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            var count =
                await stream.ReadAsync(
                    byteBuffer.AsMemory(
                        0,
                        byteBuffer.Length),
                    cancellationToken);

            if (count <= 0)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    cancellationToken);

                continue;
            }

            var charCount =
                decoder.GetChars(
                    byteBuffer,
                    0,
                    count,
                    charBuffer,
                    0,
                    flush: false);

            pending.Append(
                charBuffer,
                0,
                charCount);

            if (
                firstText &&
                pending.Length > 0)
            {
                firstText = false;

                if (pending[0] == '\uFEFF')
                {
                    pending.Remove(
                        0,
                        1);
                }
            }

            PublishCompleteLines(
                pending);
        }
    }

    /// <summary>
    /// Emits only newline-terminated records. This is important for very large
    /// @DS@ command/player JSON lines: reading a file while the logger is still
    /// writing one of those lines must not expose a truncated JSON fragment.
    /// </summary>
    private void PublishCompleteLines(
        StringBuilder pending)
    {
        var batch =
            new List<string>(
                MaximumBatchSize);

        var lineStart = 0;

        for (
            var index = 0;
            index < pending.Length;
            index++)
        {
            if (pending[index] != '\n')
                continue;

            var lineLength =
                index - lineStart;

            if (
                lineLength > 0 &&
                pending[index - 1] == '\r')
            {
                lineLength--;
            }

            batch.Add(
                pending.ToString(
                    lineStart,
                    lineLength));

            lineStart =
                index + 1;

            if (batch.Count >= MaximumBatchSize)
            {
                LinesReceived?.Invoke(
                    this,
                    new ServerLogLinesEventArgs(
                        batch.ToArray()));

                batch.Clear();
            }
        }

        if (lineStart > 0)
        {
            pending.Remove(
                0,
                lineStart);
        }

        if (batch.Count > 0)
        {
            LinesReceived?.Invoke(
                this,
                new ServerLogLinesEventArgs(
                    batch.ToArray()));
        }
    }


    public void Dispose()
    {
        StopMonitoring();
    }
}

public sealed class ServerLogLinesEventArgs : EventArgs
{
    public ServerLogLinesEventArgs(
        IReadOnlyList<string> lines)
    {
        Lines = lines;
    }

    public IReadOnlyList<string> Lines { get; }
}
