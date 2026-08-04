using System;
using System.Collections.Generic;
using System.Linq;

namespace BCSTool.Services;

/// <summary>
/// RGB foreground color stored in one virtual terminal cell.
///
/// IsDefault=true means "use the terminal's normal foreground color" rather
/// than a specific RGB value.
/// </summary>
public readonly record struct TerminalColor(
    byte R,
    byte G,
    byte B,
    bool IsDefault = false)
{
    public static TerminalColor Default =>
        new(0, 0, 0, IsDefault: true);
}


/// <summary>
/// Text rendition that belongs to a terminal cell.
///
/// v1.5 currently preserves foreground color and bold/intensity. The structure
/// can be expanded later for underline/background/inverse if Bannerlord begins
/// using those attributes.
/// </summary>
public readonly record struct TerminalCellStyle(
    TerminalColor Foreground,
    bool Bold)
{
    public static TerminalCellStyle Default =>
        new(
            TerminalColor.Default,
            Bold: false);
}


/// <summary>
/// Consecutive text cells that share one terminal style.
/// Grouping cells into runs makes WPF rendering substantially cheaper than
/// drawing every character separately.
/// </summary>
public sealed record TerminalTextRun(
    string Text,
    TerminalCellStyle Style);


/// <summary>
/// One rendered terminal row.
/// </summary>
public sealed class TerminalStyledLine
{
    public string PlainText { get; }
    public IReadOnlyList<TerminalTextRun> Runs { get; }

    public TerminalStyledLine(
        string plainText,
        IReadOnlyList<TerminalTextRun> runs)
    {
        PlainText = plainText;
        Runs = runs;
    }
}


/// <summary>
/// Immutable styled snapshot of the complete reconstructed terminal screen.
/// </summary>
public sealed class TerminalScreenSnapshot
{
    public static TerminalScreenSnapshot Empty { get; } =
        new(
            Array.Empty<TerminalStyledLine>(),
            cursorRow: 0,
            cursorColumn: 0);

    public IReadOnlyList<TerminalStyledLine> Lines { get; }

    /// <summary>
    /// Current terminal cursor position in zero-based character cells.
    ///
    /// v1.7 uses this to keep the WPF command box caret synchronized with
    /// Bannerlord's native console line editor.
    /// </summary>
    public int CursorRow { get; }
    public int CursorColumn { get; }

    public TerminalScreenSnapshot(
        IReadOnlyList<TerminalStyledLine> lines,
        int cursorRow,
        int cursorColumn)
    {
        Lines = lines;
        CursorRow = cursorRow;
        CursorColumn = cursorColumn;
    }

    public IReadOnlyList<string> PlainLines =>
        Lines
            .Select(line => line.PlainText)
            .ToArray();
}
