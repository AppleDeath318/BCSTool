using System;
using System.IO;

namespace BCSTool.Services;

/// <summary>
/// Very small file logger used for diagnostics.
///
/// Logs are intentionally kept separate from the visible server console.
/// They are stored under the current Windows user's LocalAppData folder:
///     %LOCALAPPDATA%\BCSServerTool\Logs
///
/// The legacy folder name is intentionally retained so existing diagnostic
/// history remains in one place after the project/namespace rename.
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
            "BCSServerTool",
            "Logs");

        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Appends one timestamped line to today's log file.
    /// The lock prevents two threads from writing the same file concurrently.
    /// </summary>
    public void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        var file = Path.Combine(LogDirectory, $"BCSTool-{DateTime.Now:yyyy-MM-dd}.log");

        lock (_sync)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }
}
