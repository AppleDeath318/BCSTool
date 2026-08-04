using System;
using System.Collections.Generic;
using System.Text;

namespace BCSTool.Services;

/// <summary>
/// Small VT/ANSI terminal screen emulator used with Windows ConPTY.
///
/// ConPTY does not return a ready-made two-dimensional screen. It returns a
/// byte/text stream containing terminal control sequences such as:
///
///     ESC [ row ; column H     move cursor
///     ESC [ 2 J                clear screen
///     ESC [ K                  erase line
///
/// A real terminal (Windows Terminal, cmd, etc.) interprets those sequences
/// and updates cells on a screen. BCS Tool now performs the same basic job so
/// it can inspect Bannerlord Coop's screen-positioned Players pane.
///
/// This is intentionally not a complete xterm implementation. It implements
/// the common cursor/erase/scroll operations needed by terminal UIs and can
/// be expanded later if Bannerlord uses an unsupported sequence.
/// </summary>
internal sealed class VirtualTerminalScreen
{
    private enum ParserState
    {
        Normal,
        Escape,
        Csi,
        Osc,
        OscEscape,
        IgnoreOne
    }

    private readonly object _sync = new();
    private char[,] _cells;
    private TerminalCellStyle[,] _styles;

    private TerminalCellStyle _currentStyle =
        TerminalCellStyle.Default;

    private readonly StringBuilder _csiBuffer = new();

    private ParserState _state = ParserState.Normal;

    private int _row;
    private int _column;

    private int _savedRow;
    private int _savedColumn;

    // VT terminals do not immediately move to the next row when a character
    // is written into the final column. Instead they enter a "wrap pending"
    // state and only wrap when the NEXT printable character arrives.
    //
    // The earlier implementation wrapped immediately, which could generate
    // an extra line feed/scroll during Bannerlord's fixed-width redraws and
    // occasionally push the top Log / Players header off-screen.
    private bool _wrapPending;

    private int _scrollTop;
    private int _scrollBottom;

    public int Width { get; private set; }
    public int Height { get; private set; }


    public VirtualTerminalScreen(
        int width,
        int height)
    {
        Width = Math.Max(40, width);
        Height = Math.Max(10, height);

        _cells = new char[Height, Width];
        _styles = new TerminalCellStyle[Height, Width];

        _scrollTop = 0;
        _scrollBottom = Height - 1;

        ClearAll();
    }


    /// <summary>
    /// Feeds VT/ANSI terminal text into the virtual screen.
    /// </summary>
    public void Feed(string text)
    {
        lock (_sync)
        {
            foreach (var ch in text)
            {
                ProcessCharacter(ch);
            }
        }
    }


    /// <summary>
    /// Returns a stable copy of the currently rendered terminal rows.
    /// Trailing blank cells are trimmed, but internal spacing is preserved.
    /// </summary>
    public IReadOnlyList<string> SnapshotLines()
    {
        lock (_sync)
        {
            var lines = new string[Height];

            var rowBuffer = new char[Width];

            for (var r = 0; r < Height; r++)
            {
                for (var c = 0; c < Width; c++)
                {
                    rowBuffer[c] = _cells[r, c];
                }

                lines[r] =
                    new string(rowBuffer)
                        .TrimEnd();
            }

            return lines;
        }
    }


    /// <summary>
    /// Returns a stable styled copy of the terminal.
    ///
    /// Each row is compressed into contiguous same-style runs so the WPF
    /// renderer can draw ANSI-colored terminal output efficiently.
    /// </summary>
    public TerminalScreenSnapshot Snapshot()
    {
        lock (_sync)
        {
            var lines =
                new TerminalStyledLine[Height];

            for (
                var row = 0;
                row < Height;
                row++)
            {
                var lastVisibleColumn =
                    Width - 1;

                while (
                    lastVisibleColumn >= 0 &&
                    _cells[row, lastVisibleColumn] == ' ')
                {
                    lastVisibleColumn--;
                }

                if (lastVisibleColumn < 0)
                {
                    lines[row] =
                        new TerminalStyledLine(
                            "",
                            Array.Empty<TerminalTextRun>());

                    continue;
                }

                var plainBuilder =
                    new StringBuilder(
                        lastVisibleColumn + 1);

                var runs =
                    new List<TerminalTextRun>();

                var runBuilder =
                    new StringBuilder();

                var runStyle =
                    _styles[row, 0];

                for (
                    var column = 0;
                    column <= lastVisibleColumn;
                    column++)
                {
                    var ch =
                        _cells[row, column];

                    var style =
                        _styles[row, column];

                    plainBuilder.Append(ch);

                    if (
                        column == 0 ||
                        style == runStyle)
                    {
                        runBuilder.Append(ch);
                    }
                    else
                    {
                        runs.Add(
                            new TerminalTextRun(
                                runBuilder.ToString(),
                                runStyle));

                        runBuilder.Clear();
                        runBuilder.Append(ch);
                    }

                    runStyle = style;
                }

                if (runBuilder.Length > 0)
                {
                    runs.Add(
                        new TerminalTextRun(
                            runBuilder.ToString(),
                            runStyle));
                }

                lines[row] =
                    new TerminalStyledLine(
                        plainBuilder.ToString(),
                        runs);
            }

            return
                new TerminalScreenSnapshot(
                    lines,
                    cursorRow: _row,
                    cursorColumn: _column);
        }
    }



