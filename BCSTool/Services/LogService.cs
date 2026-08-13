using System;
using System.IO;

namespace BCSTool.Services;

/// <summary>
/// Very small file logger used for diagnostics.
///
/// Logs are intentionally kept separate from the visible server console.
/// They are stored under the current Windows user's LocalAppData folder:
///     %LOCALAPPDATA%\BCS Tool\Logs
///
/// A different file is used each day.
/// </summary>
public sealed class LogService
{
    private readonly object _sync = new();

    public string LogDirectory { get; }

    public LogService()
    {
        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BCS Tool",
            "Logs");

        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Appends one timestamped line to today's log file. The timestamp mirrors
    /// the server-log prefix and is intentionally not included in the in-app
    /// BCS Tool Console.
    /// The lock prevents two threads from writing the same file concurrently.
    /// </summary>
    public void Write(string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss.fff}  {message}";
        var file = Path.Combine(LogDirectory, $"BCSTool-{now:yyyy-MM-dd}.log");

        lock (_sync)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }
}
