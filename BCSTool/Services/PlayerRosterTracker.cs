using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BCSTool.Services;

/// <summary>
/// Extracts the native Bannerlord Coop Players pane from the reconstructed
/// ConPTY terminal screen.
///
/// v1.2.1 adds "sticky" native-pane state.
///
/// Why this is needed:
///
/// Terminal applications redraw their screen in multiple VT operations.
/// During a redraw, BCS Tool can briefly receive an intermediate snapshot
/// where the Players header is temporarily absent.
///
/// v1.2 behavior:
///
///     valid Players pane
///         ↓
///     intermediate redraw frame
///         ↓
///     header temporarily missing
///         ↓
///     fallback to players=N
///         ↓
///     UI shows "waiting for ConPTY..."
///         ↓
///     next frame restores roster
///
/// This caused visible flicker.
///
/// v1.2.1 behavior:
///
///     once native Players pane has been seen
///         ↓
///     transient header-missing frames keep last valid roster
///         ↓
///     roster changes only when a complete native pane says it changed
///
/// The delayed `players=N` pulse remains useful before the native pane has
/// ever been detected, but it no longer overrides a valid native roster.
/// </summary>
public sealed class PlayerRosterTracker
{
    private static readonly Regex PlayersHeaderRegex = new(
        @"Players\s*\((?<count>\d+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PulseCountRegex = new(
        @"\bplayers=(?<count>\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly List<string> _rosterLines = new();

    // Once true, transient snapshots are not allowed to replace the native
    // roster with pulse-only fallback text.
    private bool _hasSeenNativePane;

    public int PlayerCount { get; private set; }

    public IReadOnlyList<string> RosterLines =>
        _rosterLines;

    /// <summary>
    /// True after BCS Tool has successfully parsed the native ConPTY Players
    /// pane at least once during the current server instance.
    /// </summary>
    public bool HasNativePane =>
        _hasSeenNativePane;

    /// <summary>
    /// Column where the native Players pane begins.
    ///
    /// -1 means the current snapshot did not contain the pane.
    /// </summary>
    public int PlayerPanelColumn { get; private set; } = -1;


    /// <summary>
    /// Processes one complete terminal-screen snapshot.
    ///
    /// Returns true only when the user-visible player data actually changed.
    /// </summary>
    public bool ProcessScreen(
        IReadOnlyList<string> screenLines)
    {
        var previousCount =
            PlayerCount;

        var previousRoster =
            _rosterLines.ToArray();

        var previousHasNativePane =
            _hasSeenNativePane;

        // Search for the native Players pane in this snapshot.
        var headerRow = -1;
        var headerColumn = -1;
        var headerCount = -1;

        for (
            var row = 0;
            row < screenLines.Count;
            row++)
        {
            var match =
                PlayersHeaderRegex.Match(
                    screenLines[row]);

            if (!match.Success)
                continue;

            headerRow = row;
            headerColumn = match.Index;

            if (
                int.TryParse(
                    match.Groups["count"].Value,
                    out var parsedCount))
            {
                headerCount = parsedCount;
            }

            break;
        }


        // ====================================================
        // COMPLETE NATIVE PANE FOUND
        // ====================================================
        if (headerRow >= 0)
        {
            _hasSeenNativePane = true;

            PlayerPanelColumn =
                FindPanelStart(
                    screenLines[headerRow],
                    headerColumn);

            var newRoster =
                ParseNativeRoster(
                    screenLines,
                    headerRow,
                    PlayerPanelColumn);

            PlayerCount =
                Math.Max(
                    0,
                    headerCount);

            // An explicit Players (0) is authoritative and should clear the
            // roster immediately.
            if (PlayerCount == 0)
            {
                newRoster.Clear();
            }

            _rosterLines.Clear();
            _rosterLines.AddRange(
                newRoster);
        }
        else
        {
            // =================================================
            // NATIVE PANE TEMPORARILY MISSING
            // =================================================
            //
            // This is common during a VT redraw. If we have already seen a
            // valid native pane, KEEP the last good player data.
            //
            // Most importantly:
            //
            //     DO NOT clear _rosterLines
            //     DO NOT show pulse fallback text
            //
            // This removes the visible flicker.
            //
            PlayerPanelColumn = -1;

            if (!_hasSeenNativePane)
            {
                // Before the native pane is ever seen, the pulse count is
                // still useful as a startup fallback.
                var fallbackCount =
                    FindNewestPulseCount(
                        screenLines);

                if (fallbackCount >= 0)
                {
                    PlayerCount =
                        fallbackCount;
                }

                if (PlayerCount == 0)
                {
                    _rosterLines.Clear();
                }
            }
        }


        return
            previousCount != PlayerCount ||
            previousHasNativePane != _hasSeenNativePane ||
            !previousRoster.SequenceEqual(
                _rosterLines,
                StringComparer.OrdinalIgnoreCase);
    }


    /// <summary>
    /// Returns the left/log portion of the reconstructed terminal.
    ///
    /// If the Players pane is absent only because we caught an intermediate
    /// redraw frame, use the most recently known pane column when possible so
    /// the right-side terminal content does not flash into the log pane.
    /// </summary>
    public IReadOnlyList<string> GetLogPaneLines(
        IReadOnlyList<string> screenLines)
    {
        // If current snapshot contains a native pane, split there.
        if (PlayerPanelColumn >= 0)
        {
            return SliceLogPane(
                screenLines,
                PlayerPanelColumn);
        }

        // During transient redraw frames there may be no reliable split point.
        // Returning the whole screen is preferable to modifying player state.
        // The WPF player panel itself remains stable.
        return screenLines.ToArray();
    }


    /// <summary>
    /// Clears all state for a new/stopped server instance.
    /// </summary>
    public void Reset()
    {
        PlayerCount = 0;
        PlayerPanelColumn = -1;
        _hasSeenNativePane = false;
        _rosterLines.Clear();
    }


    private static List<string> ParseNativeRoster(
        IReadOnlyList<string> screenLines,
        int headerRow,
        int playerPanelColumn)
    {
        var roster =
            new List<string>();

        for (
            var row = headerRow + 1;
            row < screenLines.Count;
            row++)
        {
            var line =
                screenLines[row];

            if (
                playerPanelColumn < 0 ||
                line.Length <= playerPanelColumn)
            {
                continue;
            }

            var segment =
                line[playerPanelColumn..];

            if (IsBottomBorder(segment))
                break;

            var content =
                CleanPanelRow(segment);

            if (content.Length == 0)
                continue;

            if (
                content.Contains(
                    "no one online",
                    StringComparison.OrdinalIgnoreCase))
            {
                roster.Clear();
                break;
            }

            if (
                PlayersHeaderRegex.IsMatch(
                    content))
            {
                continue;
            }

            if (!IsBorderOnly(content))
            {
                roster.Add(content);
            }
        }

        return roster;
    }


    private static IReadOnlyList<string> SliceLogPane(
        IReadOnlyList<string> screenLines,
        int column)
    {
        var result =
            new string[screenLines.Count];

        for (
            var i = 0;
            i < screenLines.Count;
            i++)
        {
            var line =
                screenLines[i];

            result[i] =
                line.Length > column
                    ? line[..column].TrimEnd()
                    : line;
        }

        return result;
    }


    private static int FindPanelStart(
        string headerLine,
        int headerColumn)
    {
        for (
            var index = headerColumn;
            index >= 0;
            index--)
        {
            var ch =
                headerLine[index];

            if (
                ch == '┌' ||
                ch == '╭' ||
                ch == '+' ||
                ch == '│' ||
                ch == '|')
            {
                return index;
            }
        }

        return headerColumn;
    }


    private static int FindNewestPulseCount(
        IReadOnlyList<string> screenLines)
    {
        for (
            var row = screenLines.Count - 1;
            row >= 0;
            row--)
        {
            var matches =
                PulseCountRegex.Matches(
                    screenLines[row]);

            if (matches.Count == 0)
                continue;

            var match =
                matches[^1];

            if (
                int.TryParse(
                    match.Groups["count"].Value,
                    out var count))
            {
                return count;
            }
        }

        return -1;
    }


    private static string CleanPanelRow(
        string segment)
    {
        var value =
            segment.Trim();

        value =
            value.Trim(
                '|',
                '│',
                '┃',
                '┆',
                '┇',
                '┊',
                '┋',
                '║');

        return value.Trim();
    }


    private static bool IsBottomBorder(
        string segment)
    {
        var value =
            segment.TrimStart();

        return
            value.StartsWith(
                "└",
                StringComparison.Ordinal) ||
            value.StartsWith(
                "╰",
                StringComparison.Ordinal) ||
            value.StartsWith(
                "+--",
                StringComparison.Ordinal);
    }


    private static bool IsBorderOnly(
        string value)
    {
        if (value.Length < 2)
            return false;

        foreach (var ch in value)
        {
            if (
                ch != '-' &&
                ch != '=' &&
                ch != '_' &&
                ch != '─' &&
                ch != '━' &&
                ch != '┄' &&
                ch != '┅' &&
                ch != '┈' &&
                ch != '┉')
            {
                return false;
            }
        }

        return true;
    }
}
