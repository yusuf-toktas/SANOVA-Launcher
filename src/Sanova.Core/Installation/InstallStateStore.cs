using System.Text.Json;
using Sanova.Core.IO;

namespace Sanova.Core.Installation;

/// <summary>
/// Layout of the per-installation metadata folder (.sanova) and access to install.json.
/// <code>
/// .sanova/
///   install.json         state (the commit point of every transaction)
///   install.next.json    state being committed (exists only mid-commit)
///   journal.json         active transaction journal (exists only mid-transaction)
///   manifest/            the manifest the installation was built from
///   staging/&lt;tx&gt;/        verified files waiting to be moved into place
///   tx/&lt;tx&gt;/             backups taken during a transaction (for rollback)
///   originals/           original game files replaced by SANOVA content
///   disabled/            files of disabled optional components
///   user-backup/         player files SANOVA had to move out of the way
///   cache/               downloaded packages (resumable)
/// </code>
/// </summary>
public static class InstallStateStore
{
    public const string MetadataFolderName = ".sanova";

    public static string MetadataDirectory(string installDirectory) => Path.Combine(installDirectory, MetadataFolderName);

    public static string StatePath(string installDirectory) => Path.Combine(MetadataDirectory(installDirectory), "install.json");

    public static string NextStatePath(string installDirectory) => Path.Combine(MetadataDirectory(installDirectory), "install.next.json");

    public static string JournalPath(string installDirectory) => Path.Combine(MetadataDirectory(installDirectory), "journal.json");

    public static string ManifestDirectory(string installDirectory) => Path.Combine(MetadataDirectory(installDirectory), "manifest");

    public static string StagingRoot(string installDirectory) => Path.Combine(MetadataDirectory(installDirectory), "staging");

    public static string CacheDirectory(string installDirectory) => Path.Combine(MetadataDirectory(installDirectory), "cache");

    public static string BaseCopyProgressPath(string installDirectory) => Path.Combine(MetadataDirectory(installDirectory), "basecopy.json");

    public const string TransactionsFolder = "tx";
    public const string OriginalsFolder = "originals";
    public const string DisabledFolder = "disabled";
    public const string UserBackupFolder = "user-backup";

    public static bool Exists(string directory)
    {
        try
        {
            return File.Exists(StatePath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    public static bool HasPendingTransaction(string installDirectory) => File.Exists(JournalPath(installDirectory));

    public static InstallState? Load(string installDirectory)
    {
        var path = StatePath(installDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var state = AtomicFile.ReadJson<InstallState>(path) ?? throw new JsonException("empty");
            return Normalize(state);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw SanovaException.Wrap(ErrorCodes.InstallStateCorrupt, $"Install state is unreadable: {ex.Message}", ex, ("path", path));
        }
    }

    public static InstallState Normalize(InstallState state)
    {
        state.Files = new Dictionary<string, InstalledFile>(state.Files ?? [], SafePath.Comparer);
        state.Stashed = new Dictionary<string, InstalledFile>(state.Stashed ?? [], SafePath.Comparer);
        state.Originals = new Dictionary<string, InstalledFile>(state.Originals ?? [], SafePath.Comparer);
        state.EnabledComponents ??= [];
        state.DisplacedUserFiles ??= [];
        return state;
    }

    public static void Save(string installDirectory, InstallState state)
    {
        EnsureMetadataDirectory(installDirectory);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        AtomicFile.WriteJson(StatePath(installDirectory), state);
    }

    public static void EnsureMetadataDirectory(string installDirectory)
    {
        var metadata = MetadataDirectory(installDirectory);
        if (!Directory.Exists(metadata))
        {
            Directory.CreateDirectory(metadata);
            FileSystem.SetHidden(metadata);
        }
    }

    /// <summary>Resolves a path stored relative to the metadata folder.</summary>
    public static string MetadataPath(string installDirectory, string relative)
        => SafePath.GetFullPathUnder(MetadataDirectory(installDirectory), relative);
}

/// <summary>Exclusive lock so two launcher instances never modify the same installation.</summary>
public sealed class InstallLock : IDisposable
{
    private readonly FileStream _stream;

    private InstallLock(FileStream stream)
    {
        _stream = stream;
    }

    public static InstallLock Acquire(string installDirectory)
    {
        InstallStateStore.EnsureMetadataDirectory(installDirectory);
        var path = Path.Combine(InstallStateStore.MetadataDirectory(installDirectory), "lock");
        try
        {
            return new InstallLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException ex)
        {
            throw SanovaException.Wrap(ErrorCodes.InstallLocked, "Another process is modifying this installation.", ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}
