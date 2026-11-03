namespace Sanova.Core.Settings;

/// <summary>
/// Locations of launcher-owned data (settings, logs, caches, backups). Everything lives
/// under one per-user root so the launcher never needs administrator rights.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        Root = Path.GetFullPath(rootDirectory);
    }

    /// <summary>%LocalAppData%\SANOVA</summary>
    public static AppPaths CreateDefault()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SANOVA"));

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "Launcher", "settings.json");

    public string LogsDirectory => Path.Combine(Root, "Logs");

    public string CacheDirectory => Path.Combine(Root, "Cache");

    public string ManifestCacheDirectory => Path.Combine(CacheDirectory, "Manifests");

    public string HttpCacheDirectory => Path.Combine(CacheDirectory, "Http");

    public string SaveBackupsDirectory => Path.Combine(Root, "Backups", "Saves");

    /// <summary>Drop-in translations for testing new languages without rebuilding.</summary>
    public string LanguagesDirectory => Path.Combine(Root, "Languages");

    public string HistoryFile => Path.Combine(Root, "Launcher", "history.json");

    public void EnsureCreated()
    {
        foreach (var directory in new[]
                 {
                     Path.GetDirectoryName(SettingsFile)!, LogsDirectory, CacheDirectory, ManifestCacheDirectory,
                     HttpCacheDirectory, SaveBackupsDirectory, LanguagesDirectory,
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
