using Sanova.Core.Content;
using Sanova.Core.IO;
using Sanova.Core.Updates;

namespace Sanova.Core.Installation;

public enum PlanMode
{
    Install,
    Update,
    Components,
    Repair,
}

/// <summary>The operations needed to move an installation to a target manifest and component selection.</summary>
public sealed class InstallPlan
{
    public InstallPlan(string transactionId, InstallState newState)
    {
        TransactionId = transactionId;
        NewState = newState;
    }

    public string TransactionId { get; }

    public InstallState NewState { get; }

    /// <summary>Removals first, then placements; acquired files are staged under staging/&lt;tx&gt;/.</summary>
    public List<TxOperation> Operations { get; } = [];

    public List<ManifestFile> ToAcquire { get; } = [];

    /// <summary>Damaged original game files to re-copy from the player's original installation.</summary>
    public List<(string Path, InstalledFile Record)> BaseRestores { get; } = [];

    /// <summary>Parked files that become obsolete and are deleted after commit.</summary>
    public List<string> ObsoleteStashes { get; } = [];

    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Disabled { get; set; }
    public int Enabled { get; set; }

    public long AcquireBytes => ToAcquire.Sum(f => f.Size);

    /// <summary>Approximate download volume (compressed sizes when known).</summary>
    public long DownloadEstimate => ToAcquire.Sum(f => f.PackedSize ?? f.Size);

    public bool IsEmpty => Operations.Count == 0;

    public string StagingPrefix => $"staging/{TransactionId}/";
}

