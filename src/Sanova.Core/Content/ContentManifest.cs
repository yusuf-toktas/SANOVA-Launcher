using System.Text.Json.Serialization;
using Sanova.Core.Game;
using Sanova.Core.IO;

namespace Sanova.Core.Content;

/// <summary>
/// Describes one SANOVA content release: every file it installs, which component owns it,
/// where its bytes come from and how to verify them. Published next to the release
/// packages (see docs/CONTENT-PIPELINE.md).
/// </summary>
public sealed class ContentManifest
{
    public const int SupportedSchemaVersion = 1;
    public const string FileName = "sanova-manifest.json";
    public const string SignatureFileName = "sanova-manifest.json.sig";

    public int SchemaVersion { get; set; }

    public string Product { get; set; } = "sanova";

    public string ContentVersion { get; set; } = string.Empty;

    public string? Channel { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>Oldest launcher able to install this release correctly.</summary>
    public string? MinLauncherVersion { get; set; }

    public LocalizedText? Title { get; set; }

    /// <summary>Markdown release notes, per language.</summary>
    public LocalizedText? ReleaseNotes { get; set; }

    public CompatibilityRules? Compatibility { get; set; }

    public BaseGameReference? BaseGame { get; set; }

    public List<CategorySpec> Categories { get; set; } = [];

    public List<ComponentSpec> Components { get; set; } = [];

    public List<PackageSpec> Packages { get; set; } = [];

    public List<ManifestFile> Files { get; set; } = [];

    [JsonIgnore]
    public SemanticVersion Version => SemanticVersion.Parse(ContentVersion);

    public ComponentSpec? FindComponent(string id)
        => Components.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public PackageSpec? FindPackage(string id)
        => Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ManifestFile> FilesOf(string componentId)
        => Files.Where(f => string.Equals(f.Component, componentId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ManifestFile> FilesOf(IReadOnlySet<string> componentIds)
        => Files.Where(f => componentIds.Contains(f.Component));

    public long SizeOf(string componentId) => FilesOf(componentId).Sum(f => f.Size);

    public long SizeOf(IReadOnlySet<string> componentIds) => FilesOf(componentIds).Sum(f => f.Size);
}

public sealed class CategorySpec
{
    public string Id { get; set; } = string.Empty;

    public LocalizedText? Name { get; set; }

    public int Order { get; set; }
}

/// <summary>Whether a component's files may be published in SANOVA's own release packages.</summary>
public enum Redistribution
{
    /// <summary>Not determined yet. Never packaged for public releases.</summary>
    Unknown,

    /// <summary>The license allows redistribution.</summary>
    Allowed,

    /// <summary>The author granted SANOVA permission (keep the record with the content config).</summary>
    Permission,

    /// <summary>Must be obtained from the author's own source; the launcher downloads or asks for it.</summary>
    External,
}

public sealed class ComponentSpec
{
    public string Id { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public LocalizedText? Description { get; set; }

    public string Category { get; set; } = "general";

    public bool Required { get; set; }

    public bool DefaultEnabled { get; set; } = true;

    public string? Version { get; set; }

    public List<string> Authors { get; set; } = [];

    public string? Homepage { get; set; }

    public string? License { get; set; }

    public Redistribution Redistribution { get; set; } = Redistribution.Unknown;

    public List<string> Dependencies { get; set; } = [];

    public List<string> Conflicts { get; set; } = [];

    /// <summary>Where the files come from. Null means SANOVA's own release packages.</summary>
    public ComponentSource? Source { get; set; }

    public int Order { get; set; }

    [JsonIgnore]
    public ComponentSourceKind SourceKind => Source?.Type ?? ComponentSourceKind.Package;
}

public enum ComponentSourceKind
{
    Package,

    /// <summary>Downloaded by the launcher from the author's official direct link.</summary>
    External,

    /// <summary>The player downloads the archive from the author's page and points the launcher at it.</summary>
    Manual,
}

public sealed class ComponentSource
{
    public ComponentSourceKind Type { get; set; } = ComponentSourceKind.Package;

    /// <summary>Direct download URL of the original archive (External).</summary>
    public string? Url { get; set; }

    /// <summary>Human-facing download page (Manual, and informational for External).</summary>
    public string? PageUrl { get; set; }

    /// <summary>Expected file name of the archive the player downloads (Manual).</summary>
    public string? FileName { get; set; }

    public long? Size { get; set; }

    /// <summary>SHA-256 of the original archive. Required for External and Manual sources.</summary>
    public string? Sha256 { get; set; }

    public LocalizedText? Instructions { get; set; }
}

/// <summary>A zip archive published with a release.</summary>
public sealed class PackageSpec
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Asset file name, resolved relative to the manifest location.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Optional absolute URL, e.g. an unchanged package reused from an earlier release.</summary>
    public string? Url { get; set; }

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string Format { get; set; } = "zip";
}

public sealed class ManifestFile
{
    /// <summary>Install path relative to the game folder, with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string Component { get; set; } = string.Empty;

    /// <summary>Package containing the file; null for External/Manual components (their archive is the source).</summary>
    public string? Package { get; set; }

    /// <summary>Entry name inside the archive when it differs from <see cref="Path"/>.</summary>
    public string? Entry { get; set; }

    /// <summary>Compressed size inside the archive, for download-size estimates.</summary>
    public long? PackedSize { get; set; }

    [JsonIgnore]
    public string EntryName => string.IsNullOrEmpty(Entry) ? Path : Entry;

    [JsonIgnore]
    public string NormalizedPath => SafePath.NormalizeRelative(Path);
}
