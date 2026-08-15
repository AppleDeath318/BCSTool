using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Persists BCS Tool's player banlist, whitelist, and learned Steam/Hero/name
/// identity cache under %LOCALAPPDATA%\BCS Tool.
///
/// SteamID64 is the authority. HeroId and character name are metadata used to
/// explain and accelerate identity resolution; runtime enforcement is performed
/// only after the active save confirms the current HeroId -> SteamID mapping.
/// The HeroId -> name evidence comes from the live hero list or from a cached
/// name whose HeroId/SteamID pair is revalidated against that active save.
/// </summary>
public sealed class PlayerAccessService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

    private readonly LogService _logService;
    private readonly CoopConfigService _coopConfigService;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public PlayerAccessService(
        LogService logService,
        CoopConfigService coopConfigService)
    {
        _logService = logService;
        _coopConfigService = coopConfigService;

        DataDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "BCS Tool");

        BanlistPath = Path.Combine(DataDirectory, "banlist.json");
        WhitelistPath = Path.Combine(DataDirectory, "whitelist.json");
        IdentityCachePath = Path.Combine(DataDirectory, "player-identities.json");

        Directory.CreateDirectory(DataDirectory);
        EnsureFileExists(BanlistPath);
        EnsureFileExists(WhitelistPath);
        EnsureFileExists(IdentityCachePath);
    }

    public string DataDirectory { get; }
    public string BanlistPath { get; }
    public string WhitelistPath { get; }
    public string IdentityCachePath { get; }

    public async Task<IReadOnlyList<PlayerAccessEntry>> LoadBanlistAsync()
    {
        return await LoadEntriesAsync<PlayerAccessEntry>(BanlistPath);
    }

    public async Task<IReadOnlyList<PlayerAccessEntry>> LoadWhitelistAsync()
    {
        return await LoadEntriesAsync<PlayerAccessEntry>(WhitelistPath);
    }

    public async Task<IReadOnlyList<PlayerIdentityEntry>> LoadIdentityCacheAsync()
    {
        return await LoadEntriesAsync<PlayerIdentityEntry>(IdentityCachePath);
    }

    public Task SaveBanlistAsync(IEnumerable<PlayerAccessEntry> entries) =>
        SaveEntriesAsync(BanlistPath, NormalizeAccessEntries(entries));

    public Task SaveWhitelistAsync(IEnumerable<PlayerAccessEntry> entries) =>
        SaveEntriesAsync(WhitelistPath, NormalizeAccessEntries(entries));

    public Task SaveIdentityCacheAsync(IEnumerable<PlayerIdentityEntry> entries) =>
        SaveEntriesAsync(IdentityCachePath, NormalizeIdentityEntries(entries));

    /// <summary>
    /// Merges resolved identities into the persistent cache with one atomic
    /// write. Unchanged entries do not rewrite the file.
    /// </summary>
    public async Task UpsertIdentitiesAsync(
        IEnumerable<PlayerIdentityEntry> identities)
    {
        var incoming =
            NormalizeIdentityEntries(identities);

        if (incoming.Count == 0)
            return;

        await _fileLock.WaitAsync();

        try
        {
            var entries =
                await LoadEntriesWithoutLockAsync<PlayerIdentityEntry>(
                    IdentityCachePath);

            var changed =
                false;

            foreach (var identity in incoming)
            {
                var existing =
                    entries.FirstOrDefault(
                        entry =>
                            string.Equals(
                                entry.SteamId,
                                identity.SteamId,
                                StringComparison.Ordinal));

                if (existing is null)
                {
                    entries.Add(identity);
                    changed = true;
                    continue;
                }

                if (
                    string.Equals(
                        existing.HeroId,
                        identity.HeroId,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        existing.LastKnownCharacterName,
                        identity.LastKnownCharacterName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                existing.HeroId = identity.HeroId;
                existing.LastKnownCharacterName =
                    identity.LastKnownCharacterName;
                changed = true;
            }

            if (!changed)
                return;

            await WriteEntriesAtomicAsync(
                IdentityCachePath,
                NormalizeIdentityEntries(entries));
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Reads the ControllerId -> HeroId relationships from the active Coop save
    /// companion JSON. The save's Players array is the authoritative persistent
    /// controller mapping supplied by Bannerlord Coop.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> LoadActiveSavePlayerMapAsync()
    {
        var config = _coopConfigService.LoadServerConfig();
        var saveName = config.SaveName.Trim();

        if (saveName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) ||
            saveName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            saveName = Path.GetFileNameWithoutExtension(saveName);
        }

        var dedicatedServerDirectory =
            Path.GetDirectoryName(_coopConfigService.ServerConfigPath)
            ?? throw new InvalidOperationException(
                "Could not determine the Bannerlord Coop DedicatedServer directory.");

        var saveJsonPath =
            Path.Combine(
                dedicatedServerDirectory,
                "Game Saves",
                saveName + ".json");

        if (!File.Exists(saveJsonPath))
        {
            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        }

        string text;

        using (var stream = new FileStream(
                   saveJsonPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete,
                   64 * 1024,
                   FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream))
        {
            text = await reader.ReadToEndAsync();
        }

        using var document = JsonDocument.Parse(text);

        var result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        if (!document.RootElement.TryGetProperty("Players", out var players) ||
            players.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var player in players.EnumerateArray())
        {
            if (player.ValueKind != JsonValueKind.Object)
                continue;

            var controllerId = GetString(player, "ControllerId");
            var heroId = GetString(player, "HeroId");

            if (!IsValidSteamId64(controllerId) ||
                string.IsNullOrWhiteSpace(heroId))
            {
                continue;
            }

            result[heroId.Trim()] = controllerId.Trim();
        }

        return result;
    }

    public static bool IsValidSteamId64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 17)
            return false;

        foreach (var character in value)
        {
            if (!char.IsDigit(character))
                return false;
        }

        return true;
    }

    private async Task<IReadOnlyList<T>> LoadEntriesAsync<T>(string path)
        where T : new()
    {
        await _fileLock.WaitAsync();

        try
        {
            return await LoadEntriesWithoutLockAsync<T>(path);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<List<T>> LoadEntriesWithoutLockAsync<T>(string path)
        where T : new()
    {
        try
        {
            var text = await File.ReadAllTextAsync(path);

            if (string.IsNullOrWhiteSpace(text))
                return new List<T>();

            using var document = JsonDocument.Parse(text);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                _logService.Write(
                    $"Player access file is not a JSON array; treating it as empty: {path}");
                return new List<T>();
            }

            var result = new List<T>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                try
                {
                    var entry = item.Deserialize<T>(JsonOptions);
                    if (entry is not null)
                        result.Add(entry);
                }
                catch (JsonException ex)
                {
                    _logService.Write(
                        $"Ignored malformed player access entry in {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            _logService.Write(
                $"Could not parse player access file {path}; treating it as empty: {ex.Message}");
            return new List<T>();
        }
        catch (IOException ex)
        {
            _logService.Write(
                $"Could not read player access file {path}; treating it as empty: {ex.Message}");
            return new List<T>();
        }
    }

    private async Task SaveEntriesAsync<T>(string path, IReadOnlyList<T> entries)
    {
        await _fileLock.WaitAsync();

        try
        {
            await WriteEntriesAtomicAsync(path, entries);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static async Task WriteEntriesAtomicAsync<T>(
        string path,
        IReadOnlyList<T> entries)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Invalid player access path."));

        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(entries, JsonOptions);

        await File.WriteAllTextAsync(tempPath, json);

        File.Move(
            tempPath,
            path,
            overwrite: true);
    }

    internal static IReadOnlyList<PlayerAccessEntry> NormalizeAccessEntries(
        IEnumerable<PlayerAccessEntry> entries)
    {
        return entries
            .Where(entry => IsValidSteamId64(entry.SteamId?.Trim()))
            .GroupBy(
                entry => entry.SteamId?.Trim() ?? "",
                StringComparer.Ordinal)
            .Select(group => group.Last())
            .Select(
                entry =>
                    new PlayerAccessEntry
                    {
                        SteamId = entry.SteamId?.Trim() ?? "",
                        LastKnownCharacterName = entry.LastKnownCharacterName?.Trim() ?? "",
                        HeroId = entry.HeroId?.Trim() ?? "",
                        Note = entry.Note?.Trim() ?? ""
                    })
            .OrderBy(entry => entry.SteamId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<PlayerIdentityEntry> NormalizeIdentityEntries(
        IEnumerable<PlayerIdentityEntry> entries)
    {
        return entries
            .Where(entry => IsValidSteamId64(entry.SteamId?.Trim()))
            .GroupBy(
                entry => entry.SteamId?.Trim() ?? "",
                StringComparer.Ordinal)
            .Select(group => group.Last())
            .Select(
                entry =>
                    new PlayerIdentityEntry
                    {
                        SteamId = entry.SteamId?.Trim() ?? "",
                        HeroId = entry.HeroId?.Trim() ?? "",
                        LastKnownCharacterName = entry.LastKnownCharacterName?.Trim() ?? ""
                    })
            .OrderBy(entry => entry.SteamId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString() ?? "";
    }

    private static void EnsureFileExists(string path)
    {
        if (File.Exists(path))
            return;

        File.WriteAllText(path, "[]" + Environment.NewLine);
    }
}
