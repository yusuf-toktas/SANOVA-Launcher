using System.Text.Json;
using Sanova.Core.Content;
using Sanova.Core.IO;

namespace Sanova.Core.Updates;

/// <summary>
/// A validated manifest together with the exact bytes it was parsed from and the resolved
/// download locations of its packages and external archives.
/// </summary>
public sealed class ResolvedManifest
{
    public ResolvedManifest(
        ContentManifest manifest,
        byte[] bytes,
        bool signatureVerified,
        IReadOnlyDictionary<string, IReadOnlyList<Uri>> packageLocations,
        string? sourceDescription,
        string? releaseNotesFallback)
    {
        Manifest = manifest;
        Bytes = bytes;
        Sha256 = FileHashing.Sha256(bytes);
        SignatureVerified = signatureVerified;
        PackageLocations = packageLocations;
        SourceDescription = sourceDescription;
        ReleaseNotesFallback = releaseNotesFallback;
    }

    public ContentManifest Manifest { get; }

    public byte[] Bytes { get; }

    public string Sha256 { get; }

    public bool SignatureVerified { get; }

    /// <summary>Package id → candidate URIs (primary first, then mirrors).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Uri>> PackageLocations { get; }

    public string? SourceDescription { get; }

    /// <summary>Release body from the hosting platform, used when the manifest has no notes.</summary>
    public string? ReleaseNotesFallback { get; }

    public SemanticVersion Version => Manifest.Version;

    public IReadOnlyList<Uri> GetPackageUris(PackageSpec package)
        => PackageLocations.TryGetValue(package.Id, out var uris) ? uris : [];

    private sealed record CacheFile(string SourceDescription, string? ReleaseNotesFallback, bool SignatureVerified, Dictionary<string, List<string>> Locations);

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllBytes(Path.Combine(directory, ContentManifest.FileName), Bytes);
        var cache = new CacheFile(
            SourceDescription ?? string.Empty,
            ReleaseNotesFallback,
            SignatureVerified,
            PackageLocations.ToDictionary(p => p.Key, p => p.Value.Select(u => u.ToString()).ToList()));
        AtomicFile.WriteJson(Path.Combine(directory, "manifest-locations.json"), cache);
    }

    /// <summary>Reloads a manifest saved with <see cref="Save"/>, re-validating it.</summary>
    public static ResolvedManifest? TryLoad(string directory)
    {
        var manifestPath = Path.Combine(directory, ContentManifest.FileName);
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            var manifest = ManifestLoader.Parse(bytes);
            var cachePath = Path.Combine(directory, "manifest-locations.json");
            var cache = File.Exists(cachePath) ? JsonSerializer.Deserialize<CacheFile>(File.ReadAllBytes(cachePath), JsonDefaults.Indented) : null;
            var locations = cache?.Locations.ToDictionary(
                p => p.Key,
                p => (IReadOnlyList<Uri>)p.Value.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null).OfType<Uri>().ToList(),
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, IReadOnlyList<Uri>>();
            return new ResolvedManifest(manifest, bytes, cache?.SignatureVerified ?? false, locations, cache?.SourceDescription, cache?.ReleaseNotesFallback);
        }
        catch (Exception ex) when (ex is IOException or JsonException or SanovaException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public static class ManifestLoader
{
    public const int MaxManifestBytes = 64 * 1024 * 1024;

    /// <summary>Parses and validates manifest bytes; throws <see cref="SanovaException"/> on any problem.</summary>
    public static ContentManifest Parse(byte[] bytes)
    {
        if (bytes.Length > MaxManifestBytes)
        {
            throw SanovaException.Create(ErrorCodes.ManifestInvalid, "Manifest is too large.", ("detail", "too large"));
        }

        int schemaVersion;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            schemaVersion = document.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.TryGetInt32(out var value) ? value : 0;
        }
        catch (JsonException ex)
        {
            throw SanovaException.Wrap(ErrorCodes.ManifestInvalid, $"Manifest is not valid JSON: {ex.Message}", ex, ("detail", ex.Message));
        }

        if (schemaVersion > ContentManifest.SupportedSchemaVersion)
        {
            throw SanovaException.Create(ErrorCodes.ManifestSchemaUnsupported, $"Manifest schema {schemaVersion} requires a newer launcher.", ("schema", schemaVersion));
        }

        ContentManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ContentManifest>(bytes, JsonDefaults.Indented)
                       ?? throw new JsonException("Manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw SanovaException.Wrap(ErrorCodes.ManifestInvalid, $"Manifest could not be read: {ex.Message}", ex, ("detail", ex.Message));
        }

        var errors = ManifestValidator.Validate(manifest);
        if (errors.Count > 0)
        {
            throw SanovaException.Create(ErrorCodes.ManifestInvalid, "Manifest failed validation: " + string.Join(" | ", errors.Take(10)),
                ("detail", errors[0]), ("count", errors.Count));
        }
        return manifest;
    }

    /// <summary>Parses, validates and applies the signature policy.</summary>
    public static ResolvedManifest Load(byte[] bytes, string? signature, ManifestTrustConfig trust, ContentRelease? release)
    {
        var manifest = Parse(bytes);
        var trustedKeys = trust.PublicKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();

        var verified = false;
        if (signature is not null && trustedKeys.Count > 0)
        {
            verified = ManifestSignature.Verify(bytes, signature, trustedKeys);
            if (!verified)
            {
                throw SanovaException.Create(ErrorCodes.ManifestSignatureInvalid, "Manifest signature does not match any trusted key.");
            }
        }
        else if (trust.RequireSignature)
        {
            throw SanovaException.Create(ErrorCodes.ManifestSignatureMissing,
                trustedKeys.Count == 0 ? "Signatures are required but no trusted key is configured." : "Manifest is not signed.");
        }

        var locations = new Dictionary<string, IReadOnlyList<Uri>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in manifest.Packages)
        {
            locations[package.Id] = release?.ResolvePackage(package)
                ?? (package.Url is not null && Uri.TryCreate(package.Url, UriKind.Absolute, out var uri) ? [uri] : []);
        }

        return new ResolvedManifest(manifest, bytes, verified, locations, release?.SourceDescription, release?.NotesMarkdown);
    }
}
