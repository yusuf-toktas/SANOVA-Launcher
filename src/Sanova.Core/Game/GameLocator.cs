using Microsoft.Extensions.Logging;
using Sanova.Core.Installation;
using Sanova.Core.IO;

namespace Sanova.Core.Game;

public enum GameDetectionSource
{
    Registry,
    Uninstall,
    Steam,
    SampClient,
    RockstarLauncher,
    CommonLocation,
    Saved,
    Manual,
}

/// <summary>A folder that looks like a GTA San Andreas installation.</summary>
public sealed record GameCandidate(
    string Directory,
    GameDetectionSource Source,
    string? ExecutableName,
    bool IsSanovaInstallation,
    bool IsDefinitiveEdition);

/// <summary>
/// Finds GTA San Andreas installations from the retail installer's registry keys, uninstall
/// entries, Steam libraries, the SA-MP client setting and common folders. Detection only
/// reads; it never modifies anything it finds.
/// </summary>
public sealed class GameLocator
{
    public const string SteamAppId = "12120";

    private const string RockstarKey = @"SOFTWARE\Rockstar Games";
    private const string RetailInstallationKey = @"SOFTWARE\Rockstar Games\GTA San Andreas\Installation";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly IRegistryReader _registry;
    private readonly ILogger<GameLocator> _logger;
    private readonly Func<IEnumerable<string>> _fixedDriveRoots;

    public GameLocator(IRegistryReader registry, ILogger<GameLocator> logger, Func<IEnumerable<string>>? fixedDriveRoots = null)
    {
        _registry = registry;
        _logger = logger;
        _fixedDriveRoots = fixedDriveRoots ?? DefaultFixedDriveRoots;
    }

    public IReadOnlyList<GameCandidate> Locate(IEnumerable<(string Path, GameDetectionSource Source)>? additional = null)
    {
        var found = new Dictionary<string, GameCandidate>(SafePath.Comparer);

        foreach (var (path, source) in additional ?? [])
        {
            Consider(found, path, source);
        }

        foreach (var view in new[] { RegView.Registry32, RegView.Registry64 })
        {
            Consider(found, _registry.GetString(RegHive.LocalMachine, view, RetailInstallationKey, "ExePath"), GameDetectionSource.Registry);
        }
        Consider(found, _registry.GetString(RegHive.CurrentUser, RegView.Default, RetailInstallationKey, "ExePath"), GameDetectionSource.Registry);

        ScanRockstarKeys(found);
        ScanUninstallEntries(found);
        Consider(found, _registry.GetString(RegHive.CurrentUser, RegView.Default, @"Software\SAMP", "gta_sa_exe"), GameDetectionSource.SampClient);
        ScanSteam(found);
        ScanCommonLocations(found);

        _logger.LogInformation("Game detection found {Count} candidate(s)", found.Count);
        return found.Values
            .OrderBy(c => c.IsSanovaInstallation)
            .ThenBy(c => c.IsDefinitiveEdition)
            .ThenBy(c => (int)c.Source)
            .ToList();
    }

    /// <summary>Validates a single user-chosen folder (or executable path).</summary>
    public GameCandidate? Inspect(string path, GameDetectionSource source = GameDetectionSource.Manual)
    {
        var found = new Dictionary<string, GameCandidate>(SafePath.Comparer);
        Consider(found, path, source);
        return found.Values.FirstOrDefault();
    }

    private void ScanRockstarKeys(Dictionary<string, GameCandidate> found)
    {
        foreach (var view in new[] { RegView.Registry32, RegView.Registry64 })
        {
            foreach (var name in _registry.GetSubKeyNames(RegHive.LocalMachine, view, RockstarKey))
            {
                if (!name.Contains("San Andreas", StringComparison.OrdinalIgnoreCase)) continue;
                var key = $@"{RockstarKey}\{name}";
                foreach (var value in new[] { "InstallFolder", "InstallLocation", "ExePath" })
                {
                    Consider(found, _registry.GetString(RegHive.LocalMachine, view, key, value), GameDetectionSource.RockstarLauncher);
                }
            }
        }
    }

