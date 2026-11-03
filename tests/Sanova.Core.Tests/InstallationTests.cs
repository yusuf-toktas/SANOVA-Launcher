using System.Text.Json;
using Sanova.Core.Downloads;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Core.Updates;
using Sanova.ManifestTool;

namespace Sanova.Core.Tests;

/// <summary>
/// Builds real content releases with the maintainer tool from a temporary Root folder and
/// installs them with the real engine into temporary folders.
/// </summary>
public sealed class ContentFixture : IDisposable
{
    public ContentFixture()
    {
        Temp = new TempDir();
        Executable = Bytes.Random(4096, 42);
        Game = FakeGame.Create(Temp.Sub("original-game"), Executable);
        Root = Temp.Sub("root");
        Install = Path.Combine(Temp.Path, "SANOVA");
        WriteRoot("SANOVA.asi", "core plugin v1");
        WriteRoot("data/handling.cfg", "sanova handling v1");
        WriteRoot("modloader/hd/roads.txd", "hd roads v1");
        WriteRoot("modloader/hd/readme.txt", "hd readme");
        WriteRoot("modloader/extra/extra.dff", "extra model");
    }

    public TempDir Temp { get; }
    public byte[] Executable { get; }
    public string Game { get; }
    public string Root { get; }
    public string Install { get; }

    public void WriteRoot(string path, string content) => TempDir.WriteFile(Root, path, System.Text.Encoding.UTF8.GetBytes(content));

    public void WriteRoot(string path, byte[] content) => TempDir.WriteFile(Root, path, content);

