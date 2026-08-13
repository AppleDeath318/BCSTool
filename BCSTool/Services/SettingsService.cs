using System;
using System.Threading.Tasks;
using BCSTool.Models;
using Microsoft.Win32;

namespace BCSTool.Services;

/// <summary>
/// Loads and saves BCS Tool settings in the current Windows user's Registry.
///
/// Registry location:
///
///     HKEY_CURRENT_USER\Software\BCS Tool
///
/// Why use HKEY_CURRENT_USER?
///
/// - No administrator permission is required.
/// - Each Windows user gets their own BCS Tool settings.
/// - Settings survive replacing/updating BCS Tool.exe.
/// - No visible settings.json file is needed beside the executable.
///
/// The rest of the application still talks to this service through
/// LoadAsync(...) and SaveAsync(...), so changing the storage mechanism does
/// not require the UI or server-management code to understand Registry APIs.
/// </summary>
public sealed class SettingsService
{
    /// <summary>
    /// Registry subkey used by BCS Tool.
    /// This is relative to HKEY_CURRENT_USER.
    /// </summary>
    private const string RegistryPath =
        @"Software\BCS Tool";

    // Compatibility-only path used to migrate settings created by builds
    // released before the BCS Tool naming cleanup. Keeping it assembled from
    // pieces prevents the obsolete product name from remaining as branding.
    private static string LegacyRegistryPath =>
        @"Software\BCS" + "Server" + "Tool";

    /// <summary>
    /// Human-readable location shown in the BCS Tool console.
    /// </summary>
    public string StorageLocation =>
        @"HKEY_CURRENT_USER\Software\BCS Tool";

    public SettingsService()
    {
        MigrateLegacyRegistrySettings();
    }


    /// <summary>
    /// Loads saved settings from the Registry.
    ///
    /// If the Registry key does not exist yet, this is considered a normal
    /// first launch and the defaults from ServerSettings are returned.
    ///
    /// Each Registry value also falls back to its corresponding default if
    /// that individual value is missing.
    /// </summary>
    public Task<ServerSettings> LoadAsync()
    {
        var settings = new ServerSettings();

        using var key =
            Registry.CurrentUser.OpenSubKey(
                RegistryPath,
                writable: false);

        // First launch: no Registry key has been created yet.
        if (key is null)
        {
            return Task.FromResult(settings);
        }

        settings.ServerDirectory =
            ReadString(
                key,
                nameof(ServerSettings.ServerDirectory),
                settings.ServerDirectory);

        settings.ServerExecutable =
            ReadString(
                key,
                nameof(ServerSettings.ServerExecutable),
                settings.ServerExecutable);

        settings.RestartEveryHours =
            ReadInt(
                key,
                nameof(ServerSettings.RestartEveryHours),
                settings.RestartEveryHours);

        settings.RestartMinute =
            ReadInt(
                key,
                nameof(ServerSettings.RestartMinute),
                settings.RestartMinute);

        // v1.6 supports 0..10 minutes. Clamp Registry values saved by older
        // versions so upgrading cannot leave the ComboBox without a selection.
        settings.WarningMinutesBefore =
            Math.Clamp(
                ReadInt(
                    key,
                    nameof(ServerSettings.WarningMinutesBefore),
                    settings.WarningMinutesBefore),
                0,
                10);

        settings.SaveWaitSeconds =
            ReadInt(
                key,
                nameof(ServerSettings.SaveWaitSeconds),
                settings.SaveWaitSeconds);

        settings.RestartDelaySeconds =
            ReadInt(
                key,
                nameof(ServerSettings.RestartDelaySeconds),
                settings.RestartDelaySeconds);

        settings.ShutdownTimeoutSeconds =
            ReadInt(
                key,
                nameof(ServerSettings.ShutdownTimeoutSeconds),
                settings.ShutdownTimeoutSeconds);

        settings.CrashRecoverySettleSeconds =
            ReadInt(
                key,
                nameof(ServerSettings.CrashRecoverySettleSeconds),
                settings.CrashRecoverySettleSeconds);

        settings.PortReleaseTimeoutSeconds =
            ReadInt(
                key,
                nameof(ServerSettings.PortReleaseTimeoutSeconds),
                settings.PortReleaseTimeoutSeconds);

        settings.ServerPort =
            ReadInt(
                key,
                nameof(ServerSettings.ServerPort),
                settings.ServerPort);

        settings.AutoRestartOnCrash =
            ReadBool(
                key,
                nameof(ServerSettings.AutoRestartOnCrash),
                settings.AutoRestartOnCrash);

        settings.SaveBackupsEnabled =
            ReadBool(
                key,
                nameof(ServerSettings.SaveBackupsEnabled),
                settings.SaveBackupsEnabled);

        settings.SaveBackupCount =
            Math.Clamp(
                ReadInt(
                    key,
                    nameof(ServerSettings.SaveBackupCount),
                    settings.SaveBackupCount),
                1,
                5);

        settings.PlayerAccessMode =
            ReadPlayerAccessMode(
                key,
                nameof(ServerSettings.PlayerAccessMode),
                settings.PlayerAccessMode);

        settings.BroadcastSaving =
            ReadString(
                key,
                nameof(ServerSettings.BroadcastSaving),
                settings.BroadcastSaving);

        settings.BroadcastRestarting =
            ReadString(
                key,
                nameof(ServerSettings.BroadcastRestarting),
                settings.BroadcastRestarting);

        return Task.FromResult(settings);
    }


