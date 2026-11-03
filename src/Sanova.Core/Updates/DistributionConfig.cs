using System.Text.Json;
using Sanova.Core.Settings;

namespace Sanova.Core.Updates;

/// <summary>
/// Where launcher builds and SANOVA content are published. Shipped as distribution.json
/// inside the launcher; empty values mean "not published yet" and the UI says so.
/// </summary>
public sealed class DistributionConfig
{
    public LauncherUpdateConfig LauncherUpdates { get; set; } = new();

    public ContentSourceConfig Content { get; set; } = new();

    public ManifestTrustConfig Trust { get; set; } = new();

    public LinksConfig Links { get; set; } = new();

    public static DistributionConfig Load(Stream json)
        => JsonSerializer.Deserialize<DistributionConfig>(json, JsonDefaults.Indented) ?? new DistributionConfig();

    /// <summary>Applies the user's developer-mode override, if any.</summary>
    public ContentSourceConfig EffectiveContentSource(LauncherSettings settings)
    {
        var overrideSource = settings.ContentSource;
        if (!settings.DeveloperMode || overrideSource.Kind == ContentSourceKind.Default || string.IsNullOrWhiteSpace(overrideSource.Value))
        {
            return Content;
        }
        return overrideSource.Kind switch
        {
            ContentSourceKind.GitHub => Content.With(type: ContentSourceType.GitHub, repository: overrideSource.Value),
            ContentSourceKind.ManifestUrl => Content.With(type: ContentSourceType.Url, manifestUrl: overrideSource.Value),
            ContentSourceKind.LocalFolder => Content.With(type: ContentSourceType.LocalFolder, localFolder: overrideSource.Value),
            _ => Content,
        };
    }
}

public sealed class LauncherUpdateConfig
{
    /// <summary>Environment variable that points a test build at another update feed (URL or folder).</summary>
    public const string OverrideVariable = "SANOVA_LAUNCHER_UPDATE_URL";

    /// <summary>GitHub repository hosting launcher releases: "owner/repo" or a github.com URL.</summary>
    public string? Repository { get; set; }

    /// <summary>
    /// Alternative to <see cref="Repository"/>: an https URL or local folder containing Velopack
    /// release files (releases.win.json and packages), e.g. a CDN or a test folder.
    /// </summary>
    public string? Url { get; set; }

    public bool AllowPrerelease { get; set; }

    public bool IsConfigured => GitHubRepository.TryParse(Repository, out _) || !string.IsNullOrWhiteSpace(Url);
}

public enum ContentSourceType
{
    GitHub,
    Url,
    LocalFolder,
}

public sealed class ContentSourceConfig
{
    public ContentSourceType Type { get; set; } = ContentSourceType.GitHub;

    /// <summary>GitHub repository whose releases carry the manifest and packages.</summary>
    public string? Repository { get; set; }

    public string ManifestAssetName { get; set; } = "sanova-manifest.json";

    /// <summary>Direct manifest URL (Type = Url). Packages resolve relative to it.</summary>
    public string? ManifestUrl { get; set; }

    /// <summary>Folder containing a manifest and packages (Type = LocalFolder; development and testing).</summary>
    public string? LocalFolder { get; set; }

    /// <summary>Extra base URLs that host copies of the release packages.</summary>
    public List<string> Mirrors { get; set; } = [];

    public bool IsConfigured => Type switch
    {
        ContentSourceType.GitHub => GitHubRepository.TryParse(Repository, out _),
        ContentSourceType.Url => Uri.TryCreate(ManifestUrl, UriKind.Absolute, out _),
        ContentSourceType.LocalFolder => !string.IsNullOrWhiteSpace(LocalFolder),
        _ => false,
    };

    public ContentSourceConfig With(ContentSourceType? type = null, string? repository = null, string? manifestUrl = null, string? localFolder = null)
        => new()
        {
            Type = type ?? Type,
            Repository = repository ?? Repository,
            ManifestAssetName = ManifestAssetName,
            ManifestUrl = manifestUrl ?? ManifestUrl,
            LocalFolder = localFolder ?? LocalFolder,
            Mirrors = [.. Mirrors],
        };
}

public sealed class ManifestTrustConfig
{
    /// <summary>PEM-encoded ECDSA P-256 public keys trusted to sign content manifests.</summary>
    public List<string> PublicKeys { get; set; } = [];

    /// <summary>When true, unsigned or wrongly signed manifests are rejected.</summary>
    public bool RequireSignature { get; set; }
}

/// <summary>Optional public links shown in the About page; hidden while empty.</summary>
public sealed class LinksConfig
{
    public string? Website { get; set; }

    public string? SourceCode { get; set; }

    public string? Community { get; set; }

    public string? Support { get; set; }
}

public readonly record struct GitHubRepository(string Owner, string Name)
{
    public override string ToString() => $"{Owner}/{Name}";

    public string Url => $"https://github.com/{Owner}/{Name}";

    /// <summary>Accepts "owner/repo", "https://github.com/owner/repo" and "...repo.git".</summary>
    public static bool TryParse(string? value, out GitHubRepository repository)
    {
        repository = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
            text = uri.AbsolutePath.Trim('/');
        }
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts.All(IsValidName)) return false;
        repository = new GitHubRepository(parts[0], parts[1]);
        return true;
    }

    private static bool IsValidName(string part) => part.Length is > 0 and <= 100 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
