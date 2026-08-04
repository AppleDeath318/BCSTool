using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BCSTool.Services;

/// <summary>
/// Locates BannerlordCoopServer.exe without scanning entire drives.
///
/// Detection priority:
///
/// 1. A currently running BannerlordCoopServer process.
/// 2. BCS Tool's own directory.
/// 3. Bannerlord's Documents\CoopData tree.
/// 4. Bannerlord Steam Workshop content in every Steam library.
/// 5. Installed Steam libraries that contain Mount & Blade II Bannerlord.
///
/// Steam libraries are discovered from Steam's Registry path plus
/// steamapps\libraryfolders.vdf.
///
/// Workshop detection scans:
///
/// steamapps\workshop\content±550\<workshop item>\DedicatedServer/// BannerlordCoopServer.exe
///
/// without hardcoding a specific Workshop item ID.
///
/// Bannerlord's Steam app manifest (261550) is also used when available to
/// resolve the normal game installation directory.
/// </summary>
public sealed class ServerExecutableLocator
{
    public const string DefaultExecutableName =
        "BannerlordCoopServer.exe";

    private const string BannerlordSteamAppId =
        "261550";

    private const int MaximumCandidates =
        64;


    public Task<ServerExecutableDetectionResult> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Detect(cancellationToken),
            cancellationToken);
    }


    private ServerExecutableDetectionResult Detect(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A running process is the strongest possible signal because it is
        // the exact executable the user is already using.
        var runningPath =
            TryGetRunningProcessPath();

        if (runningPath is not null)
        {
            return new ServerExecutableDetectionResult(
                runningPath,
                "running BannerlordCoopServer process",
                1);
        }

        var candidates =
            new List<Candidate>();

        AddDirectCandidate(
            candidates,
            Path.Combine(
                AppContext.BaseDirectory,
                DefaultExecutableName),
            "BCS Tool directory",
            priority: 10);

        cancellationToken.ThrowIfCancellationRequested();

        AddDocumentsCandidates(
            candidates,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        AddSteamCandidates(
            candidates,
            cancellationToken);

        var unique =
            candidates
                .Where(
                    candidate =>
                        File.Exists(candidate.Path))
                .GroupBy(
                    candidate =>
                        Path.GetFullPath(candidate.Path),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.Priority)
                .ThenByDescending(
                    candidate =>
                        GetLastWriteTimeUtcSafe(candidate.Path))
                .ThenBy(
                    candidate => candidate.Path,
                    StringComparer.OrdinalIgnoreCase)
                .Take(MaximumCandidates)
                .ToArray();

        if (unique.Length == 0)
        {
            return ServerExecutableDetectionResult.NotFound;
        }

        var best =
            unique[0];

        return new ServerExecutableDetectionResult(
            best.Path,
            best.Source,
            unique.Length);
    }


    private static string? TryGetRunningProcessPath()
    {
        Process[] processes;

        try
        {
            processes =
                Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(
                        DefaultExecutableName));
        }
        catch
        {
            return null;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var path =
                        process.MainModule?.FileName;

                    if (
                        IsExpectedExecutable(
                            path))
                    {
                        return
                            Path.GetFullPath(path!);
                    }
                }
                catch
                {
                    // Access to another process's MainModule can be denied.
                    // Continue with the remaining detection strategies.
                }
            }
        }

        return null;
    }


    private static void AddDocumentsCandidates(
        List<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        var documents =
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments);

        if (string.IsNullOrWhiteSpace(documents))
            return;

        var coopData =
            Path.Combine(
                documents,
                "Mount and Blade II Bannerlord",
                "CoopData");

        if (!Directory.Exists(coopData))
            return;

        AddDirectCandidate(
            candidates,
            Path.Combine(
                coopData,
                "DedicatedServer",
                DefaultExecutableName),
            "Bannerlord CoopData DedicatedServer directory",
            priority: 20);

        // CoopData is normally small. A bounded targeted search here catches
        // installations where the executable is placed in a CoopData
        // subdirectory without scanning the user's full Documents folder.
        AddRecursiveCandidates(
            candidates,
            coopData,
            "Bannerlord CoopData",
            priority: 25,
            cancellationToken);
    }


    private static void AddSteamCandidates(
        List<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        foreach (
            var steamRoot in
            GetSteamRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (
                var libraryRoot in
                GetSteamLibraryRoots(
                    steamRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Bannerlord Coop Server is commonly installed by the mod as
                // a Steam Workshop item rather than under steamapps\common.
                //
                // Scan each immediate Bannerlord Workshop item directory and
                // check the known DedicatedServer location. This remains a
                // bounded search and does not recurse through entire drives.
                AddSteamWorkshopCandidates(
                    candidates,
                    libraryRoot,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                var bannerlordRoot =
                    ResolveBannerlordInstallRoot(
                        libraryRoot);

                if (
                    bannerlordRoot is null ||
                    !Directory.Exists(
                        bannerlordRoot))
                {
                    continue;
                }

                AddDirectCandidate(
                    candidates,
                    Path.Combine(
                        bannerlordRoot,
                        DefaultExecutableName),
                    "Steam Bannerlord directory",
                    priority: 30);

                var modulesDirectory =
                    Path.Combine(
                        bannerlordRoot,
                        "Modules");

                AddRecursiveCandidates(
                    candidates,
                    modulesDirectory,
                    "Steam Bannerlord Modules",
                    priority: 31,
                    cancellationToken);

                var binDirectory =
                    Path.Combine(
                        bannerlordRoot,
                        "bin");

                AddRecursiveCandidates(
                    candidates,
                    binDirectory,
                    "Steam Bannerlord bin directory",
                    priority: 35,
                    cancellationToken);
            }
        }
    }


    /// <summary>
    /// Searches Bannerlord's Workshop content directory in one Steam library.
    ///
    /// Expected layout:
    ///
    /// steamapps\workshop\content\261550\<item id>\DedicatedServer\
    /// BannerlordCoopServer.exe
    ///
    /// The Workshop item ID is intentionally not hardcoded.
    /// </summary>
    private static void AddSteamWorkshopCandidates(
        List<Candidate> candidates,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        var bannerlordWorkshopRoot =
            Path.Combine(
                libraryRoot,
                "steamapps",
                "workshop",
                "content",
                BannerlordSteamAppId);

        if (
            !Directory.Exists(
                bannerlordWorkshopRoot))
        {
            return;
        }

        IEnumerable<string> workshopItems;

        try
        {
            workshopItems =
                Directory.EnumerateDirectories(
                    bannerlordWorkshopRoot);
        }
        catch
        {
            return;
        }

        foreach (
            var workshopItem in
            workshopItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dedicatedServerExecutable =
                Path.Combine(
                    workshopItem,
                    "DedicatedServer",
                    DefaultExecutableName);

            AddDirectCandidate(
                candidates,
                dedicatedServerExecutable,
                "Steam Workshop Bannerlord Coop dedicated server",
                priority: 15);

            if (
                candidates.Count >=
                MaximumCandidates)
            {
                return;
            }
        }
    }


    private static IEnumerable<string> GetSteamRoots()
    {
        var roots =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        AddRegistrySteamRoot(
            roots,
            Registry.CurrentUser,
            @"Software\Valve\Steam",
            "SteamPath");

        AddRegistrySteamRoot(
            roots,
            Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam",
            "InstallPath");

        AddRegistrySteamRoot(
            roots,
            Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam",
            "InstallPath");

        var programFilesX86 =
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86);

        if (
            !string.IsNullOrWhiteSpace(
                programFilesX86))
        {
            AddDirectoryIfPresent(
                roots,
                Path.Combine(
                    programFilesX86,
                    "Steam"));
        }

        var programFiles =
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);

        if (
            !string.IsNullOrWhiteSpace(
                programFiles))
        {
            AddDirectoryIfPresent(
                roots,
                Path.Combine(
                    programFiles,
                    "Steam"));
        }

        return roots;
    }


    private static void AddRegistrySteamRoot(
        HashSet<string> roots,
        RegistryKey hive,
        string subKey,
        string valueName)
    {
        try
        {
            using var key =
                hive.OpenSubKey(
                    subKey,
                    writable: false);

            var value =
                key?.GetValue(
                    valueName) as string;

            if (
                !string.IsNullOrWhiteSpace(
                    value))
            {
                AddDirectoryIfPresent(
                    roots,
                    value);
            }
        }
        catch
        {
            // Registry access is a convenience signal. Failure should never
            // prevent BCS Tool from starting.
        }
    }


    private static IEnumerable<string> GetSteamLibraryRoots(
        string steamRoot)
    {
        var libraries =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        AddDirectoryIfPresent(
            libraries,
            steamRoot);

        var libraryFile =
            Path.Combine(
                steamRoot,
                "steamapps",
                "libraryfolders.vdf");

        if (!File.Exists(libraryFile))
            return libraries;

        try
        {
            foreach (
                var line in
                File.ReadLines(
                    libraryFile))
            {
                if (
                    !TryReadVdfPair(
                        line,
                        out var key,
                        out var value))
                {
                    continue;
                }

                if (
                    !key.Equals(
                        "path",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value =
                    value.Replace(
                        @"\\",
                        @"\",
                        StringComparison.Ordinal);

                AddDirectoryIfPresent(
                    libraries,
                    value);
            }
        }
        catch
        {
            // A malformed or inaccessible libraryfolders.vdf should simply
            // reduce auto-detection coverage, not break application startup.
        }

        return libraries;
    }


    private static string? ResolveBannerlordInstallRoot(
        string libraryRoot)
    {
        var steamApps =
            Path.Combine(
                libraryRoot,
                "steamapps");

        var manifest =
            Path.Combine(
                steamApps,
                $"appmanifest_{BannerlordSteamAppId}.acf");

        var installDirectoryName =
            TryReadVdfValue(
                manifest,
                "installdir");

        if (
            !string.IsNullOrWhiteSpace(
                installDirectoryName))
        {
            var manifestResolved =
                Path.Combine(
                    steamApps,
                    "common",
                    installDirectoryName);

            if (
                Directory.Exists(
                    manifestResolved))
            {
                return manifestResolved;
            }
        }

        // Fallback for installations where the manifest cannot be read but
        // the standard Steam common directory is present.
        var standard =
            Path.Combine(
                steamApps,
                "common",
                "Mount & Blade II Bannerlord");

        return
            Directory.Exists(standard)
                ? standard
                : null;
    }


    private static string? TryReadVdfValue(
        string path,
        string requestedKey)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            foreach (
                var line in
                File.ReadLines(
                    path))
            {
                if (
                    TryReadVdfPair(
                        line,
                        out var key,
                        out var value) &&
                    key.Equals(
                        requestedKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return
                        value.Replace(
                            @"\\",
                            @"\",
                            StringComparison.Ordinal);
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }


    private static bool TryReadVdfPair(
        string line,
        out string key,
        out string value)
    {
        key = "";
        value = "";

        var firstQuote =
            line.IndexOf('"');

        if (firstQuote < 0)
            return false;

        var secondQuote =
            line.IndexOf(
                '"',
                firstQuote + 1);

        if (secondQuote < 0)
            return false;

        var thirdQuote =
            line.IndexOf(
                '"',
                secondQuote + 1);

        if (thirdQuote < 0)
            return false;

        var fourthQuote =
            line.IndexOf(
                '"',
                thirdQuote + 1);

        if (fourthQuote < 0)
            return false;

        key =
            line[
                (firstQuote + 1)..
                secondQuote];

        value =
            line[
                (thirdQuote + 1)..
                fourthQuote];

        return true;
    }


    private static void AddRecursiveCandidates(
        List<Candidate> candidates,
        string directory,
        string source,
        int priority,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return;

        try
        {
            var options =
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip =
                        FileAttributes.ReparsePoint
                };

            foreach (
                var path in
                Directory.EnumerateFiles(
                    directory,
                    DefaultExecutableName,
                    options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                AddDirectCandidate(
                    candidates,
                    path,
                    source,
                    priority);

                if (
                    candidates.Count >=
                    MaximumCandidates)
                {
                    return;
                }
            }
        }
        catch (
            OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Some game/mod directories can contain inaccessible entries.
            // Detection is best-effort and Browse remains available.
        }
    }


    private static void AddDirectCandidate(
        List<Candidate> candidates,
        string path,
        string source,
        int priority)
    {
        if (!IsExpectedExecutable(path))
            return;

        candidates.Add(
            new Candidate(
                Path.GetFullPath(path),
                source,
                priority));
    }


    private static bool IsExpectedExecutable(
        string? path)
    {
        if (
            string.IsNullOrWhiteSpace(
                path) ||
            !File.Exists(
                path))
        {
            return false;
        }

        return
            Path.GetFileName(path)
                .Equals(
                    DefaultExecutableName,
                    StringComparison.OrdinalIgnoreCase);
    }


    private static void AddDirectoryIfPresent(
        HashSet<string> directories,
        string? directory)
    {
        if (
            string.IsNullOrWhiteSpace(
                directory))
        {
            return;
        }

        try
        {
            var fullPath =
                Path.GetFullPath(
                    directory);

            if (
                Directory.Exists(
                    fullPath))
            {
                directories.Add(
                    fullPath);
            }
        }
        catch
        {
            // Ignore malformed paths from stale Registry/VDF entries.
        }
    }


    private static DateTime GetLastWriteTimeUtcSafe(
        string path)
    {
        try
        {
            return
                File.GetLastWriteTimeUtc(
                    path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }


    private sealed record Candidate(
        string Path,
        string Source,
        int Priority);
}


/// <summary>
/// Result returned by ServerExecutableLocator.
/// </summary>
public sealed class ServerExecutableDetectionResult
{
    public static ServerExecutableDetectionResult NotFound { get; } =
        new(
            null,
            "not found",
            0);


    public ServerExecutableDetectionResult(
        string? path,
        string source,
        int candidateCount)
    {
        Path = path;
        Source = source;
        CandidateCount = candidateCount;
    }


    public string? Path { get; }

    public string Source { get; }

    public int CandidateCount { get; }

    public bool Found =>
        !string.IsNullOrWhiteSpace(
            Path);
}
