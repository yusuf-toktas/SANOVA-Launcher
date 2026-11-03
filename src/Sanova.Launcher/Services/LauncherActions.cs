using Microsoft.Extensions.Logging;
using Sanova.Core;
using Sanova.Core.Content;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Core.Settings;
using Sanova.Launcher.Infrastructure;

namespace Sanova.Launcher.Services;

/// <summary>
/// High-level workflows shared by every page. Each one validates preconditions, protects
/// saved games, runs through the <see cref="OperationRunner"/> and refreshes state afterwards.
/// </summary>
public sealed class LauncherActions
{
    private readonly AppState _state;
    private readonly OperationRunner _runner;
    private readonly InstallationService _installation;
    private readonly SaveGameService _saves;
    private readonly GameLauncher _gameLauncher;
    private readonly DialogService _dialogs;
    private readonly NotificationService _notifications;
    private readonly LocalizationService _loc;
    private readonly DesktopServices _desktop;
    private readonly ILogger<LauncherActions> _logger;
    private readonly Dictionary<string, string> _manualArchives = new(StringComparer.OrdinalIgnoreCase);

    public LauncherActions(AppState state, OperationRunner runner, InstallationService installation, SaveGameService saves, GameLauncher gameLauncher,
        DialogService dialogs, NotificationService notifications, LocalizationService loc, DesktopServices desktop, ILogger<LauncherActions> logger)
    {
        _state = state;
        _runner = runner;
        _installation = installation;
        _saves = saves;
        _gameLauncher = gameLauncher;
        _dialogs = dialogs;
        _notifications = notifications;
        _loc = loc;
        _desktop = desktop;
        _logger = logger;
    }

    private ApplyOptions Options(LauncherSettings settings) => new(
        new Dictionary<string, string>(_manualArchives, StringComparer.OrdinalIgnoreCase),
        settings.KeepDownloadCache,
        settings.MaxParallelDownloads,
        settings.UpdateChannel);

    private async Task<bool> EnsureGameNotRunningAsync()
    {
        if (!_state.RunState.IsAnyRunning) return true;
        await _dialogs.AlertAsync(_loc.T("dialog.gameRunning.title"), _loc.T("dialog.gameRunning.body"), Tone.Warning).ConfigureAwait(true);
        return false;
    }

