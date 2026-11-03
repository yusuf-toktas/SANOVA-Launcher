using System.IO.Compression;
using Sanova.Core.Game;
using Sanova.Core.Settings;

namespace Sanova.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Persists_and_reloads_settings()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var store = new SettingsStore(paths, Log.For<SettingsStore>());
        Assert.Equal(SettingsLoadOutcome.CreatedDefaults, store.Load());
        Assert.Equal(AppTheme.Dark, store.Current.Theme);
        Assert.Equal("auto", store.Current.Language);

        store.Update(s =>
        {
            s.Language = "TR";
            s.Theme = AppTheme.Light;
            s.MaxParallelDownloads = 99;
            s.ComponentChoices["HD"] = false;
        });

        var reloaded = new SettingsStore(paths, Log.For<SettingsStore>());
        Assert.Equal(SettingsLoadOutcome.Loaded, reloaded.Load());
        Assert.Equal("tr", reloaded.Current.Language);
        Assert.Equal(AppTheme.Light, reloaded.Current.Theme);
        Assert.Equal(8, reloaded.Current.MaxParallelDownloads);
        Assert.False(reloaded.Current.ComponentChoices["hd"]);
    }

    [Fact]
    public void Recovers_from_a_damaged_settings_file()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        File.WriteAllText(paths.SettingsFile, "{ this is not json");

        var store = new SettingsStore(paths, Log.For<SettingsStore>());
        Assert.Equal(SettingsLoadOutcome.RecoveredFromCorruption, store.Load());
        Assert.NotNull(store.CorruptBackupPath);
        Assert.True(File.Exists(store.CorruptBackupPath));
        Assert.Equal(AppTheme.Dark, store.Current.Theme);
    }

    [Fact]
    public void Snapshots_are_independent_copies()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(new AppPaths(temp.Path), Log.For<SettingsStore>());
        store.Load();
        var snapshot = store.Current;
        snapshot.Language = "tr";
        Assert.Equal("auto", store.Current.Language);
    }
}

public class SaveGameServiceTests
{
    [Fact]
    public async Task Backs_up_and_restores_saves_with_a_safety_backup()
    {
        using var temp = new TempDir();
        var documents = temp.Sub("Documents");
        var userFiles = Path.Combine(documents, SaveGameService.UserFilesFolderName);
        TempDir.WriteFile(userFiles, "GTASAsf1.b", "slot one"u8.ToArray());
        TempDir.WriteFile(userFiles, "gta_sa.set", "settings"u8.ToArray());
        TempDir.WriteFile(userFiles, "User Tracks/song.mp3", new byte[100]);
        var service = new SaveGameService(new AppPaths(temp.Sub("data")), Log.For<SaveGameService>(), () => documents);

        Assert.Single(service.GetSaveSlots());
        var backup = await service.CreateBackupAsync("manual", TestContext.Current.CancellationToken);
        Assert.NotNull(backup);
        Assert.Equal(2, backup!.FileCount);

        File.WriteAllText(Path.Combine(userFiles, "GTASAsf1.b"), "changed later");
        await service.RestoreBackupAsync(backup, TestContext.Current.CancellationToken);

        Assert.Equal("slot one", File.ReadAllText(Path.Combine(userFiles, "GTASAsf1.b")));
        var backups = service.GetBackups();
        Assert.Equal(2, backups.Count);
        Assert.Contains(backups, b => b.Reason == "before-restore");
    }

    [Fact]
    public async Task Returns_null_when_there_is_nothing_to_back_up()
    {
        using var temp = new TempDir();
        var service = new SaveGameService(new AppPaths(temp.Sub("data")), Log.For<SaveGameService>(), () => temp.Sub("EmptyDocuments"));
        Assert.Null(await service.CreateBackupAsync("manual", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_backups_with_unexpected_paths()
    {
        using var temp = new TempDir();
        var documents = temp.Sub("Documents");
        var paths = new AppPaths(temp.Sub("data"));
        Directory.CreateDirectory(paths.SaveBackupsDirectory);
        var evil = Path.Combine(paths.SaveBackupsDirectory, "saves-evil.zip");
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("../outside.txt").Open();
            stream.Write("x"u8);
        }
        var service = new SaveGameService(paths, Log.For<SaveGameService>(), () => documents);
        var backup = new SaveBackup(evil, DateTimeOffset.Now, 10, 1, "manual");

        await Assert.ThrowsAsync<SanovaException>(() => service.RestoreBackupAsync(backup, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(documents, "outside.txt")));
    }
}
