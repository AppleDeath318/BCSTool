using System.Text.RegularExpressions;
using System.Windows.Media;

namespace BCSTool.Models;

/// <summary>
/// One visible line in BCS Tool's own console. File timestamps are deliberately
/// excluded here; LogService adds them only to the persisted diagnostic log.
/// </summary>
public sealed class BcsToolConsoleLine
{
    private static readonly Regex ErrorTokenRegex =
        new(
            @"\b(?:ERROR|FAILED?|FAILURE|FATAL|CRASH(?:ED)?|ABORTED|INVALID|BLOCKED|UNEXPECTED)\b",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);

    private static readonly Regex WarningTokenRegex =
        new(
            @"\b(?:WARNING|WAITING|DISABLED|PAUSED|MISSING|OCCUPIED|PRESERVED|DENIED|KICKED)\b",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);

    private static readonly Regex SuccessTokenRegex =
        new(
            @"\b(?:READY|SUCCESSFULLY|STARTED|SAVED|ENABLED|CREATED|LOADED|DETECTED)\b",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);

    private static readonly Regex ActionTokenRegex =
        new(
            @"\b(?:RESTART|RESTARTING|SAVING|REFRESHING|FOLLOWING|SELECTED|SCHEDULED|COMMAND SENT)\b",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);

    private static readonly Brush ErrorBrush =
        CreateBrush(0xC6, 0x28, 0x28);

    private static readonly Brush WarningBrush =
        CreateBrush(0x9A, 0x67, 0x00);

    private static readonly Brush SuccessBrush =
        CreateBrush(0x2E, 0x7D, 0x32);

    private static readonly Brush ActionBrush =
        CreateBrush(0x00, 0x5A, 0x9C);

    private static readonly Brush NormalBrush =
        CreateBrush(0x20, 0x20, 0x20);

    private BcsToolConsoleLine(
        string text,
        Brush foreground)
    {
        Text = text;
        Foreground = foreground;
    }

    public string Text { get; }

    public Brush Foreground { get; }

    public static BcsToolConsoleLine FromMessage(
        string message)
    {
        return
            new BcsToolConsoleLine(
                $"[BCS Tool] {message}",
                ClassifyForeground(message));
    }

    private static Brush ClassifyForeground(
        string message)
    {
        if (
            ErrorTokenRegex.IsMatch(message) ||
            message.Contains(
                "could not",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "cannot ",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "did not ",
                StringComparison.OrdinalIgnoreCase))
        {
            return ErrorBrush;
        }

        if (
            WarningTokenRegex.IsMatch(message) ||
            message.Contains(
                "not found",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "not detected",
                StringComparison.OrdinalIgnoreCase))
        {
            return WarningBrush;
        }

        if (
            SuccessTokenRegex.IsMatch(message) ||
            message.Contains(
                "stopped gracefully",
                StringComparison.OrdinalIgnoreCase))
        {
            return SuccessBrush;
        }

        if (ActionTokenRegex.IsMatch(message))
            return ActionBrush;

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