    public void DeleteRoot(string path) => File.Delete(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));

    public async Task<ResolvedManifest> BuildAsync(string version, string? previousManifest = null, string? previousBaseUrl = null)
    {
        var output = Temp.Sub("release-" + version);
        var config = new
        {
            contentVersion = version,
            compatibility = new
            {
                additionalExecutables = new[]
                {
                    new { id = "test-1.0", label = "Test executable", edition = "retail", version = "1.0", region = "US", sha256 = new[] { Bytes.Sha(Executable) }, support = "supported" },
                },
            },
            components = new object[]
            {
                new { id = "core", name = new { en = "Core", tr = "Çekirdek" }, required = true, include = new[] { "**" }, redistribution = "permission" },
                new { id = "hd", name = new { en = "HD roads" }, include = new[] { "modloader/hd/**" }, priority = 10, redistribution = "allowed", authors = new[] { "Someone" } },
                new { id = "extra", name = new { en = "Extra" }, include = new[] { "modloader/extra/**" }, priority = 10, defaultEnabled = false, dependencies = new[] { "hd" }, redistribution = "allowed" },
            },
        };
        var configPath = Path.Combine(Temp.Path, $"content-{version}.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));
        await ManifestBuilder.BuildAsync(new BuildOptions
        {
            SourceDirectory = Root,
            ConfigPath = configPath,
            OutputDirectory = output,
            PreviousManifestPath = previousManifest,
            PreviousBaseUrl = previousBaseUrl,
            Log = TextWriter.Null,
        });
        return await LoadAsync(output);
    }

    public static async Task<ResolvedManifest> LoadAsync(string releaseFolder)
    {
        var source = new LocalFolderContentSource(releaseFolder, new ContentSourceConfig());
        var release = await source.GetLatestAsync("stable", CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(release!.ManifestUri.LocalPath);
        return ManifestLoader.Load(bytes, null, new ManifestTrustConfig(), release);
    }

    public InstallationService Service()
    {
        var http = new FakeHttpServer().CreateClient();
        var downloader = new ResumableDownloader(http, new BandwidthThrottle(), Log.For<ResumableDownloader>());
        return new InstallationService(new ContentAcquirer(downloader, http, new BandwidthThrottle(), Log.For<ContentAcquirer>()),
            new BaseGameCopier(Log.For<BaseGameCopier>()), new TransactionEngine(Log.For<TransactionEngine>()), Log.For<InstallationService>(), "1.0.0");
    }

    public Task<CompatibilityReport> CheckAsync(ResolvedManifest manifest)
        => new CompatibilityChecker(GameVersionDatabase.LoadEmbedded(), [], Log.For<CompatibilityChecker>())
            .CheckAsync(Game, new CompatibilityCheckOptions(false, manifest.Manifest.Compatibility, manifest.Manifest.BaseGame));

    public async Task InstallAsync(ResolvedManifest manifest, IReadOnlySet<string>? components = null)
    {
        var report = await CheckAsync(manifest);
        Assert.True(report.CanProceed, string.Join(", ", report.Findings.Select(f => f.Code)));
        await Service().InstallAsync(new InstallRequest
        {
            OriginalGamePath = Game,
            InstallPath = Install,
            Manifest = manifest,
            Components = components ?? Sanova.Core.Content.ComponentResolver.InitialSelection(manifest.Manifest, null),
            Compatibility = report,
        }, null, CancellationToken.None);
    }

    public string Read(string relative) => File.ReadAllText(Path.Combine(Install, relative.Replace('/', Path.DirectorySeparatorChar)));

    public bool Exists(string relative) => File.Exists(Path.Combine(Install, relative.Replace('/', Path.DirectorySeparatorChar)));

    public Dictionary<string, string> HashTree(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Bytes.Sha(File.ReadAllBytes(f)), StringComparer.OrdinalIgnoreCase);

    public void Dispose() => Temp.Dispose();
}

public class InstallationTests
{
    [Fact]
    public async Task Installs_a_separate_copy_and_never_touches_the_original_game()
    {
        using var fx = new ContentFixture();
        var originalBefore = fx.HashTree(fx.Game);
        var manifest = await fx.BuildAsync("1.0.0");

        await fx.InstallAsync(manifest);

        Assert.Equal(originalBefore, fx.HashTree(fx.Game));
        var state = InstallStateStore.Load(fx.Install)!;
        Assert.Equal(InstallPhase.Ready, state.Phase);
        Assert.Equal("1.0.0", state.ContentVersion);
        Assert.Equal(["core", "hd"], state.EnabledComponents);
        Assert.Equal("core plugin v1", fx.Read("SANOVA.asi"));
        Assert.Equal("hd roads v1", fx.Read("modloader/hd/roads.txd"));
        Assert.False(fx.Exists("modloader/extra/extra.dff"), "optional components that are off by default are not installed");
        Assert.Equal("sanova handling v1", fx.Read("data/handling.cfg"));
        Assert.Equal("original handling", File.ReadAllText(Path.Combine(InstallStateStore.MetadataDirectory(fx.Install), "originals", "data", "handling.cfg")));
        Assert.True(state.Files["data/handling.cfg"].OverridesBase);
        Assert.Equal(FileOrigin.Base, state.Files["gta_sa.exe"].Origin);
        Assert.Equal(Bytes.Sha(fx.Executable), state.Files["gta_sa.exe"].Sha256);
        Assert.True(state.LastVerificationPassed);
        Assert.False(Directory.Exists(InstallStateStore.CacheDirectory(fx.Install)), "the download cache is cleared after a successful install");
    }

    [Fact]
    public async Task Verification_finds_damage_and_repair_restores_it()
    {
        using var fx = new ContentFixture();
        var manifest = await fx.BuildAsync("1.0.0");
        await fx.InstallAsync(manifest);
        var service = fx.Service();

        File.WriteAllText(Path.Combine(fx.Install, "SANOVA.asi"), "tampered");
        File.Delete(Path.Combine(fx.Install, "models", "gta3.img"));

        var report = await service.VerifyAsync(fx.Install, VerifyMode.Full, null, CancellationToken.None);
        Assert.False(report.Passed);
        Assert.Contains(report.Issues, i => i is { Path: "SANOVA.asi", Problem: VerificationProblem.Modified, Origin: FileOrigin.Content });
        Assert.Contains(report.Issues, i => i is { Path: "models/gta3.img", Problem: VerificationProblem.Missing, Origin: FileOrigin.Base });

        var after = await service.RepairAsync(fx.Install, manifest, new ApplyOptions(), null, CancellationToken.None);
        Assert.True(after.Passed);
        Assert.Equal("core plugin v1", fx.Read("SANOVA.asi"));
        Assert.True(fx.Exists("models/gta3.img"));
        // The player's modified copy was kept, not silently destroyed.
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(InstallStateStore.MetadataDirectory(fx.Install), "user-backup"), "SANOVA.asi", SearchOption.AllDirectories),
            f => File.ReadAllText(f) == "tampered");
    }

    [Fact]
    public async Task Updates_apply_only_the_differences_and_protect_player_files()
    {
        using var fx = new ContentFixture();
        await fx.InstallAsync(await fx.BuildAsync("1.0.0"));

        fx.WriteRoot("modloader/hd/roads.txd", "hd roads v2");
        fx.DeleteRoot("modloader/hd/readme.txt");
        fx.WriteRoot("cleo/new.cs", "new script");
        var v2 = await fx.BuildAsync("1.1.0");
        // The player put their own file where v2 wants to install one.
        TempDir.WriteFile(fx.Install, "cleo/new.cs", "player's own script"u8.ToArray());

        var service = fx.Service();
        var preview = service.Preview(fx.Install, v2, null);
        Assert.Equal((1, 1, 1), (preview.Added, preview.Updated, preview.Removed));

        await service.UpdateAsync(fx.Install, v2, null, new ApplyOptions(), null, CancellationToken.None);

        Assert.Equal("hd roads v2", fx.Read("modloader/hd/roads.txd"));
        Assert.False(fx.Exists("modloader/hd/readme.txt"));
        Assert.Equal("new script", fx.Read("cleo/new.cs"));
        var state = InstallStateStore.Load(fx.Install)!;
        Assert.Equal("1.1.0", state.ContentVersion);
        var displaced = Assert.Single(state.DisplacedUserFiles);
        Assert.Equal("cleo/new.cs", displaced.Path);
        Assert.Equal("player's own script", File.ReadAllText(InstallStateStore.MetadataPath(fx.Install, displaced.BackupPath)));
    }

    [Fact]
    public async Task Disabling_parks_files_and_enabling_restores_them_without_downloading()
    {
        using var fx = new ContentFixture();
        var manifest = await fx.BuildAsync("1.0.0");
        await fx.InstallAsync(manifest);
        var service = fx.Service();

        await service.ApplyComponentChangesAsync(fx.Install, new HashSet<string> { "core" }, new ApplyOptions(), null, CancellationToken.None);
        Assert.False(fx.Exists("modloader/hd/roads.txd"));
        Assert.True(File.Exists(Path.Combine(InstallStateStore.MetadataDirectory(fx.Install), "disabled", "modloader", "hd", "roads.txd")));

        // Remove the release packages: re-enabling must work from the parked copies alone.
        foreach (var package in Directory.EnumerateFiles(Path.Combine(fx.Temp.Path, "release-1.0.0"), "*.zip")) File.Delete(package);
        var summary = await service.ApplyComponentChangesAsync(fx.Install, new HashSet<string> { "core", "hd" }, new ApplyOptions(), null, CancellationToken.None);

        Assert.Equal(2, summary.Enabled);
        Assert.Equal(0, summary.Added);
        Assert.Equal("hd roads v1", fx.Read("modloader/hd/roads.txd"));
        Assert.Equal(["core", "hd"], InstallStateStore.Load(fx.Install)!.EnabledComponents);
    }

    [Fact]
    public async Task Removing_content_that_replaced_an_original_file_restores_the_original()
    {
        using var fx = new ContentFixture();
        await fx.InstallAsync(await fx.BuildAsync("1.0.0"));
        fx.DeleteRoot("data/handling.cfg");
        await fx.Service().UpdateAsync(fx.Install, await fx.BuildAsync("1.1.0"), null, new ApplyOptions(), null, CancellationToken.None);

        Assert.Equal("original handling", fx.Read("data/handling.cfg"));
        var state = InstallStateStore.Load(fx.Install)!;
        Assert.Equal(FileOrigin.Base, state.Files["data/handling.cfg"].Origin);
        Assert.False(state.Originals.ContainsKey("data/handling.cfg"));
    }

    [Fact]
    public async Task A_corrupted_package_aborts_without_applying_anything()
    {
        using var fx = new ContentFixture();
        var manifest = await fx.BuildAsync("1.0.0");
        foreach (var package in Directory.EnumerateFiles(Path.Combine(fx.Temp.Path, "release-1.0.0"), "*.zip"))
        {
            var bytes = await File.ReadAllBytesAsync(package);
            bytes[40] ^= 0xFF; // damage the first entry's data
            await File.WriteAllBytesAsync(package, bytes);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => fx.InstallAsync(manifest));

        var state = InstallStateStore.Load(fx.Install)!;
        Assert.Equal(InstallPhase.BaseReady, state.Phase);
        Assert.Null(state.ContentVersion);
        Assert.False(fx.Exists("SANOVA.asi"));
        Assert.False(InstallStateStore.HasPendingTransaction(fx.Install));
        Assert.Equal("original handling", fx.Read("data/handling.cfg"));
    }

    [Fact]
    public async Task Uninstall_removes_managed_files_and_keeps_everything_else()
    {
        using var fx = new ContentFixture();
        var originalBefore = fx.HashTree(fx.Game);
        await fx.InstallAsync(await fx.BuildAsync("1.0.0"));
        TempDir.WriteFile(fx.Install, "screenshots/my shot.png", [1, 2, 3]);
        File.WriteAllText(Path.Combine(fx.Install, "modloader", "hd", "readme.txt"), "edited by the player");

        var result = await fx.Service().UninstallAsync(fx.Install, null, CancellationToken.None);

        Assert.Contains("screenshots/my shot.png", result.KeptUnknown);
        Assert.Contains("modloader/hd/readme.txt", result.KeptModified);
        Assert.False(result.DirectoryRemoved);
        Assert.False(fx.Exists("SANOVA.asi"));
        Assert.False(fx.Exists("gta_sa.exe"));
        Assert.True(fx.Exists("screenshots/my shot.png"));
        Assert.False(Directory.Exists(InstallStateStore.MetadataDirectory(fx.Install)));
        Assert.Equal(originalBefore, fx.HashTree(fx.Game));
    }

    [Fact]
    public async Task Install_path_validation_explains_problems()
    {
        using var fx = new ContentFixture();
        var service = fx.Service();
        Assert.True(service.ValidateInstallPath(fx.Install, fx.Game, 1).IsValid);
        Assert.Equal(ErrorCodes.PathInsideGame, service.ValidateInstallPath(Path.Combine(fx.Game, "SANOVA"), fx.Game, 1).ErrorCode);
        TempDir.WriteFile(Path.Combine(fx.Temp.Path, "busy"), "x.txt", [1]);
        Assert.Equal(ErrorCodes.PathNotEmpty, service.ValidateInstallPath(Path.Combine(fx.Temp.Path, "busy"), fx.Game, 1).ErrorCode);
        Assert.Equal(ErrorCodes.DiskSpace, service.ValidateInstallPath(fx.Install, fx.Game, long.MaxValue).ErrorCode);
        Assert.Equal(ErrorCodes.PathUnsafe, service.ValidateInstallPath("relative\\path", fx.Game, 1).ErrorCode);
        Assert.Equal("path_not_ansi", service.ValidateInstallPath(@"C:\ゲーム\SANOVA", fx.Game, 1).ErrorCode);
    }

    [Fact]
    public async Task Partial_updates_fetch_only_needed_bytes_over_http()
    {
        using var fx = new ContentFixture();
        // A large package where only one small file changes: the update must use range requests.
        fx.WriteRoot("modloader/hd/huge.img", Bytes.Random(18_000_000, 77));
        var v1 = await fx.BuildAsync("1.0.0");
        await fx.InstallAsync(v1);
        fx.WriteRoot("modloader/hd/roads.txd", "hd roads v2");
        var releaseFolder = Path.Combine(fx.Temp.Path, "release-1.1.0");
        await fx.BuildAsync("1.1.0");

        var server = new FakeHttpServer();
        foreach (var file in Directory.EnumerateFiles(releaseFolder)) server.Files["/r/" + Path.GetFileName(file)] = await File.ReadAllBytesAsync(file);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(releaseFolder, "sanova-manifest.json"));
        var parsed = ManifestLoader.Parse(manifestBytes);
        var locations = parsed.Packages.ToDictionary(p => p.Id, p => (IReadOnlyList<Uri>)[new Uri("https://cdn.test/r/" + p.FileName)], StringComparer.OrdinalIgnoreCase);
        var remote = new ResolvedManifest(parsed, manifestBytes, false, locations, "test", null);

        var http = server.CreateClient();
        var downloader = new ResumableDownloader(http, new BandwidthThrottle(), Log.For<ResumableDownloader>());
        var service = new InstallationService(new ContentAcquirer(downloader, http, new BandwidthThrottle(), Log.For<ContentAcquirer>()),
            new BaseGameCopier(Log.For<BaseGameCopier>()), new TransactionEngine(Log.For<TransactionEngine>()), Log.For<InstallationService>(), "1.0.0");
        await service.UpdateAsync(fx.Install, remote, null, new ApplyOptions(), null, CancellationToken.None);

        var hdPackage = parsed.Packages.Single(p => p.Id == "hd");
        Assert.True(hdPackage.Size > 16 * 1024 * 1024);
        Assert.Equal("hd roads v2", fx.Read("modloader/hd/roads.txd"));
        Assert.True(server.BytesServed < hdPackage.Size / 4, $"served {server.BytesServed} of {hdPackage.Size}");
    }
}

