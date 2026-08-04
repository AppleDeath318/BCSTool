using System;
using System.Collections.Generic;

namespace BCSTool.Services;

/// <summary>
/// Immutable snapshot of the reconstructed ConPTY terminal screen.
///
/// `Snapshot` contains both characters and ANSI/VT styling.
/// `Lines` is retained as a convenient plain-text view for readiness/header
/// logic that does not care about color.
/// </summary>
public sealed class TerminalScreenUpdatedEventArgs : EventArgs
{
    public TerminalScreenSnapshot Snapshot { get; }

    public IReadOnlyList<string> Lines =>
        Snapshot.PlainLines;

    public TerminalScreenUpdatedEventArgs(
        TerminalScreenSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}
