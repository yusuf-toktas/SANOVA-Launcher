using System.Text.Json;

namespace Sanova.Core.Game;

public sealed class BaseGameFile
{
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public string? Sha256 { get; set; }
}

/// <summary>
/// The file list of a clean, unmodified game installation for a given executable variant.
/// Used to detect incomplete installations, spot pre-existing modifications, and copy only
/// original game files into the SANOVA folder. Generated with <c>sanova-manifest basegame</c>.
/// </summary>
public sealed class BaseGameReference
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Executable ids this file list applies to; empty means any.</summary>
    public List<string> ExecutableIds { get; set; } = [];

    public string? Description { get; set; }

    public List<BaseGameFile> Files { get; set; } = [];

    public long TotalSize => Files.Sum(f => f.Size);

    public bool AppliesTo(string? executableId)
        => ExecutableIds.Count == 0 || (executableId is not null && ExecutableIds.Any(id => string.Equals(id, executableId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Files that must exist when no reference list matches the executable.</summary>
    public static IReadOnlyList<string> MinimalRequiredFiles { get; } =
    [
        "models/gta3.img",
        "models/gta_int.img",
        "data/gta.dat",
        "data/script/main.scm",
        "anim/ped.ifp",
        "text/american.gxt",
    ];

    private static readonly string[] EmbeddedReferences = ["basegame-sa-1.0-us.json"];

    /// <summary>Loads every reference file shipped with the launcher.</summary>
    public static IReadOnlyList<BaseGameReference> LoadEmbedded()
    {
        var result = new List<BaseGameReference>();
        foreach (var name in EmbeddedReferences)
        {
            using var stream = typeof(BaseGameReference).Assembly.GetManifestResourceStream($"Sanova.Core.Data.{name}");
            if (stream is null) continue;
            var reference = JsonSerializer.Deserialize<BaseGameReference>(stream, JsonDefaults.Indented);
            if (reference is not null) result.Add(reference);
        }
        return result;
    }
}
