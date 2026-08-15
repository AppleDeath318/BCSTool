using System.Windows;
using System.Windows.Media;

namespace BCSTool.Models;

public enum BcsToolMessageType
{
    Information,
    Action,
    Success,
    Warning,
    Error
}

/// <summary>
/// One visible line in BCS Tool's own console. File timestamps are deliberately
/// excluded here; LogService adds them only to the persisted diagnostic log.
/// </summary>
public sealed class BcsToolConsoleLine
{
    private BcsToolConsoleLine(
        string text,
        string foregroundResourceKey)
    {
        Text = text;
        ForegroundResourceKey = foregroundResourceKey;
    }

    public string Text { get; }

    private string ForegroundResourceKey { get; }

    public Brush Foreground =>
        Application.Current?.TryFindResource(
            ForegroundResourceKey) as Brush
        ?? Brushes.Black;

    public static BcsToolConsoleLine FromMessage(
        string message,
        BcsToolMessageType messageType)
    {
        return
            new BcsToolConsoleLine(
                $"[BCS Tool] {message}",
                GetForegroundResourceKey(messageType));
    }

    private static string GetForegroundResourceKey(
        BcsToolMessageType messageType)
    {
        return messageType switch
        {
            BcsToolMessageType.Information => "ConsoleForegroundBrush",
            BcsToolMessageType.Action => "ConsoleActionBrush",
            BcsToolMessageType.Success => "ConsoleSuccessBrush",
            BcsToolMessageType.Warning => "ConsoleWarningBrush",
            BcsToolMessageType.Error => "ConsoleErrorBrush",
            _ => throw new ArgumentOutOfRangeException(
                nameof(messageType),
                messageType,
                "Unsupported BCS Tool message type.")
        };
    }
}
