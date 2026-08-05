using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BCSTool.Services;

/// <summary>
/// Maintains a bounded rotating history of Bannerlord Coop campaign saves.
///
/// A Coop save is a pair of files with the same base name:
///
///     saveauto1.sav
///     saveauto1.json
///
/// The two files are always treated as one backup generation. BCS Tool never
/// intentionally keeps or rotates only one half of the pair.
///
/// The active game save is never renamed or moved. Backups live in a separate
/// BCS Backups directory so Bannerlord Coop continues to see only its normal
/// save files.
///
/// Rotation for a retention count of 5 is performed for BOTH extensions:
///
///     .backup4.sav/.json -> .backup5.sav/.json
///     .backup3.sav/.json -> .backup4.sav/.json
///     .backup2.sav/.json -> .backup3.sav/.json
///     .backup1.sav/.json -> .backup2.sav/.json
///     active.sav/.json -> .backup1.sav/.json
///
/// File operations are serialized so two closely-spaced save-completion
/// messages cannot rotate the same set concurrently.
/// </summary>
public sealed class SaveBackupService
{
    public const int MinimumBackupCount = 1;
    public const int MaximumBackupCount = 5;

    private readonly CoopConfigService _configService;
    private readonly SemaphoreSlim _rotationLock = new(1, 1);

    public SaveBackupService(
        CoopConfigService configService)
    {
        _configService =
            configService;
    }

    public string GameSavesDirectory
    {
        get
        {
            var dedicatedServerDirectory =
                Path.GetDirectoryName(
                    _configService.ServerConfigPath);

            if (string.IsNullOrWhiteSpace(dedicatedServerDirectory))
            {
                throw new InvalidOperationException(
                    "Could not determine the Bannerlord Coop DedicatedServer directory.");
            }

            return
                Path.Combine(
                    dedicatedServerDirectory,
                    "Game Saves");
        }
    }

    public string BackupDirectory =>
        Path.Combine(
            GameSavesDirectory,
            "BCS Backups");

    /// <summary>
    /// Creates one new paired rotation after Bannerlord reports a successful
    /// save.
    ///
    /// Returns the newest backup pair when a generation is created. Returns
    /// null when BOTH active files are unchanged from backup #1, which also
    /// prevents duplicate ConPTY redraws from creating duplicate rotations.
    /// </summary>
    public async Task<SaveBackupResult?> CreateBackupAsync(
        int retentionCount,
        CancellationToken cancellationToken)
    {
        retentionCount =
            Math.Clamp(
                retentionCount,
                MinimumBackupCount,
                MaximumBackupCount);

        await _rotationLock.WaitAsync(
            cancellationToken);

        try
        {
            var activePair =
                ResolveActiveSavePair();

            var sourceSnapshot =
                await WaitForStableSavePairAsync(
                    activePair,
                    cancellationToken);

            Directory.CreateDirectory(
                BackupDirectory);

            // The backup filename changed from ".bcsN" to ".backupN".
            // Preserve existing complete backup pairs by migrating them before
            // any new rotation takes place.
            RemoveIncompleteBackupPairs(
                activePair.BaseName);
// Older prototypes could also leave only one half of a save pair.
            // Remove those incomplete generations so restore never combines a
            // .sav and .json from different points in time.
            RemoveIncompleteBackupPairs(
                activePair.BaseName);

            var newestBackupPair =
                GetBackupPair(
                    activePair.BaseName,
                    1);

            // The ConPTY terminal can repaint old text. Only skip when BOTH
            // halves of the current save pair match backup generation #1.
            if (
                BackupPairExists(newestBackupPair) &&
                IsSameSnapshot(
                    newestBackupPair.SavPath,
                    sourceSnapshot.Sav) &&
                IsSameSnapshot(
                    newestBackupPair.JsonPath,
                    sourceSnapshot.Json))
            {
                return null;
            }

            for (
                var index = retentionCount;
                index >= 2;
                index--)
            {
                var previous =
                    GetBackupPair(
                        activePair.BaseName,
                        index - 1);

                var destination =
                    GetBackupPair(
                        activePair.BaseName,
                        index);

                if (BackupPairExists(previous))
                {
                    CopyPairPreservingTimestamp(
                        previous,
                        destination);
                }
                else
                {
                    // Prevent a stale/partial old generation from pretending
                    // to belong to the current rotation chain.
                    DeletePair(destination);
                }
            }

            CopyPairPreservingTimestamp(
                new SaveFilePair(
                    activePair.BaseName,
                    activePair.SavPath,
                    activePair.JsonPath),
                newestBackupPair);

            DeleteBeyondRetention(
                activePair.BaseName,
                retentionCount);

            return
                new SaveBackupResult(
                    newestBackupPair.SavPath,
                    newestBackupPair.JsonPath);
        }
        finally
        {
            _rotationLock.Release();
        }
    }