    /// <summary>
    /// Resizes the emulated terminal screen to match a live ConPTY resize.
    ///
    /// Existing visible cells are preserved where the old and new screen
    /// overlap. Bannerlord will normally redraw its terminal immediately
    /// after ResizePseudoConsole, but retaining the overlap avoids a blank
    /// flash while that redraw is arriving.
    /// </summary>
    public void Resize(
        int width,
        int height)
    {
        lock (_sync)
        {
            var newWidth =
                Math.Max(
                    40,
                    width);

            var newHeight =
                Math.Max(
                    10,
                    height);

            if (
                newWidth == Width &&
                newHeight == Height)
            {
                return;
            }

            var oldCells = _cells;
            var oldStyles = _styles;
            var oldWidth = Width;
            var oldHeight = Height;

            Width = newWidth;
            Height = newHeight;

            _cells =
                new char[Height, Width];

            _styles =
                new TerminalCellStyle[Height, Width];

            ClearAll();

            var copyRows =
                Math.Min(
                    oldHeight,
                    Height);

            var copyColumns =
                Math.Min(
                    oldWidth,
                    Width);

            for (
                var row = 0;
                row < copyRows;
                row++)
            {
                for (
                    var column = 0;
                    column < copyColumns;
                    column++)
                {
                    _cells[row, column] =
                        oldCells[row, column];

                    _styles[row, column] =
                        oldStyles[row, column];
                }
            }

            _row =
                ClampRow(
                    _row);

            _column =
                ClampColumn(
                    _column);

            _savedRow =
                ClampRow(
                    _savedRow);

            _savedColumn =
                ClampColumn(
                    _savedColumn);

            _wrapPending = false;

            // A terminal resize conventionally resets the active scrolling
            // region to the complete screen. The hosted application can set
            // new margins immediately afterward if it uses them.
            _scrollTop = 0;
            _scrollBottom = Height - 1;
        }
    }


    public void Clear()
    {
        lock (_sync)
        {
            ClearAll();
            _currentStyle = TerminalCellStyle.Default;
            _row = 0;
            _column = 0;
            _wrapPending = false;
            _scrollTop = 0;
            _scrollBottom = Height - 1;
        }
    }


    private void ProcessCharacter(char ch)
    {
        switch (_state)
        {
            case ParserState.Normal:
                ProcessNormal(ch);
                break;

            case ParserState.Escape:
                ProcessEscape(ch);
                break;

            case ParserState.Csi:
                ProcessCsiCharacter(ch);
                break;

            case ParserState.Osc:
                // OSC strings end with BEL or ESC backslash.
                if (ch == '\a')
                {
                    _state = ParserState.Normal;
                }
                else if (ch == '\x1B')
                {
                    _state = ParserState.OscEscape;
                }

                break;

            case ParserState.OscEscape:
                _state =
                    ch == '\\'
                        ? ParserState.Normal
                        : ParserState.Osc;
                break;

            case ParserState.IgnoreOne:
                _state = ParserState.Normal;
                break;
        }
    }


