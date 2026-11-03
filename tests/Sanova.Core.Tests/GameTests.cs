using Sanova.Core.Game;
using Sanova.Core.Installation;

namespace Sanova.Core.Tests;

internal sealed class FakeRegistry : IRegistryReader
{
    public Dictionary<(RegHive, RegView, string, string), string> Values { get; } = new();
    public Dictionary<(RegHive, RegView, string), List<string>> SubKeys { get; } = new();

    public string? GetString(RegHive hive, RegView view, string subKey, string valueName)
        => Values.GetValueOrDefault((hive, view, subKey, valueName));

    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, RegView view, string subKey)
        => SubKeys.GetValueOrDefault((hive, view, subKey)) ?? [];
}

public static class FakeGame
{
    /// <summary>Creates a small folder that looks like a GTA San Andreas installation.</summary>
    public static string Create(string root, byte[]? executable = null, string exeName = "gta_sa.exe")
    {
        Directory.CreateDirectory(root);
        TempDir.WriteFile(root, exeName, executable ?? Bytes.Random(4096, 7));
        foreach (var file in BaseGameReference.MinimalRequiredFiles)
        {
            TempDir.WriteFile(root, file, Bytes.Random(512 + file.Length, file.Length));
        }
        TempDir.WriteFile(root, "data/handling.cfg", System.Text.Encoding.UTF8.GetBytes("original handling"));
        TempDir.WriteFile(root, "vorbisFile.dll", new byte[65536]);
        return root;
    }

    /// <summary>A minimal 32-bit PE image with the given section names.</summary>
    public static byte[] Pe(params string[] sections)
    {
        var data = new byte[0x400];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(data, 0x3C);
        "PE\0\0"u8.CopyTo(data.AsSpan(0x80));
        BitConverter.GetBytes((ushort)0x14C).CopyTo(data, 0x84);
        BitConverter.GetBytes((ushort)sections.Length).CopyTo(data, 0x86);
        BitConverter.GetBytes(0x42A00000u).CopyTo(data, 0x88);
        BitConverter.GetBytes((ushort)0xE0).CopyTo(data, 0x94);
        BitConverter.GetBytes((ushort)0x10B).CopyTo(data, 0x98);
        BitConverter.GetBytes(0x1000u).CopyTo(data, 0x98 + 16);
        BitConverter.GetBytes(0x400000u).CopyTo(data, 0x98 + 28);
        var sectionTable = 0x98 + 0xE0;
        for (var i = 0; i < sections.Length; i++)
        {
            var offset = sectionTable + i * 40;
            System.Text.Encoding.ASCII.GetBytes(sections[i]).CopyTo(data, offset);
            BitConverter.GetBytes(0x1000u * (uint)(i + 1)).CopyTo(data, offset + 12);
            BitConverter.GetBytes(0x100u).CopyTo(data, offset + 8);
        }
        return data;
    }
}

public class KeyValuesTests
{
    [Fact]
    public void Parses_current_libraryfolders_layout()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "label" "" "apps" { "12120" "123" } }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameLocator.ParseLibraryFolders(vdf));
    }

    [Fact]
    public void Parses_legacy_libraryfolders_layout()
    {
        const string vdf = """
            "LibraryFolders"
            {
                "TimeNextStatsReport" "1234"
                "1" "E:\\Games\\Steam"
            }
            """;
        Assert.Equal([@"E:\Games\Steam"], GameLocator.ParseLibraryFolders(vdf));
    }
}

public class GameLocatorTests
{
    [Fact]
    public void Finds_installations_from_registry_steam_and_samp_and_deduplicates()
    {
        using var temp = new TempDir();
        var retail = FakeGame.Create(temp.Sub("Rockstar", "GTA San Andreas"));
        var steamRoot = temp.Sub("Steam");
        var steamGame = FakeGame.Create(Path.Combine(steamRoot, "steamapps", "common", "Grand Theft Auto San Andreas"), exeName: "gta-sa.exe");
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { }");
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", "appmanifest_12120.acf"), "\"AppState\" { \"appid\" \"12120\" \"installdir\" \"Grand Theft Auto San Andreas\" }");
        var sanova = FakeGame.Create(temp.Sub("SANOVA"));
        InstallStateStore.Save(sanova, new InstallState());