public class ManifestBuilderTests
{
    [Fact]
    public async Task Unchanged_components_reuse_packages_from_the_previous_release()
    {
        using var fx = new ContentFixture();
        await fx.BuildAsync("1.0.0");
        var previous = Path.Combine(fx.Temp.Path, "release-1.0.0", "sanova-manifest.json");
        fx.WriteRoot("SANOVA.asi", "core plugin v2");

        var v2 = await fx.BuildAsync("1.1.0", previous, "https://github.com/owner/repo/releases/download/v1.0.0/");

        var hd = v2.Manifest.FindPackage("hd")!;
        Assert.Equal("https://github.com/owner/repo/releases/download/v1.0.0/sanova-1.0.0-hd.zip", hd.Url);
        Assert.False(File.Exists(Path.Combine(fx.Temp.Path, "release-1.1.0", "sanova-1.1.0-hd.zip")));
        Assert.Null(v2.Manifest.FindPackage("core")!.Url);
        Assert.True(File.Exists(Path.Combine(fx.Temp.Path, "release-1.1.0", "sanova-1.1.0-core.zip")));
    }

    [Fact]
    public async Task Refuses_unassigned_files_and_unknown_redistribution()
    {
        using var temp = new TempDir();
        var root = temp.Sub("root");
        TempDir.WriteFile(root, "a.txt", [1]);
        TempDir.WriteFile(root, "b/c.txt", [2]);
        var config = temp.File("c.json", """{ "contentVersion": "1.0.0", "components": [ { "id": "b", "name": "B", "include": ["b/**"], "redistribution": "unknown" } ] }""");

        var unassigned = await Assert.ThrowsAsync<InvalidOperationException>(() => ManifestBuilder.BuildAsync(new BuildOptions
            { SourceDirectory = root, ConfigPath = config, OutputDirectory = temp.Sub("out"), Log = TextWriter.Null }));
        Assert.Contains("match no component", unassigned.Message, StringComparison.Ordinal);

        File.Delete(Path.Combine(root, "a.txt"));
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => ManifestBuilder.BuildAsync(new BuildOptions
            { SourceDirectory = root, ConfigPath = config, OutputDirectory = temp.Sub("out2"), Log = TextWriter.Null }));
        Assert.Contains("unknown redistribution", unknown.Message, StringComparison.Ordinal);

