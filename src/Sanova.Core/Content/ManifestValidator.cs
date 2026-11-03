using System.Text.RegularExpressions;
using Sanova.Core.Installation;
using Sanova.Core.IO;

namespace Sanova.Core.Content;

/// <summary>
/// Structural and security validation of a content manifest. A manifest that fails
/// validation is never used, because every later stage trusts its paths and hashes.
/// </summary>
public static partial class ManifestValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static IReadOnlyList<string> Validate(ContentManifest manifest)
    {
        var errors = new List<string>();

        // JSON null elements are allowed by the serializer even for List<T> where T
        // is non-nullable. Reject them before following any references below.
        if (manifest.Components is null || manifest.Packages is null || manifest.Files is null || manifest.Categories is null ||
            manifest.Components.Any(c => c is null || c.Name is null || c.Dependencies is null || c.Conflicts is null || c.Authors is null ||
                c.Dependencies.Any(d => d is null) || c.Conflicts.Any(d => d is null)) ||
            manifest.Packages.Any(p => p is null) || manifest.Files.Any(f => f is null) || manifest.Categories.Any(c => c is null))
        {
            errors.Add("Manifest collections and their entries must not be null.");
            return errors;
        }

        if (manifest.SchemaVersion != ContentManifest.SupportedSchemaVersion)
        {
            errors.Add($"Unsupported schemaVersion {manifest.SchemaVersion} (expected {ContentManifest.SupportedSchemaVersion}).");
            return errors;
        }
        if (!string.Equals(manifest.Product, "sanova", StringComparison.Ordinal))
        {
            errors.Add($"Unexpected product '{manifest.Product}'.");
        }
        if (!SemanticVersion.TryParse(manifest.ContentVersion, out _))
        {
            errors.Add($"contentVersion '{manifest.ContentVersion}' is not a semantic version.");
        }
        if (manifest.MinLauncherVersion is not null && !SemanticVersion.TryParse(manifest.MinLauncherVersion, out _))
        {
            errors.Add($"minLauncherVersion '{manifest.MinLauncherVersion}' is not a semantic version.");
        }

        var components = new Dictionary<string, ComponentSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in manifest.Components)
        {
            if (!IdPattern().IsMatch(component.Id ?? string.Empty))
            {
                errors.Add($"Component id '{component.Id}' is invalid (lowercase letters, digits, '.', '_' and '-').");
                continue;
            }
            if (!components.TryAdd(component.Id!, component))
            {
                errors.Add($"Duplicate component id '{component.Id}'.");
            }
            if (component.Name.Count == 0)
            {
                errors.Add($"Component '{component.Id}' has no name.");
            }
            ValidateSource(component, errors);
        }

        foreach (var component in manifest.Components)
        {
            foreach (var dependency in component.Dependencies)
            {
                if (!components.ContainsKey(dependency)) errors.Add($"Component '{component.Id}' depends on unknown component '{dependency}'.");
                if (string.Equals(dependency, component.Id, StringComparison.OrdinalIgnoreCase)) errors.Add($"Component '{component.Id}' depends on itself.");
            }
            foreach (var conflict in component.Conflicts)
            {
                if (!components.TryGetValue(conflict, out var other))
                {
                    errors.Add($"Component '{component.Id}' conflicts with unknown component '{conflict}'.");
                }
                else if (component.Required && other.Required)
                {
                    errors.Add($"Required components '{component.Id}' and '{conflict}' conflict with each other.");
                }
            }
        }

        if (HasDependencyCycle(manifest.Components, out var cycle))
        {
            errors.Add($"Component dependency cycle: {cycle}.");
        }

        var packages = new Dictionary<string, PackageSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in manifest.Packages)
        {
            if (!IdPattern().IsMatch(package.Id ?? string.Empty))
            {
                errors.Add($"Package id '{package.Id}' is invalid.");
                continue;
            }
            if (!packages.TryAdd(package.Id!, package)) errors.Add($"Duplicate package id '{package.Id}'.");
            if (!string.Equals(package.Format, "zip", StringComparison.OrdinalIgnoreCase)) errors.Add($"Package '{package.Id}' has unsupported format '{package.Format}'.");
            if (!FileHashing.IsValidSha256(package.Sha256)) errors.Add($"Package '{package.Id}' has an invalid sha256.");
            if (package.Size <= 0) errors.Add($"Package '{package.Id}' has an invalid size.");
            if (string.IsNullOrWhiteSpace(package.Url))
            {
                if (string.IsNullOrWhiteSpace(package.FileName) || package.FileName.IndexOfAny(['/', '\\', ':']) >= 0 || package.FileName is "." or "..")
                {
                    errors.Add($"Package '{package.Id}' needs a plain fileName or an absolute url.");
                }
            }
            else if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add($"Package '{package.Id}' url must be an absolute https URL.");
            }
        }

        var paths = new HashSet<string>(SafePath.Comparer);
        foreach (var file in manifest.Files)
        {
            if (!SafePath.TryNormalizeRelative(file.Path, out var normalized, out var reason))
            {
                errors.Add($"File path '{file.Path}' is unsafe: {reason}.");
                continue;
            }
            if (normalized.StartsWith(InstallStateStore.MetadataFolderName + "/", SafePath.Comparison) ||
                string.Equals(normalized, InstallStateStore.MetadataFolderName, SafePath.Comparison))
            {
                errors.Add($"File path '{file.Path}' targets the launcher's metadata folder.");
            }
            if (!paths.Add(normalized)) errors.Add($"Duplicate file path '{file.Path}' (paths are case-insensitive).");
            if (!FileHashing.IsValidSha256(file.Sha256)) errors.Add($"File '{file.Path}' has an invalid sha256.");
            if (file.Size < 0) errors.Add($"File '{file.Path}' has a negative size.");
            if (file.Entry is not null && !SafePath.TryNormalizeRelative(file.Entry, out _, out var entryReason))
            {
                errors.Add($"Archive entry '{file.Entry}' for '{file.Path}' is unsafe: {entryReason}.");
            }

            if (!components.TryGetValue(file.Component ?? string.Empty, out var owner))
            {
                errors.Add($"File '{file.Path}' belongs to unknown component '{file.Component}'.");
                continue;
            }

            if (owner.SourceKind == ComponentSourceKind.Package)
            {
                if (file.Package is null || !packages.ContainsKey(file.Package))
                {
                    errors.Add($"File '{file.Path}' references unknown package '{file.Package}'.");
                }
            }
            else if (file.Package is not null)
            {
                errors.Add($"File '{file.Path}' belongs to an external component and must not reference a package.");
            }
        }

        // Directories and files must not collide (e.g. "a" as a file and "a/b").
        foreach (var path in paths)
        {
            var slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                var parent = path[..slash];
                if (paths.Contains(parent))
                {
                    errors.Add($"Path '{parent}' is used both as a file and as a folder.");
                    break;
                }
                slash = parent.LastIndexOf('/');
            }
        }

        return errors;
    }

    private static void ValidateSource(ComponentSpec component, List<string> errors)
    {
        var source = component.Source;
        if (source is null || source.Type == ComponentSourceKind.Package) return;

        if (!FileHashing.IsValidSha256(source.Sha256))
        {
            errors.Add($"Component '{component.Id}' has an external source without a valid sha256.");
        }
        if (source.Type == ComponentSourceKind.External &&
            (!Uri.TryCreate(source.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"Component '{component.Id}' external source needs an absolute https url.");
        }
        if (source.Type == ComponentSourceKind.Manual &&
            (!Uri.TryCreate(source.PageUrl, UriKind.Absolute, out var page) || (page.Scheme != Uri.UriSchemeHttps && page.Scheme != Uri.UriSchemeHttp)))
        {
            errors.Add($"Component '{component.Id}' manual source needs a pageUrl.");
        }
    }

    private static bool HasDependencyCycle(List<ComponentSpec> components, out string cycle)
    {
        var byId = components
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        string? found = null;

        bool Visit(string id)
        {
            if (state.TryGetValue(id, out var s))
            {
                if (s == 1)
                {
                    var start = stack.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
                    found = string.Join(" -> ", stack.Skip(start).Append(id));
                    return true;
                }
                return false;
            }
            state[id] = 1;
            stack.Add(id);
            if (byId.TryGetValue(id, out var component))
            {
                foreach (var dependency in component.Dependencies)
                {
                    if (byId.ContainsKey(dependency) && Visit(dependency)) return true;
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
            return false;
        }

        foreach (var id in byId.Keys)
        {
            if (Visit(id))
            {
                cycle = found!;
                return true;
            }
        }
        cycle = string.Empty;
        return false;
    }
}
