using Sanova.Core.IO;

namespace Sanova.Core.Installation;

public enum InstallPhase
{
    /// <summary>The original game files are still being copied (resumable).</summary>
    CopyingBase,

    /// <summary>Original files are in place; SANOVA content has not been applied yet.</summary>
    BaseReady,

    /// <summary>SANOVA content is installed.</summary>
    Ready,
}

public enum FileOrigin
{
    /// <summary>Copied from the player's original game installation.</summary>
    Base,

    /// <summary>Installed from SANOVA content.</summary>
    Content,
}

/// <summary>A file the launcher placed and therefore owns. Anything not listed here belongs to the player.</summary>
public sealed class InstalledFile
{
    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public FileOrigin Origin { get; set; }

    public string? Component { get; set; }

    /// <summary>Recorded write time for fast verification; a mismatch triggers a full hash check.</summary>
    public DateTime? LastWriteUtc { get; set; }

    /// <summary>A content file that replaced an original game file; the original is kept in .sanova/originals.</summary>
    public bool OverridesBase { get; set; }

    public InstalledFile Copy() => (InstalledFile)MemberwiseClone();
}

/// <summary>A player file that SANOVA had to move out of the way; kept in .sanova/user-backup.</summary>
public sealed class DisplacedFile
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Location relative to the metadata folder.</summary>
    public string BackupPath { get; set; } = string.Empty;

    public DateTimeOffset DisplacedAt { get; set; }
}

/// <summary>Everything the launcher knows about one SANOVA installation (stored in .sanova/install.json).</summary>
public sealed class InstallState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string InstallId { get; set; } = Guid.NewGuid().ToString("N");

    public InstallPhase Phase { get; set; } = InstallPhase.CopyingBase;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? LauncherVersion { get; set; }

    public string? OriginalGamePath { get; set; }

    public string? ExecutableId { get; set; }

    public string ExecutableName { get; set; } = "gta_sa.exe";

    public string? ContentVersion { get; set; }

    public string? ManifestSha256 { get; set; }

    public string? Channel { get; set; }

    public List<string> EnabledComponents { get; set; } = [];

    public Dictionary<string, InstalledFile> Files { get; set; } = new(SafePath.Comparer);

    /// <summary>Files of disabled optional components, parked in .sanova/disabled for instant re-enabling.</summary>
    public Dictionary<string, InstalledFile> Stashed { get; set; } = new(SafePath.Comparer);

    /// <summary>Original game files replaced by content, preserved in .sanova/originals for restoration.</summary>
    public Dictionary<string, InstalledFile> Originals { get; set; } = new(SafePath.Comparer);

    public List<DisplacedFile> DisplacedUserFiles { get; set; } = [];

    public DateTimeOffset? LastVerifiedAt { get; set; }

    public bool? LastVerificationPassed { get; set; }

    public long ContentBytes => Files.Values.Where(f => f.Origin == FileOrigin.Content).Sum(f => f.Size);

    public long TotalBytes => Files.Values.Sum(f => f.Size);

    public InstallState Clone()
    {
        var copy = (InstallState)MemberwiseClone();
        copy.EnabledComponents = [.. EnabledComponents];
        copy.Files = Files.ToDictionary(p => p.Key, p => p.Value.Copy(), SafePath.Comparer);
        copy.Stashed = Stashed.ToDictionary(p => p.Key, p => p.Value.Copy(), SafePath.Comparer);
        copy.Originals = Originals.ToDictionary(p => p.Key, p => p.Value.Copy(), SafePath.Comparer);
        copy.DisplacedUserFiles = DisplacedUserFiles.Select(d => new DisplacedFile { Path = d.Path, BackupPath = d.BackupPath, DisplacedAt = d.DisplacedAt }).ToList();
        return copy;
    }
}