    private void ScanUninstallEntries(Dictionary<string, GameCandidate> found)
    {
        var locations = new[]
        {
            (RegHive.LocalMachine, RegView.Registry32),
            (RegHive.LocalMachine, RegView.Registry64),
            (RegHive.CurrentUser, RegView.Default),
        };
        foreach (var (hive, view) in locations)
        {
            foreach (var name in _registry.GetSubKeyNames(hive, view, UninstallKey))
            {
                var key = $@"{UninstallKey}\{name}";
                var displayName = _registry.GetString(hive, view, key, "DisplayName");
                if (displayName is null || !displayName.Contains("San Andreas", StringComparison.OrdinalIgnoreCase)) continue;
                Consider(found, _registry.GetString(hive, view, key, "InstallLocation"), GameDetectionSource.Uninstall);
            }
        }
    }

    private void ScanSteam(Dictionary<string, GameCandidate> found)
    {
        var steamRoots = new[]
        {
            _registry.GetString(RegHive.CurrentUser, RegView.Default, @"Software\Valve\Steam", "SteamPath"),
            _registry.GetString(RegHive.LocalMachine, RegView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };

        var libraries = new HashSet<string>(SafePath.Comparer);
        foreach (var root in steamRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            try
            {
                var steamRoot = Path.GetFullPath(root!);
                libraries.Add(steamRoot);
                var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (var library in ParseLibraryFolders(File.ReadAllText(vdf)))
                {
                    libraries.Add(library);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _logger.LogDebug(ex, "Could not read Steam library list");
            }
        }

        foreach (var library in libraries)
        {
            try
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf");
                if (!File.Exists(manifest)) continue;
                var installDir = KeyValuesParser.Parse(File.ReadAllText(manifest))["AppState"]?["installdir"]?.Value;
                if (string.IsNullOrWhiteSpace(installDir)) continue;
                Consider(found, Path.Combine(library, "steamapps", "common", installDir), GameDetectionSource.Steam);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogDebug(ex, "Could not read Steam app manifest in {Library}", library);
            }
        }
    }

    /// <summary>Extracts library paths from both the old and the current libraryfolders.vdf layouts.</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdfText)
    {
        var result = new List<string>();
        var root = KeyValuesParser.Parse(vdfText);
        var folders = root["libraryfolders"] ?? root["LibraryFolders"];
        if (folders is null) return result;
        foreach (var (key, node) in folders.Children)
        {
            if (!key.All(char.IsAsciiDigit)) continue;
            var path = node.Value ?? node["path"]?.Value;
            if (!string.IsNullOrWhiteSpace(path)) result.Add(path);
        }
        return result;
    }

    private void ScanCommonLocations(Dictionary<string, GameCandidate> found)
    {
        string[] relative =
        [
            @"Program Files (x86)\Rockstar Games\GTA San Andreas",
            @"Program Files\Rockstar Games\GTA San Andreas",
            @"Games\Rockstar Games\GTA San Andreas",
            @"Games\GTA San Andreas",
            @"Rockstar Games\GTA San Andreas",
            @"GTA San Andreas",
            @"Program Files (x86)\Steam\steamapps\common\Grand Theft Auto San Andreas",
        ];
        foreach (var drive in _fixedDriveRoots())
        {
            foreach (var path in relative)
            {
                Consider(found, Path.Combine(drive, path), GameDetectionSource.CommonLocation);
            }
        }
    }

    private void Consider(Dictionary<string, GameCandidate> found, string? rawPath, GameDetectionSource source)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        try
        {
            var path = rawPath.Trim().Trim('"').Trim();
            if (path.Length == 0) return;
            path = Path.GetFullPath(path);
            if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.GetDirectoryName(path)!;
            }
            if (!Directory.Exists(path)) return;

            var directory = SafePath.NormalizeRoot(path);
            if (found.ContainsKey(directory)) return;

            var isDefinitive = ExecutableIdentifier.LooksLikeDefinitiveEdition(directory);
            var executable = ExecutableIdentifier.FindExecutable(directory);
            if (executable is null && !isDefinitive) return;

            found[directory] = new GameCandidate(
                directory,
                source,
                executable is null ? null : Path.GetFileName(executable),
                InstallStateStore.Exists(directory),
                isDefinitive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Ignoring candidate path {Path}", rawPath);
        }
    }

    private static IEnumerable<string> DefaultFixedDriveRoots()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }
        foreach (var drive in drives)
        {
            bool ready;
            try
            {
                ready = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }
            if (ready) yield return drive.RootDirectory.FullName;
        }
    }
}
