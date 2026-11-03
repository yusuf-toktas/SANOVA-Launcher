using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sanova.Core;
using Sanova.Core.Downloads;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Core.Settings;
using Sanova.Core.Updates;
using Sanova.Launcher.Infrastructure;

namespace Sanova.Launcher.Services;

public enum ContentStatus
{
    Unknown,
    Checking,
    NotConfigured,
    Available,
    NoRelease,
    Offline,
    Failed,
}

/// <summary>
/// The launcher's single source of truth: settings, detected games, compatibility, the
/// installation state, the latest content release and whether the game is running.
/// View models observe it; services update it.
/// </summary>
public sealed partial class AppState : ObservableObject
{
    private readonly SettingsStore _settingsStore;
    private readonly LocalizationService _localization;
    private readonly ThemeService _theme;
    private readonly GameLocator _locator;
    private readonly CompatibilityChecker _compatibilityChecker;
    private readonly InstallationService _installation;
    private readonly ContentUpdateService _content;
    private readonly GameProcessMonitor _processes;
    private readonly BandwidthThrottle _throttle;
    private readonly ILogger<AppState> _logger;
    private int _contentRevision;

    [ObservableProperty]
    private LauncherSettings _settings;

    [ObservableProperty]
    private IReadOnlyList<GameCandidate> _candidates = [];

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private CompatibilityReport? _compatibility;

    [ObservableProperty]
    private bool _isCheckingCompatibility;

    [ObservableProperty]
    private InstallState? _install;

    [ObservableProperty]
    private ResolvedManifest? _installedManifest;

    [ObservableProperty]
    private bool _hasPendingTransaction;

    [ObservableProperty]
    private ContentStatus _contentStatus;

    [ObservableProperty]
    private ResolvedManifest? _latestManifest;

    [ObservableProperty]
    private SanovaException? _contentError;

    [ObservableProperty]
    private DateTimeOffset? _lastContentCheck;

    [ObservableProperty]
    private string? _contentSource;

    [ObservableProperty]
    private GameRunState _runState = GameRunState.NotRunning;

    public AppState(
        SettingsStore settingsStore,
        LocalizationService localization,
        ThemeService theme,
        DistributionConfig distribution,
        GameLocator locator,
        CompatibilityChecker compatibilityChecker,
        InstallationService installation,
        ContentUpdateService content,
        GameProcessMonitor processes,
        BandwidthThrottle throttle,
        ILogger<AppState> logger)
    {
        _settingsStore = settingsStore;
        _localization = localization;
        _theme = theme;
        Distribution = distribution;
        _locator = locator;
        _compatibilityChecker = compatibilityChecker;
        _installation = installation;
        _content = content;
        _processes = processes;
        _throttle = throttle;
        _logger = logger;
        _settings = settingsStore.Current;
        _processes.StateChanged += (_, state) => Dispatcher.UIThread.Post(() => RunState = state);
    }

    public DistributionConfig Distribution { get; }

    public SettingsLoadOutcome SettingsOutcome => _settingsStore.LoadOutcome;

    public ContentSourceConfig ContentSourceConfig => Distribution.EffectiveContentSource(Settings);

    /// <summary>Production always requires a trusted signature. Unsigned fixtures are developer-only.</summary>
    public ManifestTrustConfig EffectiveTrust => new()
    {
        PublicKeys = [.. Distribution.Trust.PublicKeys],
        RequireSignature = !Settings.DeveloperMode,
    };

    public bool HasGame => !string.IsNullOrWhiteSpace(Settings.OriginalGamePath);

    public bool IsGameCompatible => Compatibility?.CanProceed == true;

    public bool IsInstalled => Install?.Phase == InstallPhase.Ready;

    public bool IsInstallIncomplete => Install is { Phase: not InstallPhase.Ready };

    public SemanticVersion? InstalledVersion => SemanticVersion.TryParse(Install?.ContentVersion, out var version) ? version : null;

    public bool IsUpdateAvailable => IsInstalled && LatestManifest is not null && InstalledVersion is { } installed && LatestManifest.Version > installed;

