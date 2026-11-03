using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sanova.Core.IO;

namespace Sanova.Core.Installation;

public enum TxOperationKind
{
    /// <summary>Move <see cref="TxOperation.Source"/> to the target, backing up whatever was there.</summary>
    Place,

    /// <summary>Move the target into the transaction backup.</summary>
    Delete,

    /// <summary>Park the target in .sanova/disabled (disabling an optional component).</summary>
    Stash,

    /// <summary>Move a parked file back into place (re-enabling a component).</summary>
    Unstash,
}

/// <summary>What happens to a backup once the transaction has committed.</summary>
public enum BackupHandling
{
    Discard,

    /// <summary>An original game file replaced by content: keep it in .sanova/originals.</summary>
    KeepAsOriginal,

    /// <summary>A player file (or player-modified file): keep it in .sanova/user-backup.</summary>
    KeepAsUserBackup,
}

public sealed class TxOperation
{
    public TxOperationKind Kind { get; set; }

    /// <summary>Target path relative to the installation folder.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Relative to the metadata folder: staged file, preserved original or parked file.</summary>
    public string? Source { get; set; }

    /// <summary>Relative to the metadata folder: parking location for <see cref="TxOperationKind.Stash"/>.</summary>
    public string? Stash { get; set; }

    /// <summary>Relative to the metadata folder; assigned by the engine.</summary>
    public string Backup { get; set; } = string.Empty;

    public BackupHandling BackupHandling { get; set; }
}

public enum TxPhase
{
    Applying,
    Committing,
    Committed,
}

public sealed class TxJournal
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public TxPhase Phase { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public int Completed { get; set; }

    public List<TxOperation> Operations { get; set; } = [];
}

public enum RecoveryOutcome
{
    NothingToRecover,

    /// <summary>An interrupted change was undone; the previous state is intact.</summary>
    RolledBack,

    /// <summary>An interrupted change had already committed and was finished.</summary>
    Completed,

    /// <summary>The journal was unreadable; verification and repair are recommended.</summary>
    Failed,
}

/// <summary>
/// Applies a set of file operations atomically with respect to the installation state.
/// Protocol: journal → apply (with backups) → write install.next.json → phase Committing →
/// swap into install.json (the commit point) → phase Committed → post-commit cleanup.
/// Rollback inspects the file system rather than trusting the journal's progress counter,
/// so it is correct even when a crash happened between an operation and its journal write.
/// </summary>
public sealed class TransactionEngine
{
    private readonly ILogger<TransactionEngine> _logger;

    public TransactionEngine(ILogger<TransactionEngine> logger)
    {
        _logger = logger;
    }

