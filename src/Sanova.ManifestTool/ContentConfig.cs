using System.Text.Json;
using System.Text.RegularExpressions;
using Sanova.Core;
using Sanova.Core.Content;
using Sanova.Core.Game;

namespace Sanova.ManifestTool;

/// <summary>
/// The maintainers' description of a content release (sanova-content.json), kept next to
/// the Root/ folder. It assigns Root files to components and records each component's
/// authorship, license and redistribution status.
/// </summary>
public sealed class ContentConfig
{
    public string ContentVersion { get; set; } = "0.1.0";

    public string Channel { get; set; } = "stable";

    public string? MinLauncherVersion { get; set; }

    public LocalizedText? Title { get; set; }

    /// <summary>Language code → Markdown file (relative to the config) with release notes.</summary>
    public Dictionary<string, string> ReleaseNotesFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CompatibilityRules? Compatibility { get; set; }

    /// <summary>Optional base-game reference file (from <c>sanova-manifest basegame</c>) embedded in the manifest.</summary>
    public string? BaseGameReference { get; set; }

    public List<CategorySpec> Categories { get; set; } = [];

    public List<ComponentConfig> Components { get; set; } = [];

    public static ContentConfig Load(string path)
        => JsonSerializer.Deserialize<ContentConfig>(File.ReadAllBytes(path), JsonDefaults.Indented)
           ?? throw new InvalidDataException("Content config is empty.");
}

public sealed class ComponentConfig
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
    public int Order { get; set; }

    /// <summary>Glob patterns (relative to Root, '/' separators, '**' for any depth) selecting this component's files.</summary>
    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];

    /// <summary>When several components match a file, the highest priority wins.</summary>
    public int Priority { get; set; }

    /// <summary>Omit for files SANOVA may publish; set for mods that must come from the author's own source.</summary>
    public ExternalSourceConfig? Source { get; set; }
}

public sealed class ExternalSourceConfig
{
    public ComponentSourceKind Type { get; set; } = ComponentSourceKind.External;
    public string? Url { get; set; }
    public string? PageUrl { get; set; }
    public string? FileName { get; set; }
    public long? Size { get; set; }
    public string? Sha256 { get; set; }
    public LocalizedText? Instructions { get; set; }

    /// <summary>Install-path prefix that is not part of the archive layout (e.g. "modloader/").</summary>
    public string InstallPrefix { get; set; } = string.Empty;

    /// <summary>Archive folder that holds the files (e.g. "MyMod v2/").</summary>
    public string ArchivePrefix { get; set; } = string.Empty;
}

/// <summary>Case-insensitive glob matching for Root-relative paths.</summary>
public static class Glob
{
    public static Regex ToRegex(string pattern)
    {
        var p = pattern.Replace('\\', '/').TrimStart('/');
        var regex = new System.Text.StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*')
            {
                var isDouble = i + 1 < p.Length && p[i + 1] == '*';
                if (isDouble)
                {
                    var followedBySlash = i + 2 < p.Length && p[i + 2] == '/';
                    regex.Append(followedBySlash ? "(?:.*/)?" : ".*");
                    i += followedBySlash ? 2 : 1;
                }
                else
                {
                    regex.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }
        regex.Append('$');
        return new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool Matches(string path, IEnumerable<string> patterns) => patterns.Any(p => ToRegex(p).IsMatch(path));
}
