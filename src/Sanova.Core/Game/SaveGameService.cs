using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sanova.Core.IO;
using Sanova.Core.Settings;

namespace Sanova.Core.Game;

public sealed record SaveSlot(int Slot, string Path, long Size, DateTimeOffset Modified);

public sealed record SaveBackup(string Path, DateTimeOffset Created, long Size, int FileCount, string Reason);

/// <summary>
/// Protects the player's saved games. GTA San Andreas keeps saves in
/// "Documents\GTA San Andreas User Files", shared by every installation on the machine,
/// so the launcher backs them up before it changes anything.
/// </summary>
public sealed class SaveGameService
{
    public const string UserFilesFolderName = "GTA San Andreas User Files";
    private const string BackupInfoEntry = "sanova-backup.json";
    private const int DefaultRetention = 20;

    private readonly AppPaths _paths;
    private readonly ILogger<SaveGameService> _logger;
    private readonly Func<string> _documentsFolder;

    private sealed record BackupInfo(DateTimeOffset Created, string Reason, List<string> Files);

    public SaveGameService(AppPaths paths, ILogger<SaveGameService> logger, Func<string>? documentsFolder = null)
    {
        _paths = paths;
        _logger = logger;
        _documentsFolder = documentsFolder ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    public string UserFilesDirectory => Path.Combine(_documentsFolder(), UserFilesFolderName);

    public string BackupsDirectory => _paths.SaveBackupsDirectory;

    public IReadOnlyList<SaveSlot> GetSaveSlots()
    {
        var result = new List<SaveSlot>();
        for (var slot = 1; slot <= 8; slot++)
        {
            var file = new FileInfo(Path.Combine(UserFilesDirectory, $"GTASAsf{slot}.b"));
            if (file.Exists)
            {
                result.Add(new SaveSlot(slot, file.FullName, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }
        return result;
    }

    public IReadOnlyList<SaveBackup> GetBackups()
    {
        if (!Directory.Exists(BackupsDirectory)) return [];
        var result = new List<SaveBackup>();
        foreach (var file in new DirectoryInfo(BackupsDirectory).EnumerateFiles("saves-*.zip"))
        {
            try
            {
                using var archive = ZipFile.OpenRead(file.FullName);
                var infoEntry = archive.GetEntry(BackupInfoEntry);
                BackupInfo? info = null;
                if (infoEntry is not null)
                {
                    using var stream = infoEntry.Open();
                    info = JsonSerializer.Deserialize<BackupInfo>(stream, JsonDefaults.Compact);
                }
                result.Add(new SaveBackup(
                    file.FullName,
                    info?.Created ?? new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                    file.Length,
                    info?.Files.Count ?? archive.Entries.Count(e => e.FullName != BackupInfoEntry),
                    info?.Reason ?? "manual"));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Skipping unreadable save backup {File}", file.FullName);
            }
        }
        return result.OrderByDescending(b => b.Created).ToList();
    }

    /// <summary>Backs up saves and settings. Returns null when there is nothing to back up.</summary>
    public async Task<SaveBackup?> CreateBackupAsync(string reason, CancellationToken cancellationToken = default)
    {
        var source = UserFilesDirectory;
        if (!Directory.Exists(source)) return null;

        // Saves and settings live at the top level; "User Tracks" and "Gallery" can be large and are not game state.
        var files = new DirectoryInfo(source).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .ToList();
        if (files.Count == 0) return null;

        Directory.CreateDirectory(BackupsDirectory);
        var created = DateTimeOffset.Now;
        var name = $"saves-{created.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..6]}.zip";
        var target = Path.Combine(BackupsDirectory, name);
        var temp = target + ".tmp";

        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal);
                    entry.LastWriteTime = file.LastWriteTime;
                    await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await using var entryStream = entry.Open();
                    await input.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                }

                var info = new BackupInfo(created, reason, files.Select(f => f.Name).ToList());
                var infoEntry = archive.CreateEntry(BackupInfoEntry, CompressionLevel.Optimal);
                await using (var infoStream = infoEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(infoStream, info, JsonDefaults.Compact, cancellationToken).ConfigureAwait(false);
                }
            }
            FileSystem.MoveFile(temp, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileSystem.TryDeleteFile(temp);
            throw SanovaException.Wrap(ErrorCodes.SavesBackupFailed, $"Could not back up saves: {ex.Message}", ex, ("detail", ex.Message));
        }
        catch
        {
            FileSystem.TryDeleteFile(temp);
            throw;
        }

        _logger.LogInformation("Backed up {Count} save file(s) to {Target} ({Reason})", files.Count, target, reason);
        PruneBackups(DefaultRetention);
        return new SaveBackup(target, created, new FileInfo(target).Length, files.Count, reason);
    }

    /// <summary>Restores a backup. Current saves are backed up first, so a restore can always be undone.</summary>
    public async Task RestoreBackupAsync(SaveBackup backup, CancellationToken cancellationToken = default)
    {
        if (!SafePath.IsStrictlyUnder(BackupsDirectory, backup.Path) || !File.Exists(backup.Path))
        {
            throw SanovaException.Create(ErrorCodes.SavesRestoreFailed, $"Backup not found: {backup.Path}", ("detail", backup.Path));
        }

        await CreateBackupAsync("before-restore", cancellationToken).ConfigureAwait(false);

        var target = UserFilesDirectory;
        Directory.CreateDirectory(target);
        try
        {
            using var archive = ZipFile.OpenRead(backup.Path);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName == BackupInfoEntry || entry.FullName.EndsWith('/')) continue;

                // Backups only ever contain top-level files; anything else is rejected outright.
                var name = SafePath.NormalizeRelative(entry.FullName);
                if (name.Contains('/', StringComparison.Ordinal))
                {
                    throw SanovaException.Create(ErrorCodes.ArchiveUnsafeEntry, $"Unexpected entry '{entry.FullName}' in save backup.", ("path", entry.FullName));
                }

                var destination = SafePath.GetFullPathUnder(target, name);
                var temp = destination + ".sanova-restore";
                await using (var input = entry.Open())
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await FileHashing.CopyAndHashAsync(input, output, entry.Length, null, cancellationToken).ConfigureAwait(false);
                }
                FileSystem.MoveFile(temp, destination, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw SanovaException.Wrap(ErrorCodes.SavesRestoreFailed, $"Could not restore saves: {ex.Message}", ex, ("detail", ex.Message));
        }
        _logger.LogInformation("Restored saves from {Backup}", backup.Path);
    }

    public void PruneBackups(int keep)
    {
        foreach (var old in GetBackups().Skip(keep))
        {
            FileSystem.TryDeleteFile(old.Path);
        }
    }
}
