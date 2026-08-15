using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BCSTool.Infrastructure;

namespace BCSTool.Services;

/// <summary>
/// Loads the developer's public GitHub profile once per application launch.
/// The last successful name and avatar are cached for offline About windows.
/// </summary>
public sealed class GitHubProfileService : IDisposable
{
    private const string ProfileApiUrl =
        "https://api.github.com/user/84254026";
    private const string FallbackLogin = "AppleDeath318";
    private const string FallbackDisplayName = "Apar";
    private const int MaximumAvatarBytes = 5 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly object _refreshLock = new();
    private readonly string _cacheDirectory;
    private readonly string _metadataPath;
    private readonly string _avatarPath;

    private GitHubProfileCache? _cache;
    private Task<GitHubProfileSnapshot>? _refreshTask;

    public GitHubProfileService()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "BCS Tool",
            "Cache",
            "GitHubProfile");
        _metadataPath = Path.Combine(
            _cacheDirectory,
            "profile.json");
        _avatarPath = Path.Combine(
            _cacheDirectory,
            "avatar.image");

        _cache = TryReadCache();
        CachedProfile = CreateSnapshot(_cache);

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
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

    public GitHubProfileSnapshot CachedProfile { get; private set; }

    /// <summary>
    /// Shares one refresh task across every About window opened during this
    /// process, so GitHub is contacted at most once per application launch.
    /// </summary>
    public Task<GitHubProfileSnapshot> RefreshAsync()
    {
        lock (_refreshLock)
        {
            return _refreshTask ??= RefreshCoreAsync();
        }
    }

    private async Task<GitHubProfileSnapshot> RefreshCoreAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                ProfileApiUrl);

            if (
                !string.IsNullOrWhiteSpace(_cache?.ProfileETag) &&
                EntityTagHeaderValue.TryParse(
                    _cache.ProfileETag,
                    out var profileETag))
            {
                request.Headers.IfNoneMatch.Add(profileETag);
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (
                    TryCreateHttpsUri(
                        _cache?.AvatarUrl,
                        out var cachedAvatarUri))
                {
                    await TryRefreshAvatarAsync(cachedAvatarUri);
                }

                if (_cache is not null)
                    await TryWriteCacheAsync(_cache);

                return UpdateSnapshot();
            }

            response.EnsureSuccessStatusCode();

            await using var profileStream =
                await response.Content.ReadAsStreamAsync();
            var profile =
                await JsonSerializer.DeserializeAsync<GitHubUserResponse>(
                    profileStream)
                ?? throw new InvalidDataException(
                    "GitHub returned an empty user profile.");

            var previousAvatarUrl = _cache?.AvatarUrl;
            _cache ??= new GitHubProfileCache();
            _cache.DisplayName = string.IsNullOrWhiteSpace(profile.Name)
                ? profile.Login
                : profile.Name.Trim();
            _cache.Login = string.IsNullOrWhiteSpace(profile.Login)
                ? FallbackLogin
                : profile.Login.Trim();
            _cache.ProfileUrl = TryCreateHttpsUri(
                    profile.HtmlUrl,
                    out var profileUri)
                ? profileUri.AbsoluteUri
                : $"https://github.com/{_cache.Login}";
            _cache.AvatarUrl = profile.AvatarUrl;
            _cache.ProfileETag = response.Headers.ETag?.ToString();

            if (!string.Equals(
                    previousAvatarUrl,
                    _cache.AvatarUrl,
                    StringComparison.Ordinal))
            {
                _cache.AvatarETag = null;
            }

            if (
                TryCreateHttpsUri(
                    _cache.AvatarUrl,
                    out var avatarUri))
            {
                await TryRefreshAvatarAsync(avatarUri);
            }

            await TryWriteCacheAsync(_cache);
        }
        catch
        {
            // The About window deliberately falls back to the last successful
            // cache. Profile decoration must never block the rest of BCS Tool.
        }

        return UpdateSnapshot();
    }

    private async Task TryRefreshAvatarAsync(Uri avatarUri)
    {
        string? temporaryPath = null;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                avatarUri);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("image/*"));

            if (
                File.Exists(_avatarPath) &&
                !string.IsNullOrWhiteSpace(_cache?.AvatarETag) &&
                EntityTagHeaderValue.TryParse(
                    _cache.AvatarETag,
                    out var avatarETag))
            {
                request.Headers.IfNoneMatch.Add(avatarETag);
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            if (response.StatusCode == HttpStatusCode.NotModified)
                return;

            response.EnsureSuccessStatusCode();

            var mediaType =
                response.Content.Headers.ContentType?.MediaType;

            if (
                string.IsNullOrWhiteSpace(mediaType) ||
                !mediaType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "GitHub returned an invalid avatar content type.");
            }

            if (
                response.Content.Headers.ContentLength is >
                    MaximumAvatarBytes)
            {
                throw new InvalidDataException(
                    "The GitHub avatar exceeds the cache size limit.");
            }

            Directory.CreateDirectory(_cacheDirectory);
            temporaryPath = Path.Combine(
                _cacheDirectory,
                $"avatar-{Guid.NewGuid():N}.tmp");

            await using (
                var source =
                    await response.Content.ReadAsStreamAsync())
            await using (
                var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
            {
                var buffer = new byte[81920];
                var totalBytes = 0;

                while (true)
                {
                    var bytesRead = await source.ReadAsync(
                        buffer.AsMemory(0, buffer.Length));

                    if (bytesRead == 0)
                        break;

                    totalBytes += bytesRead;

                    if (totalBytes > MaximumAvatarBytes)
                    {
                        throw new InvalidDataException(
                            "The GitHub avatar exceeds the cache size limit.");
                    }

                    await destination.WriteAsync(
                        buffer.AsMemory(0, bytesRead));
                }

                await destination.FlushAsync();
            }

            File.Move(temporaryPath, _avatarPath, overwrite: true);
            temporaryPath = null;

            if (_cache is not null)
                _cache.AvatarETag = response.Headers.ETag?.ToString();
        }
        catch
        {
            // Keep the existing cached avatar when a refresh fails.
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // A leftover temporary file is harmless.
                }
            }
        }
    }

    private GitHubProfileSnapshot UpdateSnapshot()
    {
        CachedProfile = CreateSnapshot(_cache);
        return CachedProfile;
    }

    private GitHubProfileSnapshot CreateSnapshot(
        GitHubProfileCache? cache)
    {
        var login = string.IsNullOrWhiteSpace(cache?.Login)
            ? FallbackLogin
            : cache.Login;
        var displayName = string.IsNullOrWhiteSpace(cache?.DisplayName)
            ? FallbackDisplayName
            : cache.DisplayName;
        var profileUrl = TryCreateHttpsUri(
                cache?.ProfileUrl,
                out var cachedProfileUri)
            ? cachedProfileUri.AbsoluteUri
            : $"https://github.com/{login}";

        return new GitHubProfileSnapshot(
            displayName,
            login,
            profileUrl,
            File.Exists(_avatarPath)
                ? _avatarPath
                : null);
    }

    private GitHubProfileCache? TryReadCache()
    {
        try
        {
            if (!File.Exists(_metadataPath))
                return null;

            return JsonSerializer.Deserialize<GitHubProfileCache>(
                File.ReadAllText(_metadataPath));
        }
        catch
        {
            return null;
        }
    }

    private async Task TryWriteCacheAsync(
        GitHubProfileCache cache)
    {
        string? temporaryPath = null;

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            temporaryPath = Path.Combine(
                _cacheDirectory,
                $"profile-{Guid.NewGuid():N}.tmp");
            var json = JsonSerializer.Serialize(
                cache,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _metadataPath, overwrite: true);
            temporaryPath = null;
        }
        catch
        {
            // A cache write failure should not affect the About window.
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // A leftover temporary file is harmless.
                }
            }
        }
    }

    private static bool TryCreateHttpsUri(
        string? value,
        out Uri uri)
    {
        if (
            Uri.TryCreate(value, UriKind.Absolute, out var parsedUri) &&
            parsedUri.Scheme == Uri.UriSchemeHttps)
        {
            uri = parsedUri;
            return true;
        }

        uri = null!;
        return false;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class GitHubUserResponse
    {
        [JsonPropertyName("login")]
        public string Login { get; init; } = "";

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = "";

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; init; } = "";
    }

    private sealed class GitHubProfileCache
    {
        public string DisplayName { get; set; } = "";
        public string Login { get; set; } = "";
        public string ProfileUrl { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string? ProfileETag { get; set; }
        public string? AvatarETag { get; set; }
    }
}

public sealed record GitHubProfileSnapshot(
    string DisplayName,
    string Login,
    string ProfileUrl,
    string? AvatarPath);
