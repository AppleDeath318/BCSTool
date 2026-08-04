using System;
using System.Collections.Generic;
using System.IO;

namespace BCSTool.Models;

/// <summary>
/// Serializable configuration for BCS Tool.
///
/// SettingsService persists this class in the current user's Registry.
/// Using a normal C# object instead of hard-coded constants means the
/// application can change settings without being recompiled.
/// </summary>
public sealed class ServerSettings
{
    // Folder containing BannerlordCoopServer.exe.
    // Empty means "use the folder containing BCS Tool".
    public string ServerDirectory { get; set; } = "";
    public string ServerExecutable { get; set; } = "BannerlordCoopServer.exe";

    // Clock-aligned restart interval.
    // Example: 2 => 00:xx, 02:xx, 04:xx, 06:xx...
    public int RestartEveryHours { get; set; } = 2;
    // Minute inside the scheduled hour, e.g. 55 => 02:55.
    public int RestartMinute { get; set; } = 55;
    // Broadcast every minute starting this many minutes before restart.
    public int WarningMinutesBefore { get; set; } = 5;

    // Automation is not enabled until server stdout contains this text.
    public string ReadyText { get; set; } = "coop server up, waiting for clients";

    public int SaveWaitSeconds { get; set; } = 10;
    public int RestartDelaySeconds { get; set; } = 10;
    public int ShutdownTimeoutSeconds { get; set; } = 60;
    public int CrashRecoverySettleSeconds { get; set; } = 10;
    public int PortReleaseTimeoutSeconds { get; set; } = 30;

    // Optional network-port safety guard.
    //
    // 0 = disabled (recommended unless you know the server's actual,
    // exclusive listening port).
    //
    // Do NOT guess this value from unrelated console forwarding messages.
    public int ServerPort { get; set; } = 0;

    // The first server launch is always manual. Opening BCS Tool never
    // launches BannerlordCoopServer.exe by itself.
    public bool AutoRestartOnCrash { get; set; } = true;

    public string BroadcastSaving { get; set; } = "Saving Files...";
    public string BroadcastRestarting { get; set; } = "Restarting...";

    /// <summary>
    /// Resolves the effective server directory.
    /// </summary>
    public string ResolveServerDirectory()
    {
        if (!string.IsNullOrWhiteSpace(ServerDirectory))
            return Path.GetFullPath(ServerDirectory);

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Produces the full absolute path to BannerlordCoopServer.exe.
    /// </summary>
    public string ResolveServerExecutablePath()
    {
        return Path.Combine(ResolveServerDirectory(), ServerExecutable);
    }

    /// <summary>
    /// Validates settings before they are used for server operations.
    /// Returning all errors at once gives the user better feedback than
    /// failing one field at a time.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (RestartEveryHours is < 1 or > 24)
            errors.Add("Restart interval must be between 1 and 24 hours.");

        if (RestartMinute is < 0 or > 59)
            errors.Add("Restart minute must be between 0 and 59.");

        if (WarningMinutesBefore is < 0 or > 10)
            errors.Add("Restart warning lead time must be between 0 and 10 minutes.");

        if (ServerPort is < 0 or > 65535)
            errors.Add("Server port must be 0 (disabled) or between 1 and 65535.");

        if (SaveWaitSeconds < 0)
            errors.Add("Save wait cannot be negative.");

        if (RestartDelaySeconds < 0)
            errors.Add("Restart delay cannot be negative.");

        if (ShutdownTimeoutSeconds < 1)
            errors.Add("Shutdown timeout must be at least 1 second.");

        if (string.IsNullOrWhiteSpace(ReadyText))
            errors.Add("Ready text cannot be empty.");

        if (string.IsNullOrWhiteSpace(ServerExecutable))
            errors.Add("Server executable cannot be empty.");

        return errors;
    }
}
