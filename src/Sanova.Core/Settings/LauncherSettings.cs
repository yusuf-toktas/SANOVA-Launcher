using System.Text.Json;

namespace Sanova.Core.Settings;

public enum AppTheme
{
    Dark,
    Light,
    System,
}

public enum AfterLaunchBehavior
{
    KeepOpen,
    Minimize,
    Close,
}

public enum ContentSourceKind
{
    /// <summary>Use the source configured in the launcher's distribution.json.</summary>
    Default,
    GitHub,
    ManifestUrl,
    LocalFolder,
}

/// <summary>Advanced override of where SANOVA content comes from (developer mode).</summary>
public sealed class ContentSourceOverride
{
    public ContentSourceKind Kind { get; set; } = ContentSourceKind.Default;

    /// <summary>owner/repo for GitHub, an https URL for ManifestUrl, a folder for LocalFolder.</summary>
    public string? Value { get; set; }
}

/// <summary>Persisted user preferences. Paths are machine-specific, hence stored per user locally.</summary>
public sealed class LauncherSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // General
    /// <summary>"auto" follows the Windows display language; otherwise a language code such as "tr".</summary>
    public string Language { get; set; } = "auto";

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public bool SetupCompleted { get; set; }

    // Game
    public string? OriginalGamePath { get; set; }

    public string? InstallPath { get; set; }

    public AfterLaunchBehavior AfterLaunch { get; set; } = AfterLaunchBehavior.Minimize;

    public string LaunchArguments { get; set; } = string.Empty;

    public bool BackupSavesBeforeChanges { get; set; } = true;

    /// <summary>Optional components chosen before installation (component id → enabled).</summary>
    public Dictionary<string, bool> ComponentChoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Downloads
    public int MaxParallelDownloads { get; set; } = 3;

    /// <summary>Kilobytes per second; 0 means unlimited.</summary>
    public int DownloadSpeedLimitKBps { get; set; }

    public bool KeepDownloadCache { get; set; }

    // Updates
    public bool CheckLauncherUpdatesOnStartup { get; set; } = true;

    public bool CheckContentUpdatesOnStartup { get; set; } = true;

    public string UpdateChannel { get; set; } = "stable";

    // Diagnostics & advanced
    public bool VerboseLogging { get; set; }

    public bool AllowUnsupportedGameVersions { get; set; }

    public bool DeveloperMode { get; set; }

    public ContentSourceOverride ContentSource { get; set; } = new();

    public LauncherSettings Clone()
    {
        var copy = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.Compact), JsonDefaults.Compact)!;
        // JSON round-trips lose the dictionary comparer; component ids are case-insensitive.
        copy.ComponentChoices = new Dictionary<string, bool>(copy.ComponentChoices ?? new(), StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>Clamps values edited by hand or written by older versions into valid ranges.</summary>
    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Language = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim().ToLowerInvariant();
        MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 8);
        DownloadSpeedLimitKBps = Math.Max(0, DownloadSpeedLimitKBps);
        UpdateChannel = UpdateChannel is "beta" ? "beta" : "stable";
        LaunchArguments ??= string.Empty;
        ComponentChoices = (ComponentChoices ?? new())
            .GroupBy(p => p.Key.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        ContentSource ??= new ContentSourceOverride();
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.Dark;
        if (!Enum.IsDefined(AfterLaunch)) AfterLaunch = AfterLaunchBehavior.Minimize;
        OriginalGamePath = string.IsNullOrWhiteSpace(OriginalGamePath) ? null : OriginalGamePath.Trim();
        InstallPath = string.IsNullOrWhiteSpace(InstallPath) ? null : InstallPath.Trim();
    }
}
