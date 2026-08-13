using System.Text.RegularExpressions;
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

    private static readonly Brush ErrorBrush =
        CreateBrush(0xC6, 0x28, 0x28);

    private static readonly Brush WarningBrush =
        CreateBrush(0x9A, 0x67, 0x00);

    private static readonly Brush SuccessBrush =
        CreateBrush(0x2E, 0x7D, 0x32);

    private static readonly Brush ConnectingBrush =
        CreateBrush(0x00, 0x78, 0x8A);

    private static readonly Brush DisconnectedBrush =
        CreateBrush(0xA1, 0x5C, 0x00);

    private static readonly Brush LauncherBrush =
        CreateBrush(0x6B, 0x6B, 0x6B);

    private static readonly Brush NormalBrush =
        CreateBrush(0x00, 0x00, 0x00);

    private ServerConsoleLine(
        string text,
        Brush foreground)
    {
        Text = text;
        Foreground = foreground;
    }

    public string Text { get; }

    public Brush Foreground { get; }

    public static ServerConsoleLine FromText(
        string text)
    {
        return
            new ServerConsoleLine(
                text,
                ClassifyForeground(text));
    }

    private static Brush ClassifyForeground(
        string line)
    {
        // Severe conditions take priority over every presentation category.
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
            return ErrorBrush;
        }

        if (WarningTokenRegex.IsMatch(line))
            return WarningBrush;

        if (
            line.Contains(
                "Successfully saved",
                StringComparison.OrdinalIgnoreCase))
        {
            return SuccessBrush;
        }

        if (
            line.Contains(
                "player connecting",
                StringComparison.OrdinalIgnoreCase))
        {
            return ConnectingBrush;
        }

        if (
            line.Contains(
                "player disconnected",
                StringComparison.OrdinalIgnoreCase))
        {
            return DisconnectedBrush;
        }

        // Launcher diagnostics are intentionally subdued even when they contain
        // words such as "ready" in explanatory/autocomplete messages.
        if (
            line.Contains(
                "[launcher]",
                StringComparison.OrdinalIgnoreCase))
        {
            return LauncherBrush;
        }

        if (
            line.Contains(
                "SERVING",
                StringComparison.OrdinalIgnoreCase) ||
            line.Contains(
                "server ready",
                StringComparison.OrdinalIgnoreCase) ||
            line.Contains(
                "coop server up, waiting for clients",
                StringComparison.OrdinalIgnoreCase))
        {
            return SuccessBrush;
        }

        return NormalBrush;
    }

    private static Brush CreateBrush(
        byte red,
        byte green,
        byte blue)
    {
        var brush =
            new SolidColorBrush(
                Color.FromRgb(
                    red,
                    green,
                    blue));

        brush.Freeze();
        return brush;
    }
}