    /// <summary>
    /// Removes only complete/incomplete generations above the selected
    /// retention count. Disabling backups does not call this method, so
    /// existing recovery history is never deleted merely because the feature
    /// was turned off.
    /// </summary>
    public async Task TrimBackupsAsync(
        int retentionCount,
        CancellationToken cancellationToken)
    {
        retentionCount =
            Math.Clamp(
                retentionCount,
                MinimumBackupCount,
                MaximumBackupCount);

        await _rotationLock.WaitAsync(
            cancellationToken);

        try
        {
            var activePair =
                ResolveActiveSavePair();

            RemoveIncompleteBackupPairs(
                activePair.BaseName);
RemoveIncompleteBackupPairs(
                activePair.BaseName);

            DeleteBeyondRetention(
                activePair.BaseName,
                retentionCount);
        }
        finally
        {
            _rotationLock.Release();
        }
    }

    /// <summary>
    /// Returns every complete backup generation for the currently configured
    /// save. Generation 1 is the newest rotation slot.
    ///
    /// Incomplete pairs are never returned to the UI because restoring only a
    /// .sav or only a .json would create an invalid Coop save.
    /// </summary>
    public async Task<IReadOnlyList<SaveBackupInfo>> GetBackupsAsync(
        CancellationToken cancellationToken)
    {
        await _rotationLock.WaitAsync(
            cancellationToken);

        try
        {
            var activePair =
                ResolveActiveSavePair();

            if (!Directory.Exists(BackupDirectory))
            {
                return
                    Array.Empty<SaveBackupInfo>();
            }

            RemoveIncompleteBackupPairs(
                activePair.BaseName);
RemoveIncompleteBackupPairs(
                activePair.BaseName);

            var results =
                new List<SaveBackupInfo>();

            for (
                var generation = 1;
                generation <= MaximumBackupCount;
                generation++)
            {
                var pair =
                    GetBackupPair(
                        activePair.BaseName,
                        generation);

                if (!BackupPairExists(pair))
                    continue;

                var savInfo =
                    new FileInfo(
                        pair.SavPath);

                var jsonInfo =
                    new FileInfo(
                        pair.JsonPath);

                var modifiedUtc =
                    savInfo.LastWriteTimeUtc >= jsonInfo.LastWriteTimeUtc
                        ? savInfo.LastWriteTimeUtc
                        : jsonInfo.LastWriteTimeUtc;

                results.Add(
                    new SaveBackupInfo(
                        generation,
                        $"{activePair.BaseName}.backup{generation}",
                        modifiedUtc.ToLocalTime(),
                        pair.SavPath,
                        pair.JsonPath));
            }

            return
                results;
        }
        finally
        {
            _rotationLock.Release();
        }
    }