        var registry = new FakeRegistry();
        registry.Values[(RegHive.LocalMachine, RegView.Registry32, @"SOFTWARE\Rockstar Games\GTA San Andreas\Installation", "ExePath")] = $"\"{Path.Combine(retail, "gta_sa.exe")}\"";
        registry.Values[(RegHive.CurrentUser, RegView.Default, @"Software\SAMP", "gta_sa_exe")] = Path.Combine(retail, "gta_sa.exe");
        registry.Values[(RegHive.CurrentUser, RegView.Default, @"Software\Valve\Steam", "SteamPath")] = steamRoot.Replace('\\', '/');
        registry.Values[(RegHive.LocalMachine, RegView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{X}", "DisplayName")] = "GTA San Andreas";
        registry.Values[(RegHive.LocalMachine, RegView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{X}", "InstallLocation")] = Path.Combine(temp.Path, "does-not-exist");
        registry.SubKeys[(RegHive.LocalMachine, RegView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")] = ["{X}"];

        var locator = new GameLocator(registry, Log.For<GameLocator>(), () => []);
        var found = locator.Locate([(sanova, GameDetectionSource.Saved)]);

        Assert.Equal(3, found.Count);
        var retailHit = Assert.Single(found, c => c.Directory.Equals(retail, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GameDetectionSource.Registry, retailHit.Source);
        var steamHit = Assert.Single(found, c => c.Source == GameDetectionSource.Steam);
        Assert.Equal("gta-sa.exe", steamHit.ExecutableName);
        Assert.Equal(sanova, Assert.Single(found, c => c.IsSanovaInstallation).Directory, ignoreCase: true);
        Assert.True(found[^1].IsSanovaInstallation);
    }
}

public class ExecutableIdentifierTests
{
    [Fact]
    public async Task Matches_database_entries_by_hash()
    {
        using var temp = new TempDir();
        var exe = Bytes.Random(2048, 3);
        var game = FakeGame.Create(temp.Sub("game"), exe);
        var database = new GameVersionDatabase([new KnownExecutable { Id = "test", Sha256 = [Bytes.Sha(exe)], Support = ExecutableSupport.Supported, Version = "1.0", Region = "US" }]);
        var identity = await ExecutableIdentifier.IdentifyAsync(game, database, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutableKind.Known, identity.Kind);
        Assert.Equal("test", identity.Match!.Id);
        Assert.Equal(ExecutableSupport.Supported, identity.EffectiveSupport);
    }

    [Fact]
    public async Task Classifies_unrecognized_executables()
    {
        using var temp = new TempDir();
        var empty = new GameVersionDatabase([]);

        var rerelease = FakeGame.Create(temp.Sub("steam"), exeName: "gta-sa.exe");
        Assert.Equal(ExecutableKind.ModernRerelease, (await ExecutableIdentifier.IdentifyAsync(rerelease, empty, TestContext.Current.CancellationToken)).Kind);

        var retail = FakeGame.Create(temp.Sub("retail"), FakeGame.Pe(".text", ".rdata", ".securom"));
        var retailIdentity = await ExecutableIdentifier.IdentifyAsync(retail, empty, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutableKind.RetailUnrecognized, retailIdentity.Kind);
        Assert.True(retailIdentity.Pe!.Is32BitX86);

        var unknown = FakeGame.Create(temp.Sub("unknown"), FakeGame.Pe(".text"));
        Assert.Equal(ExecutableKind.Unknown, (await ExecutableIdentifier.IdentifyAsync(unknown, empty, TestContext.Current.CancellationToken)).Kind);

        var definitive = temp.Sub("de");
        TempDir.WriteFile(definitive, "Gameface/Binaries/Win64/SanAndreas.exe", [1, 2, 3]);
        Assert.Equal(ExecutableKind.DefinitiveEdition, (await ExecutableIdentifier.IdentifyAsync(definitive, empty, TestContext.Current.CancellationToken)).Kind);

        Assert.Equal(ExecutableKind.Missing, (await ExecutableIdentifier.IdentifyAsync(temp.Sub("empty"), empty, TestContext.Current.CancellationToken)).Kind);
    }

    [Fact]
    public void Embedded_database_contains_the_verified_retail_executable()
    {
        var database = GameVersionDatabase.LoadEmbedded();
        var retail = database.Find("sa-1.0-us-retail");
        Assert.NotNull(retail);
        Assert.Equal(ExecutableSupport.Supported, retail!.Support);
        Assert.Contains("securom", retail.Notes);
        var supported = database.WithRules(new CompatibilityRules { UnsupportedExecutables = ["sa-1.0-us-retail"] }).Find("sa-1.0-us-retail");
        Assert.Equal(ExecutableSupport.Unsupported, supported!.Support);
    }

    /// <summary>Read-only check against a real v1.0 installation (SANOVA_REAL_GAME_PATH), when configured.</summary>
    [Fact]
    public async Task Identifies_the_real_retail_installation_when_available()
    {
        if (RealGame.Location is not { } realGame)
        {
            Assert.Skip(RealGame.SkipReason);
            return;
        }
        var identity = await ExecutableIdentifier.IdentifyAsync(realGame, GameVersionDatabase.LoadEmbedded(), TestContext.Current.CancellationToken);
        Assert.Equal("sa-1.0-us-retail", identity.Match?.Id);
        Assert.True(identity.Pe!.HasSection(".securom"));
    }
}

public class CompatibilityCheckerTests
{
    private static CompatibilityChecker Checker(GameVersionDatabase? database = null, IReadOnlyList<BaseGameReference>? references = null)
        => new(database ?? new GameVersionDatabase([]), references ?? [], Log.For<CompatibilityChecker>());

    [Fact]
    public async Task Unknown_executables_are_blocked_unless_overridden()
    {
        using var temp = new TempDir();
        var game = FakeGame.Create(temp.Sub("game"));
        var report = await Checker().CheckAsync(game, new CompatibilityCheckOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(CompatibilityVerdict.Incompatible, report.Verdict);
        Assert.False(report.CanProceed);
        Assert.Contains(report.Findings, f => f.Code == "exe_unknown" && f.Overridable);

        var overridden = await Checker().CheckAsync(game, new CompatibilityCheckOptions(AllowUnsupported: true), TestContext.Current.CancellationToken);
        Assert.True(overridden.CanProceed);
        Assert.True(overridden.UsedOverride);
    }

    [Fact]
    public async Task Manifest_rules_can_declare_executables_supported()
    {
        using var temp = new TempDir();
        var exe = Bytes.Random(1024, 11);
        var game = FakeGame.Create(temp.Sub("game"), exe);
        var rules = new CompatibilityRules
        {
            AdditionalExecutables = [new KnownExecutable { Id = "custom", Sha256 = [Bytes.Sha(exe)], Version = "1.0", Region = "EU", Support = ExecutableSupport.Supported }],
        };
        var report = await Checker().CheckAsync(game, new CompatibilityCheckOptions(Rules: rules), TestContext.Current.CancellationToken);
        Assert.Equal(CompatibilityVerdict.Compatible, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "exe_supported");
    }

    [Fact]
    public async Task Reports_missing_and_modified_files_and_existing_mods()
    {
        using var temp = new TempDir();
        var exe = Bytes.Random(1024, 12);
        var game = FakeGame.Create(temp.Sub("game"), exe);
        TempDir.WriteFile(game, "dinput8.dll", [1]);
        Directory.CreateDirectory(Path.Combine(game, "modloader"));
        var database = new GameVersionDatabase([new KnownExecutable { Id = "v", Sha256 = [Bytes.Sha(exe)], Support = ExecutableSupport.Supported, Version = "1.0" }]);
        var reference = new BaseGameReference
        {
            ExecutableIds = ["v"],
            Files =
            [
                new BaseGameFile { Path = "gta_sa.exe", Size = exe.Length },
                new BaseGameFile { Path = "data/handling.cfg", Size = 999 },
                new BaseGameFile { Path = "audio/SFX/GENRL", Size = 10 },
            ],
        };

        var report = await Checker(database, [reference]).CheckAsync(game, new CompatibilityCheckOptions(), TestContext.Current.CancellationToken);
        Assert.Same(reference, report.Reference);
        Assert.Contains(report.Findings, f => f.Code == "files_missing" && f.Items.Contains("audio/SFX/GENRL"));
        Assert.Contains(report.Findings, f => f.Code == "files_modified" && f.Items.Contains("data/handling.cfg"));
        Assert.Contains(report.Findings, f => f.Code == "mods_detected_filtered" && f.Items.Contains("dinput8.dll") && f.Items.Contains("modloader/"));
        Assert.False(report.CanProceed);
    }

    [Fact]
    public async Task Refuses_sanova_installations_and_missing_folders()
    {
        using var temp = new TempDir();
        var sanova = FakeGame.Create(temp.Sub("sanova"));
        InstallStateStore.Save(sanova, new InstallState());
        Assert.Contains((await Checker().CheckAsync(sanova, new(), TestContext.Current.CancellationToken)).Findings, f => f.Code == "folder_is_sanova");
        Assert.Contains((await Checker().CheckAsync(Path.Combine(temp.Path, "nope"), new(), TestContext.Current.CancellationToken)).Findings, f => f.Code == "folder_missing");
    }

    /// <summary>Read-only check of the real v1.0 installation against the shipped reference list.</summary>
    [Fact]
    public async Task Real_retail_installation_is_compatible_when_available()
    {
        if (RealGame.Location is not { } realGame)
        {
            Assert.Skip(RealGame.SkipReason);
            return;
        }
        var checker = new CompatibilityChecker(GameVersionDatabase.LoadEmbedded(), BaseGameReference.LoadEmbedded(), Log.For<CompatibilityChecker>());
        var report = await checker.CheckAsync(realGame, new CompatibilityCheckOptions(), TestContext.Current.CancellationToken);
        Assert.True(report.CanProceed, string.Join(", ", report.Findings.Select(f => f.Code)));
        Assert.NotNull(report.Reference);
        Assert.Equal(416, report.Reference!.Files.Count);
        Assert.DoesNotContain(report.Findings, f => f.Code == "files_missing");
    }
}