    private void ProcessNormal(char ch)
    {
        // 7-bit ESC
        if (ch == '\x1B')
        {
            _state = ParserState.Escape;
            return;
        }

        // 8-bit CSI
        if (ch == '\u009B')
        {
            _csiBuffer.Clear();
            _state = ParserState.Csi;
            return;
        }

        switch (ch)
        {
            case '\r':
                _wrapPending = false;
                _column = 0;
                return;

            case '\n':
                _wrapPending = false;
                LineFeed();
                return;

            case '\b':
                _wrapPending = false;
                _column = Math.Max(0, _column - 1);
                return;

            case '\t':
                _wrapPending = false;
                _column =
                    Math.Min(
                        Width - 1,
                        ((_column / 8) + 1) * 8);
                return;
        }

        // Ignore remaining C0 control characters.
        if (ch < ' ')
            return;

        PutCharacter(ch);
    }


    private void ProcessEscape(char ch)
    {
        switch (ch)
        {
            case '[':
                _csiBuffer.Clear();
                _state = ParserState.Csi;
                return;

            case ']':
                _state = ParserState.Osc;
                return;

            // Save/restore cursor (DEC).
            case '7':
                SaveCursor();
                _state = ParserState.Normal;
                return;

            case '8':
                RestoreCursor();
                _state = ParserState.Normal;
                return;

            // Index / reverse index / next line.
            case 'D':
                _wrapPending = false;
                LineFeed();
                _state = ParserState.Normal;
                return;

            case 'M':
                _wrapPending = false;
                ReverseIndex();
                _state = ParserState.Normal;
                return;

            case 'E':
                _wrapPending = false;
                _column = 0;
                LineFeed();
                _state = ParserState.Normal;
                return;

            // RIS: reset terminal.
            case 'c':
                ClearAll();
                _currentStyle = TerminalCellStyle.Default;
                _row = 0;
                _column = 0;
                _wrapPending = false;
                _scrollTop = 0;
                _scrollBottom = Height - 1;
                _state = ParserState.Normal;
                return;

            // Character-set selectors contain one following byte.
            case '(':
            case ')':
            case '*':
            case '+':
            case '#':
                _state = ParserState.IgnoreOne;
                return;

            default:
                // Keypad/application modes and unsupported short ESC
                // sequences are safe to ignore for screen reconstruction.
                _state = ParserState.Normal;
                return;
        }
    }


    private void ProcessCsiCharacter(char ch)
    {
        // CSI final bytes are in the range 0x40-0x7E.
        if (ch >= '@' && ch <= '~')
        {
            HandleCsi(
                _csiBuffer.ToString(),
                ch);

            _csiBuffer.Clear();
            _state = ParserState.Normal;
            return;
        }

        _csiBuffer.Append(ch);
    }