    /// <summary>
    /// Replaces the currently configured active save pair with one selected
    /// backup generation.
    ///
    /// The caller is responsible for ensuring the Bannerlord server is fully
    /// stopped before invoking this method.
    ///
    /// Restore is staged first, and the previous active files are held in
    /// temporary rollback copies while the pair is replaced. If either active
    /// copy fails, BCS Tool attempts to restore the previous active pair before
    /// reporting the failure.
    /// </summary>
    public async Task<SaveBackupRestoreResult> RestoreBackupAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        if (
            generation < MinimumBackupCount ||
            generation > MaximumBackupCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                $"Backup generation must be between {MinimumBackupCount} and {MaximumBackupCount}.");
        }

        await _rotationLock.WaitAsync(
            cancellationToken);

        try
        {
            var activePair =
                ResolveActiveSavePair();

            if (!Directory.Exists(BackupDirectory))
            {
                throw new DirectoryNotFoundException(
                    "The BCS Backups folder does not exist yet.");
            }

            RemoveIncompleteBackupPairs(
                activePair.BaseName);
RemoveIncompleteBackupPairs(
                activePair.BaseName);

            var selectedBackup =
                GetBackupPair(
                    activePair.BaseName,
                    generation);

            ValidateBackupPairForRestore(
                selectedBackup);

            Directory.CreateDirectory(
                GameSavesDirectory);

            var operationId =
                Guid.NewGuid().ToString("N");

            var stagedPair =
                new SaveFilePair(
                    activePair.BaseName,
                    Path.Combine(
                        GameSavesDirectory,
                        $"{activePair.BaseName}.bcs-restore-stage-{operationId}.sav.tmp"),
                    Path.Combine(
                        GameSavesDirectory,
                        $"{activePair.BaseName}.bcs-restore-stage-{operationId}.json.tmp"));

            var rollbackPair =
                new SaveFilePair(
                    activePair.BaseName,
                    Path.Combine(
                        GameSavesDirectory,
                        $"{activePair.BaseName}.bcs-restore-rollback-{operationId}.sav.tmp"),
                    Path.Combine(
                        GameSavesDirectory,
                        $"{activePair.BaseName}.bcs-restore-rollback-{operationId}.json.tmp"));

            var activeDestination =
                new SaveFilePair(
                    activePair.BaseName,
                    activePair.SavPath,
                    activePair.JsonPath);

            var hadActiveSav =
                File.Exists(
                    activePair.SavPath);

            var hadActiveJson =
                File.Exists(
                    activePair.JsonPath);

            try
            {
                // Stage the selected backup first. The active save is not
                // touched until both backup files have copied successfully.
                CopyPairPreservingTimestamp(
                    selectedBackup,
                    stagedPair);

                if (hadActiveSav)
                {
                    CopyPreservingTimestamp(
                        activePair.SavPath,
                        rollbackPair.SavPath);
                }

                if (hadActiveJson)
                {
                    CopyPreservingTimestamp(
                        activePair.JsonPath,
                        rollbackPair.JsonPath);
                }

                try
                {
                    CopyPairPreservingTimestamp(
                        stagedPair,
                        activeDestination);
                }
                catch (Exception restoreException)
                {
                    try
                    {
                        RestorePreviousActivePair(
                            activePair,
                            rollbackPair,
                            hadActiveSav,
                            hadActiveJson);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new IOException(
                            "The selected backup could not be applied, and BCS Tool was also unable to fully restore the previous active save pair.",
                            new AggregateException(
                                restoreException,
                                rollbackException));
                    }

                    throw;
                }

                return
                    new SaveBackupRestoreResult(
                        generation,
                        $"{activePair.BaseName}.backup{generation}",
                        activePair.SavPath,
                        activePair.JsonPath);
            }
            finally
            {
                DeletePair(
                    stagedPair);

                DeletePair(
                    rollbackPair);
            }
        }
        finally
        {
            _rotationLock.Release();
        }
    }


    private ActiveSavePair ResolveActiveSavePair()
    {
        var config =
            _configService.LoadServerConfig();

        var saveName =
            config.SaveName.Trim();

        if (saveName.EndsWith(
                ".sav",
                StringComparison.OrdinalIgnoreCase))
        {
            saveName =
                saveName[..^4];
        }

        if (
            string.IsNullOrWhiteSpace(saveName) ||
            !string.Equals(
                Path.GetFileName(saveName),
                saveName,
                StringComparison.Ordinal) ||
            saveName.IndexOfAny(
                Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                "The configured save name is not a valid file name.");
        }

        return
            new ActiveSavePair(
                saveName,
                Path.Combine(
                    GameSavesDirectory,
                    saveName + ".sav"),
                Path.Combine(
                    GameSavesDirectory,
                    saveName + ".json"));
    }

    private SaveFilePair GetBackupPair(
        string baseName,
        int generation)
    {
        var backupBaseName =
            $"{baseName}.backup{generation}";

        return
            new SaveFilePair(
                baseName,
                Path.Combine(
                    BackupDirectory,
                    backupBaseName + ".sav"),
                Path.Combine(
                    BackupDirectory,
                    backupBaseName + ".json"));
    }

    private void DeleteBeyondRetention(
        string baseName,
        int retentionCount)
    {
        for (
            var index = retentionCount + 1;
            index <= MaximumBackupCount;
            index++)
        {
            DeletePair(
                GetBackupPair(
                    baseName,
                    index));
        }
    }

    /// <summary>
    /// Deletes any generation for which exactly one half exists. This also
    /// cleans up .sav-only generations created by the first backup prototype.
    /// A partial generation is unsafe for future automatic recovery.
    /// </summary>
    private void RemoveIncompleteBackupPairs(
        string baseName)
    {
        for (
            var index = 1;
            index <= MaximumBackupCount;
            index++)
        {
            var pair =
                GetBackupPair(
                    baseName,
                    index);

            var savExists =
                File.Exists(pair.SavPath);

            var jsonExists =
                File.Exists(pair.JsonPath);

            if (savExists != jsonExists)
            {
                DeletePair(pair);
            }
        }
    }

    private static void ValidateBackupPairForRestore(
        SaveFilePair pair)
    {
        if (!File.Exists(pair.SavPath))
        {
            throw new FileNotFoundException(
                "The selected backup .sav file no longer exists.",
                pair.SavPath);
        }

        if (!File.Exists(pair.JsonPath))
        {
            throw new FileNotFoundException(
                "The selected backup companion .json file no longer exists.",
                pair.JsonPath);
        }

        if (
            new FileInfo(pair.SavPath).Length <= 0 ||
            new FileInfo(pair.JsonPath).Length <= 0)
        {
            throw new IOException(
                "The selected backup pair contains an empty file and cannot be restored safely.");
        }
    }


    private static void RestorePreviousActivePair(
        ActiveSavePair activePair,
        SaveFilePair rollbackPair,
        bool hadActiveSav,
        bool hadActiveJson)
    {
        if (hadActiveSav)
        {
            CopyPreservingTimestamp(
                rollbackPair.SavPath,
                activePair.SavPath);
        }
        else if (File.Exists(activePair.SavPath))
        {
            File.Delete(
                activePair.SavPath);
        }

        if (hadActiveJson)
        {
            CopyPreservingTimestamp(
                rollbackPair.JsonPath,
                activePair.JsonPath);
        }
        else if (File.Exists(activePair.JsonPath))
        {
            File.Delete(
                activePair.JsonPath);
        }
    }


    private static bool BackupPairExists(
        SaveFilePair pair) =>
        File.Exists(pair.SavPath) &&
        File.Exists(pair.JsonPath);

    private static void DeletePair(
        SaveFilePair pair)
    {
        if (File.Exists(pair.SavPath))
        {
            File.Delete(pair.SavPath);
        }

        if (File.Exists(pair.JsonPath))
        {
            File.Delete(pair.JsonPath);
        }
    }

    /// <summary>
    /// Even though the server's success marker is emitted after the save
    /// process completes, wait until BOTH files have two matching snapshots.
    /// This protects against delayed filesystem/OneDrive writes and avoids
    /// pairing files from different moments in the save operation.
    /// </summary>
    private static async Task<SavePairSnapshot> WaitForStableSavePairAsync(
        ActiveSavePair pair,
        CancellationToken cancellationToken)
    {
        SavePairSnapshot? previous = null;
        var stableChecks = 0;

        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (
                File.Exists(pair.SavPath) &&
                File.Exists(pair.JsonPath))
            {
                var savInfo =
                    new FileInfo(pair.SavPath);

                var jsonInfo =
                    new FileInfo(pair.JsonPath);

                var current =
                    new SavePairSnapshot(
                        new FileSnapshot(
                            savInfo.Length,
                            savInfo.LastWriteTimeUtc),
                        new FileSnapshot(
                            jsonInfo.Length,
                            jsonInfo.LastWriteTimeUtc));

                if (
                    current.Sav.Length > 0 &&
                    current.Json.Length > 0 &&
                    previous == current)
                {
                    stableChecks++;

                    if (stableChecks >= 2)
                    {
                        return current;
                    }
                }
                else
                {
                    stableChecks = 0;
                }

                previous =
                    current;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
        }

        if (!File.Exists(pair.SavPath))
        {
            throw new FileNotFoundException(
                "The active Bannerlord Coop .sav file was not found after a successful-save notification.",
                pair.SavPath);
        }

        if (!File.Exists(pair.JsonPath))
        {
            throw new FileNotFoundException(
                "The active Bannerlord Coop companion .json file was not found after a successful-save notification.",
                pair.JsonPath);
        }

        throw new IOException(
            "The Bannerlord Coop save pair did not become stable before backup rotation. " +
            $"SAV: {pair.SavPath}; JSON: {pair.JsonPath}");
    }

    private static bool IsSameSnapshot(
        string path,
        FileSnapshot source)
    {
        var info =
            new FileInfo(path);

        return
            info.Length == source.Length &&
            info.LastWriteTimeUtc == source.LastWriteTimeUtc;
    }

    /// <summary>
    /// Copies a .sav/.json pair as one logical operation. If either copy fails,
    /// both destination files are removed so BCS never leaves a generation
    /// that looks valid while containing only half a save.
    /// </summary>
    private static void CopyPairPreservingTimestamp(
        SaveFilePair source,
        SaveFilePair destination)
    {
        try
        {
            CopyPreservingTimestamp(
                source.SavPath,
                destination.SavPath);

            CopyPreservingTimestamp(
                source.JsonPath,
                destination.JsonPath);
        }
        catch
        {
            DeletePair(destination);
            throw;
        }
    }

    private static void CopyPreservingTimestamp(
        string source,
        string destination)
    {
        File.Copy(
            source,
            destination,
            overwrite: true);

        File.SetLastWriteTimeUtc(
            destination,
            File.GetLastWriteTimeUtc(source));
    }

    public sealed record SaveBackupInfo(
        int Generation,
        string Name,
        DateTime DateModified,
        string SavPath,
        string JsonPath);


    public sealed record SaveBackupRestoreResult(
        int Generation,
        string BackupName,
        string ActiveSavPath,
        string ActiveJsonPath);


    public sealed record SaveBackupResult(
        string SavPath,
        string JsonPath);

    private readonly record struct ActiveSavePair(
        string BaseName,
        string SavPath,
        string JsonPath);

    private readonly record struct SaveFilePair(
        string BaseName,
        string SavPath,
        string JsonPath);

    private readonly record struct SavePairSnapshot(
        FileSnapshot Sav,
        FileSnapshot Json);

    private readonly record struct FileSnapshot(
        long Length,
        DateTime LastWriteTimeUtc);
}
