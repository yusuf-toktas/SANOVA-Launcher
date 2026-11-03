using System.Net;
using Microsoft.Extensions.Logging;
using Sanova.Core.Downloads;
using Sanova.Core.Settings;

namespace Sanova.Core.Updates;

public enum ContentCheckStatus
{
    /// <summary>No content source has been configured (releases not published yet).</summary>
    NotConfigured,
    Available,
    NoRelease,
    Offline,
    Failed,
}

public sealed record ContentCheckResult(
    ContentCheckStatus Status,
    ResolvedManifest? Manifest,
    SanovaException? Error,
    DateTimeOffset CheckedAt,
    string? SourceDescription);

/// <summary>
/// Finds the latest SANOVA content release on the configured source, downloads and verifies
/// its manifest, and keeps a validated copy on disk so the launcher stays useful offline.
/// </summary>
public sealed class ContentUpdateService
{
    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly GitHubReleasesClient _github;
    private readonly ILogger<ContentUpdateService> _logger;
    private ResolvedManifest? _cached;
    private bool _cacheLoaded;

    public ContentUpdateService(HttpClient apiHttp, AppPaths paths, GitHubReleasesClient github, ILogger<ContentUpdateService> logger)
    {
        _http = apiHttp;
        _paths = paths;
        _github = github;
        _logger = logger;
    }

    /// <summary>The most recently verified manifest (possibly from a previous session).</summary>
    public ResolvedManifest? CachedManifest
    {
        get
        {
            if (!_cacheLoaded)
            {
                _cached = ResolvedManifest.TryLoad(_paths.ManifestCacheDirectory);
                _cacheLoaded = true;
            }
            return _cached;
        }
    }

    public IContentSource? CreateSource(ContentSourceConfig config)
    {
        if (!config.IsConfigured) return null;
        return config.Type switch
        {
            ContentSourceType.GitHub when GitHubRepository.TryParse(config.Repository, out var repository)
                => new GitHubContentSource(repository, config, _github, _logger),
            ContentSourceType.Url when Uri.TryCreate(config.ManifestUrl, UriKind.Absolute, out var uri)
                => new UrlContentSource(uri, config),
            ContentSourceType.LocalFolder => new LocalFolderContentSource(config.LocalFolder!, config),
            _ => null,
        };
    }

    public async Task<ContentCheckResult> CheckAsync(ContentSourceConfig config, ManifestTrustConfig trust, string channel, bool allowInsecure, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var source = CreateSource(config);
        if (source is null)
        {
            return new ContentCheckResult(ContentCheckStatus.NotConfigured, null, null, now, null);
        }

        try
        {
            var release = await source.GetLatestAsync(channel, cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                return new ContentCheckResult(ContentCheckStatus.NoRelease, null, null, now, source.Description);
            }

            var bytes = await FetchAsync(release.ManifestUri, allowInsecure, required: true, cancellationToken).ConfigureAwait(false)
                        ?? throw SanovaException.Create(ErrorCodes.SourceNoRelease, "Manifest missing.", ("source", source.Description));
            string? signature = null;
            if (release.SignatureUri is not null)
            {
                var signatureBytes = await FetchAsync(release.SignatureUri, allowInsecure, required: false, cancellationToken).ConfigureAwait(false);
                signature = signatureBytes is null ? null : System.Text.Encoding.ASCII.GetString(signatureBytes);
            }

            var resolved = ManifestLoader.Load(bytes, signature, trust, release);
            resolved.Save(_paths.ManifestCacheDirectory);
            _cached = resolved;
            _cacheLoaded = true;
            _logger.LogInformation("Content release {Version} available from {Source} (signature verified: {Verified})",
                resolved.Manifest.ContentVersion, source.Description, resolved.SignatureVerified);
            return new ContentCheckResult(ContentCheckStatus.Available, resolved, null, now, source.Description);
        }
        catch (SanovaException ex) when (ex.Code is ErrorCodes.SourceUnreachable)
        {
            _logger.LogWarning("Content source unreachable: {Message}", ex.Message);
            return new ContentCheckResult(ContentCheckStatus.Offline, null, ex, now, source.Description);
        }
        catch (SanovaException ex) when (ex.Code is ErrorCodes.SourceNoRelease)
        {
            return new ContentCheckResult(ContentCheckStatus.NoRelease, null, ex, now, source.Description);
        }
        catch (SanovaException ex)
        {
            _logger.LogWarning(ex, "Content check failed");
            return new ContentCheckResult(ContentCheckStatus.Failed, null, ex, now, source.Description);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var offline = RetryPolicy.IsOffline(ex);
            _logger.LogWarning(ex, "Content check failed (offline: {Offline})", offline);
            var error = SanovaException.Wrap(offline ? ErrorCodes.SourceUnreachable : ErrorCodes.SourceHttpError, ex.Message, ex, ("detail", ex.Message));
            return new ContentCheckResult(offline ? ContentCheckStatus.Offline : ContentCheckStatus.Failed, null, error, now, source.Description);
        }
    }

    private async Task<byte[]?> FetchAsync(Uri uri, bool allowInsecure, bool required, CancellationToken cancellationToken)
    {
        if (uri.IsFile)
        {
            if (File.Exists(uri.LocalPath)) return await File.ReadAllBytesAsync(uri.LocalPath, cancellationToken).ConfigureAwait(false);
            return required ? throw SanovaException.Create(ErrorCodes.SourceNoRelease, $"Missing {uri.LocalPath}", ("source", uri.LocalPath)) : null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecure && uri.Scheme == Uri.UriSchemeHttp))
        {
            throw SanovaException.Create(ErrorCodes.SourceInsecure, $"Refusing non-HTTPS URL {uri}.", ("url", uri.GetLeftPart(UriPartial.Path)));
        }

        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && !required) return null;
        if (!response.IsSuccessStatusCode)
        {
            throw SanovaException.Wrap(ErrorCodes.SourceHttpError, $"HTTP {(int)response.StatusCode} for {uri.GetLeftPart(UriPartial.Path)}",
                HttpStatusException.From(response, uri), ("status", (int)response.StatusCode));
        }
        if (response.Content.Headers.ContentLength > ManifestLoader.MaxManifestBytes)
        {
            throw SanovaException.Create(ErrorCodes.ManifestInvalid, "Manifest is too large.", ("detail", "too large"));
        }
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
