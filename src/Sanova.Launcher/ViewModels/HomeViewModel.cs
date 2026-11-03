using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

public enum PrimaryAction
{
    SetUp,
    FixGame,
    Install,
    Resume,
    Update,
    UpdateLauncher,
    Repair,
    Play,
    Running,
    Busy,
    Unavailable,
}

/// <summary>
/// Home: the hero, the adaptive main button (set up → install → update → play) and an
/// at-a-glance ledger of the game, the SANOVA installation and the launcher itself.
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly AppState _state;
    private readonly OperationRunner _runner;
    private readonly LauncherActions _actions;
    private readonly LauncherUpdater _launcherUpdater;
    private readonly LocalizationService _loc;

    [ObservableProperty]
    private PrimaryAction _action;

    [ObservableProperty]
    private bool _actionEnabled;

    [ObservableProperty]
    private Geometry? _actionIcon;

    public HomeViewModel(IServiceProvider services, AppState state, OperationRunner runner, LauncherActions actions, LauncherUpdater launcherUpdater, LocalizationService loc)
    {
        _services = services;
        _state = state;
        _runner = runner;
        _actions = actions;
        _launcherUpdater = launcherUpdater;
        _loc = loc;

        ActionLabel = new LocalizedString(l => l.T(ActionKey()));
        StatusTitle = new LocalizedString(StatusTitleText);
        StatusDetail = new LocalizedString(StatusDetailText);
        GameValue = new LocalizedString(l => _state.Compatibility is { } c ? Present.Executable(l, c.Executable) : l.T(_state.HasGame ? "home.ledger.checking" : "home.ledger.gameNone"));
        GameDetail = new LocalizedString(l => _state.Settings.OriginalGamePath ?? l.T("home.ledger.gameHint"));
        ContentValue = new LocalizedString(l => _state.IsInstalled
            ? l.T("home.ledger.version", ("version", _state.Install!.ContentVersion ?? "?"))
            : l.T(_state.IsInstallIncomplete ? "home.ledger.incomplete" : "home.ledger.notInstalled"));
        ContentDetail = new LocalizedString(ContentDetailText);
        LauncherValue = new LocalizedString(l => l.T("home.ledger.version", ("version", AppInfo.Version)));
        LauncherDetail = new LocalizedString(LauncherDetailText);
        NotesTitle = new LocalizedString(l => _state.LatestManifest is { } m ? l.T("home.notesTitle", ("version", m.Manifest.ContentVersion)) : string.Empty);

        _state.PropertyChanged += OnChanged;
        _runner.PropertyChanged += OnChanged;
        _launcherUpdater.PropertyChanged += OnChanged;
        _loc.LanguageChanged += (_, _) => Recompute();
        Recompute();
    }

    public OperationRunner Operations => _runner;

    public LocalizedString ActionLabel { get; }

    public LocalizedString StatusTitle { get; }

    public LocalizedString StatusDetail { get; }

    public LocalizedString GameValue { get; }

    public LocalizedString GameDetail { get; }

    public LocalizedString ContentValue { get; }

    public LocalizedString ContentDetail { get; }

    public LocalizedString LauncherValue { get; }

    public LocalizedString LauncherDetail { get; }

    public LocalizedString NotesTitle { get; }

    public bool IsBusy => _runner.IsRunning;

    public bool ShowPlayAnyway => Action is PrimaryAction.Update or PrimaryAction.UpdateLauncher && _state.IsInstalled && !_runner.IsRunning;

    public bool CanVerify => _state.Install is not null && !_runner.IsRunning;

    public bool IsGameOk => _state.Compatibility?.Verdict == CompatibilityVerdict.Compatible;

    public bool IsGameWarning => _state.Compatibility?.Verdict == CompatibilityVerdict.CompatibleWithWarnings;

    public bool IsGameError => _state.HasGame && _state.Compatibility?.Verdict == CompatibilityVerdict.Incompatible;

    public bool IsContentOk => _state.IsInstalled && _state.Install!.LastVerificationPassed != false && !_state.IsUpdateAvailable;

    public bool IsContentWarning => _state.IsUpdateAvailable || _state.IsInstallIncomplete || _state.NeedsRepair;

    public bool IsLauncherWarning => _launcherUpdater.State is LauncherUpdateState.Available or LauncherUpdateState.ReadyToRestart;

    public string? ReleaseNotes
    {
        get
        {
            var manifest = _state.LatestManifest;
            if (manifest is null) return null;
            var notes = manifest.Manifest.ReleaseNotes is { Count: > 0 } localized ? _loc.Resolve(localized) : manifest.ReleaseNotesFallback;
            return string.IsNullOrWhiteSpace(notes) ? null : notes;
        }
    }

    public bool HasReleaseNotes => ReleaseNotes is not null;

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Recompute();

    private void Recompute()
    {
        Action = Decide();
        ActionEnabled = Action is not (PrimaryAction.Running or PrimaryAction.Busy or PrimaryAction.Unavailable);
        ActionIcon = Icons.Get(Action switch
        {
            PrimaryAction.Play or PrimaryAction.Running => "Play",
            PrimaryAction.Install or PrimaryAction.Resume or PrimaryAction.Update => "Download",
            PrimaryAction.UpdateLauncher => "Rocket",
            PrimaryAction.Repair => "Wrench",
            PrimaryAction.FixGame => "Warning",
            PrimaryAction.Unavailable => "CloudOff",
            PrimaryAction.Busy => "Sync",
            _ => "ChevronRight",
        });

        foreach (var text in new[] { ActionLabel, StatusTitle, StatusDetail, GameValue, GameDetail, ContentValue, ContentDetail, LauncherValue, LauncherDetail, NotesTitle })
        {
            text.Refresh();
        }
        foreach (var name in new[]
                 {
                     nameof(IsBusy), nameof(ShowPlayAnyway), nameof(CanVerify), nameof(IsGameOk), nameof(IsGameWarning), nameof(IsGameError),
                     nameof(IsContentOk), nameof(IsContentWarning), nameof(IsLauncherWarning), nameof(ReleaseNotes), nameof(HasReleaseNotes),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private PrimaryAction Decide()
    {
        if (_runner.IsRunning) return PrimaryAction.Busy;
        if (_state.RunState.IsSanovaRunning) return PrimaryAction.Running;
        if (!_state.Settings.SetupCompleted || !_state.HasGame) return PrimaryAction.SetUp;
        if (_state.HasPendingTransaction) return PrimaryAction.Repair;

        if (!_state.IsInstalled)
        {
            if (_state.Compatibility is { CanProceed: false }) return PrimaryAction.FixGame;
            if (_state.LatestManifest is null) return PrimaryAction.Unavailable;
            if (_state.IsLauncherTooOld) return PrimaryAction.UpdateLauncher;
            return _state.IsInstallIncomplete ? PrimaryAction.Resume : PrimaryAction.Install;
        }

        if (_state.NeedsRepair) return PrimaryAction.Repair;
        if (_state.IsUpdateAvailable) return _state.IsLauncherTooOld ? PrimaryAction.UpdateLauncher : PrimaryAction.Update;
        return PrimaryAction.Play;
    }

    private string ActionKey() => Action switch
    {
        PrimaryAction.SetUp => "home.action.setUp",
        PrimaryAction.FixGame => "home.action.fixGame",
        PrimaryAction.Install => "home.action.install",
        PrimaryAction.Resume => "home.action.resume",
        PrimaryAction.Update => "home.action.update",
        PrimaryAction.UpdateLauncher => "home.action.updateLauncher",
        PrimaryAction.Repair => "home.action.repair",
        PrimaryAction.Running => "home.action.running",
        PrimaryAction.Busy => "home.action.working",
        PrimaryAction.Unavailable => "home.action.install",
        _ => "home.action.play",
    };

    private string StatusTitleText(LocalizationService l) => Action switch
    {
        PrimaryAction.Busy => _runner.Title.Value,
        PrimaryAction.Running => l.T("home.status.running.title"),
        PrimaryAction.SetUp => l.T("home.status.setUp.title"),
        PrimaryAction.FixGame => l.T("home.status.fixGame.title"),
        PrimaryAction.Install => l.T("home.status.install.title"),
        PrimaryAction.Resume => l.T("home.status.resume.title"),
        PrimaryAction.Update => l.T("home.status.update.title"),
        PrimaryAction.UpdateLauncher => l.T("home.status.updateLauncher.title"),
        PrimaryAction.Repair => l.T("home.status.repair.title"),
        PrimaryAction.Unavailable => l.T(UnavailableKey() + ".title"),
        _ => l.T("home.status.play.title"),
    };

    private string StatusDetailText(LocalizationService l)
    {
        var manifest = _state.LatestManifest;
        return Action switch
        {
            PrimaryAction.Busy => _runner.StageText.Value,
            PrimaryAction.Running => l.T("home.status.running.body"),
            PrimaryAction.SetUp => l.T("home.status.setUp.body"),
            PrimaryAction.FixGame => _state.Compatibility?.Findings.FirstOrDefault(f => f.Severity == FindingSeverity.Error) is { } error
                ? l.T($"compat.{error.Code}.title", error.Args)
                : l.T("home.status.fixGame.body"),
            PrimaryAction.Install when manifest is not null => l.T("home.status.install.body",
                ("version", manifest.Manifest.ContentVersion),
                ("size", l.Bytes(manifest.Manifest.Packages.Sum(p => p.Size)))),
            PrimaryAction.Resume => l.T("home.status.resume.body"),
            PrimaryAction.Update when manifest is not null => l.T("home.status.update.body",
                ("version", manifest.Manifest.ContentVersion), ("current", _state.Install?.ContentVersion ?? "?")),
            PrimaryAction.UpdateLauncher => l.T("home.status.updateLauncher.body", ("version", manifest?.Manifest.ContentVersion ?? "?")),
            PrimaryAction.Repair => l.T(_state.HasPendingTransaction ? "home.status.repair.pending" : "home.status.repair.body"),
            PrimaryAction.Unavailable => UnavailableDetail(l),
            _ => l.T(_state.ContentStatus is ContentStatus.Available ? "home.status.play.upToDate" : "home.status.play.body",
                ("version", _state.Install?.ContentVersion ?? "?")),
        };
    }

    private string UnavailableKey() => _state.ContentStatus switch
    {
        ContentStatus.NotConfigured => "home.status.notConfigured",
        ContentStatus.Offline => "home.status.offline",
        ContentStatus.NoRelease => "home.status.noRelease",
        ContentStatus.Failed => "home.status.checkFailed",
        _ => "home.status.checking",
    };

    private string UnavailableDetail(LocalizationService l)
    {
        if (_state.ContentStatus == ContentStatus.Failed && _state.ContentError is { } error)
        {
            return DialogService.DescribeError(l, error).Message;
        }
        return l.T(UnavailableKey() + ".body", ("channel", l.T($"settings.channel.{_state.Settings.UpdateChannel}")));
    }

    private string ContentDetailText(LocalizationService l)
    {
        var install = _state.Install;
        if (install is null) return l.T(_state.LatestManifest is { } m ? "home.ledger.available" : "home.ledger.contentHint", ("version", _state.LatestManifest?.Manifest.ContentVersion ?? ""));
        if (_state.IsUpdateAvailable) return l.T("home.ledger.updateAvailable", ("version", _state.LatestManifest!.Manifest.ContentVersion));
        if (install.LastVerificationPassed == false) return l.T("home.ledger.needsRepair");
        return install.LastVerifiedAt is { } verified ? l.T("home.ledger.verified", ("when", l.Relative(verified))) : l.T("home.ledger.notVerified");
    }

    private string LauncherDetailText(LocalizationService l) => _launcherUpdater.State switch
    {
        LauncherUpdateState.NotConfigured => l.T("updates.launcher.notConfigured.short"),
        LauncherUpdateState.DevelopmentBuild => l.T("updates.launcher.devBuild.short"),
        LauncherUpdateState.Checking => l.T("updates.checking"),
        LauncherUpdateState.Available => l.T("updates.launcher.available.short", ("version", _launcherUpdater.AvailableVersion ?? "?")),
        LauncherUpdateState.ReadyToRestart => l.T("updates.launcher.ready.short"),
        LauncherUpdateState.Failed => l.T("updates.launcher.failed.short"),
        LauncherUpdateState.UpToDate => l.T("updates.upToDate"),
        _ => l.T("updates.launcher.idle.short"),
    };

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        switch (Action)
        {
            case PrimaryAction.SetUp:
                _services.GetRequiredService<MainViewModel>().OpenSetup();
                break;
            case PrimaryAction.FixGame:
                _services.GetRequiredService<MainViewModel>().Navigate("game");
                break;
            case PrimaryAction.Install:
            case PrimaryAction.Resume:
                await _actions.InstallAsync().ConfigureAwait(true);
                break;
            case PrimaryAction.Update:
                await _actions.UpdateAsync().ConfigureAwait(true);
                break;
            case PrimaryAction.UpdateLauncher:
                _services.GetRequiredService<MainViewModel>().Navigate("updates");
                break;
            case PrimaryAction.Repair:
                if (_state.HasPendingTransaction) _state.RecoverInterruptedChanges();
                await _actions.RepairAsync().ConfigureAwait(true);
                break;
            case PrimaryAction.Play:
                await _actions.LaunchAsync().ConfigureAwait(true);
                break;
        }
    }

    [RelayCommand]
    private Task PlayAnywayAsync() => _actions.LaunchAsync();

    [RelayCommand]
    private Task VerifyAsync() => _actions.VerifyAsync();

    [RelayCommand]
    private void Go(string page) => _services.GetRequiredService<MainViewModel>().Navigate(page);
}