    public static string NewTransactionId()
        => DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];

    public void Commit(string installDirectory, string transactionId, string kind, IReadOnlyList<TxOperation> operations, InstallState newState,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (InstallStateStore.HasPendingTransaction(installDirectory))
        {
            throw SanovaException.Create(ErrorCodes.InstallBusy, "A previous transaction has not been recovered.");
        }

        var journal = new TxJournal
        {
            Id = transactionId,
            Kind = kind,
            Phase = TxPhase.Applying,
            StartedAt = DateTimeOffset.UtcNow,
            Operations = operations.ToList(),
        };
        for (var index = 0; index < journal.Operations.Count; index++)
        {
            var operation = journal.Operations[index];
            operation.Path = SafePath.NormalizeRelative(operation.Path);
            // Unique per operation: one transaction may touch the same path twice (remove content, restore the original).
            operation.Backup = $"{InstallStateStore.TransactionsFolder}/{transactionId}/b/{index}/{operation.Path}";
            if (operation.Kind == TxOperationKind.Stash && operation.Stash is not null)
            {
                // A stale parked copy is about to be replaced; remove it so rollback cannot mistake it for ours.
                FileSystem.DeleteFile(Meta(installDirectory, operation.Stash));
            }
        }

        WriteJournal(installDirectory, journal);
        var nextStatePath = InstallStateStore.NextStatePath(installDirectory);
        var clock = Stopwatch.StartNew();
        try
        {
            for (var i = 0; i < journal.Operations.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = journal.Operations[i];
                Execute(installDirectory, operation);
                journal.Completed = i + 1;
                if (clock.ElapsedMilliseconds >= 250)
                {
                    WriteJournal(installDirectory, journal);
                    progress?.Report(new OperationProgress(OperationStage.Applying, ItemsDone: i + 1, ItemsTotal: journal.Operations.Count, CurrentItem: operation.Path));
                    clock.Restart();
                }
            }
            WriteJournal(installDirectory, journal);
            AtomicFile.WriteJson(nextStatePath, newState);
            journal.Phase = TxPhase.Committing;
            WriteJournal(installDirectory, journal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction {Id} ({Kind}) failed after {Completed}/{Total} operations; rolling back",
                transactionId, kind, journal.Completed, journal.Operations.Count);
            progress?.Report(new OperationProgress(OperationStage.RollingBack));
            journal.Phase = TxPhase.Applying;
            try
            {
                Rollback(installDirectory, journal);
                FileSystem.TryDeleteFile(nextStatePath);
                Cleanup(installDirectory, journal);
            }
            catch (Exception rollbackError)
            {
                _logger.LogCritical(rollbackError, "Rollback of transaction {Id} failed; the journal is kept for recovery", transactionId);
                throw SanovaException.Wrap(ErrorCodes.RollbackFailed, "Rollback failed.", new AggregateException(ex, rollbackError));
            }
            if (ex is OperationCanceledException or SanovaException) throw;
            throw FileSystem.MapIoException(ex, installDirectory);
        }

        // Past this point the change is logically committed; recovery would finish it.
        try
        {
            FileSystem.MoveFile(nextStatePath, InstallStateStore.StatePath(installDirectory), overwrite: true);
            journal.Phase = TxPhase.Committed;
            WriteJournal(installDirectory, journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Transaction {Id} committed but finalization failed; it will complete on next start", transactionId);
            throw FileSystem.MapIoException(ex, installDirectory);
        }

        FinishCommit(installDirectory, journal);
        progress?.Report(new OperationProgress(OperationStage.Applying, ItemsDone: journal.Operations.Count, ItemsTotal: journal.Operations.Count));
        _logger.LogInformation("Transaction {Id} ({Kind}) committed: {Count} operation(s)", transactionId, kind, journal.Operations.Count);
    }

    /// <summary>Finishes or undoes a transaction interrupted by a crash or power loss.</summary>
    public RecoveryOutcome Recover(string installDirectory)
    {
        var journalPath = InstallStateStore.JournalPath(installDirectory);
        var nextStatePath = InstallStateStore.NextStatePath(installDirectory);
        if (!File.Exists(journalPath))
        {
            FileSystem.TryDeleteFile(nextStatePath);
            return RecoveryOutcome.NothingToRecover;
        }

        TxJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize<TxJournal>(File.ReadAllBytes(journalPath), JsonDefaults.Compact) ?? throw new JsonException("empty journal");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogCritical(ex, "Transaction journal is unreadable");
            var preserved = journalPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            FileSystem.MoveFile(journalPath, preserved, overwrite: true);
            return RecoveryOutcome.Failed;
        }

        switch (journal.Phase)
        {
            case TxPhase.Applying:
                _logger.LogWarning("Rolling back interrupted transaction {Id} ({Kind})", journal.Id, journal.Kind);
                Rollback(installDirectory, journal);
                FileSystem.TryDeleteFile(nextStatePath);
                Cleanup(installDirectory, journal);
                return RecoveryOutcome.RolledBack;

            case TxPhase.Committing:
                if (File.Exists(nextStatePath))
                {
                    FileSystem.MoveFile(nextStatePath, InstallStateStore.StatePath(installDirectory), overwrite: true);
                }
                journal.Phase = TxPhase.Committed;
                WriteJournal(installDirectory, journal);
                FinishCommit(installDirectory, journal);
                return RecoveryOutcome.Completed;

            default:
                FinishCommit(installDirectory, journal);
                return RecoveryOutcome.Completed;
        }
    }

    private static void Execute(string installDirectory, TxOperation operation)
    {
        var target = SafePath.GetFullPathUnder(installDirectory, operation.Path);
        var backup = Meta(installDirectory, operation.Backup);
        switch (operation.Kind)
        {
            case TxOperationKind.Place:
            {
                var source = Meta(installDirectory, operation.Source!);
                if (!File.Exists(source))
                {
                    throw SanovaException.Create(ErrorCodes.IoError, $"Staged file missing: {operation.Source}", ("path", operation.Path), ("detail", "staged file missing"));
                }
                if (File.Exists(target)) FileSystem.MoveFile(target, backup, overwrite: true);
                FileSystem.MoveFile(source, target, overwrite: false);
                break;
            }
            case TxOperationKind.Delete:
                if (File.Exists(target)) FileSystem.MoveFile(target, backup, overwrite: true);
                break;
            case TxOperationKind.Stash:
                if (File.Exists(target)) FileSystem.MoveFile(target, Meta(installDirectory, operation.Stash!), overwrite: true);
                break;
            case TxOperationKind.Unstash:
            {
                var parked = Meta(installDirectory, operation.Source!);
                if (!File.Exists(parked))
                {
                    throw SanovaException.Create(ErrorCodes.IoError, $"Parked file missing: {operation.Source}", ("path", operation.Path), ("detail", "parked file missing"));
                }
                if (File.Exists(target)) FileSystem.MoveFile(target, backup, overwrite: true);
                FileSystem.MoveFile(parked, target, overwrite: false);
                break;
            }
        }
    }

    /// <summary>Undoes every operation in reverse order; operations that never ran are no-ops.</summary>
    private void Rollback(string installDirectory, TxJournal journal)
    {
        var failures = new List<Exception>();
        for (var i = journal.Operations.Count - 1; i >= 0; i--)
        {
            var operation = journal.Operations[i];
            try
            {
                Undo(installDirectory, operation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SanovaException)
            {
                _logger.LogError(ex, "Could not undo {Kind} of {Path}", operation.Kind, operation.Path);
                failures.Add(ex);
            }
        }
        foreach (var operation in journal.Operations.Where(o => o.Kind is TxOperationKind.Place or TxOperationKind.Unstash))
        {
            var directory = Path.GetDirectoryName(SafePath.GetFullPathUnder(installDirectory, operation.Path));
            if (directory is not null) FileSystem.DeleteEmptyParents(installDirectory, directory);
        }
        if (failures.Count > 0) throw new AggregateException("Rollback incomplete.", failures);
    }

    private static void Undo(string installDirectory, TxOperation operation)
    {
        var target = SafePath.GetFullPathUnder(installDirectory, operation.Path);
        var backup = Meta(installDirectory, operation.Backup);
        switch (operation.Kind)
        {
            case TxOperationKind.Place:
            {
                var source = Meta(installDirectory, operation.Source!);
                if (!File.Exists(source) && File.Exists(target)) FileSystem.MoveFile(target, source, overwrite: false);
                if (File.Exists(backup)) FileSystem.MoveFile(backup, target, overwrite: true);
                break;
            }
            case TxOperationKind.Delete:
                if (File.Exists(backup) && !File.Exists(target)) FileSystem.MoveFile(backup, target, overwrite: false);
                break;
            case TxOperationKind.Stash:
            {
                var parked = Meta(installDirectory, operation.Stash!);
                if (File.Exists(parked) && !File.Exists(target)) FileSystem.MoveFile(parked, target, overwrite: false);
                break;
            }
            case TxOperationKind.Unstash:
            {
                var parked = Meta(installDirectory, operation.Source!);
                if (!File.Exists(parked) && File.Exists(target)) FileSystem.MoveFile(target, parked, overwrite: false);
                if (File.Exists(backup)) FileSystem.MoveFile(backup, target, overwrite: true);
                break;
            }
        }
    }

    private void FinishCommit(string installDirectory, TxJournal journal)
    {
        foreach (var operation in journal.Operations)
        {
            try
            {
                var backup = Meta(installDirectory, operation.Backup);
                if (File.Exists(backup))
                {
                    switch (operation.BackupHandling)
                    {
                        case BackupHandling.KeepAsOriginal:
                            FileSystem.MoveFile(backup, Meta(installDirectory, $"{InstallStateStore.OriginalsFolder}/{operation.Path}"), overwrite: true);
                            break;
                        case BackupHandling.KeepAsUserBackup:
                            FileSystem.MoveFile(backup, Meta(installDirectory, $"{InstallStateStore.UserBackupFolder}/{journal.Id}/{operation.Path}"), overwrite: true);
                            break;
                        default:
                            FileSystem.DeleteFile(backup);
                            break;
                    }
                }
                if (operation.Kind is TxOperationKind.Delete or TxOperationKind.Stash)
                {
                    var directory = Path.GetDirectoryName(SafePath.GetFullPathUnder(installDirectory, operation.Path));
                    if (directory is not null) FileSystem.DeleteEmptyParents(installDirectory, directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Post-commit cleanup failed for {Path}", operation.Path);
            }
        }
        Cleanup(installDirectory, journal);
    }

    private void Cleanup(string installDirectory, TxJournal journal)
    {
        try
        {
            FileSystem.DeleteDirectory(Meta(installDirectory, $"{InstallStateStore.TransactionsFolder}/{journal.Id}"));
            FileSystem.DeleteDirectory(Path.Combine(InstallStateStore.StagingRoot(installDirectory), journal.Id));
            FileSystem.DeleteFile(InstallStateStore.JournalPath(installDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not clean up transaction {Id}", journal.Id);
        }
    }

    private static void WriteJournal(string installDirectory, TxJournal journal)
        => AtomicFile.WriteJson(InstallStateStore.JournalPath(installDirectory), journal, JsonDefaults.Compact);

    private static string Meta(string installDirectory, string relative) => InstallStateStore.MetadataPath(installDirectory, relative);
}
