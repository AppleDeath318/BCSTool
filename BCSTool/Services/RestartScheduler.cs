using System;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Pure scheduling logic.
///
/// Keeping date/time calculation in its own class makes it easier to test
/// independently from the UI and the actual server process.
/// </summary>
public sealed class RestartScheduler
{
    /// <summary>
    /// Calculates the next clock-aligned restart timestamp.
    ///
    /// Example:
    /// RestartEveryHours = 2
    /// RestartMinute     = 55
    ///
    /// Results:
    /// 00:55, 02:55, 04:55, 06:55, ...
    ///
    /// A restart time that already passed is never "replayed".
    /// </summary>
    public DateTime CalculateNextRestart(DateTime now, ServerSettings settings)
    {
        var cursor = new DateTime(
            now.Year,
            now.Month,
            now.Day,
            now.Hour,
            settings.RestartMinute,
            0,
            now.Kind);

        if (cursor <= now)
            cursor = cursor.AddHours(1);

        // Preserve the PowerShell tool's clock-aligned behavior:
        // 2 hours => 00, 02, 04, 06...
        while ((cursor.Hour % settings.RestartEveryHours) != 0)
        {
            cursor = cursor.AddHours(1);
        }

        return cursor;
    }
}
