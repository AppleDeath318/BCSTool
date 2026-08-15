using System;

namespace BCSTool.Models;

/// <summary>
/// One independently scheduled server announcement.
/// </summary>
public sealed class ScheduledBroadcastEntry
{
    public const int MinimumIntervalMinutes = 5;
    public const int MaximumIntervalMinutes = 1440;
    public const int MaximumMessageLength = 500;
    public const int MaximumEntryCount = 50;

    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 10;
    public string Message { get; set; } = "";

    public static string NormalizeMessage(string? message) =>
        (message ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
}
