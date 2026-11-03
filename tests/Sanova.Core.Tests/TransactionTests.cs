using System.Text.Json;
using Sanova.Core.Installation;
using Sanova.Core.IO;

namespace Sanova.Core.Tests;

public class TransactionEngineTests
{
    private static Dictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).StartsWith(InstallStateStore.MetadataFolderName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), f => File.ReadAllText(f), StringComparer.OrdinalIgnoreCase);

    private static string Stage(string install, string tx, string path, string content)
        => TempDir.WriteFile(InstallStateStore.MetadataDirectory(install), $"staging/{tx}/{path}", System.Text.Encoding.UTF8.GetBytes(content));

    private static TransactionEngine Engine() => new(Log.For<TransactionEngine>());

    [Fact]
    public void Commits_all_operation_kinds_and_handles_backups()
    {
        using var temp = new TempDir();
        var install = temp.Sub("install");
        TempDir.WriteFile(install, "data/handling.cfg", "original"u8.ToArray());
        TempDir.WriteFile(install, "old.asi", "obsolete"u8.ToArray());
        TempDir.WriteFile(install, "modloader/hd/a.txd", "hd"u8.ToArray());
        TempDir.WriteFile(InstallStateStore.MetadataDirectory(install), "disabled/modloader/extra/e.dff", "extra"u8.ToArray());
        InstallStateStore.Save(install, new InstallState { ContentVersion = "1.0.0" });

        const string tx = "t1";
        Stage(install, tx, "data/handling.cfg", "sanova");
        Stage(install, tx, "new.asi", "fresh");
        var operations = new List<TxOperation>
        {
            new() { Kind = TxOperationKind.Delete, Path = "old.asi" },
            new() { Kind = TxOperationKind.Stash, Path = "modloader/hd/a.txd", Stash = "disabled/modloader/hd/a.txd" },
            new() { Kind = TxOperationKind.Place, Path = "data/handling.cfg", Source = $"staging/{tx}/data/handling.cfg", BackupHandling = BackupHandling.KeepAsOriginal },
            new() { Kind = TxOperationKind.Place, Path = "new.asi", Source = $"staging/{tx}/new.asi" },
            new() { Kind = TxOperationKind.Unstash, Path = "modloader/extra/e.dff", Source = "disabled/modloader/extra/e.dff" },
        };

        Engine().Commit(install, tx, "update", operations, new InstallState { ContentVersion = "2.0.0" }, null, TestContext.Current.CancellationToken);

        var files = Snapshot(install);
        Assert.Equal("sanova", files["data/handling.cfg"]);
        Assert.Equal("fresh", files["new.asi"]);
        Assert.Equal("extra", files["modloader/extra/e.dff"]);
        Assert.False(files.ContainsKey("old.asi"));
        Assert.False(files.ContainsKey("modloader/hd/a.txd"));
        Assert.False(Directory.Exists(Path.Combine(install, "modloader", "hd")), "empty folders are removed");
        var meta = InstallStateStore.MetadataDirectory(install);
        Assert.Equal("original", File.ReadAllText(Path.Combine(meta, "originals", "data", "handling.cfg")));
        Assert.Equal("hd", File.ReadAllText(Path.Combine(meta, "disabled", "modloader", "hd", "a.txd")));
        Assert.Equal("2.0.0", InstallStateStore.Load(install)!.ContentVersion);
        Assert.False(InstallStateStore.HasPendingTransaction(install));
        Assert.False(Directory.Exists(Path.Combine(meta, "tx", tx)));
        Assert.False(Directory.Exists(Path.Combine(meta, "staging", tx)));
    }

    [Fact]
    public void Failure_midway_restores_the_exact_previous_state()
    {
        using var temp = new TempDir();
        var install = temp.Sub("install");
        TempDir.WriteFile(install, "a.txt", "A"u8.ToArray());
        TempDir.WriteFile(install, "b.txt", "B"u8.ToArray());
        InstallStateStore.Save(install, new InstallState { ContentVersion = "1.0.0" });
        var before = Snapshot(install);

        const string tx = "t2";
        Stage(install, tx, "a.txt", "A2");
        Stage(install, tx, "c/new.txt", "C");
        var operations = new List<TxOperation>
        {
            new() { Kind = TxOperationKind.Place, Path = "a.txt", Source = $"staging/{tx}/a.txt" },
            new() { Kind = TxOperationKind.Place, Path = "c/new.txt", Source = $"staging/{tx}/c/new.txt" },
            new() { Kind = TxOperationKind.Delete, Path = "b.txt" },
            new() { Kind = TxOperationKind.Place, Path = "d.txt", Source = $"staging/{tx}/missing.txt" },
        };

        Assert.Throws<SanovaException>(() =>
            Engine().Commit(install, tx, "update", operations, new InstallState { ContentVersion = "2.0.0" }, null, TestContext.Current.CancellationToken));

        Assert.Equal(before, Snapshot(install));
        Assert.Equal("1.0.0", InstallStateStore.Load(install)!.ContentVersion);
        Assert.False(InstallStateStore.HasPendingTransaction(install));
        Assert.False(Directory.Exists(Path.Combine(install, "c")));
    }

    [Fact]
    public void Cancellation_rolls_back()
    {
        using var temp = new TempDir();
        var install = temp.Sub("install");
        TempDir.WriteFile(install, "a.txt", "A"u8.ToArray());
        InstallStateStore.Save(install, new InstallState());
        Stage(install, "t3", "a.txt", "A2");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => Engine().Commit(install, "t3", "update",
            [new TxOperation { Kind = TxOperationKind.Place, Path = "a.txt", Source = "staging/t3/a.txt" }], new InstallState(), null, cancelled.Token));
        Assert.Equal("A", File.ReadAllText(Path.Combine(install, "a.txt")));
    }

    [Fact]
    public void Recovers_from_a_crash_during_the_apply_phase()
    {
        using var temp = new TempDir();
        var install = temp.Sub("install");
        TempDir.WriteFile(install, "a.txt", "A"u8.ToArray());
        TempDir.WriteFile(install, "b.txt", "B"u8.ToArray());
        InstallStateStore.Save(install, new InstallState { ContentVersion = "1.0.0" });
        var before = Snapshot(install);
        var meta = InstallStateStore.MetadataDirectory(install);

        // Simulate a power loss after the first operation completed and the second was half done,
        // while the journal still reports zero completed operations.
        const string tx = "t4";
        Stage(install, tx, "a.txt", "A2");
        Stage(install, tx, "b.txt", "B2");
        var journal = new TxJournal
        {
            Id = tx,
            Kind = "update",
            Phase = TxPhase.Applying,
            Operations =
            [
                new TxOperation { Kind = TxOperationKind.Place, Path = "a.txt", Source = $"staging/{tx}/a.txt", Backup = $"tx/{tx}/b/a.txt" },
                new TxOperation { Kind = TxOperationKind.Place, Path = "b.txt", Source = $"staging/{tx}/b.txt", Backup = $"tx/{tx}/b/b.txt" },
            ],
        };
        File.WriteAllText(InstallStateStore.JournalPath(install), JsonSerializer.Serialize(journal, JsonDefaults.Compact));
        FileSystem.MoveFile(Path.Combine(install, "a.txt"), Path.Combine(meta, "tx", tx, "b", "a.txt"), false);
        FileSystem.MoveFile(Path.Combine(meta, "staging", tx, "a.txt"), Path.Combine(install, "a.txt"), false);
        FileSystem.MoveFile(Path.Combine(install, "b.txt"), Path.Combine(meta, "tx", tx, "b", "b.txt"), false);

        Assert.Equal(RecoveryOutcome.RolledBack, Engine().Recover(install));
        Assert.Equal(before, Snapshot(install));
        Assert.False(InstallStateStore.HasPendingTransaction(install));
        Assert.Equal(RecoveryOutcome.NothingToRecover, Engine().Recover(install));
    }

    [Fact]
    public void Completes_a_transaction_interrupted_after_the_commit_point()
    {
        using var temp = new TempDir();
        var install = temp.Sub("install");
        InstallStateStore.Save(install, new InstallState { ContentVersion = "1.0.0" });
        AtomicFile.WriteJson(InstallStateStore.NextStatePath(install), new InstallState { ContentVersion = "2.0.0" });
        var journal = new TxJournal { Id = "t5", Kind = "update", Phase = TxPhase.Committing, Operations = [] };
        File.WriteAllText(InstallStateStore.JournalPath(install), JsonSerializer.Serialize(journal, JsonDefaults.Compact));

        Assert.Equal(RecoveryOutcome.Completed, Engine().Recover(install));
        Assert.Equal("2.0.0", InstallStateStore.Load(install)!.ContentVersion);
        Assert.False(File.Exists(InstallStateStore.NextStatePath(install)));
        Assert.False(InstallStateStore.HasPendingTransaction(install));
    }
}
