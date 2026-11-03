using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sanova.Core;
using Sanova.Core.Content;
using Sanova.Core.Game;
using Sanova.Core.IO;

namespace Sanova.ManifestTool;

public sealed class BuildOptions
{
    public required string SourceDirectory { get; init; }
    public required string ConfigPath { get; init; }
    public required string OutputDirectory { get; init; }
    public string? VersionOverride { get; init; }
    public string? ChannelOverride { get; init; }
    public long MaxPackageBytes { get; init; } = 1024L * 1024 * 1024;
    public string? PreviousManifestPath { get; init; }
    public string? PreviousBaseUrl { get; init; }
    public string? SigningKeyPath { get; init; }
    public bool AllowUnknownRedistribution { get; init; }
    public TextWriter Log { get; init; } = Console.Out;
}

public sealed record BuildResult(ContentManifest Manifest, string ManifestPath, IReadOnlyList<string> Assets, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds a content release from the Root/ folder: assigns files to components, hashes them,
/// packs redistributable components into deterministic zip packages (reusing unchanged
/// packages from the previous release when possible), writes and optionally signs the manifest.
/// </summary>
public static class ManifestBuilder
{
    private static readonly DateTimeOffset FixedTimestamp = new(2005, 6, 7, 0, 0, 0, TimeSpan.Zero);

    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".png", ".jpg", ".jpeg", ".ogg", ".mp3", ".mp4", ".webm", ".gz",
    };

    public static async Task<BuildResult> BuildAsync(BuildOptions options, CancellationToken cancellationToken = default)
    {
        var config = ContentConfig.Load(options.ConfigPath);
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ConfigPath))!;
        var source = SafePath.NormalizeRoot(options.SourceDirectory);
        var output = Path.GetFullPath(options.OutputDirectory);
        if (SafePath.Overlaps(source, output))
        {
            throw new InvalidOperationException("The output folder must not be inside the source folder (or vice versa).");
        }
        Directory.CreateDirectory(output);

        var warnings = new List<string>();
        var version = options.VersionOverride ?? config.ContentVersion;
        if (!SemanticVersion.TryParse(version, out _)) throw new InvalidOperationException($"'{version}' is not a semantic version.");

        // 1. Assign every source file to exactly one component.
        var matchers = config.Components.Select(c => (Config: c, Include: c.Include.Select(Glob.ToRegex).ToList(), Exclude: c.Exclude.Select(Glob.ToRegex).ToList())).ToList();
        var assignments = new List<(string Path, string FullPath, ComponentConfig Component)>();
        var unassigned = new List<string>();
        foreach (var file in EnumerateSource(source))
        {
            var relative = SafePath.NormalizeRelative(Path.GetRelativePath(source, file).Replace('\\', '/'));
            var owner = matchers
                .Where(m => m.Include.Any(r => r.IsMatch(relative)) && !m.Exclude.Any(r => r.IsMatch(relative)))
                .OrderByDescending(m => m.Config.Priority)
                .Select(m => m.Config)
                .FirstOrDefault();
            if (owner is null) unassigned.Add(relative);
            else assignments.Add((relative, file, owner));
        }
        if (unassigned.Count > 0)
        {
            throw new InvalidOperationException($"{unassigned.Count} file(s) match no component, e.g. '{unassigned[0]}'. Add include patterns to the config.");
        }

        // 2. Redistribution policy.
        foreach (var component in config.Components)
        {
            var isExternal = component.Source is { Type: not ComponentSourceKind.Package };
            if (!isExternal && component.Redistribution == Redistribution.External)
            {
                throw new InvalidOperationException($"Component '{component.Id}' is marked external but has no external source.");
            }
            if (!isExternal && component.Redistribution == Redistribution.Unknown)
            {
                var message = $"Component '{component.Id}' has unknown redistribution status.";
                if (!options.AllowUnknownRedistribution)
                {
                    throw new InvalidOperationException(message + " Record the license/permission, or pass --allow-unknown-redistribution for private test builds.");
                }
                warnings.Add(message + " (allowed for this build only)");
            }
        }

        // 3. Hash files.
        options.Log.WriteLine($"Hashing {assignments.Count} file(s)...");
        var files = new List<ManifestFile>(assignments.Count);
        foreach (var (path, fullPath, component) in assignments.OrderBy(a => a.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sha = await FileHashing.Sha256FileAsync(fullPath, null, cancellationToken).ConfigureAwait(false);
            var entry = MapEntry(path, component);
            files.Add(new ManifestFile
            {
                Path = path,
                Size = new FileInfo(fullPath).Length,
                Sha256 = sha,
                Component = component.Id,
                Entry = entry == path ? null : entry,
            });
        }

        // 3b. Never publish unmodified original game files: the launcher copies those from the
        //     player's own installation. Files of external/manual components are not checked,
        //     because SANOVA does not redistribute them.
        BaseGameReference? baseGame = null;
        if (config.BaseGameReference is not null)
        {
            var referencePath = Path.Combine(configDirectory, config.BaseGameReference);
            baseGame = JsonSerializer.Deserialize<BaseGameReference>(File.ReadAllBytes(referencePath), JsonDefaults.Indented);
        }
        var originalHashes = BaseGameReference.LoadEmbedded().Append(baseGame).OfType<BaseGameReference>()
            .SelectMany(r => r.Files)
            .Where(f => f.Size > 0 && f.Sha256 is not null)
            .Select(f => f.Sha256!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packaged = config.Components.Where(c => c.Source is null or { Type: ComponentSourceKind.Package }).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originalCopies = files.Where(f => f.Size > 0 && packaged.Contains(f.Component) && originalHashes.Contains(f.Sha256)).Select(f => f.Path).ToList();
        if (originalCopies.Count > 0)
        {
            throw new InvalidOperationException(
                $"{originalCopies.Count} file(s) in the source folder are identical to original game files, e.g. '{originalCopies[0]}'. " +
                "Original game files must not be published; the launcher copies them from the player's own game. Remove them from the source folder.");
        }

        // 4. Packages (reused when a component's files are unchanged since the previous release).
        var previous = options.PreviousManifestPath is null ? null : ManifestLoaderFacade.Load(options.PreviousManifestPath);
        var packages = new List<PackageSpec>();
        var assets = new List<string>();
        foreach (var component in config.Components.Where(c => c.Source is null or { Type: ComponentSourceKind.Package }))
        {
            var componentFiles = files.Where(f => f.Component == component.Id).ToList();
            if (componentFiles.Count == 0)
            {
                warnings.Add($"Component '{component.Id}' has no files.");
                continue;
            }

            if (TryReuse(previous, options.PreviousBaseUrl, component.Id, componentFiles, out var reused))
            {
                options.Log.WriteLine($"  {component.Id}: unchanged, reusing {reused.Count} package(s) from the previous release");
                packages.AddRange(reused.Select(r => r.Package));
                foreach (var (file, packageId, packedSize) in reused.SelectMany(r => r.Files))
                {
                    file.Package = packageId;
                    file.PackedSize = packedSize;
                }
                continue;
            }

            var chunks = Chunk(componentFiles, options.MaxPackageBytes);
            for (var i = 0; i < chunks.Count; i++)
            {
                var packageId = chunks.Count == 1 ? component.Id : $"{component.Id}-{i + 1}";
                var fileName = $"sanova-{version}-{packageId}.zip";
                var packagePath = Path.Combine(output, fileName);
                options.Log.WriteLine($"  {component.Id}: packing {chunks[i].Count} file(s) into {fileName}");
                await WritePackageAsync(packagePath, chunks[i], source, cancellationToken).ConfigureAwait(false);
                packages.Add(new PackageSpec
                {
                    Id = packageId,
                    FileName = fileName,
                    Size = new FileInfo(packagePath).Length,
                    Sha256 = await FileHashing.Sha256FileAsync(packagePath, null, cancellationToken).ConfigureAwait(false),
                });
                foreach (var file in chunks[i]) file.Package = packageId;
                RecordPackedSizes(packagePath, chunks[i]);
                assets.Add(packagePath);
            }
        }

        // 5. Manifest.
        var notes = new List<KeyValuePair<string, string>>();
        foreach (var (language, notesPath) in config.ReleaseNotesFiles)
        {
            notes.Add(new(language, await File.ReadAllTextAsync(Path.Combine(configDirectory, notesPath), cancellationToken).ConfigureAwait(false)));
        }

        var manifest = new ContentManifest
        {
            SchemaVersion = ContentManifest.SupportedSchemaVersion,
            ContentVersion = version,
            Channel = options.ChannelOverride ?? config.Channel,
            ReleasedAt = DateTimeOffset.UtcNow,
            MinLauncherVersion = config.MinLauncherVersion,
            Title = config.Title,
            ReleaseNotes = notes.Count > 0 ? new LocalizedText(notes) : null,
            Compatibility = config.Compatibility,
            BaseGame = baseGame,
            Categories = config.Categories,
            Components = config.Components.Select(ToSpec).ToList(),
            Packages = packages,
            Files = files,
        };

        var errors = ManifestValidator.Validate(manifest);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("The generated manifest is invalid:\n  " + string.Join("\n  ", errors));
        }

        var manifestPath = Path.Combine(output, ContentManifest.FileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Indented);
        AtomicFile.WriteAllBytes(manifestPath, bytes);
        assets.Insert(0, manifestPath);

        if (options.SigningKeyPath is not null)
        {
            var privateKey = await File.ReadAllTextAsync(options.SigningKeyPath, cancellationToken).ConfigureAwait(false);
            var signature = ManifestSignature.Sign(bytes, privateKey);
            var signaturePath = Path.Combine(output, ContentManifest.SignatureFileName);
            AtomicFile.WriteAllText(signaturePath, signature);
            assets.Insert(1, signaturePath);
        }
        else
        {
            warnings.Add("The manifest is not signed (pass --sign-key for public releases).");
        }

        await File.WriteAllLinesAsync(Path.Combine(output, "release-assets.txt"), assets.Select(Path.GetFileName)!, cancellationToken).ConfigureAwait(false);
        return new BuildResult(manifest, manifestPath, assets, warnings);
    }

    private static IEnumerable<string> EnumerateSource(string source)
        => new DirectoryInfo(source)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(f => f.FullName);

    private static string MapEntry(string path, ComponentConfig component)
    {
        if (component.Source is null or { Type: ComponentSourceKind.Package }) return path;
        var relative = path;
        var installPrefix = component.Source.InstallPrefix.Replace('\\', '/');
        if (installPrefix.Length > 0)
        {
            if (!relative.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"'{path}' does not start with installPrefix '{installPrefix}' of component '{component.Id}'.");
            }
            relative = relative[installPrefix.Length..];
        }
        return component.Source.ArchivePrefix.Replace('\\', '/') + relative;
    }

    private static ComponentSpec ToSpec(ComponentConfig c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description,
        Category = c.Category,
        Required = c.Required,
        DefaultEnabled = c.DefaultEnabled,
        Version = c.Version,
        Authors = c.Authors,
        Homepage = c.Homepage,
        License = c.License,
        Redistribution = c.Redistribution,
        Dependencies = c.Dependencies,
        Conflicts = c.Conflicts,
        Order = c.Order,
        Source = c.Source is null or { Type: ComponentSourceKind.Package }
            ? null
            : new ComponentSource
            {
                Type = c.Source.Type,
                Url = c.Source.Url,
                PageUrl = c.Source.PageUrl,
                FileName = c.Source.FileName,
                Size = c.Source.Size,
                Sha256 = c.Source.Sha256,
                Instructions = c.Source.Instructions,
            },
    };

    private static List<List<ManifestFile>> Chunk(List<ManifestFile> files, long maxBytes)
    {
        var chunks = new List<List<ManifestFile>> { new() };
        long current = 0;
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (current > 0 && current + file.Size > maxBytes)
            {
                chunks.Add([]);
                current = 0;
            }
            chunks[^1].Add(file);
            current += file.Size;
        }
        return chunks;
    }

    private static async Task WritePackageAsync(string packagePath, IReadOnlyList<ManifestFile> files, string source, CancellationToken cancellationToken)
    {
        var temp = packagePath + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var level = StoredExtensions.Contains(Path.GetExtension(file.Path)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var entry = archive.CreateEntry(file.EntryName, level);
                entry.LastWriteTime = FixedTimestamp;
                await using var input = File.OpenRead(SafePath.GetFullPathUnder(source, file.Path));
                await using var entryStream = entry.Open();
                await input.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }
        }
        FileSystem.MoveFile(temp, packagePath, overwrite: true);
    }

    private static void RecordPackedSizes(string packagePath, IReadOnlyList<ManifestFile> files)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var sizes = archive.Entries.ToDictionary(e => e.FullName, e => e.CompressedLength, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (sizes.TryGetValue(file.EntryName, out var packed)) file.PackedSize = packed;
        }
    }

    private sealed record ReusedPackage(PackageSpec Package, List<(ManifestFile File, string PackageId, long? PackedSize)> Files);

    private static bool TryReuse(ContentManifest? previous, string? previousBaseUrl, string componentId, List<ManifestFile> files, out List<ReusedPackage> reused)
    {
        reused = [];
        if (previous is null || string.IsNullOrWhiteSpace(previousBaseUrl)) return false;

        var old = previous.FilesOf(componentId).ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase);
        if (old.Count != files.Count) return false;
        foreach (var file in files)
        {
            if (!old.TryGetValue(file.Path, out var before) || before.Size != file.Size || !FileHashing.HashEquals(before.Sha256, file.Sha256) ||
                !string.Equals(before.EntryName, file.EntryName, StringComparison.Ordinal) || before.Package is null)
            {
                return false;
            }
        }

        var packageIds = old.Values.Select(f => f.Package!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Only reuse packages that contain nothing but this component's files.
        if (previous.Files.Any(f => f.Package is not null && packageIds.Contains(f.Package, StringComparer.OrdinalIgnoreCase) &&
                                    !string.Equals(f.Component, componentId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var baseUrl = previousBaseUrl.EndsWith('/') ? previousBaseUrl : previousBaseUrl + "/";
        foreach (var packageId in packageIds)
        {
            var package = previous.FindPackage(packageId);
            if (package is null) return false;
            var url = package.Url ?? new Uri(new Uri(baseUrl), Uri.EscapeDataString(package.FileName)).ToString();
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            var spec = new PackageSpec { Id = package.Id, FileName = package.FileName, Url = url, Size = package.Size, Sha256 = package.Sha256, Format = package.Format };
            var mapped = files
                .Where(f => string.Equals(old[f.Path].Package, packageId, StringComparison.OrdinalIgnoreCase))
                .Select(f => (f, packageId, old[f.Path].PackedSize))
                .ToList();
            reused.Add(new ReusedPackage(spec, mapped));
        }
        return true;
    }
}

internal static class ManifestLoaderFacade
{
    public static ContentManifest Load(string path) => Sanova.Core.Updates.ManifestLoader.Parse(File.ReadAllBytes(path));
}