    private void HandleCsi(
        string parameterText,
        char final)
    {
        var isPrivate =
            parameterText.StartsWith("?") ||
            parameterText.StartsWith(">") ||
            parameterText.StartsWith("!");

        var cleanParameters =
            parameterText.TrimStart('?', '>', '!');

        var parameters =
            ParseParameters(cleanParameters);

        int P(int index, int defaultValue = 1)
        {
            if (index >= parameters.Count)
                return defaultValue;

            var value = parameters[index];

            return value == 0
                ? defaultValue
                : value;
        }

        // Cursor/editing CSI operations cancel a pending right-margin wrap.
        // SGR (`m`) and mode toggles (`h` / `l`) do not move the cursor and
        // therefore intentionally preserve it.
        if (
            final != 'm' &&
            final != 'h' &&
            final != 'l' &&
            final != 's')
        {
            _wrapPending = false;
        }

        switch (final)
        {
            // CUU / CUD / CUF / CUB
            case 'A':
                _row = ClampRow(_row - P(0));
                break;

            case 'B':
                _row = ClampRow(_row + P(0));
                break;

            case 'C':
                _column = ClampColumn(_column + P(0));
                break;

            case 'D':
                _column = ClampColumn(_column - P(0));
                break;

            // CNL / CPL
            case 'E':
                _row = ClampRow(_row + P(0));
                _column = 0;
                break;

            case 'F':
                _row = ClampRow(_row - P(0));
                _column = 0;
                break;

            // CHA
            case 'G':
                _column = ClampColumn(P(0) - 1);
                break;

            // CUP / HVP
            case 'H':
            case 'f':
                _row = ClampRow(P(0) - 1);
                _column = ClampColumn(P(1) - 1);
                break;

            // VPA
            case 'd':
                _row = ClampRow(P(0) - 1);
                break;

            // ED / EL
            case 'J':
                EraseDisplay(
                    parameters.Count > 0
                        ? parameters[0]
                        : 0);
                break;

            case 'K':
                EraseLine(
                    parameters.Count > 0
                        ? parameters[0]
                        : 0);
                break;

            // DCH / ICH / ECH
            case 'P':
                DeleteCharacters(P(0));
                break;

            case '@':
                InsertCharacters(P(0));
                break;

            case 'X':
                EraseCharacters(P(0));
                break;

            // IL / DL
            case 'L':
                InsertLines(P(0));
                break;

            case 'M':
                DeleteLines(P(0));
                break;

            // SU / SD
            case 'S':
                ScrollUpRegion(P(0));
                break;

            case 'T':
                ScrollDownRegion(P(0));
                break;

            // DECSTBM: scrolling margins.
            case 'r':
                if (!isPrivate)
                {
                    var top =
                        parameters.Count > 0 && parameters[0] > 0
                            ? parameters[0] - 1
                            : 0;

                    var bottom =
                        parameters.Count > 1 && parameters[1] > 0
                            ? parameters[1] - 1
                            : Height - 1;

                    top = ClampRow(top);
                    bottom = ClampRow(bottom);

                    if (top < bottom)
                    {
                        _scrollTop = top;
                        _scrollBottom = bottom;
                        _row = 0;
                        _column = 0;
                    }
                }

                break;

            // Save/restore cursor (ANSI).
            case 's':
                SaveCursor();
                break;

            case 'u':
                RestoreCursor();
                break;

            // SGR: Select Graphic Rendition.
            //
            // This is where Bannerlord's cyan/blue/green/etc. terminal text
            // arrives. v1.5 preserves the foreground rendition instead of
            // discarding it.
            case 'm':
                ApplySgr(parameters);
                break;

            // Private mode set/reset.
            case 'h':
            case 'l':
                // ?1049h switches to alternate screen buffer.
                // BCS Tool uses one virtual buffer, so clear it when the
                // application enters the alternate screen.
                if (
                    isPrivate &&
                    cleanParameters.Contains(
                        "1049",
                        StringComparison.Ordinal))
                {
                    ClearAll();
                    _row = 0;
                    _column = 0;
                    _wrapPending = false;
                }

                break;
        }
    }


    private static List<int> ParseParameters(string text)
    {
        var result = new List<int>();

        if (string.IsNullOrEmpty(text))
            return result;

        foreach (var piece in text.Split(';'))
        {
            if (int.TryParse(piece, out var value))
            {
                result.Add(value);
            }
            else
            {
                result.Add(0);
            }
        }

        return result;
    }


    private void PutCharacter(char ch)
    {
        // Real VT autowrap is deferred:
        //
        // 1. A character written into the final column stays there.
        // 2. The cursor enters a pending-wrap state.
        // 3. Only the NEXT printable character performs CR + LF.
        //
        // This distinction matters for full-screen TUIs such as Bannerlord's
        // console. Immediate wrapping can introduce a phantom extra scroll.
        if (_wrapPending)
        {
            _column = 0;
            LineFeed();
            _wrapPending = false;
        }

        _cells[_row, _column] = ch;
        _styles[_row, _column] = _currentStyle;

        if (_column == Width - 1)
        {
            _wrapPending = true;
        }
        else
        {
            _column++;
        }
    }


    private void LineFeed()
    {
        if (_row == _scrollBottom)
        {
            ScrollUpRegion(1);
            return;
        }

        _row = ClampRow(_row + 1);
    }


    private void ReverseIndex()
    {
        if (_row == _scrollTop)
        {
            ScrollDownRegion(1);
            return;
        }

        _row = ClampRow(_row - 1);
    }


    private void SaveCursor()
    {
        _savedRow = _row;
        _savedColumn = _column;
    }


    private void RestoreCursor()
    {
        _row = ClampRow(_savedRow);
        _column = ClampColumn(_savedColumn);
        _wrapPending = false;
    }


    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                // Cursor through end of screen.
                for (var c = _column; c < Width; c++)
                    ClearCell(_row, c);

                for (var r = _row + 1; r < Height; r++)
                    ClearRow(r);

                break;

            case 1:
                // Start of screen through cursor.
                for (var r = 0; r < _row; r++)
                    ClearRow(r);

                for (var c = 0; c <= _column; c++)
                    ClearCell(_row, c);

                break;