    /// <summary>
    /// Saves only the server executable location.
    ///
    /// This is intentionally separate from restart settings so choosing or
    /// auto-detecting BannerlordCoopServer.exe persists immediately without
    /// depending on the Restart Settings UI.
    /// </summary>
    public Task SaveServerExecutableAsync(
        ServerSettings settings)
    {
        using var key =
            Registry.CurrentUser.CreateSubKey(
                RegistryPath,
                writable: true);

        if (key is null)
        {
            throw new InvalidOperationException(
                "Could not create or open the BCS Tool Registry settings key.");
        }

        WriteString(
            key,
            nameof(ServerSettings.ServerDirectory),
            settings.ServerDirectory);

        WriteString(
            key,
            nameof(ServerSettings.ServerExecutable),
            settings.ServerExecutable);

        return Task.CompletedTask;
    }


    /// <summary>
    /// Saves only the settings controlled by the Restart Settings panel.
    ///
    /// Server executable selection is deliberately not written here.
    /// </summary>
    public Task SaveRestartSettingsAsync(
        ServerSettings settings)
    {
        using var key =
            Registry.CurrentUser.CreateSubKey(
                RegistryPath,
                writable: true);

        if (key is null)
        {
            throw new InvalidOperationException(
                "Could not create or open the BCS Tool Registry settings key.");
        }

        WriteInt(
            key,
            nameof(ServerSettings.RestartEveryHours),
            settings.RestartEveryHours);

        WriteInt(
            key,
            nameof(ServerSettings.RestartMinute),
            settings.RestartMinute);

        WriteInt(
            key,
            nameof(ServerSettings.WarningMinutesBefore),
            settings.WarningMinutesBefore);

        WriteBool(
            key,
            nameof(ServerSettings.AutoRestartOnCrash),
            settings.AutoRestartOnCrash);

        return Task.CompletedTask;
    }


    /// <summary>
    /// Saves only BCS Tool's rotating save-backup settings.
    /// </summary>
    public Task SaveBackupSettingsAsync(
        ServerSettings settings)
    {
        using var key =
            Registry.CurrentUser.CreateSubKey(
                RegistryPath,
                writable: true);

        if (key is null)
        {
            throw new InvalidOperationException(
                "Could not create or open the BCS Tool Registry settings key.");
        }

        WriteBool(
            key,
            nameof(ServerSettings.SaveBackupsEnabled),
            settings.SaveBackupsEnabled);

        WriteInt(
            key,
            nameof(ServerSettings.SaveBackupCount),
            Math.Clamp(
                settings.SaveBackupCount,
                1,
                5));

        return Task.CompletedTask;
    }


