using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sanova.Core.Updates;
using Sanova.Launcher.Infrastructure;
using Velopack;
using Velopack.Sources;

namespace Sanova.Launcher.Services;

public enum LauncherUpdateState
{
    /// <summary>No release repository configured in distribution.json.</summary>
    NotConfigured,

    /// <summary>Running from a build folder rather than a Velopack installation; self-update is unavailable.</summary>
    DevelopmentBuild,

    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed,
}

/// <summary>
/// Launcher self-updates through Velopack and GitHub Releases. Velopack downloads delta or
/// full packages with checksum verification and swaps versions only after the launcher exits,
/// so a failed update never leaves a half-replaced application.
/// </summary>
public sealed partial class LauncherUpdater : ObservableObject
{
    private readonly ILogger<LauncherUpdater> _logger;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    [ObservableProperty]
    private LauncherUpdateState _state;

    [ObservableProperty]
    private string? _availableVersion;

    [ObservableProperty]
    private string? _releaseNotes;

    [ObservableProperty]
    private int _downloadPercent;

    [ObservableProperty]
    private string? _errorDetail;

    [ObservableProperty]
    private DateTimeOffset? _lastChecked;

    public LauncherUpdater(DistributionConfig distribution, ILogger<LauncherUpdater> logger)
    {
        _logger = logger;
        var config = distribution.LauncherUpdates;
        var feed = Environment.GetEnvironmentVariable(LauncherUpdateConfig.OverrideVariable) is { Length: > 0 } overrideFeed ? overrideFeed : config.Url;
        var hasRepository = GitHubRepository.TryParse(config.Repository, out var repository);
        if (string.IsNullOrWhiteSpace(feed) && !hasRepository)
        {
            State = LauncherUpdateState.NotConfigured;
            return;
        }

        try
        {
            // A plain feed (https URL or folder) takes precedence; otherwise GitHub Releases.
            _manager = !string.IsNullOrWhiteSpace(feed)
                ? new UpdateManager(feed)
                : new UpdateManager(new GithubSource(repository.Url, accessToken: null, prerelease: config.AllowPrerelease));
            if (!string.IsNullOrWhiteSpace(feed)) _logger.LogInformation("Launcher updates use the feed {Feed}", feed);
            State = _manager.IsInstalled ? LauncherUpdateState.Idle : LauncherUpdateState.DevelopmentBuild;
            if (_manager.UpdatePendingRestart is { } pending)
            {
                AvailableVersion = pending.Version.ToString();
                State = LauncherUpdateState.ReadyToRestart;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Launcher self-update is unavailable");
            State = LauncherUpdateState.DevelopmentBuild;
        }
    }

    public string CurrentVersion => AppInfo.Version;

    public bool CanCheck => State is LauncherUpdateState.Idle or LauncherUpdateState.UpToDate or LauncherUpdateState.Available or LauncherUpdateState.Failed;

    partial void OnStateChanged(LauncherUpdateState value) => OnPropertyChanged(nameof(CanCheck));

    public async Task CheckAsync()
    {
        if (_manager is null || !CanCheck) return;
        State = LauncherUpdateState.Checking;
        try
        {
            _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);
            LastChecked = DateTimeOffset.UtcNow;
            if (_pending is null)
            {
                State = LauncherUpdateState.UpToDate;
                return;
            }
            AvailableVersion = _pending.TargetFullRelease.Version.ToString();
            ReleaseNotes = _pending.TargetFullRelease.NotesMarkdown;
            State = LauncherUpdateState.Available;
            _logger.LogInformation("Launcher update {Version} available", AvailableVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Launcher update check failed");
            ErrorDetail = ex.Message;
            State = LauncherUpdateState.Failed;
        }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken)
    {
        if (_manager is null || _pending is null) return;
        State = LauncherUpdateState.Downloading;
        DownloadPercent = 0;
        try
        {
            await _manager.DownloadUpdatesAsync(_pending, p => Avalonia.Threading.Dispatcher.UIThread.Post(() => DownloadPercent = p), cancellationToken)
                .ConfigureAwait(true);
            State = LauncherUpdateState.ReadyToRestart;
        }
        catch (OperationCanceledException)
        {
            State = LauncherUpdateState.Available;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Launcher update download failed");
            ErrorDetail = ex.Message;
            State = LauncherUpdateState.Failed;
            throw;
        }
    }

    /// <summary>Exits the launcher, applies the downloaded version and starts it again.</summary>
    public void ApplyAndRestart()
    {
        if (_manager is null) return;
        var target = _pending?.TargetFullRelease ?? _manager.UpdatePendingRestart;
        if (target is null) return;
        _logger.LogInformation("Restarting to apply launcher update {Version}", target.Version);
        _manager.ApplyUpdatesAndRestart(target);
    }
}
