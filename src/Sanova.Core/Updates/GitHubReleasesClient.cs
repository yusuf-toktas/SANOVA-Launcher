using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sanova.Core.Downloads;
using Sanova.Core.IO;

namespace Sanova.Core.Updates;

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
    /// <summary>"sha256:&lt;hex&gt;" when GitHub has computed it.</summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

/// <summary>
/// Minimal GitHub Releases API client. Responses are cached with their ETag: conditional
/// requests answered with 304 do not count against GitHub's 60 requests/hour anonymous limit,
/// and the cached copy keeps the launcher informative when GitHub is unreachable.
/// </summary>
public sealed class GitHubReleasesClient
{
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly ILogger<GitHubReleasesClient> _logger;

    private sealed record CacheEntry(string? ETag, string Body);

    public GitHubReleasesClient(HttpClient http, string cacheDirectory, ILogger<GitHubReleasesClient> logger)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(GitHubRepository repository, CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.github.com/repos/{repository.Owner}/{repository.Name}/releases?per_page=30");
        var cachePath = Path.Combine(_cacheDirectory, $"github-{repository.Owner}-{repository.Name}-releases.json".ToLowerInvariant());
        var cached = ReadCache(cachePath);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (cached?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw SanovaException.Wrap(ErrorCodes.SourceUnreachable, $"GitHub is unreachable: {ex.Message}", ex, ("detail", ex.Message));
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                _logger.LogDebug("GitHub releases for {Repository} unchanged (304)", repository);
                return Parse(cached.Body);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
            {
                DateTimeOffset? reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) && long.TryParse(resetValues.FirstOrDefault(), out var epoch)
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                    : null;
                throw SanovaException.Create(ErrorCodes.SourceRateLimited, "GitHub API rate limit reached.", ("resetAt", reset?.ToLocalTime().ToString("t")));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw SanovaException.Create(ErrorCodes.SourceNoRelease, $"Repository {repository} was not found.", ("source", repository.ToString()));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw SanovaException.Wrap(ErrorCodes.SourceHttpError, $"GitHub returned HTTP {(int)response.StatusCode}.",
                    HttpStatusException.From(response, uri), ("status", (int)response.StatusCode));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var releases = Parse(body);
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                AtomicFile.WriteJson(cachePath, new CacheEntry(response.Headers.ETag?.ToString(), body), JsonDefaults.Compact);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not cache GitHub response");
            }
            return releases;
        }
    }

    private static IReadOnlyList<GitHubRelease> Parse(string body)
        => JsonSerializer.Deserialize<List<GitHubRelease>>(body) ?? [];

    private static CacheEntry? ReadCache(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllBytes(path), JsonDefaults.Compact) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
