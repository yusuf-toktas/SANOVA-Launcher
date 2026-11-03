using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

public sealed class SaveSlotItem
{
    public SaveSlotItem(SaveSlot slot)
    {
        Slot = slot;
        Title = new LocalizedString(l => l.T("game.saves.slot", ("number", slot.Slot)));
        When = new LocalizedString(l => l.Date(slot.Modified));
    }

    public SaveSlot Slot { get; }

    public LocalizedString Title { get; }

    public LocalizedString When { get; }
}

public sealed class BackupItem
{
    public BackupItem(SaveBackup backup)
    {
        Backup = backup;
        Title = new LocalizedString(l => l.Date(backup.Created));
        Detail = new LocalizedString(l =>
        {
            var key = $"game.saves.reason.{backup.Reason}";
            var reason = l.Localizer.HasKey(key) ? l.T(key) : backup.Reason;
            return l.Plural("game.saves.backupDetail", backup.FileCount, ("reason", reason));
        });
    }

    public SaveBackup Backup { get; }

    public LocalizedString Title { get; }

    public LocalizedString Detail { get; }
}

/// <summary>My game: the original installation, the SANOVA installation and saved games.</summary>
public sealed partial class GameViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly AppState _state;
    private readonly LauncherActions _actions;
    private readonly SaveGameService _saves;
    private readonly DesktopServices _desktop;
    private readonly DialogService _dialogs;
    private readonly OperationRunner _runner;
    private readonly LocalizationService _loc;

    [ObservableProperty]
    private bool _showTechnical;

    public GameViewModel(IServiceProvider services, AppState state, LauncherActions actions, SaveGameService saves, DesktopServices desktop, DialogService dialogs,
        OperationRunner runner, LocalizationService loc)
    {
        _services = services;
        _state = state;
        _actions = actions;
        _saves = saves;
        _desktop = desktop;
        _dialogs = dialogs;
        _runner = runner;
        _loc = loc;

        ExecutableText = new LocalizedString(l => _state.Compatibility is { } c ? Present.Executable(l, c.Executable) : l.T(_state.HasGame ? "home.ledger.checking" : "home.ledger.gameNone"));
        EditionText = new LocalizedString(l => _state.Compatibility is { } c ? Present.Edition(l, c.Executable) : string.Empty);
        VerdictText = new LocalizedString(l => _state.Compatibility?.Verdict switch
        {
            CompatibilityVerdict.Compatible => l.T("game.verdict.ok"),
            CompatibilityVerdict.CompatibleWithWarnings => l.T("game.verdict.warnings"),
            CompatibilityVerdict.Incompatible => l.T(_state.Compatibility.CanProceed ? "game.verdict.override" : "game.verdict.blocked"),
            _ => string.Empty,
        });
        InstallStatus = new LocalizedString(InstallStatusText);
        InstallDetail = new LocalizedString(InstallDetailText);
        VerifiedText = new LocalizedString(l => _state.Install?.LastVerifiedAt is { } at
            ? l.T(_state.Install.LastVerificationPassed == true ? "game.install.verifiedOk" : "game.install.verifiedIssues", ("when", l.Relative(at)))
            : l.T("game.install.neverVerified"));
        _state.PropertyChanged += OnStateChanged;
        _runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationRunner.IsRunning)) NotifyAll();
        };
        Rebuild();
    }

    public LocalizedString ExecutableText { get; }

    public LocalizedString EditionText { get; }

    public LocalizedString VerdictText { get; }

    public LocalizedString InstallStatus { get; }

    public LocalizedString InstallDetail { get; }

    public LocalizedString VerifiedText { get; }

    public ObservableCollection<FindingItem> Findings { get; } = [];

    public ObservableCollection<SaveSlotItem> Slots { get; } = [];

    public ObservableCollection<BackupItem> Backups { get; } = [];

    public string? GamePath => _state.Settings.OriginalGamePath;

    public bool HasGame => _state.HasGame;

    public bool IsChecking => _state.IsCheckingCompatibility;

    public bool IsVerdictOk => _state.Compatibility?.Verdict == CompatibilityVerdict.Compatible;

    public bool IsVerdictWarning => _state.Compatibility?.Verdict == CompatibilityVerdict.CompatibleWithWarnings;

    public bool IsVerdictError => _state.Compatibility?.Verdict == CompatibilityVerdict.Incompatible;

    public string? InstallPath => _state.Settings.InstallPath;

    public bool HasInstall => _state.Install is not null;

    public bool HasNoInstall => _state.Install is null;

    public bool IsInstalled => _state.IsInstalled;

    public bool CanModify => _state.Install is not null && !_runner.IsRunning;

    public string UserFilesPath => _saves.UserFilesDirectory;

    public bool HasSlots => Slots.Count > 0;

    public bool HasBackups => Backups.Count > 0;

    public string TechnicalDetails
    {
        get
        {
            var exe = _state.Compatibility?.Executable;
            if (exe is null || exe.Path is null) return string.Empty;
            var lines = new List<string>
            {
                $"{_loc.T("game.tech.file")}: {exe.FileName}",
                $"{_loc.T("game.tech.size")}: {exe.Size.ToString("N0", _loc.Culture)}",
                $"SHA-256: {exe.Sha256}",
                $"MD5: {exe.Md5}",
            };
            if (exe.Pe is { } pe) lines.Add($"{_loc.T("game.tech.linkTime")}: {pe.LinkTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC");
            lines.Add($"{_loc.T("game.tech.id")}: {exe.Match?.Id ?? exe.Kind.ToString()}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public override Task OnNavigatedToAsync()
    {
        RefreshSaves();
        return Task.CompletedTask;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.Compatibility) or nameof(AppState.Install) or nameof(AppState.Settings) or nameof(AppState.IsCheckingCompatibility))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Findings.Clear();
        foreach (var finding in _state.Compatibility?.Findings.OrderByDescending(f => f.Severity) ?? Enumerable.Empty<CompatibilityFinding>())
        {
            Findings.Add(new FindingItem(finding));
        }
        NotifyAll();
    }

    private void NotifyAll()
    {
        foreach (var text in new[] { ExecutableText, EditionText, VerdictText, InstallStatus, InstallDetail, VerifiedText }) text.Refresh();
        foreach (var name in new[]
                 {
                     nameof(GamePath), nameof(HasGame), nameof(IsChecking), nameof(IsVerdictOk), nameof(IsVerdictWarning), nameof(IsVerdictError),
                     nameof(InstallPath), nameof(HasInstall), nameof(HasNoInstall), nameof(IsInstalled), nameof(CanModify), nameof(TechnicalDetails),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private void RefreshSaves()
    {
        Slots.Clear();
        foreach (var slot in _saves.GetSaveSlots()) Slots.Add(new SaveSlotItem(slot));
        Backups.Clear();
        foreach (var backup in _saves.GetBackups().Take(10)) Backups.Add(new BackupItem(backup));
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(HasBackups));
    }

    private string InstallStatusText(LocalizationService l)
    {
        var install = _state.Install;
        if (install is null) return l.T("game.install.none");
        return install.Phase switch
        {
            InstallPhase.Ready => l.T("game.install.ready", ("version", install.ContentVersion ?? "?")),
            _ => l.T("game.install.incomplete"),
        };
    }

    private string InstallDetailText(LocalizationService l)
    {
        var install = _state.Install;
        if (install is null) return l.T("game.install.noneBody");
        return l.T("game.install.detail",
            ("components", install.EnabledComponents.Count),
            ("files", install.Files.Count),
            ("size", l.Bytes(install.TotalBytes)));
    }

    [RelayCommand]
    private async Task ChangeGameAsync()
    {
        var folder = await _desktop.PickFolderAsync(_loc.T("setup.game.pickTitle"), GamePath).ConfigureAwait(true);
        if (folder is null) return;
        _state.UpdateSettings(s => s.OriginalGamePath = folder);
    }

    [RelayCommand]
    private Task RecheckAsync() => _state.RefreshCompatibilityAsync();

    [RelayCommand]
    private void OpenGameFolder() => ShellIntegration.OpenFolder(GamePath);

    [RelayCommand]
    private void OpenInstallFolder() => ShellIntegration.OpenFolder(InstallPath);

    [RelayCommand]
    private void OpenSaves()
    {
        if (!ShellIntegration.OpenFolder(UserFilesPath))
        {
            _ = _dialogs.AlertAsync(_loc.T("game.saves.title"), _loc.T("game.saves.noFolder"));
        }
    }

    [RelayCommand]
    private void OpenBackups()
    {
        Directory.CreateDirectory(_saves.BackupsDirectory);
        ShellIntegration.OpenFolder(_saves.BackupsDirectory);
    }

    [RelayCommand]
    private Task CopyTechnicalAsync() => _desktop.CopyTextAsync(TechnicalDetails);

    [RelayCommand]
    private void ToggleTechnical() => ShowTechnical = !ShowTechnical;

    [RelayCommand]
    private Task VerifyAsync() => _actions.VerifyAsync();

    [RelayCommand]
    private Task RepairAsync() => _actions.RepairAsync();

    [RelayCommand]
    private Task UninstallAsync() => _actions.UninstallAsync();

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        await _actions.BackupSavesAsync().ConfigureAwait(true);
        RefreshSaves();
    }

    [RelayCommand]
    private async Task RestoreAsync(BackupItem item)
    {
        await _actions.RestoreSavesAsync(item.Backup).ConfigureAwait(true);
        RefreshSaves();
    }

    [RelayCommand]
    private void SetUp() => _services.GetRequiredService<MainViewModel>().OpenSetup();
}