        var allowed = await ManifestBuilder.BuildAsync(new BuildOptions
            { SourceDirectory = root, ConfigPath = config, OutputDirectory = temp.Sub("out3"), AllowUnknownRedistribution = true, Log = TextWriter.Null });
        Assert.Contains(allowed.Warnings, w => w.Contains("unknown redistribution", StringComparison.Ordinal));
    }

    [Fact]
    public async Task External_components_reference_the_authors_archive_instead_of_a_package()
    {
        using var temp = new TempDir();
        var root = temp.Sub("root");
        TempDir.WriteFile(root, "core.txt", [1]);
        TempDir.WriteFile(root, "modloader/Author Mod/mod.txd", [2]);
        var config = temp.File("c.json", $$"""
            { "contentVersion": "1.0.0", "components": [
              { "id": "core", "name": "Core", "required": true, "include": ["**"], "redistribution": "allowed" },
              { "id": "author-mod", "name": "Author mod", "include": ["modloader/Author Mod/**"], "priority": 5, "redistribution": "external",
                "source": { "type": "external", "url": "https://example.com/mod.zip", "sha256": "{{new string('b', 64)}}", "installPrefix": "modloader/", "archivePrefix": "" } } ] }
            """);

        var result = await ManifestBuilder.BuildAsync(new BuildOptions { SourceDirectory = root, ConfigPath = config, OutputDirectory = temp.Sub("out"), Log = TextWriter.Null });

        var file = result.Manifest.Files.Single(f => f.Component == "author-mod");
        Assert.Null(file.Package);
        Assert.Equal("Author Mod/mod.txd", file.EntryName);
        Assert.DoesNotContain(result.Manifest.Packages, p => p.Id.Contains("author", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refuses_to_package_unmodified_original_game_files()
    {
        using var temp = new TempDir();
        var root = temp.Sub("root");
        byte[] original = [7, 7, 7, 7];
        TempDir.WriteFile(root, "SANOVA.asi", [1, 2, 3]);
        TempDir.WriteFile(root, "data/original.dat", original);
        TempDir.WriteFile(root, "empty.txt", []);
        temp.File("reference.json", $$"""{ "files": [ { "path": "data/original.dat", "size": 4, "sha256": "{{Bytes.Sha(original)}}" } ] }""");
        var packaged = temp.File("packaged.json", """
            { "contentVersion": "1.0.0", "baseGameReference": "reference.json",
              "components": [ { "id": "core", "name": "Core", "required": true, "include": ["**"], "redistribution": "allowed" } ] }
            """);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ManifestBuilder.BuildAsync(new BuildOptions
            { SourceDirectory = root, ConfigPath = packaged, OutputDirectory = temp.Sub("out"), Log = TextWriter.Null }));
        Assert.Contains("identical to original game files", refused.Message, StringComparison.Ordinal);
        Assert.Contains("data/original.dat", refused.Message, StringComparison.Ordinal);

        // The same file in a component SANOVA does not redistribute (the author's own archive) is fine.
        var external = temp.File("external.json", $$"""
            { "contentVersion": "1.0.0", "baseGameReference": "reference.json", "components": [
              { "id": "core", "name": "Core", "required": true, "include": ["**"], "redistribution": "allowed" },
              { "id": "author-mod", "name": "Author mod", "include": ["data/**"], "priority": 5, "redistribution": "external",
                "source": { "type": "external", "url": "https://example.com/mod.zip", "sha256": "{{new string('c', 64)}}" } } ] }
            """);
        var result = await ManifestBuilder.BuildAsync(new BuildOptions { SourceDirectory = root, ConfigPath = external, OutputDirectory = temp.Sub("out2"), Log = TextWriter.Null });
        Assert.Equal("author-mod", result.Manifest.Files.Single(f => f.Path == "data/original.dat").Component);
        Assert.Equal("core", result.Manifest.Files.Single(f => f.Path == "empty.txt").Component);
    }

    [Fact]
    public async Task Rebuilding_unchanged_content_produces_identical_packages()
    {
        using var temp = new TempDir();
        var root = temp.Sub("root");
        TempDir.WriteFile(root, "SANOVA.asi", Bytes.Random(50_000, 1));
        TempDir.WriteFile(root, "models/a.txd", Bytes.Random(120_000, 2));
        TempDir.WriteFile(root, "textures/b.png", Bytes.Random(30_000, 3));
        var config = temp.File("c.json", """{ "contentVersion": "1.0.0", "components": [ { "id": "core", "name": "Core", "required": true, "include": ["**"], "redistribution": "allowed" } ] }""");

        var first = await ManifestBuilder.BuildAsync(new BuildOptions { SourceDirectory = root, ConfigPath = config, OutputDirectory = temp.Sub("out1"), Log = TextWriter.Null });
        await Task.Delay(1100, TestContext.Current.CancellationToken); // different wall-clock second
        File.SetLastWriteTimeUtc(Path.Combine(root, "SANOVA.asi"), DateTime.UtcNow);
        var second = await ManifestBuilder.BuildAsync(new BuildOptions { SourceDirectory = root, ConfigPath = config, OutputDirectory = temp.Sub("out2"), Log = TextWriter.Null });

        Assert.Equal(first.Manifest.Packages.Single().Sha256, second.Manifest.Packages.Single().Sha256);
        Assert.Equal(first.Manifest.Packages.Single().Size, second.Manifest.Packages.Single().Size);
    }

    [Fact]
    public void Documented_example_content_config_is_well_formed()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SANOVA.Launcher.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var config = ContentConfig.Load(Path.Combine(directory!.FullName, "docs", "examples", "sanova-content.example.json"));

        Assert.True(SemanticVersion.TryParse(config.ContentVersion, out _));
        var ids = config.Components.Select(c => c.Id).ToHashSet();
        var categories = config.Categories.Select(c => c.Id).ToHashSet();
        Assert.Equal(config.Components.Count, ids.Count);
        foreach (var component in config.Components)
        {
            Assert.NotEqual(Sanova.Core.Content.Redistribution.Unknown, component.Redistribution);
            Assert.NotEmpty(component.Include);
            Assert.Contains(component.Category, categories);
            Assert.All(component.Dependencies.Concat(component.Conflicts), id => Assert.Contains(id, ids));
            Assert.Equal(component.Redistribution == Sanova.Core.Content.Redistribution.External, component.Source is not null);
        }
    }

    [Fact]
    public void Glob_patterns_match_as_documented()
    {
        Assert.Matches(Glob.ToRegex("**"), "a/b/c.txt");
        Assert.Matches(Glob.ToRegex("modloader/**"), "modloader/x/y.dff");
        Assert.DoesNotMatch(Glob.ToRegex("modloader/*"), "modloader/x/y.dff");
        Assert.Matches(Glob.ToRegex("**/*.asi"), "plugin.asi");
        Assert.Matches(Glob.ToRegex("**/*.asi"), "scripts/plugin.ASI");
        Assert.Matches(Glob.ToRegex("data/?.dat"), "data/a.dat");
    }
}