    public bool NeedsRepair => IsInstalled && Install!.LastVerificationPassed == false;

    public bool IsLauncherTooOld => LatestManifest?.Manifest.MinLauncherVersion is { } required
                                    && SemanticVersion.TryParse(required, out var minimum)
                                    && SemanticVersion.TryParse(AppInfo.Version, out var current)
                                    && current < minimum;

    /// <summary>Manifest to show optional components from: the installed one, or the latest when not installed.</summary>
    public ResolvedManifest? ComponentsManifest => IsInstalled ? InstalledManifest ?? LatestManifest : LatestManifest;

    public string? InstallPath => Settings.InstallPath;

    partial void OnSettingsChanged(LauncherSettings value) => NotifyDerived();
    partial void OnInstallChanged(InstallState? value) => NotifyDerived();
    partial void OnLatestManifestChanged(ResolvedManifest? value) => NotifyDerived();
    partial void OnInstalledManifestChanged(ResolvedManifest? value) => NotifyDerived();
    partial void OnCompatibilityChanged(CompatibilityReport? value) => NotifyDerived();

    private void NotifyDerived()
    {
        foreach (var name in new[]
                 {
                     nameof(HasGame), nameof(IsGameCompatible), nameof(IsInstalled), nameof(IsInstallIncomplete), nameof(InstalledVersion),
                     nameof(IsUpdateAvailable), nameof(NeedsRepair), nameof(IsLauncherTooOld), nameof(ComponentsManifest), nameof(InstallPath),
                     nameof(ContentSourceConfig),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Applies settings that affect the running app (language, theme, logging, bandwidth).</summary>
    public void ApplyRuntimeSettings()
    {
        _localization.Apply(Settings.Language);
        _theme.Apply(Settings.Theme);
        LoggingSetup.SetVerbose(Settings.VerboseLogging);
        _throttle.BytesPerSecond = Settings.DownloadSpeedLimitKBps * 1024L;
        _processes.InstallDirectory = Settings.InstallPath;
    }

    public void UpdateSettings(Action<LauncherSettings> mutate)
    {
        var previous = Settings;
        try
        {
            Settings = _settingsStore.Update(mutate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save settings");
            throw SanovaException.Wrap(ErrorCodes.IoError, "Could not save settings.", ex, ("path", "settings.json"), ("detail", ex.Message));
        }
        ApplyRuntimeSettings();

        var contentChanged = previous.DeveloperMode != Settings.DeveloperMode
            || !string.Equals(previous.UpdateChannel, Settings.UpdateChannel, StringComparison.Ordinal)
            || previous.ContentSource.Kind != Settings.ContentSource.Kind
            || !string.Equals(previous.ContentSource.Value, Settings.ContentSource.Value, StringComparison.Ordinal);
        if (contentChanged)
        {
            _contentRevision++;
            LatestManifest = null;
            ContentError = null;
            LastContentCheck = null;
            ContentSource = null;
            ContentStatus = ContentSourceConfig.IsConfigured ? ContentStatus.Unknown : ContentStatus.NotConfigured;
        }

        if (!string.Equals(previous.InstallPath, Settings.InstallPath, StringComparison.OrdinalIgnoreCase)
            || previous.DeveloperMode != Settings.DeveloperMode) RefreshInstall();
        if (!string.Equals(previous.OriginalGamePath, Settings.OriginalGamePath, StringComparison.OrdinalIgnoreCase) ||
            previous.AllowUnsupportedGameVersions != Settings.AllowUnsupportedGameVersions || contentChanged)
        {
            _ = RefreshCompatibilityAsync();
        }
    }

    public async Task InitializeAsync()
    {
        ApplyRuntimeSettings();
        _processes.Start();
        RefreshInstall();
        LatestManifest = _content.GetCachedManifest(EffectiveTrust, ContentSourceConfig, Settings.UpdateChannel);
        ContentStatus = ContentSourceConfig.IsConfigured ? ContentStatus.Unknown : ContentStatus.NotConfigured;
        await RefreshCompatibilityAsync().ConfigureAwait(true);
    }

    public async Task DetectGamesAsync()
    {
        IsDetecting = true;
        try
        {
            var saved = Settings.OriginalGamePath;
            Candidates = await Task.Run(() => _locator.Locate(saved is null ? null : [(saved, GameDetectionSource.Saved)])).ConfigureAwait(true);
        }
        finally
        {
            IsDetecting = false;
        }
    }

    public GameCandidate? Inspect(string path) => _locator.Inspect(path);

    public Task<CompatibilityReport> CheckCompatibilityAsync(string gameDirectory, CancellationToken cancellationToken = default)
        => Task.Run(() => _compatibilityChecker.CheckAsync(gameDirectory, new CompatibilityCheckOptions(
            Settings.AllowUnsupportedGameVersions,
            LatestManifest?.Manifest.Compatibility,
            LatestManifest?.Manifest.BaseGame), cancellationToken), cancellationToken);

    public async Task RefreshCompatibilityAsync()
    {
        var path = Settings.OriginalGamePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Compatibility = null;
            return;
        }
        IsCheckingCompatibility = true;
        try
        {
            Compatibility = await CheckCompatibilityAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Compatibility check failed");
            Compatibility = null;
        }
        finally
        {
            IsCheckingCompatibility = false;
        }
    }

    public void RefreshInstall()
    {
        var path = Settings.InstallPath;
        _processes.InstallDirectory = path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Install = null;
            InstalledManifest = null;
            HasPendingTransaction = false;
            return;
        }
        try
        {
            HasPendingTransaction = InstallStateStore.HasPendingTransaction(path);
            Install = InstallStateStore.Load(path);
            InstalledManifest = Install is null ? null : _installation.GetInstalledManifest(path);
        }
        catch (SanovaException ex)
        {
            _logger.LogError(ex, "Install state unreadable");
            Install = null;
            InstalledManifest = null;
        }
    }

    /// <summary>Finishes or undoes a change that was interrupted (crash, power loss) before anything else runs.</summary>
    public RecoveryOutcome RecoverInterruptedChanges()
    {
        var path = Settings.InstallPath;
        if (string.IsNullOrWhiteSpace(path) || !InstallStateStore.HasPendingTransaction(path)) return RecoveryOutcome.NothingToRecover;
        try
        {
            return _installation.Recover(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery failed");
            return RecoveryOutcome.Failed;
        }
        finally
        {
            RefreshInstall();
        }
    }

    public async Task CheckContentAsync()
    {
        if (ContentStatus == ContentStatus.Checking) return;
        var config = ContentSourceConfig;
        var revision = _contentRevision;
        if (!config.IsConfigured)
        {
            ContentStatus = ContentStatus.NotConfigured;
            return;
        }

        ContentStatus = ContentStatus.Checking;
        var settings = Settings;
        var trust = EffectiveTrust;
        var result = await Task.Run(() => _content.CheckAsync(config, trust, settings.UpdateChannel, settings.DeveloperMode, CancellationToken.None))
            .ConfigureAwait(true);
        // A source/channel change may have started another check while this one was downloading.
        if (revision != _contentRevision) return;
        LastContentCheck = result.CheckedAt;
        ContentSource = result.SourceDescription;
        ContentError = result.Error;
        if (result.Manifest is not null) LatestManifest = result.Manifest;
        ContentStatus = result.Status switch
        {
            ContentCheckStatus.Available => ContentStatus.Available,
            ContentCheckStatus.NotConfigured => ContentStatus.NotConfigured,
            ContentCheckStatus.NoRelease => ContentStatus.NoRelease,
            ContentCheckStatus.Offline => ContentStatus.Offline,
            _ => ContentStatus.Failed,
        };

        // A newer manifest can change which executables are supported.
        if (result.Manifest is not null && HasGame) await RefreshCompatibilityAsync().ConfigureAwait(true);
    }
}