    /// <summary>
    /// Saves only the selected player access-control mode. Banlist/whitelist
    /// contents live as JSON files under %LOCALAPPDATA%\BCS Tool.
    /// </summary>
    public Task SavePlayerAccessModeAsync(
        ServerSettings settings)
    {
        using var key =
            Registry.CurrentUser.CreateSubKey(
                RegistryPath,
                writable: true);

        if (key is null)
        {
            throw new InvalidOperationException(
                "Could not create or open the BCS Tool Registry settings key.");
        }

        WriteString(
            key,
            nameof(ServerSettings.PlayerAccessMode),
            settings.PlayerAccessMode.ToString());

        return Task.CompletedTask;
    }


    /// <summary>
    /// Saves all current settings to the Registry.
    ///
    /// Retained for compatibility with older code paths. The current UI uses
    /// SaveServerExecutableAsync(...) and SaveRestartSettingsAsync(...) so the
    /// executable path and restart controls have independent persistence.
    /// </summary>
    public Task SaveAsync(ServerSettings settings)
    {
        using var key =
            Registry.CurrentUser.CreateSubKey(
                RegistryPath,
                writable: true);

        if (key is null)
        {
            throw new InvalidOperationException(
                "Could not create or open the BCS Tool Registry settings key.");
        }

        // Strings are stored as REG_SZ.
        WriteString(
            key,
            nameof(ServerSettings.ServerDirectory),
            settings.ServerDirectory);

        WriteString(
            key,
            nameof(ServerSettings.ServerExecutable),
            settings.ServerExecutable);

        WriteString(
            key,
            nameof(ServerSettings.BroadcastSaving),
            settings.BroadcastSaving);

        WriteString(
            key,
            nameof(ServerSettings.BroadcastRestarting),
            settings.BroadcastRestarting);

        // Numeric values are stored as REG_DWORD.
        WriteInt(
            key,
            nameof(ServerSettings.RestartEveryHours),
            settings.RestartEveryHours);

        WriteInt(
            key,
            nameof(ServerSettings.RestartMinute),
            settings.RestartMinute);

        WriteInt(
            key,
            nameof(ServerSettings.WarningMinutesBefore),
            settings.WarningMinutesBefore);

        WriteInt(
            key,
            nameof(ServerSettings.SaveWaitSeconds),
            settings.SaveWaitSeconds);

        WriteInt(
            key,
            nameof(ServerSettings.RestartDelaySeconds),
            settings.RestartDelaySeconds);

        WriteInt(
            key,
            nameof(ServerSettings.ShutdownTimeoutSeconds),
            settings.ShutdownTimeoutSeconds);

        WriteInt(
            key,
            nameof(ServerSettings.CrashRecoverySettleSeconds),
            settings.CrashRecoverySettleSeconds);

        WriteInt(
            key,
            nameof(ServerSettings.PortReleaseTimeoutSeconds),
            settings.PortReleaseTimeoutSeconds);

        WriteInt(
            key,
            nameof(ServerSettings.ServerPort),
            settings.ServerPort);

        // Booleans are represented as REG_DWORD:
        // 0 = false
        // 1 = true
        WriteBool(
            key,
            nameof(ServerSettings.AutoRestartOnCrash),
            settings.AutoRestartOnCrash);

        WriteBool(
            key,
            nameof(ServerSettings.SaveBackupsEnabled),
            settings.SaveBackupsEnabled);

        WriteInt(
            key,
            nameof(ServerSettings.SaveBackupCount),
            Math.Clamp(
                settings.SaveBackupCount,
                1,
                5));

        WriteString(
            key,
            nameof(ServerSettings.PlayerAccessMode),
            settings.PlayerAccessMode.ToString());

        return Task.CompletedTask;
    }


    /// <summary>
    /// Deletes the entire BCS Tool settings key.
    ///
    /// The next LoadAsync call will therefore return a fresh ServerSettings
    /// instance containing the built-in defaults.
    ///
    /// DeleteSubKeyTree(..., throwOnMissingSubKey: false) makes this safe even
    /// if the user has never saved settings before.
    /// </summary>
    public Task ResetAsync()
    {
        Registry.CurrentUser.DeleteSubKeyTree(
            RegistryPath,
            throwOnMissingSubKey: false);

        Registry.CurrentUser.DeleteSubKeyTree(
            LegacyRegistryPath,
            throwOnMissingSubKey: false);

        return Task.CompletedTask;
    }


