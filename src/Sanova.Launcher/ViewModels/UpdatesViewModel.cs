using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sanova.Core.Installation;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

public sealed record ChoiceOption(string Value, LocalizedString Label);

/// <summary>Updates: the launcher itself (Velopack) and SANOVA content (GitHub releases), managed independently.</summary>
public sealed partial class UpdatesViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly LauncherActions _actions;
    private readonly InstallationService _installation;
    private readonly OperationRunner _runner;
    private readonly DialogService _dialogs;
    private readonly ILogger<UpdatesViewModel> _logger;

    [ObservableProperty]
    private PlanSummary? _plan;

    public UpdatesViewModel(AppState state, LauncherUpdater launcher, LauncherActions actions, InstallationService installation, OperationRunner runner,
        DialogService dialogs, ILogger<UpdatesViewModel> logger)
    {
        _state = state;
        Launcher = launcher;
        _actions = actions;
        _installation = installation;
        _runner = runner;
        _dialogs = dialogs;
        _logger = logger;

        Channels =
        [
            new ChoiceOption("stable", new LocalizedString("settings.channel.stable")),
            new ChoiceOption("beta", new LocalizedString("settings.channel.beta")),
        ];

        LauncherTitle = new LocalizedString(l => l.T(Launcher.State switch
        {
            LauncherUpdateState.NotConfigured => "updates.launcher.notConfigured.title",
            LauncherUpdateState.DevelopmentBuild => "updates.launcher.devBuild.title",
            LauncherUpdateState.Checking => "updates.checking",
            LauncherUpdateState.Available => "updates.launcher.available.title",
            LauncherUpdateState.Downloading => "updates.launcher.downloading.title",
            LauncherUpdateState.ReadyToRestart => "updates.launcher.ready.title",
            LauncherUpdateState.Failed => "updates.launcher.failed.title",
            LauncherUpdateState.UpToDate => "updates.upToDate",
            _ => "updates.launcher.idle.title",
        }, ("version", Launcher.AvailableVersion ?? "")));
        LauncherBody = new LocalizedString(l => Launcher.State switch
        {
            LauncherUpdateState.NotConfigured => l.T("updates.launcher.notConfigured.body"),
            LauncherUpdateState.DevelopmentBuild => l.T("updates.launcher.devBuild.body"),
            LauncherUpdateState.Downloading => l.T("updates.launcher.downloading.body", ("percent", Launcher.DownloadPercent)),
            LauncherUpdateState.Failed => l.T("updates.launcher.failed.body"),
            LauncherUpdateState.UpToDate when Launcher.LastChecked is { } checkedAt => l.T("updates.lastChecked", ("when", l.Relative(checkedAt))),
            _ => string.Empty,
        });
        CurrentLauncher = new LocalizedString(l => l.T("updates.currentVersion", ("version", AppInfo.Version)));

        ContentTitle = new LocalizedString(ContentTitleText);
        ContentBody = new LocalizedString(ContentBodyText);
        InstalledText = new LocalizedString(l => l.T("updates.content.installed", ("version", _state.Install?.ContentVersion ?? l.T("updates.content.none"))));
        LatestText = new LocalizedString(l => l.T("updates.content.latest", ("version", _state.LatestManifest?.Manifest.ContentVersion ?? "—")));
        PlanText = new LocalizedString(l =>
        {
            if (Plan is not { IsEmpty: false } p) return string.Empty;
            var parts = new List<string>();
            var changed = p.Added + p.Updated + p.Enabled;
            var removed = p.Removed + p.Disabled;
            if (changed > 0) parts.Add(l.Plural("updates.content.planChanged", changed));
            if (removed > 0) parts.Add(l.Plural("updates.content.planRemoved", removed));
            if (p.DownloadBytes > 0) parts.Add(l.T("updates.content.planDownload", ("size", l.Bytes(p.DownloadBytes))));
            return string.Join(", ", parts);
        });
        SourceText = new LocalizedString(l => _state.ContentSource is { } source ? l.T("updates.content.source", ("source", source)) : string.Empty);

        Launcher.PropertyChanged += (_, _) => RefreshTexts();
        _state.PropertyChanged += OnStateChanged;
        _runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationRunner.IsRunning)) RefreshTexts();
        };
    }

    public LauncherUpdater Launcher { get; }

    public IReadOnlyList<ChoiceOption> Channels { get; }

    public LocalizedString LauncherTitle { get; }

    public LocalizedString LauncherBody { get; }

    public LocalizedString CurrentLauncher { get; }

    public LocalizedString ContentTitle { get; }

    public LocalizedString ContentBody { get; }

    public LocalizedString InstalledText { get; }

    public LocalizedString LatestText { get; }

    public LocalizedString PlanText { get; }

    public LocalizedString SourceText { get; }

    public ChoiceOption SelectedChannel
    {
        get => Channels.FirstOrDefault(c => c.Value == _state.Settings.UpdateChannel) ?? Channels[0];
        set
        {
            if (value is null || value.Value == _state.Settings.UpdateChannel) return;
            _state.UpdateSettings(s => s.UpdateChannel = value.Value);
            OnPropertyChanged();
            _ = CheckContentAsync();
        }
    }

    public bool CanCheckLauncher => Launcher.CanCheck && !_runner.IsRunning;

    public bool CanDownloadLauncher => Launcher.State == LauncherUpdateState.Available && !_runner.IsRunning;

    public bool CanRestartLauncher => Launcher.State == LauncherUpdateState.ReadyToRestart;

    public bool HasLauncherNotes => Launcher.State is LauncherUpdateState.Available or LauncherUpdateState.ReadyToRestart && !string.IsNullOrWhiteSpace(Launcher.ReleaseNotes);

    public bool IsCheckingContent => _state.ContentStatus == ContentStatus.Checking;

    public bool CanCheckContent => !IsCheckingContent && _state.ContentSourceConfig.IsConfigured;

    public bool CanUpdateContent => _state.IsUpdateAvailable && !_state.IsLauncherTooOld && !_runner.IsRunning;

    public bool HasPlan => Plan is { IsEmpty: false } && _state.IsUpdateAvailable;

    public string? ContentNotes
    {
        get
        {
            var manifest = _state.LatestManifest;
            if (manifest is null) return null;
            var notes = manifest.Manifest.ReleaseNotes is { Count: > 0 } localized ? LocalizationService.Instance?.Resolve(localized) : manifest.ReleaseNotesFallback;
            return string.IsNullOrWhiteSpace(notes) ? null : notes;
        }
    }

    public bool HasContentNotes => ContentNotes is not null;

    public override async Task OnNavigatedToAsync()
    {
        await RefreshPlanAsync().ConfigureAwait(true);
        RefreshTexts();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.LatestManifest) or nameof(AppState.Install))
        {
            _ = RefreshPlanAsync();
        }
        RefreshTexts();
    }

    private async Task RefreshPlanAsync()
    {
        var path = _state.InstallPath;
        var manifest = _state.LatestManifest;
        if (path is null || manifest is null || !_state.IsUpdateAvailable)
        {
            Plan = null;
            return;
        }
        try
        {
            Plan = await Task.Run(() => _installation.Preview(path, manifest, null)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not compute the update plan");
            Plan = null;
        }
    }

    partial void OnPlanChanged(PlanSummary? value) => RefreshTexts();

    private void RefreshTexts()
    {
        foreach (var text in new[] { LauncherTitle, LauncherBody, ContentTitle, ContentBody, InstalledText, LatestText, PlanText, SourceText }) text.Refresh();
        foreach (var name in new[]
                 {
                     nameof(CanCheckLauncher), nameof(CanDownloadLauncher), nameof(CanRestartLauncher), nameof(HasLauncherNotes), nameof(IsCheckingContent),
                     nameof(CanCheckContent), nameof(CanUpdateContent), nameof(HasPlan), nameof(ContentNotes), nameof(HasContentNotes), nameof(SelectedChannel),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private string ContentTitleText(LocalizationService l)
    {
        if (_state.IsLauncherTooOld) return l.T("home.status.updateLauncher.title");
        if (_state.IsUpdateAvailable) return l.T("home.status.update.title");
        return _state.ContentStatus switch
        {
            ContentStatus.NotConfigured => l.T("home.status.notConfigured.title"),
            ContentStatus.Checking => l.T("updates.checking"),
            ContentStatus.Offline => l.T("home.status.offline.title"),
            ContentStatus.NoRelease => l.T("home.status.noRelease.title"),
            ContentStatus.Failed => l.T("home.status.checkFailed.title"),
            ContentStatus.Available when _state.IsInstalled => l.T("updates.upToDate"),
            ContentStatus.Available => l.T("home.status.install.title"),
            _ => l.T("updates.content.unchecked"),
        };
    }

    private string ContentBodyText(LocalizationService l)
    {
        if (_state.ContentStatus == ContentStatus.Failed && _state.ContentError is { } error) return DialogService.DescribeError(l, error).Message;
        if (_state.ContentStatus is ContentStatus.NotConfigured or ContentStatus.Offline or ContentStatus.NoRelease)
        {
            var key = _state.ContentStatus switch
            {
                ContentStatus.NotConfigured => "home.status.notConfigured.body",
                ContentStatus.Offline => "home.status.offline.body",
                _ => "home.status.noRelease.body",
            };
            return l.T(key, ("channel", l.T($"settings.channel.{_state.Settings.UpdateChannel}")));
        }
        return _state.LastContentCheck is { } at ? l.T("updates.lastChecked", ("when", l.Relative(at))) : string.Empty;
    }

    [RelayCommand]
    private Task CheckLauncherAsync() => Launcher.CheckAsync();

    [RelayCommand]
    private async Task DownloadLauncherAsync()
    {
        var result = await _runner.RunAsync(OperationKind.LauncherUpdate, (_, token) => Launcher.DownloadAsync(token)).ConfigureAwait(true);
        if (result.Succeeded && Launcher.State == LauncherUpdateState.ReadyToRestart)
        {
            var loc = LocalizationService.Instance!;
            if (await _dialogs.ConfirmAsync(loc.T("updates.launcher.ready.title"), loc.T("updates.launcher.restartBody"), loc.T("updates.launcher.restart")).ConfigureAwait(true))
            {
                RestartLauncher();
            }
        }
    }

    [RelayCommand]
    private void RestartLauncher()
    {
        if (_runner.IsRunning) return;
        Launcher.ApplyAndRestart();
    }

    [RelayCommand]
    private async Task CheckContentAsync()
    {
        await _state.CheckContentAsync().ConfigureAwait(true);
        await RefreshPlanAsync().ConfigureAwait(true);
        RefreshTexts();
    }

    [RelayCommand]
    private Task UpdateContentAsync() => _actions.UpdateAsync();
}