    private async Task BackupSavesIfEnabledAsync(string reason, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!_state.Settings.BackupSavesBeforeChanges) return;
        progress.Report(new OperationProgress(OperationStage.BackingUpSaves));
        await _saves.CreateBackupAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs an operation, asking for manually downloaded archives when a mod's license requires it.</summary>
    private async Task<OperationResult> RunWithManualDownloadsAsync(OperationKind kind, Func<ApplyOptions, IProgress<OperationProgress>, CancellationToken, Task> work, string? note)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var options = Options(_state.Settings);
            var result = await _runner.RunAsync(kind, (progress, token) => work(options, progress, token), showErrors: false, note: note).ConfigureAwait(true);
            if (result.Error is SanovaException { Code: ErrorCodes.ManualDownloadRequired } manual && await PromptForManualArchiveAsync(manual).ConfigureAwait(true))
            {
                continue;
            }
            if (result.Outcome == OperationOutcome.Failed && result.Error is not null)
            {
                await _dialogs.ShowErrorAsync(_loc.T($"op.{OperationRunner.Key(kind)}.failed"), result.Error).ConfigureAwait(true);
            }
            return result;
        }
        return new OperationResult(OperationOutcome.Cancelled, null);
    }

    private async Task<bool> PromptForManualArchiveAsync(SanovaException error)
    {
        var manifest = _state.LatestManifest ?? _state.InstalledManifest;
        var componentName = error.Args.GetValueOrDefault("component")?.ToString() ?? "?";
        var page = error.Args.GetValueOrDefault("page")?.ToString();
        var component = manifest?.Manifest.Components.FirstOrDefault(c => c.SourceKind == ComponentSourceKind.Manual && c.Name.Default == componentName);

        while (true)
        {
            var choice = await _dialogs.ShowAsync(new DialogViewModel
            {
                Title = _loc.T("dialog.manual.title", ("name", component is null ? componentName : _loc.Resolve(component.Name))),
                Message = component?.Source?.Instructions is { } instructions ? _loc.Resolve(instructions) : _loc.T("dialog.manual.body", ("file", component?.Source?.FileName ?? "")),
                Tone = Tone.Info,
                Buttons =
                [
                    new DialogButton(_loc.T("common.cancel"), "cancel"),
                    new DialogButton(_loc.T("dialog.manual.openPage"), "page"),
                    new DialogButton(_loc.T("dialog.manual.chooseFile"), "file", DialogButtonKind.Primary),
                ],
                CancelResult = "cancel",
            }).ConfigureAwait(true);

            switch (choice as string)
            {
                case "page":
                    ShellIntegration.OpenUrl(page);
                    continue;
                case "file":
                    var file = await _desktop.PickFileAsync(_loc.T("dialog.manual.chooseFile"), _loc.T("dialog.manual.filter"), "*.zip").ConfigureAwait(true);
                    if (file is null) continue;
                    if (component is not null) _manualArchives[component.Id] = file;
                    return component is not null;
                default:
                    return false;
            }
        }
    }

    public async Task InstallAsync()
    {
        var settings = _state.Settings;
        var manifest = _state.LatestManifest;
        var compatibility = _state.Compatibility;
        if (manifest is null || compatibility is null || settings.OriginalGamePath is null || settings.InstallPath is null) return;
        if (!compatibility.CanProceed)
        {
            await _dialogs.AlertAsync(_loc.T("dialog.incompatible.title"), _loc.T("dialog.incompatible.body"), Tone.Warning).ConfigureAwait(true);
            return;
        }
        if (_state.IsLauncherTooOld)
        {
            await _dialogs.AlertAsync(_loc.T("dialog.launcherTooOld.title"), _loc.T("dialog.launcherTooOld.body"), Tone.Warning).ConfigureAwait(true);
            return;
        }
        if (!await EnsureGameNotRunningAsync().ConfigureAwait(true)) return;

        var selection = ComponentResolver.InitialSelection(manifest.Manifest, settings.ComponentChoices);
        var result = await RunWithManualDownloadsAsync(OperationKind.Install, async (options, progress, token) =>
        {
            await BackupSavesIfEnabledAsync("before-install", progress, token).ConfigureAwait(false);
            await _installation.InstallAsync(new InstallRequest
            {
                OriginalGamePath = settings.OriginalGamePath,
                InstallPath = settings.InstallPath,
                Manifest = manifest,
                Components = selection,
                Compatibility = compatibility,
                Options = options,
            }, progress, token).ConfigureAwait(false);
        }, manifest.Manifest.ContentVersion).ConfigureAwait(true);

        _state.RefreshInstall();
        if (result.Succeeded)
        {
            _notifications.Show(Tone.Success, _loc.T("toast.installed.title"), _loc.T("toast.installed.body", ("version", manifest.Manifest.ContentVersion)));
        }
        else if (result.Outcome == OperationOutcome.Cancelled)
        {
            _notifications.Show(Tone.Info, _loc.T("toast.installPaused.title"), _loc.T("toast.installPaused.body"));
        }
    }

    public async Task UpdateAsync()
    {
        var manifest = _state.LatestManifest;
        var path = _state.InstallPath;
        if (manifest is null || path is null || !_state.IsInstalled) return;
        if (_state.IsLauncherTooOld)
        {
            await _dialogs.AlertAsync(_loc.T("dialog.launcherTooOld.title"), _loc.T("dialog.launcherTooOld.body"), Tone.Warning).ConfigureAwait(true);
            return;
        }
        if (!await EnsureGameNotRunningAsync().ConfigureAwait(true)) return;

        var result = await RunWithManualDownloadsAsync(OperationKind.Update, async (options, progress, token) =>
        {
            await BackupSavesIfEnabledAsync("before-update", progress, token).ConfigureAwait(false);
            await _installation.UpdateAsync(path, manifest, null, options, progress, token).ConfigureAwait(false);
        }, manifest.Manifest.ContentVersion).ConfigureAwait(true);

        _state.RefreshInstall();
        if (result.Succeeded)
        {
            _notifications.Show(Tone.Success, _loc.T("toast.updated.title"), _loc.T("toast.updated.body", ("version", manifest.Manifest.ContentVersion)));
        }
    }

    public async Task ApplyComponentsAsync(IReadOnlySet<string> selection)
    {
        var path = _state.InstallPath;
        if (path is null || !_state.IsInstalled) return;
        if (!await EnsureGameNotRunningAsync().ConfigureAwait(true)) return;

        var result = await RunWithManualDownloadsAsync(OperationKind.ApplyComponents, async (options, progress, token) =>
        {
            await BackupSavesIfEnabledAsync("before-mod-changes", progress, token).ConfigureAwait(false);
            await _installation.ApplyComponentChangesAsync(path, selection, options, progress, token).ConfigureAwait(false);
        }, null).ConfigureAwait(true);

        _state.RefreshInstall();
        if (result.Succeeded)
        {
            _notifications.Show(Tone.Success, _loc.T("toast.modsApplied.title"), null);
        }
    }

    public async Task VerifyAsync()
    {
        var path = _state.InstallPath;
        if (path is null || _state.Install is null) return;
        VerificationReport? report = null;
        var result = await _runner.RunAsync(OperationKind.Verify, async (progress, token) =>
        {
            report = await _installation.VerifyAsync(path, VerifyMode.Full, progress, token).ConfigureAwait(false);
        }).ConfigureAwait(true);
        _state.RefreshInstall();
        if (!result.Succeeded || report is null) return;

        if (report.Passed)
        {
            _notifications.Show(Tone.Success, _loc.T("toast.verified.title"), _loc.Plural("toast.verified.body", report.Checked));
            return;
        }

        var details = string.Join(Environment.NewLine, report.Issues.Take(30).Select(i => $"{(i.Problem == VerificationProblem.Missing ? "missing " : "changed ")} {i.Path}"));
        var repair = await _dialogs.ConfirmAsync(
            _loc.T("dialog.verifyFailed.title"),
            _loc.Plural("dialog.verifyFailed.body", report.Issues.Count),
            _loc.T("dialog.verifyFailed.repair"),
            details: details).ConfigureAwait(true);
        if (repair) await RepairAsync().ConfigureAwait(true);
    }

    public async Task RepairAsync()
    {
        var path = _state.InstallPath;
        if (path is null || !_state.IsInstalled) return;
        if (!await EnsureGameNotRunningAsync().ConfigureAwait(true)) return;

        VerificationReport? report = null;
        var manifest = _state.LatestManifest;
        var result = await RunWithManualDownloadsAsync(OperationKind.Repair, async (options, progress, token) =>
        {
            await BackupSavesIfEnabledAsync("before-repair", progress, token).ConfigureAwait(false);
            report = await _installation.RepairAsync(path, manifest, options, progress, token).ConfigureAwait(false);
        }, null).ConfigureAwait(true);

        _state.RefreshInstall();
        if (!result.Succeeded || report is null) return;
        if (report.Passed)
        {
            _notifications.Show(Tone.Success, _loc.T("toast.repaired.title"), _loc.T("toast.repaired.body"));
        }
        else
        {
            await _dialogs.AlertAsync(_loc.T("dialog.repairIncomplete.title"), _loc.Plural("dialog.repairIncomplete.body", report.Issues.Count), Tone.Warning,
                string.Join(Environment.NewLine, report.Issues.Take(30).Select(i => i.Path))).ConfigureAwait(true);
        }
    }

    public async Task UninstallAsync()
    {
        var path = _state.InstallPath;
        if (path is null || _state.Install is null) return;
        if (!await EnsureGameNotRunningAsync().ConfigureAwait(true)) return;

        var confirmed = await _dialogs.ConfirmAsync(
            _loc.T("dialog.uninstall.title"),
            _loc.T("dialog.uninstall.body", ("path", path)),
            _loc.T("dialog.uninstall.confirm"),
            danger: true).ConfigureAwait(true);
        if (!confirmed) return;

        UninstallResult? outcome = null;
        var result = await _runner.RunAsync(OperationKind.Uninstall, async (progress, token) =>
        {
            outcome = await _installation.UninstallAsync(path, progress, token).ConfigureAwait(false);
        }, cancellable: false).ConfigureAwait(true);

        _state.RefreshInstall();
        if (!result.Succeeded || outcome is null) return;

        var kept = outcome.KeptModified.Count + outcome.KeptUnknown.Count;
        if (kept == 0 && outcome.PreservedPlayerFiles is null)
        {
            _notifications.Show(Tone.Success, _loc.T("toast.uninstalled.title"), _loc.T("toast.uninstalled.body"));
        }
        else
        {
            var details = string.Join(Environment.NewLine, outcome.KeptModified.Concat(outcome.KeptUnknown).Take(40));
            await _dialogs.AlertAsync(_loc.T("toast.uninstalled.title"),
                _loc.Plural("dialog.uninstallKept.body", kept, ("path", path)), Tone.Info, details).ConfigureAwait(true);
        }
    }

    public async Task LaunchAsync()
    {
        var path = _state.InstallPath;
        var install = _state.Install;
        if (path is null || install is null) return;
        if (_state.RunState.IsSanovaRunning)
        {
            _notifications.Show(Tone.Info, _loc.T("toast.alreadyRunning.title"), null);
            return;
        }

        try
        {
            var pid = _gameLauncher.Launch(path, install.ExecutableName, _state.Settings.LaunchArguments);
            if (pid is null) return;
            _notifications.Show(Tone.Success, _loc.T("toast.launched.title"), null, TimeSpan.FromSeconds(3));
            switch (_state.Settings.AfterLaunch)
            {
                case AfterLaunchBehavior.Minimize:
                    _desktop.MinimizeMainWindow();
                    break;
                case AfterLaunchBehavior.Close:
                    _desktop.Shutdown();
                    break;
            }
        }
        catch (SanovaException ex)
        {
            _logger.LogWarning(ex, "Game launch failed");
            await _dialogs.ShowErrorAsync(_loc.T("op.launch.failed"), ex).ConfigureAwait(true);
        }
    }

    public async Task<SaveBackup?> BackupSavesAsync()
    {
        SaveBackup? backup = null;
        var result = await _runner.RunAsync(OperationKind.BackupSaves, async (progress, token) =>
        {
            progress.Report(new OperationProgress(OperationStage.BackingUpSaves));
            backup = await _saves.CreateBackupAsync("manual", token).ConfigureAwait(false);
        }).ConfigureAwait(true);
        if (result.Succeeded)
        {
            _notifications.Show(Tone.Success, backup is null ? _loc.T("toast.noSaves.title") : _loc.T("toast.savesBackedUp.title"),
                backup is null ? null : _loc.Plural("toast.savesBackedUp.body", backup.FileCount));
        }
        return backup;
    }

    public async Task RestoreSavesAsync(SaveBackup backup)
    {
        if (!await EnsureGameNotRunningAsync().ConfigureAwait(true)) return;
        var confirmed = await _dialogs.ConfirmAsync(_loc.T("dialog.restore.title"), _loc.T("dialog.restore.body", ("date", _loc.Date(backup.Created))),
            _loc.T("dialog.restore.confirm")).ConfigureAwait(true);
        if (!confirmed) return;

        var result = await _runner.RunAsync(OperationKind.RestoreSaves, (progress, token) =>
        {
            progress.Report(new OperationProgress(OperationStage.BackingUpSaves));
            return _saves.RestoreBackupAsync(backup, token);
        }).ConfigureAwait(true);
        if (result.Succeeded) _notifications.Show(Tone.Success, _loc.T("toast.savesRestored.title"), null);
    }
}
