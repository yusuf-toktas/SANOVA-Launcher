using System.Text.Json;
using Sanova.Core.IO;

namespace Sanova.Core.Game;

public enum ExecutableSupport
{
    Supported,
    Unverified,
    Unsupported,
}

public enum ExecutableEdition
{
    Retail,
    Steam,
    RockstarLauncher,
    Modified,
    Unknown,
}

/// <summary>A game executable identified by hash.</summary>
public sealed class KnownExecutable
{
    public string Id { get; set; } = string.Empty;

    /// <summary>English description used in logs and diagnostic reports.</summary>
    public string Label { get; set; } = string.Empty;

    public ExecutableEdition Edition { get; set; } = ExecutableEdition.Unknown;

    public string? Version { get; set; }

    public string? Region { get; set; }

    public string? FileName { get; set; }

    public long? Size { get; set; }

    public List<string> Md5 { get; set; } = [];

    public List<string> Sha256 { get; set; } = [];

    public ExecutableSupport Support { get; set; } = ExecutableSupport.Unverified;

    /// <summary>Extra hints for the UI, e.g. "securom" (retail copy protection may ask for the disc).</summary>
    public List<string> Notes { get; set; } = [];

    public KnownExecutable With(ExecutableSupport support)
    {
        var copy = (KnownExecutable)MemberwiseClone();
        copy.Support = support;
        return copy;
    }
}

/// <summary>Support policy that a content manifest can apply on top of the built-in database.</summary>
public sealed class CompatibilityRules
{
    public List<string>? SupportedExecutables { get; set; }

    public List<string>? UnverifiedExecutables { get; set; }

    public List<string>? UnsupportedExecutables { get; set; }

    public List<KnownExecutable>? AdditionalExecutables { get; set; }
}

public sealed class GameVersionDatabase
{
    private sealed class DatabaseFile
    {
        public int SchemaVersion { get; set; }
        public List<KnownExecutable> Executables { get; set; } = [];
    }

    public GameVersionDatabase(IEnumerable<KnownExecutable> executables)
    {
        Executables = executables.ToList();
    }

    public IReadOnlyList<KnownExecutable> Executables { get; }

    public static GameVersionDatabase LoadEmbedded()
    {
        using var stream = typeof(GameVersionDatabase).Assembly.GetManifestResourceStream("Sanova.Core.Data.game-versions.json")
            ?? throw new InvalidOperationException("Embedded game version database is missing.");
        var file = JsonSerializer.Deserialize<DatabaseFile>(stream, JsonDefaults.Indented)
            ?? throw new InvalidDataException("Embedded game version database is empty.");
        return new GameVersionDatabase(file.Executables);
    }

    public KnownExecutable? Find(string id)
        => Executables.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public KnownExecutable? Match(string sha256, string md5, long size)
    {
        foreach (var candidate in Executables)
        {
            if (candidate.Size is { } expectedSize && expectedSize != size)
            {
                continue;
            }
            if (candidate.Sha256.Any(h => FileHashing.HashEquals(h, sha256)) || candidate.Md5.Any(h => string.Equals(h, md5, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Returns a new database with the manifest's additions and support overrides applied.</summary>
    public GameVersionDatabase WithRules(CompatibilityRules? rules)
    {
        if (rules is null) return this;

        var merged = new List<KnownExecutable>(Executables);
        foreach (var extra in rules.AdditionalExecutables ?? [])
        {
            merged.RemoveAll(e => string.Equals(e.Id, extra.Id, StringComparison.OrdinalIgnoreCase));
            merged.Add(extra);
        }

        return new GameVersionDatabase(merged.Select(e => Override(e, rules)));
    }

    private static KnownExecutable Override(KnownExecutable executable, CompatibilityRules rules)
    {
        static bool Contains(List<string>? ids, string id) => ids?.Any(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase)) == true;

        if (Contains(rules.SupportedExecutables, executable.Id)) return executable.With(ExecutableSupport.Supported);
        if (Contains(rules.UnverifiedExecutables, executable.Id)) return executable.With(ExecutableSupport.Unverified);
        if (Contains(rules.UnsupportedExecutables, executable.Id)) return executable.With(ExecutableSupport.Unsupported);
        return executable;
    }
}
