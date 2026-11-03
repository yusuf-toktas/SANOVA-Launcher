using Sanova.Core.IO;

namespace Sanova.Core.Game;

public enum ExecutableKind
{
    /// <summary>Matched an entry in the version database.</summary>
    Known,

    /// <summary>gta-sa.exe: the 2014+ Steam / Rockstar Games Launcher re-release (v3.0 and later).</summary>
    ModernRerelease,

    /// <summary>A copy-protected retail executable whose hash is not in the database (e.g. another region or v1.01).</summary>
    RetailUnrecognized,

    /// <summary>The Unreal-based Definitive Edition, a different game.</summary>
    DefinitiveEdition,

    Unknown,
    Missing,
}

public sealed record ExecutableIdentity(
    string? Path,
    string? FileName,
    long Size,
    string? Sha256,
    string? Md5,
    PeInfo? Pe,
    KnownExecutable? Match,
    ExecutableKind Kind)
{
    public static ExecutableIdentity Missing { get; } = new(null, null, 0, null, null, null, null, ExecutableKind.Missing);

    public ExecutableSupport EffectiveSupport => Kind == ExecutableKind.Known && Match is not null
        ? Match.Support
        : ExecutableSupport.Unsupported;
}

/// <summary>Finds and fingerprints the executable in a GTA San Andreas folder.</summary>
public static class ExecutableIdentifier
{
    public const string ClassicExecutable = "gta_sa.exe";
    public const string RereleaseExecutable = "gta-sa.exe";

    public static bool LooksLikeDefinitiveEdition(string gameDirectory)
        => File.Exists(Path.Combine(gameDirectory, "Gameface", "Binaries", "Win64", "SanAndreas.exe"));

    public static string? FindExecutable(string gameDirectory)
    {
        foreach (var name in new[] { ClassicExecutable, RereleaseExecutable })
        {
            var candidate = Path.Combine(gameDirectory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static async Task<ExecutableIdentity> IdentifyAsync(string gameDirectory, GameVersionDatabase database, CancellationToken cancellationToken = default)
    {
        if (LooksLikeDefinitiveEdition(gameDirectory))
        {
            return ExecutableIdentity.Missing with { Kind = ExecutableKind.DefinitiveEdition };
        }

        var path = FindExecutable(gameDirectory);
        if (path is null)
        {
            return ExecutableIdentity.Missing;
        }

        var size = new FileInfo(path).Length;
        var (sha256, md5) = await FileHashing.Sha256AndMd5FileAsync(path, cancellationToken).ConfigureAwait(false);
        var pe = PeReader.TryRead(path);
        var fileName = System.IO.Path.GetFileName(path);
        var match = database.Match(sha256, md5, size);

        var kind = match is not null
            ? ExecutableKind.Known
            : string.Equals(fileName, RereleaseExecutable, StringComparison.OrdinalIgnoreCase)
                ? ExecutableKind.ModernRerelease
                : pe?.HasSection(".securom") == true
                    ? ExecutableKind.RetailUnrecognized
                    : ExecutableKind.Unknown;

        return new ExecutableIdentity(path, fileName, size, sha256, md5, pe, match, kind);
    }
}
