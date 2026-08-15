using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace BCSTool.Models;

/// <summary>
/// One visible line in the log-driven Server Console. The dedicated server
/// log does not preserve ANSI/VT color escapes, so BCS Tool recreates the
/// useful semantic colors from the line content instead.
/// </summary>
public sealed class ServerConsoleLine
{
    private static readonly Regex WarningTokenRegex =
        new(
            @"\b(?:WARNING|WARN)\b",
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);

    private ServerConsoleLine(
        string text,
        string foregroundResourceKey)
    {
        Text = text;
        ForegroundResourceKey = foregroundResourceKey;
    }

    public string Text { get; }

    private string ForegroundResourceKey { get; }

    /// <summary>
    /// Resolves the brush from the active theme whenever WPF evaluates the
    /// binding. MainWindow refreshes existing rows after a live theme switch.
    /// </summary>
    public Brush Foreground =>
        Application.Current?.TryFindResource(
            ForegroundResourceKey) as Brush
        ?? Brushes.Black;

    public static ServerConsoleLine FromText(
        string text)
    {
        return
            new ServerConsoleLine(
                text,
                ClassifyForegroundResource(text));
    }

    private static string ClassifyForegroundResource(
        string line)
    {
        if (
            line.Contains(
                "FATAL",
                StringComparison.OrdinalIgnoreCase) ||
            line.Contains(
                "ERROR",
                StringComparison.Ordinal) ||
            line.Contains(
                "Exception",
                StringComparison.OrdinalIgnoreCase))
        {
            return "ConsoleErrorBrush";
        }

        if (WarningTokenRegex.IsMatch(line))
            return "ConsoleWarningBrush";

        if (
            line.Contains(
                "Successfully saved",
                StringComparison.OrdinalIgnoreCase))
        {
            return "ConsoleSuccessBrush";
        }

        if (
            line.Contains(
                "player connecting",
                StringComparison.OrdinalIgnoreCase))
        {
            return "ConsoleConnectingBrush";
        }

        if (
            line.Contains(
                "player disconnected",
                StringComparison.OrdinalIgnoreCase))
        {
            return "ConsoleDisconnectedBrush";
        }

        if (
            line.Contains(
                "[launcher]",
                StringComparison.OrdinalIgnoreCase))
        {
            return "ConsoleMutedBrush";
        }

        if (
            line.Contains(
                "SERVING",
                StringComparison.OrdinalIgnoreCase) ||
            line.Contains(
                "server ready",
                StringComparison.OrdinalIgnoreCase))
        {
            return "ConsoleSuccessBrush";
        }

        return "ConsoleForegroundBrush";
    }
}
