using System.Text.Json;
using Sanova.Core.Downloads;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Core.IO;
using Sanova.ManifestTool;

namespace Sanova.Core.Tests;

/// <summary>
/// Opt-in end-to-end run against a real GTA San Andreas v1.0 installation (read-only):
/// copies the ~5 GB game into a temporary folder, applies a small content release, verifies
/// every original file against the shipped reference hashes, then uninstalls.
/// Run with SANOVA_REAL_GAME_TEST=1 and SANOVA_REAL_GAME_PATH set to the game folder.
/// </summary>
public class RealGameIntegrationTests
{
    [Fact]
    public async Task Installs_verifies_and_uninstalls_using_the_real_original_game()
    {
        if (Environment.GetEnvironmentVariable("SANOVA_REAL_GAME_TEST") != "1")
        {
            Assert.Skip("Set SANOVA_REAL_GAME_TEST=1 to run (copies about 5 GB into the temp folder).");
        }
        if (RealGame.Location is not { } realGame)
        {
            Assert.Skip(RealGame.SkipReason);
            return;
        }

        using var temp = new TempDir();
        var originalBefore = Directory.EnumerateFiles(realGame, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)));
        var exeHashBefore = await FileHashing.Sha256FileAsync(Path.Combine(realGame, "gta_sa.exe"));

        var root = temp.Sub("root");
        TempDir.WriteFile(root, "SANOVA.asi", "integration test plugin"u8.ToArray());
        TempDir.WriteFile(root, "data/handling.cfg", "integration test handling"u8.ToArray());
        var configPath = temp.File("content.json", JsonSerializer.Serialize(new
        {
            contentVersion = "0.0.1",
            components = new[] { new { id = "core", name = "Core", required = true, include = new[] { "**" }, redistribution = "permission" } },
        }));
        var release = temp.Sub("release");
        await ManifestBuilder.BuildAsync(new BuildOptions { SourceDirectory = root, ConfigPath = configPath, OutputDirectory = release, Log = TextWriter.Null });
        var manifest = await ContentFixture.LoadAsync(release);

        var checker = new CompatibilityChecker(GameVersionDatabase.LoadEmbedded(), BaseGameReference.LoadEmbedded(), Log.For<CompatibilityChecker>());
        var report = await checker.CheckAsync(realGame, new CompatibilityCheckOptions(false, manifest.Manifest.Compatibility));
        Assert.True(report.CanProceed, string.Join(", ", report.Findings.Select(f => f.Code)));
        var reference = Assert.IsType<BaseGameReference>(report.Reference);

        var http = new HttpClient();
        var downloader = new ResumableDownloader(http, new BandwidthThrottle(), Log.For<ResumableDownloader>());
        var service = new InstallationService(new ContentAcquirer(downloader, http, new BandwidthThrottle(), Log.For<ContentAcquirer>()),
            new BaseGameCopier(Log.For<BaseGameCopier>()), new TransactionEngine(Log.For<TransactionEngine>()), Log.For<InstallationService>(), "0.1.0");
        var install = Path.Combine(temp.Path, "SANOVA");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await service.InstallAsync(new InstallRequest
        {
            OriginalGamePath = realGame,
            InstallPath = install,
            Manifest = manifest,
            Components = new HashSet<string> { "core" },
            Compatibility = report,
        }, null, CancellationToken.None);
        var installSeconds = clock.Elapsed.TotalSeconds;

        var state = InstallStateStore.Load(install)!;
        Assert.Equal(InstallPhase.Ready, state.Phase);
        // Every original file is either in place or (when SANOVA content replaced it) preserved in .sanova/originals.
        Assert.Equal(reference.Files.Count, state.Files.Values.Count(f => f.Origin == FileOrigin.Base) + state.Originals.Count);
        foreach (var file in reference.Files.Where(f => f.Path != "data/handling.cfg"))
        {
            Assert.Equal(file.Sha256, state.Files[file.Path].Sha256);
        }
        Assert.Equal(reference.Files.Single(f => f.Path == "data/handling.cfg").Sha256, state.Originals["data/handling.cfg"].Sha256);
        Assert.Equal("integration test handling", File.ReadAllText(Path.Combine(install, "data", "handling.cfg")));

        clock.Restart();
        var verification = await service.VerifyAsync(install, VerifyMode.Full, null, CancellationToken.None);
        var verifySeconds = clock.Elapsed.TotalSeconds;
        Assert.True(verification.Passed);

        var result = await service.UninstallAsync(install, null, CancellationToken.None);
        Assert.True(result.DirectoryRemoved);

        // The original installation is exactly as it was.
        Assert.Equal(exeHashBefore, await FileHashing.Sha256FileAsync(Path.Combine(realGame, "gta_sa.exe")));
        var originalAfter = Directory.EnumerateFiles(realGame, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)));
        Assert.Equal(originalBefore, originalAfter);
        TestContext.Current.SendDiagnosticMessage($"Install {installSeconds:N1}s, full verify {verifySeconds:N1}s, {state.Files.Count} files");
    }
}
