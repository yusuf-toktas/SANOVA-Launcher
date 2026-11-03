using Microsoft.Extensions.Logging;
using Sanova.Core.Content;

namespace Sanova.Core.Updates;

/// <summary>A published content release as seen by a source, before its manifest is downloaded.</summary>
public sealed record ContentRelease(
    string Version,
    string? Title,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    string? NotesMarkdown,
    Uri ManifestUri,
    Uri? SignatureUri,
    string SourceDescription,
    Func<PackageSpec, IReadOnlyList<Uri>> ResolvePackage);

public interface IContentSource
{
    /// <summary>Human-readable description for diagnostics, e.g. "GitHub: owner/repo".</summary>
    string Description { get; }

    Task<ContentRelease?> GetLatestAsync(string channel, CancellationToken cancellationToken);
}

/// <summary>Content published as GitHub release assets: the manifest, its signature and the zip packages.</summary>
public sealed class GitHubContentSource : IContentSource
{
    private readonly GitHubRepository _repository;
    private readonly ContentSourceConfig _config;
    private readonly GitHubReleasesClient _client;
    private readonly ILogger _logger;

    public GitHubContentSource(GitHubRepository repository, ContentSourceConfig config, GitHubReleasesClient client, ILogger logger)
    {
        _repository = repository;
        _config = config;
        _client = client;
        _logger = logger;
    }

    public string Description => $"GitHub: {_repository}";

    public async Task<ContentRelease?> GetLatestAsync(string channel, CancellationToken cancellationToken)
    {
        var releases = await _client.GetReleasesAsync(_repository, cancellationToken).ConfigureAwait(false);
        var includePrerelease = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase);

        var candidates = releases
            .Where(r => !r.Draft && (includePrerelease || !r.Prerelease))
            .Select(r => (Release: r, Manifest: r.Assets.FirstOrDefault(a => string.Equals(a.Name, _config.ManifestAssetName, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Manifest is not null)
            .Select(x => (x.Release, x.Manifest, Version: SemanticVersion.TryParse(x.Release.TagName, out var v) ? v : (SemanticVersion?)null))
            .OrderByDescending(x => x.Version.HasValue)
            .ThenByDescending(x => x.Version ?? default)
            .ThenByDescending(x => x.Release.PublishedAt)
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogInformation("No {Channel} release with {Asset} in {Repository}", channel, _config.ManifestAssetName, _repository);
            return null;
        }

        var (release, manifestAsset, version) = candidates[0];
        var signatureAsset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, _config.ManifestAssetName + ".sig", StringComparison.OrdinalIgnoreCase));
        var assets = release.Assets.ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
        var mirrors = _config.Mirrors;
        var tag = release.TagName;
        var repository = _repository;

        IReadOnlyList<Uri> Resolve(PackageSpec package)
        {
            var uris = new List<Uri>();
            if (assets.TryGetValue(package.FileName, out var asset) && Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var assetUri))
            {
                uris.Add(assetUri);
            }
            if (package.Url is not null && Uri.TryCreate(package.Url, UriKind.Absolute, out var absolute))
            {
                uris.Add(absolute);
            }
            if (uris.Count == 0 && !string.IsNullOrEmpty(package.FileName))
            {
                uris.Add(new Uri($"{repository.Url}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(package.FileName)}"));
            }
            uris.AddRange(MirrorUris(mirrors, package.FileName));
            return uris.Distinct().ToList();
        }

        return new ContentRelease(
            version?.ToString() ?? release.TagName,
            release.Name,
            release.PublishedAt,
            release.Prerelease,
            release.Body,
            new Uri(manifestAsset!.BrowserDownloadUrl),
            signatureAsset is null ? null : new Uri(signatureAsset.BrowserDownloadUrl),
            Description,
            Resolve);
    }

    internal static IEnumerable<Uri> MirrorUris(IEnumerable<string> mirrors, string fileName)
    {
        foreach (var mirror in mirrors)
        {
            if (string.IsNullOrWhiteSpace(fileName)) yield break;
            var baseText = mirror.EndsWith('/') ? mirror : mirror + "/";
            if (Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri) && baseUri.Scheme == Uri.UriSchemeHttps)
            {
                yield return new Uri(baseUri, Uri.EscapeDataString(fileName));
            }
        }
    }
}

/// <summary>A manifest at a fixed https URL; packages are resolved relative to it (GitHub Pages, a CDN, ...).</summary>
public sealed class UrlContentSource : IContentSource
{
    private readonly Uri _manifestUri;
    private readonly ContentSourceConfig _config;

    public UrlContentSource(Uri manifestUri, ContentSourceConfig config)
    {
        _manifestUri = manifestUri;
        _config = config;
    }

    public string Description => $"URL: {_manifestUri.GetLeftPart(UriPartial.Path)}";

    public Task<ContentRelease?> GetLatestAsync(string channel, CancellationToken cancellationToken)
    {
        var manifestUri = _manifestUri;
        var mirrors = _config.Mirrors;

        IReadOnlyList<Uri> Resolve(PackageSpec package)
        {
            var uris = new List<Uri>();
            if (package.Url is not null && Uri.TryCreate(package.Url, UriKind.Absolute, out var absolute)) uris.Add(absolute);
            if (!string.IsNullOrEmpty(package.FileName)) uris.Add(new Uri(manifestUri, Uri.EscapeDataString(package.FileName)));
            uris.AddRange(GitHubContentSource.MirrorUris(mirrors, package.FileName));
            return uris.Distinct().ToList();
        }

        var release = new ContentRelease("unknown", null, null, false, null, manifestUri, new Uri(manifestUri + ".sig"), Description, Resolve);
        return Task.FromResult<ContentRelease?>(release);
    }
}

/// <summary>A folder produced by <c>sanova-manifest build</c>; used for development and local testing.</summary>
public sealed class LocalFolderContentSource : IContentSource
{
    private readonly string _folder;
    private readonly ContentSourceConfig _config;

    public LocalFolderContentSource(string folder, ContentSourceConfig config)
    {
        _folder = Path.GetFullPath(folder);
        _config = config;
    }

    public string Description => $"Local folder: {_folder}";

    public Task<ContentRelease?> GetLatestAsync(string channel, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(_folder, _config.ManifestAssetName);
        if (!File.Exists(manifestPath))
        {
            return Task.FromResult<ContentRelease?>(null);
        }

        var folder = _folder;
        IReadOnlyList<Uri> Resolve(PackageSpec package)
        {
            var uris = new List<Uri>();
            if (!string.IsNullOrEmpty(package.FileName) && package.FileName.IndexOfAny(['/', '\\', ':']) < 0)
            {
                uris.Add(new Uri(Path.Combine(folder, package.FileName)));
            }
            if (package.Url is not null && Uri.TryCreate(package.Url, UriKind.Absolute, out var absolute)) uris.Add(absolute);
            return uris;
        }

        var signaturePath = manifestPath + ".sig";
        var release = new ContentRelease("local", null, File.GetLastWriteTimeUtc(manifestPath), false, null, new Uri(manifestPath),
            File.Exists(signaturePath) ? new Uri(signaturePath) : null, Description, Resolve);
        return Task.FromResult<ContentRelease?>(release);
    }
}