/// <summary>
/// Computes install/update/toggle/repair plans. Rules that protect the player:
/// unknown files at a target path are moved to user-backup, never overwritten;
/// player-modified managed files are backed up before replacement; original game files
/// replaced by content are preserved and restored when that content goes away.
/// </summary>
public static class InstallPlanner
{
    public static InstallPlan Plan(
        string installDirectory,
        InstallState state,
        ResolvedManifest target,
        IReadOnlySet<string> components,
        PlanMode mode,
        string transactionId,
        IReadOnlyCollection<string>? damagedPaths,
        string launcherVersion,
        string channel)
    {
        var manifest = target.Manifest;
        var newState = state.Clone();
        newState.Phase = InstallPhase.Ready;
        newState.ContentVersion = manifest.ContentVersion;
        newState.ManifestSha256 = target.Sha256;
        newState.Channel = channel;
        newState.LauncherVersion = launcherVersion;
        newState.EnabledComponents = components.OrderBy(c => c, StringComparer.Ordinal).ToList();

        var plan = new InstallPlan(transactionId, newState);
        var damaged = new HashSet<string>(damagedPaths ?? [], SafePath.Comparer);
        var wanted = manifest.FilesOf(components).ToDictionary(f => f.NormalizedPath, f => f, SafePath.Comparer);
        var manifestFiles = manifest.Files.ToDictionary(f => f.NormalizedPath, f => f, SafePath.Comparer);

        // 1. Content files that should go away (component disabled or file dropped from the release).
        foreach (var (path, current) in state.Files.Where(p => p.Value.Origin == FileOrigin.Content).ToList())
        {
            if (wanted.ContainsKey(path)) continue;

            var full = SafePath.GetFullPathUnder(installDirectory, path);
            var canPark = current.Component is not null
                          && manifest.FindComponent(current.Component) is not null
                          && !components.Contains(current.Component)
                          && manifestFiles.TryGetValue(path, out var stillPublished)
                          && FileHashing.HashEquals(stillPublished.Sha256, current.Sha256)
                          && File.Exists(full)
                          && !IsModified(full, current);

            if (canPark)
            {
                plan.Operations.Add(new TxOperation { Kind = TxOperationKind.Stash, Path = path, Stash = $"{InstallStateStore.DisabledFolder}/{path}" });
                newState.Stashed[path] = current.Copy();
                plan.Disabled++;
            }
            else
            {
                plan.Operations.Add(new TxOperation
                {
                    Kind = TxOperationKind.Delete,
                    Path = path,
                    BackupHandling = File.Exists(full) && IsModified(full, current) ? BackupHandling.KeepAsUserBackup : BackupHandling.Discard,
                });
                plan.Removed++;
            }
            newState.Files.Remove(path);

            if (current.OverridesBase && state.Originals.TryGetValue(path, out var original) &&
                File.Exists(InstallStateStore.MetadataPath(installDirectory, $"{InstallStateStore.OriginalsFolder}/{path}")))
            {
                plan.Operations.Add(new TxOperation { Kind = TxOperationKind.Place, Path = path, Source = $"{InstallStateStore.OriginalsFolder}/{path}" });
                newState.Files[path] = original.Copy();
                newState.Originals.Remove(path);
            }
        }

        // 2. Wanted files: keep, re-enable from parking, or acquire.
        foreach (var (path, file) in wanted)
        {
            state.Files.TryGetValue(path, out var current);
            var isDamaged = damaged.Contains(path);
            var full = SafePath.GetFullPathUnder(installDirectory, path);

            if (current is { Origin: FileOrigin.Content } && FileHashing.HashEquals(current.Sha256, file.Sha256) && !isDamaged)
            {
                newState.Files[path].Component = file.Component;
                continue;
            }

            if (state.Stashed.TryGetValue(path, out var parked))
            {
                newState.Stashed.Remove(path);
                var parkedPath = InstallStateStore.MetadataPath(installDirectory, $"{InstallStateStore.DisabledFolder}/{path}");
                if (FileHashing.HashEquals(parked.Sha256, file.Sha256) && File.Exists(parkedPath) && current is null or { Origin: FileOrigin.Base })
                {
                    var overridesBase = current is { Origin: FileOrigin.Base };
                    plan.Operations.Add(new TxOperation
                    {
                        Kind = TxOperationKind.Unstash,
                        Path = path,
                        Source = $"{InstallStateStore.DisabledFolder}/{path}",
                        BackupHandling = overridesBase ? BackupHandling.KeepAsOriginal
                            : File.Exists(full) ? BackupHandling.KeepAsUserBackup : BackupHandling.Discard,
                    });
                    if (overridesBase) newState.Originals[path] = current!.Copy();
                    else if (File.Exists(full)) RecordDisplaced(newState, path, transactionId);
                    var restored = parked.Copy();
                    restored.Component = file.Component;
                    restored.OverridesBase = overridesBase;
                    newState.Files[path] = restored;
                    plan.Enabled++;
                    continue;
                }
                plan.ObsoleteStashes.Add(path);
            }

            var handling = current switch
            {
                null => File.Exists(full) ? BackupHandling.KeepAsUserBackup : BackupHandling.Discard,
                { Origin: FileOrigin.Base } => BackupHandling.KeepAsOriginal,
                _ => File.Exists(full) && (isDamaged || IsModified(full, current)) ? BackupHandling.KeepAsUserBackup : BackupHandling.Discard,
            };
            if (current is null && File.Exists(full)) RecordDisplaced(newState, path, transactionId);
            if (current is { Origin: FileOrigin.Base })
            {
                newState.Originals[path] = current.Copy();
            }

            plan.ToAcquire.Add(file);
            plan.Operations.Add(new TxOperation { Kind = TxOperationKind.Place, Path = path, Source = plan.StagingPrefix + path, BackupHandling = handling });
            newState.Files[path] = new InstalledFile
            {
                Size = file.Size,
                Sha256 = file.Sha256.ToLowerInvariant(),
                Origin = FileOrigin.Content,
                Component = file.Component,
                OverridesBase = current?.Origin == FileOrigin.Base || current?.OverridesBase == true,
            };
            if (current is null) plan.Added++;
            else plan.Updated++;
        }

        // 3. Parked files whose component no longer exists or whose content changed.
        foreach (var (path, parked) in state.Stashed)
        {
            if (wanted.ContainsKey(path)) continue;
            var keep = parked.Component is not null
                       && manifest.FindComponent(parked.Component) is not null
                       && manifestFiles.TryGetValue(path, out var published)
                       && FileHashing.HashEquals(published.Sha256, parked.Sha256);
            if (!keep && !plan.Operations.Any(o => o.Kind == TxOperationKind.Stash && string.Equals(o.Path, path, SafePath.Comparison)))
            {
                newState.Stashed.Remove(path);
                plan.ObsoleteStashes.Add(path);
            }
        }

        // 4. Damaged original game files (repair mode).
        if (mode == PlanMode.Repair)
        {
            foreach (var path in damaged)
            {
                if (!state.Files.TryGetValue(path, out var record) || record.Origin != FileOrigin.Base) continue;
                var full = SafePath.GetFullPathUnder(installDirectory, path);
                plan.BaseRestores.Add((path, record));
                plan.Operations.Add(new TxOperation
                {
                    Kind = TxOperationKind.Place,
                    Path = path,
                    Source = plan.StagingPrefix + path,
                    BackupHandling = File.Exists(full) ? BackupHandling.KeepAsUserBackup : BackupHandling.Discard,
                });
            }
        }

        // Removals before placements so file/folder swaps never collide.
        var ordered = plan.Operations
            .Select((operation, index) => (operation, index))
            .OrderBy(x => x.operation.Kind is TxOperationKind.Delete or TxOperationKind.Stash ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.operation)
            .ToList();
        plan.Operations.Clear();
        plan.Operations.AddRange(ordered);
        return plan;
    }

    private static void RecordDisplaced(InstallState state, string path, string transactionId)
    {
        state.DisplacedUserFiles.Add(new DisplacedFile
        {
            Path = path,
            BackupPath = $"{InstallStateStore.UserBackupFolder}/{transactionId}/{path}",
            DisplacedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Cheap modification check: size, then write time, then (only if needed) a hash.</summary>
    public static bool IsModified(string fullPath, InstalledFile record)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists) return false;
        if (info.Length != record.Size) return true;
        if (record.LastWriteUtc is { } recorded && info.LastWriteTimeUtc == recorded) return false;
        var actual = FileHashing.Sha256FileAsync(fullPath).GetAwaiter().GetResult();
        return !FileHashing.HashEquals(actual, record.Sha256);
    }
}
