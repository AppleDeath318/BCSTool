using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BCSTool.Services;

/// <summary>
/// Lists and restores Bannerlord Coop's native per-world backup generations.
/// Native backups remain beside the active save in the Game Saves directory;
/// BCS Tool does not create, rotate, delete, or move them.
/// </summary>
public sealed class NativeSaveBackupService
{
    private readonly CoopConfigService _configService;
    private readonly SemaphoreSlim _restoreLock = new(1, 1);

    public NativeSaveBackupService(
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

    /// <summary>
    /// Returns native backup generations for the save configured in
    /// server-config.json. Only complete .backupN.sav/.backupN.json pairs are
    /// returned, so a partial generation can never be loaded accidentally.
    /// </summary>
    public Task<IReadOnlyList<NativeBackupInfo>> GetBackupsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeSave =
            ResolveActiveSave();

        if (!Directory.Exists(GameSavesDirectory))
        {
            return Task.FromResult<IReadOnlyList<NativeBackupInfo>>(
                Array.Empty<NativeBackupInfo>());
        }

        var backups =
            DiscoverBackups(
                activeSave.BaseName,
                cancellationToken);

        return Task.FromResult<IReadOnlyList<NativeBackupInfo>>(
            backups);
    }

    /// <summary>
    /// Copies one complete native generation over the active save without
    /// consuming, moving, or otherwise changing either backup file.
    /// </summary>
    public async Task<NativeBackupRestoreResult> RestoreBackupAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        if (generation < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "Native backup generation must be at least 1.");
        }

        await _restoreLock.WaitAsync(
            cancellationToken);

        try
        {
            var activeSave =
                ResolveActiveSave();

            var backup =
                DiscoverBackups(
                        activeSave.BaseName,
                        cancellationToken)
                    .FirstOrDefault(
                        candidate => candidate.Generation == generation);

            if (backup is null)
            {
                throw new FileNotFoundException(
                    $"Native backup generation {generation} was not found for '{activeSave.BaseName}'.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var restoreItems =
                new List<RestoreItem>
                {
                    new(
                        backup.SavPath,
                        activeSave.SavPath),
                    new(
                        backup.JsonPath,
                        activeSave.JsonPath)
                };

            ApplyRestore(
                restoreItems,
                cancellationToken);

            return
                new NativeBackupRestoreResult(
                    backup.Name,
                    activeSave.SavPath,
                    activeSave.JsonPath);
        }
        finally
        {
            _restoreLock.Release();
        }
    }

    private IReadOnlyList<NativeBackupInfo> DiscoverBackups(
        string baseName,
        CancellationToken cancellationToken)
    {
        var discovered =
            new Dictionary<int, DiscoveredGeneration>();

        foreach (var path in Directory.EnumerateFiles(GameSavesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName =
                Path.GetFileName(path);

            if (!TryParseNativeBackupFileName(
                    baseName,
                    fileName,
                    out var generation,
                    out var kind))
            {
                continue;
            }

            if (!discovered.TryGetValue(
                    generation,
                    out var candidate))
            {
                candidate =
                    new DiscoveredGeneration();

                discovered[generation] =
                    candidate;
            }

            switch (kind)
            {
                case NativeBackupFileKind.Sav:
                    candidate.SavPath = path;
                    break;

                case NativeBackupFileKind.Json:
                    candidate.JsonPath = path;
                    break;
            }
        }

        return
            discovered
                .Where(
                    pair =>
                        !string.IsNullOrWhiteSpace(pair.Value.SavPath) &&
                        !string.IsNullOrWhiteSpace(pair.Value.JsonPath))
                .OrderBy(pair => pair.Key)
                .Select(
                    pair =>
                    {
                        var savPath =
                            pair.Value.SavPath!;

                        var modified =
                            File.GetLastWriteTime(savPath);

                        var jsonPath =
                            pair.Value.JsonPath!;

                        var jsonModified =
                            File.GetLastWriteTime(jsonPath);

                        if (jsonModified > modified)
                            modified = jsonModified;

                        return
                            new NativeBackupInfo(
                                pair.Key,
                                pair.Key == 1
                                    ? $"{baseName}.backup{pair.Key} (newest)"
                                    : $"{baseName}.backup{pair.Key}",
                                modified,
                                savPath,
                                jsonPath);
                    })
                .ToArray();
    }

    private static bool TryParseNativeBackupFileName(
        string baseName,
        string fileName,
        out int generation,
        out NativeBackupFileKind kind)
    {
        generation = 0;
        kind = NativeBackupFileKind.Sav;

        var prefix =
            baseName + ".backup";

        if (!fileName.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix =
            fileName[prefix.Length..];

        if (suffix.EndsWith(
                ".sav",
                StringComparison.OrdinalIgnoreCase))
        {
            kind = NativeBackupFileKind.Sav;
            suffix = suffix[..^4];
        }
        else if (suffix.EndsWith(
                     ".json",
                     StringComparison.OrdinalIgnoreCase))
        {
            kind = NativeBackupFileKind.Json;
            suffix = suffix[..^5];
        }
        else
        {
            return false;
        }

        return
            int.TryParse(
                suffix,
                out generation) &&
            generation > 0;
    }

    private ActiveSave ResolveActiveSave()
    {
        var config =
            _configService.LoadServerConfig();

        var baseName =
            config.SaveName.Trim();

        if (baseName.EndsWith(
                ".sav",
                StringComparison.OrdinalIgnoreCase) ||
            baseName.EndsWith(
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            baseName =
                Path.GetFileNameWithoutExtension(baseName);
        }

        if (
            string.IsNullOrWhiteSpace(baseName) ||
            !string.Equals(
                Path.GetFileName(baseName),
                baseName,
                StringComparison.Ordinal) ||
            baseName.IndexOfAny(
                Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                "The configured save name is not a valid file name.");
        }

        return
            new ActiveSave(
                baseName,
                Path.Combine(
                    GameSavesDirectory,
                    baseName + ".sav"),
                Path.Combine(
                    GameSavesDirectory,
                    baseName + ".json"));
    }

    private static void ApplyRestore(
        IReadOnlyList<RestoreItem> items,
        CancellationToken cancellationToken)
    {
        var transactionId =
            Guid.NewGuid().ToString("N");

        foreach (var item in items)
        {
            item.StagePath =
                item.TargetPath + ".bcs-native-restore-" + transactionId + ".tmp";

            item.RollbackPath =
                item.TargetPath + ".bcs-native-restore-" + transactionId + ".rollback";
        }

        try
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                File.Copy(
                    item.SourcePath,
                    item.StagePath,
                    overwrite: true);

                item.TargetExisted =
                    File.Exists(item.TargetPath);

                if (item.TargetExisted)
                {
                    File.Copy(
                        item.TargetPath,
                        item.RollbackPath,
                        overwrite: true);
                }
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Mark the attempt first so even a partial failed overwrite is
                // rolled back to the original active file.
                item.Applied =
                    true;

                File.Copy(
                    item.StagePath,
                    item.TargetPath,
                    overwrite: true);
            }
        }
        catch
        {
            foreach (var item in items.Reverse())
            {
                if (!item.Applied)
                    continue;

                if (item.TargetExisted)
                {
                    File.Copy(
                        item.RollbackPath,
                        item.TargetPath,
                        overwrite: true);
                }
                else if (File.Exists(item.TargetPath))
                {
                    File.Delete(item.TargetPath);
                }
            }

            throw;
        }
        finally
        {
            foreach (var item in items)
            {
                DeleteIfExists(item.StagePath);
                DeleteIfExists(item.RollbackPath);
            }
        }
    }

    private static void DeleteIfExists(
        string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private sealed record ActiveSave(
        string BaseName,
        string SavPath,
        string JsonPath);

    private sealed class DiscoveredGeneration
    {
        public string? SavPath { get; set; }
        public string? JsonPath { get; set; }
    }

    private sealed class RestoreItem
    {
        public RestoreItem(
            string sourcePath,
            string targetPath)
        {
            SourcePath = sourcePath;
            TargetPath = targetPath;
        }

        public string SourcePath { get; }
        public string TargetPath { get; }
        public string StagePath { get; set; } = "";
        public string RollbackPath { get; set; } = "";
        public bool TargetExisted { get; set; }
        public bool Applied { get; set; }
    }

    private enum NativeBackupFileKind
    {
        Sav,
        Json
    }

    public sealed record NativeBackupInfo(
        int Generation,
        string Name,
        DateTime DateModified,
        string SavPath,
        string JsonPath);

    public sealed record NativeBackupRestoreResult(
        string BackupName,
        string ActiveSavPath,
        string ActiveJsonPath);
}