            case 2:
            case 3:
                ClearAll();
                break;
        }
    }


    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0:
                for (var c = _column; c < Width; c++)
                    ClearCell(_row, c);
                break;

            case 1:
                for (var c = 0; c <= _column; c++)
                    ClearCell(_row, c);
                break;

            case 2:
                ClearRow(_row);
                break;
        }
    }


    private void DeleteCharacters(int count)
    {
        count = Math.Max(1, count);

        for (var c = _column; c < Width; c++)
        {
            var source = c + count;

            if (source < Width)
            {
                _cells[_row, c] =
                    _cells[_row, source];

                _styles[_row, c] =
                    _styles[_row, source];
            }
            else
            {
                ClearCell(
                    _row,
                    c);
            }
        }
    }


    private void InsertCharacters(int count)
    {
        count =
            Math.Min(
                Math.Max(1, count),
                Width - _column);

        for (var c = Width - 1; c >= _column + count; c--)
        {
            _cells[_row, c] =
                _cells[_row, c - count];

            _styles[_row, c] =
                _styles[_row, c - count];
        }

        for (var c = _column; c < _column + count; c++)
        {
            ClearCell(
                _row,
                c);
        }
    }


    private void EraseCharacters(int count)
    {
        count = Math.Max(1, count);

        var end =
            Math.Min(
                Width,
                _column + count);

        for (var c = _column; c < end; c++)
        {
            ClearCell(
                _row,
                c);
        }
    }


    private void InsertLines(int count)
    {
        if (_row < _scrollTop || _row > _scrollBottom)
            return;

        count =
            Math.Min(
                Math.Max(1, count),
                _scrollBottom - _row + 1);

        for (var r = _scrollBottom; r >= _row + count; r--)
        {
            CopyRow(r - count, r);
        }

        for (var r = _row; r < _row + count; r++)
        {
            ClearRow(r);
        }
    }


    private void DeleteLines(int count)
    {
        if (_row < _scrollTop || _row > _scrollBottom)
            return;

        count =
            Math.Min(
                Math.Max(1, count),
                _scrollBottom - _row + 1);

        for (var r = _row; r <= _scrollBottom - count; r++)
        {
            CopyRow(r + count, r);
        }

        for (
            var r = _scrollBottom - count + 1;
            r <= _scrollBottom;
            r++)
        {
            ClearRow(r);
        }
    }


    private void ScrollUpRegion(int count)
    {
        count =
            Math.Min(
                Math.Max(1, count),
                _scrollBottom - _scrollTop + 1);

        for (
            var r = _scrollTop;
            r <= _scrollBottom - count;
            r++)
        {
            CopyRow(r + count, r);
        }

        for (
            var r = _scrollBottom - count + 1;
            r <= _scrollBottom;
            r++)
        {
            ClearRow(r);
        }
    }


    private void ScrollDownRegion(int count)
    {
        count =
            Math.Min(
                Math.Max(1, count),
                _scrollBottom - _scrollTop + 1);

        for (
            var r = _scrollBottom;
            r >= _scrollTop + count;
            r--)
        {
            CopyRow(r - count, r);
        }

        for (
            var r = _scrollTop;
            r < _scrollTop + count;
            r++)
        {
            ClearRow(r);
        }
    }


    private void CopyRow(
        int sourceRow,
        int destinationRow)
    {
        for (var c = 0; c < Width; c++)
        {
            _cells[destinationRow, c] =
                _cells[sourceRow, c];

            _styles[destinationRow, c] =
                _styles[sourceRow, c];
        }
    }


    private void ClearRow(int row)
    {
        for (var c = 0; c < Width; c++)
        {
            ClearCell(
                row,
                c);
        }
    }


    private void ClearAll()
    {
        for (var r = 0; r < Height; r++)
        {
            ClearRow(r);
        }
    }


    /// <summary>
    /// Applies ANSI/VT SGR foreground styling.
    ///
    /// Supported:
    /// - reset / bold / normal intensity
    /// - standard colors 30-37
    /// - bright colors 90-97
    /// - default foreground 39
    /// - 256-color foreground: 38;5;n
    /// - truecolor foreground: 38;2;r;g;b
    /// </summary>
    private void ApplySgr(
        IReadOnlyList<int> parameters)
    {
        if (parameters.Count == 0)
        {
            _currentStyle =
                TerminalCellStyle.Default;

            return;
        }

        for (
            var index = 0;
            index < parameters.Count;
            index++)
        {
            var value =
                parameters[index];

            switch (value)
            {
                case 0:
                    _currentStyle =
                        TerminalCellStyle.Default;
                    break;

                case 1:
                    _currentStyle =
                        _currentStyle with
                        {
                            Bold = true
                        };
                    break;

                case 22:
                    _currentStyle =
                        _currentStyle with
                        {
                            Bold = false
                        };
                    break;

                case >= 30 and <= 37:
                    _currentStyle =
                        _currentStyle with
                        {
                            Foreground =
                                GetAnsi16Color(
                                    value - 30,
                                    bright: false)
                        };
                    break;

                case 39:
                    _currentStyle =
                        _currentStyle with
                        {
                            Foreground =
                                TerminalColor.Default
                        };
                    break;

                case >= 90 and <= 97:
                    _currentStyle =
                        _currentStyle with
                        {
                            Foreground =
                                GetAnsi16Color(
                                    value - 90,
                                    bright: true)
                        };
                    break;

                case 38:
                    // 38;5;n       = indexed 256-color
                    // 38;2;r;g;b   = 24-bit truecolor
                    if (
                        index + 2 < parameters.Count &&
                        parameters[index + 1] == 5)
                    {
                        _currentStyle =
                            _currentStyle with
                            {
                                Foreground =
                                    GetAnsi256Color(
                                        parameters[index + 2])
                            };

                        index += 2;
                    }
                    else if (
                        index + 4 < parameters.Count &&
                        parameters[index + 1] == 2)
                    {
                        _currentStyle =
                            _currentStyle with
                            {
                                Foreground =
                                    new TerminalColor(
                                        ClampByte(
                                            parameters[index + 2]),
                                        ClampByte(
                                            parameters[index + 3]),
                                        ClampByte(
                                            parameters[index + 4]))
                            };

                        index += 4;
                    }

                    break;
            }
        }
    }


    /// <summary>
    /// Windows-Terminal-like ANSI 16-color palette.
    ///
    /// These values reproduce the familiar console cyan/green/blue/red colors
    /// shown by Bannerlord much more closely than WPF's named brushes.
    /// </summary>
    private static TerminalColor GetAnsi16Color(
        int index,
        bool bright)
    {
        return (index, bright) switch
        {
            (0, false) => new(12, 12, 12),
            (1, false) => new(197, 15, 31),
            (2, false) => new(19, 161, 14),
            (3, false) => new(193, 156, 0),
            (4, false) => new(0, 55, 218),
            (5, false) => new(136, 23, 152),
            (6, false) => new(58, 150, 221),
            (7, false) => new(204, 204, 204),

            (0, true) => new(118, 118, 118),
            (1, true) => new(231, 72, 86),
            (2, true) => new(22, 198, 12),
            (3, true) => new(249, 241, 165),
            (4, true) => new(59, 120, 255),
            (5, true) => new(180, 0, 158),
            (6, true) => new(97, 214, 214),
            (7, true) => new(242, 242, 242),

            _ => TerminalColor.Default
        };
    }


    private static TerminalColor GetAnsi256Color(
        int value)
    {
        value =
            Math.Clamp(
                value,
                0,
                255);

        if (value < 16)
        {
            return GetAnsi16Color(
                value % 8,
                bright: value >= 8);
        }

        // 6 x 6 x 6 RGB color cube.
        if (value <= 231)
        {
            var cube =
                value - 16;

            var red =
                cube / 36;

            var green =
                (cube / 6) % 6;

            var blue =
                cube % 6;

            static byte CubeComponent(int component) =>
                component == 0
                    ? (byte)0
                    : (byte)(55 + component * 40);

            return new TerminalColor(
                CubeComponent(red),
                CubeComponent(green),
                CubeComponent(blue));
        }

        // 24-step grayscale ramp.
        var gray =
            (byte)(
                8 +
                (value - 232) * 10);

        return new TerminalColor(
            gray,
            gray,
            gray);
    }


    private static byte ClampByte(int value) =>
        (byte)Math.Clamp(
            value,
            0,
            255);


    private void ClearCell(
        int row,
        int column)
    {
        _cells[row, column] = ' ';

        _styles[row, column] =
            TerminalCellStyle.Default;
    }


    private int ClampRow(int value) =>
        Math.Clamp(value, 0, Height - 1);


    private int ClampColumn(int value) =>
        Math.Clamp(value, 0, Width - 1);
}
