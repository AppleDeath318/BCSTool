using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BCSTool.Infrastructure;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Checks the latest stable GitHub release and stages a verified replacement
/// executable. BCS Tool performs one automatic check per launch; all download
/// and installation work remains explicitly user initiated.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepositoryUrl =
        "https://github.com/AppleDeath318/BCSTool";

    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/AppleDeath318/BCSTool/releases/latest";

    private static readonly Regex Sha256Regex =
        new(
            @"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])",
            RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly LogService _logService;

    public UpdateService(LogService logService)
    {
        _logService = logService;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"BCSTool/{AppVersion.Version}");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add(
            "X-GitHub-Api-Version",
            "2022-11-28");
    }

    public event EventHandler? StatusChanged;

    public UpdateCheckState State { get; private set; } =
        UpdateCheckState.NotChecked;

    public UpdateRelease? AvailableRelease { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken))
            return;

        try
        {
            SetState(UpdateCheckState.Checking);

            using var response = await _httpClient.GetAsync(
                LatestReleaseApiUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            await using var responseStream =
                await response.Content.ReadAsStreamAsync(cancellationToken);

            var release =
                await JsonSerializer.DeserializeAsync<GitHubRelease>(
                    responseStream,
                    cancellationToken: cancellationToken)
                ?? throw new InvalidDataException(
                    "GitHub returned an empty release response.");

            if (release.Draft || release.Prerelease)
            {
                throw new InvalidDataException(
                    "GitHub did not return a stable release.");
            }

            var versionText =
                release.TagName.Trim().TrimStart('v', 'V');

            if (!Version.TryParse(versionText, out var latestVersion))
            {
                throw new InvalidDataException(
                    $"Release tag '{release.TagName}' is not a valid version.");
            }

            if (!Version.TryParse(AppVersion.Version, out var currentVersion))
            {
                throw new InvalidDataException(
                    $"Current version '{AppVersion.Version}' is not valid.");
            }

            AvailableRelease = null;

            if (latestVersion <= currentVersion)
            {
                SetState(UpdateCheckState.UpToDate);
                return;
            }

            var executableName =
                $"BCS Tool v{latestVersion}.exe";
            var checksumName =
                $"{executableName}.sha256";

            var executableAsset = release.Assets.FirstOrDefault(
                asset => string.Equals(
                    asset.Name,
                    executableName,
                    StringComparison.OrdinalIgnoreCase));
            var checksumAsset = release.Assets.FirstOrDefault(
                asset => string.Equals(
                    asset.Name,
                    checksumName,
                    StringComparison.OrdinalIgnoreCase));

            if (executableAsset is null || checksumAsset is null)
            {
                throw new InvalidDataException(
                    "The latest release does not contain the expected " +
                    "executable and SHA-256 files.");
            }

            AvailableRelease = new UpdateRelease(
                latestVersion,
                release.TagName,
                new Uri(release.HtmlUrl),
                executableName,
                new Uri(executableAsset.BrowserDownloadUrl),
                new Uri(checksumAsset.BrowserDownloadUrl));

            _logService.Write(
                $"BCS Tool update {latestVersion} is available.");
            SetState(UpdateCheckState.UpdateAvailable);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            SetFailure("The update check was canceled.");
        }
        catch (Exception ex)
        {
            SetFailure(
                $"Could not check for updates: {GetFriendlyMessage(ex)}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Downloads and verifies the selected release, then starts an external
    /// PowerShell process that waits for BCS Tool to close before replacing it.
    /// </summary>
    public async Task<bool> PrepareAndLaunchInstallerAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken))
            return false;

        string? updateDirectory = null;

        try
        {
            var release =
                AvailableRelease
                ?? throw new InvalidOperationException(
                    "No update is currently available.");

            SetState(UpdateCheckState.Downloading);

            updateDirectory = Path.Combine(
                Path.GetTempPath(),
                $"BCSTool-Update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(updateDirectory);

            var downloadedExecutablePath = Path.Combine(
                updateDirectory,
                release.ExecutableFileName);
            var checksumPath = Path.Combine(
                updateDirectory,
                $"{release.ExecutableFileName}.sha256");

            await DownloadFileAsync(
                release.ExecutableDownloadUri,
                downloadedExecutablePath,
                cancellationToken);
            await DownloadFileAsync(
                release.ChecksumDownloadUri,
                checksumPath,
                cancellationToken);

            await VerifySha256Async(
                downloadedExecutablePath,
                checksumPath,
                cancellationToken);

            var currentExecutablePath = Environment.ProcessPath;

            if (
                string.IsNullOrWhiteSpace(currentExecutablePath) ||
                !File.Exists(currentExecutablePath) ||
                !string.Equals(
                    Path.GetExtension(currentExecutablePath),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "BCS Tool could not determine its current executable path.");
            }

            var installerScriptPath = Path.Combine(
                updateDirectory,
                "Install-BCSToolUpdate.ps1");

            await File.WriteAllTextAsync(
                installerScriptPath,
                InstallerScript,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(installerScriptPath);
            startInfo.ArgumentList.Add("-ProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-CurrentExecutablePath");
            startInfo.ArgumentList.Add(currentExecutablePath);
            startInfo.ArgumentList.Add("-DownloadedExecutablePath");
            startInfo.ArgumentList.Add(downloadedExecutablePath);
            startInfo.ArgumentList.Add("-NewFileName");
            startInfo.ArgumentList.Add(release.ExecutableFileName);
            startInfo.ArgumentList.Add("-UpdateDirectory");
            startInfo.ArgumentList.Add(updateDirectory);

            using var installerProcess =
                Process.Start(startInfo);

            if (installerProcess is null)
            {
                throw new InvalidOperationException(
                    "Windows could not start the update installer.");
            }

            _logService.Write(
                $"BCS Tool update {release.Version} was downloaded and verified.");
            SetState(UpdateCheckState.Installing);
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDirectory(updateDirectory);
            SetFailure("The update download was canceled.");
            return false;
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(updateDirectory);
            SetFailure(
                $"Could not install the update: {GetFriendlyMessage(ex)}");
            return false;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task DownloadFileAsync(
        Uri downloadUri,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task VerifySha256Async(
        string executablePath,
        string checksumPath,
        CancellationToken cancellationToken)
    {
        var checksumText =
            await File.ReadAllTextAsync(
                checksumPath,
                cancellationToken);
        var match = Sha256Regex.Match(checksumText);

        if (!match.Success)
        {
            throw new InvalidDataException(
                "The release checksum file is invalid.");
        }

        await using var executable = new FileStream(
            executablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var actualHash =
            await SHA256.HashDataAsync(executable, cancellationToken);
        var actualHashText =
            Convert.ToHexString(actualHash);

        if (!string.Equals(
                actualHashText,
                match.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The downloaded executable failed SHA-256 verification.");
        }
    }

    private void SetFailure(string message)
    {
        ErrorMessage = message;
        _logService.Write(message);
        SetState(UpdateCheckState.Failed);
    }

    private void SetState(UpdateCheckState state)
    {
        State = state;

        if (state != UpdateCheckState.Failed)
            ErrorMessage = null;

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string GetFriendlyMessage(Exception exception)
    {
        if (exception is HttpRequestException httpException)
        {
            return httpException.StatusCode is null
                ? "GitHub could not be reached. Check your internet connection."
                : $"GitHub returned HTTP {(int)httpException.StatusCode.Value}.";
        }

        return exception.Message;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temporary update files can be removed by Windows later.
        }
    }

    public void Dispose()
    {
        // A startup check can still be unwinding while WPF exits. Disposing
        // HttpClient cancels its request; the semaphore is intentionally left
        // alive so that operation can safely execute its finally block.
        _httpClient.Dispose();
    }

    private const string InstallerScript =
        """
        param(
            [Parameter(Mandatory = $true)][int]$ProcessId,
            [Parameter(Mandatory = $true)][string]$CurrentExecutablePath,
            [Parameter(Mandatory = $true)][string]$DownloadedExecutablePath,
            [Parameter(Mandatory = $true)][string]$NewFileName,
            [Parameter(Mandatory = $true)][string]$UpdateDirectory
        )

        $ErrorActionPreference = 'Stop'
        $destinationDirectory = [System.IO.Path]::GetDirectoryName($CurrentExecutablePath)
        $destinationPath = Join-Path $destinationDirectory $NewFileName
        $currentBackupPath = Join-Path $UpdateDirectory 'previous-version.exe'
        $destinationBackupPath = Join-Path $UpdateDirectory 'existing-destination.exe'
        $currentMoved = $false
        $destinationMoved = $false
        $newExecutableCopied = $false

        try {
            for ($attempt = 0; $attempt -lt 90; $attempt++) {
                if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
                    break
                }

                Start-Sleep -Milliseconds 500
            }

            if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
                throw 'BCS Tool did not close within 45 seconds.'
            }

            if (-not (Test-Path -LiteralPath $DownloadedExecutablePath -PathType Leaf)) {
                throw 'The downloaded update file is missing.'
            }

            if (Test-Path -LiteralPath $CurrentExecutablePath -PathType Leaf) {
                Move-Item -LiteralPath $CurrentExecutablePath -Destination $currentBackupPath -Force
                $currentMoved = $true
            }

            if (
                $destinationPath -ne $CurrentExecutablePath -and
                (Test-Path -LiteralPath $destinationPath -PathType Leaf)
            ) {
                Move-Item -LiteralPath $destinationPath -Destination $destinationBackupPath -Force
                $destinationMoved = $true
            }

            Copy-Item -LiteralPath $DownloadedExecutablePath -Destination $destinationPath -Force
            $newExecutableCopied = $true
            Start-Process -FilePath $destinationPath -WorkingDirectory $destinationDirectory

            Start-Sleep -Seconds 2
            Remove-Item -LiteralPath $UpdateDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        catch {
            if ($newExecutableCopied) {
                Remove-Item -LiteralPath $destinationPath -Force -ErrorAction SilentlyContinue
            }

            if (
                $destinationMoved -and
                (Test-Path -LiteralPath $destinationBackupPath -PathType Leaf)
            ) {
                Move-Item -LiteralPath $destinationBackupPath -Destination $destinationPath -Force
            }

            if (
                $currentMoved -and
                (Test-Path -LiteralPath $currentBackupPath -PathType Leaf)
            ) {
                Move-Item -LiteralPath $currentBackupPath -Destination $CurrentExecutablePath -Force
            }

            if (
                -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and
                (Test-Path -LiteralPath $CurrentExecutablePath -PathType Leaf)
            ) {
                Start-Process -FilePath $CurrentExecutablePath -WorkingDirectory $destinationDirectory
            }

            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show(
                "BCS Tool could not install the update.`n`n$($_.Exception.Message)",
                'BCS Tool Update',
                'OK',
                'Error') | Out-Null
        }
        """;

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = "";

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAsset[] Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = "";
    }
}