    /// <summary>
    /// Migrates settings saved by pre-rename builds into the canonical
    /// HKEY_CURRENT_USER\Software\BCS Tool key. Migration is best-effort:
    /// failure never prevents BCS Tool from starting.
    /// </summary>
    private static void MigrateLegacyRegistrySettings()
    {
        try
        {
            using var currentKey =
                Registry.CurrentUser.OpenSubKey(
                    RegistryPath,
                    writable: false);

            if (currentKey is not null)
            {
                // The canonical key already exists. Remove any stale legacy
                // copy so future settings only live under BCS Tool.
                Registry.CurrentUser.DeleteSubKeyTree(
                    LegacyRegistryPath,
                    throwOnMissingSubKey: false);
                return;
            }

            using var legacyKey =
                Registry.CurrentUser.OpenSubKey(
                    LegacyRegistryPath,
                    writable: false);

            if (legacyKey is null)
                return;

            using var migratedKey =
                Registry.CurrentUser.CreateSubKey(
                    RegistryPath,
                    writable: true);

            if (migratedKey is null)
                return;

            foreach (var valueName in legacyKey.GetValueNames())
            {
                var value = legacyKey.GetValue(
                    valueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);

                if (value is null)
                    continue;

                migratedKey.SetValue(
                    valueName,
                    value,
                    legacyKey.GetValueKind(valueName));
            }

            Registry.CurrentUser.DeleteSubKeyTree(
                LegacyRegistryPath,
                throwOnMissingSubKey: false);
        }
        catch
        {
            // Migration exists only for upgrade compatibility. Registry
            // permission/corruption issues should not prevent application
            // startup; normal defaults remain available.
        }
    }


    // ========================================================
    // REGISTRY READ HELPERS
    // ========================================================

    /// <summary>
    /// Reads a string Registry value, or returns the supplied default.
    /// </summary>
    private static string ReadString(
        RegistryKey key,
        string name,
        string defaultValue)
    {
        var value = key.GetValue(name);

        return value?.ToString() ?? defaultValue;
    }


    /// <summary>
    /// Reads an integer Registry value, or returns the supplied default.
    ///
    /// REG_DWORD values normally arrive as Int32. Convert.ToInt32 also keeps
    /// this resilient if Windows returns another numeric representation.
    /// </summary>
    private static int ReadInt(
        RegistryKey key,
        string name,
        int defaultValue)
    {
        var value = key.GetValue(name);

        if (value is null)
            return defaultValue;

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return defaultValue;
        }
    }


    /// <summary>
    /// Reads a boolean represented as 0 or 1.
    /// </summary>
    private static bool ReadBool(
        RegistryKey key,
        string name,
        bool defaultValue)
    {
        var value = key.GetValue(name);

        if (value is null)
            return defaultValue;

        try
        {
            return Convert.ToInt32(value) != 0;
        }
        catch
        {
            return defaultValue;
        }
    }


    private static PlayerAccessMode ReadPlayerAccessMode(
        RegistryKey key,
        string name,
        PlayerAccessMode defaultValue)
    {
        var value = key.GetValue(name)?.ToString();

        // Earlier builds stored the inactive access mode as "Disabled".
        // Preserve that setting while writing the clearer "None" name from
        // now on.
        if (
            string.Equals(
                value,
                "Disabled",
                StringComparison.OrdinalIgnoreCase))
        {
            return PlayerAccessMode.None;
        }

        return
            Enum.TryParse<PlayerAccessMode>(
                value,
                ignoreCase: true,
                out var parsed)
                ? parsed
                : defaultValue;
    }


    // ========================================================
    // REGISTRY WRITE HELPERS
    // ========================================================

    private static void WriteString(
        RegistryKey key,
        string name,
        string value)
    {
        key.SetValue(
            name,
            value ?? string.Empty,
            RegistryValueKind.String);
    }


    private static void WriteInt(
        RegistryKey key,
        string name,
        int value)
    {
        key.SetValue(
            name,
            value,
            RegistryValueKind.DWord);
    }


    private static void WriteBool(
        RegistryKey key,
        string name,
        bool value)
    {
        key.SetValue(
            name,
            value ? 1 : 0,
            RegistryValueKind.DWord);
    }
}
