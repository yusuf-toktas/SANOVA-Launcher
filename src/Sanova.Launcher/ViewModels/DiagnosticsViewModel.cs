using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sanova.Core.Diagnostics;
using Sanova.Core.Settings;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

public sealed class CheckItem
{
    public CheckItem(DiagnosticCheck check)
    {
        Check = check;
        Title = new LocalizedString(l => l.T($"diag.id.{check.Id}"));
        Message = new LocalizedString(l =>
        {
            var args = check.Args.ToDictionary(a => a.Key, a => a.Value is long bytes && a.Key is "free" ? l.Bytes(bytes) : a.Value);
            if (check.Args.GetValueOrDefault("exe") is Sanova.Core.Game.ExecutableIdentity exe) args["version"] = Present.Executable(l, exe);
            return l.T($"diag.{check.Code}", args);
        });
    }

    public DiagnosticCheck Check { get; }

    public LocalizedString Title { get; }

    public LocalizedString Message { get; }

    public string? Detail => Check.Detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Check.Detail);

    public bool IsPass => Check.Status == CheckStatus.Pass;

    public bool IsWarning => Check.Status == CheckStatus.Warning;

    public bool IsFail => Check.Status == CheckStatus.Fail;

    public bool IsInfo => Check.Status is CheckStatus.Info or CheckStatus.Skipped;

    public Geometry? Icon => Icons.Get(Check.Status switch
    {
        CheckStatus.Pass => "CheckCircle",
        CheckStatus.Warning => "Warning",
        CheckStatus.Fail => "Error",
        _ => "InfoFilled",
    });
}

/// <summary>Self-checks, a shareable (redacted) report, and the recent log.</summary>
public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly DiagnosticsService _diagnostics;
    private readonly DesktopServices _desktop;
    private readonly DialogService _dialogs;
    private readonly NotificationService _notifications;
    private readonly LocalizationService _loc;
    private readonly AppPaths _paths;
    private readonly ILogger<DiagnosticsViewModel> _logger;
    private IReadOnlyList<DiagnosticCheck> _lastResults = [];

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private DateTimeOffset? _lastRun;

    [ObservableProperty]
    private string _recentLog = string.Empty;

    public DiagnosticsViewModel(AppState state, DiagnosticsService diagnostics, DesktopServices desktop, DialogService dialogs, NotificationService notifications,
        LocalizationService loc, AppPaths paths, ILogger<DiagnosticsViewModel> logger)
    {
        _state = state;
        _diagnostics = diagnostics;
        _desktop = desktop;
        _dialogs = dialogs;
        _notifications = notifications;
        _loc = loc;
        _paths = paths;
        _logger = logger;
        Summary = new LocalizedString(l => LastRun is null
            ? l.T("diagnostics.notRun")
            : l.T("diagnostics.summary",
                ("passed", l.Plural("diagnostics.passedCount", _lastResults.Count(c => c.Status == CheckStatus.Pass))),
                ("warnings", l.Plural("diagnostics.warningCount", _lastResults.Count(c => c.Status == CheckStatus.Warning))),
                ("failed", l.Plural("diagnostics.problemCount", _lastResults.Count(c => c.Status == CheckStatus.Fail))),
                ("when", l.Relative(LastRun.Value))));
    }

    public ObservableCollection<CheckItem> Checks { get; } = [];

    public LocalizedString Summary { get; }

    public bool HasResults => Checks.Count > 0;

    public string LogsFolder => _paths.LogsDirectory;

    partial void OnLastRunChanged(DateTimeOffset? value) => Summary.Refresh();

    public override async Task OnNavigatedToAsync()
    {
        RefreshLog();
        if (Checks.Count == 0) await RunAsync().ConfigureAwait(true);
    }

    private DiagnosticContext Context() => new(_state.Settings, AppInfo.Version, AppInfo.InformationalVersion, _state.Distribution, _state.SettingsOutcome,
        _state.LatestManifest?.Manifest.Compatibility, _state.LatestManifest?.Manifest.BaseGame);

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        try
        {
            _lastResults = await Task.Run(() => _diagnostics.RunAsync(Context(), CancellationToken.None)).ConfigureAwait(true);
            Checks.Clear();
            foreach (var check in _lastResults.OrderBy(c => c.Status switch { CheckStatus.Fail => 0, CheckStatus.Warning => 1, _ => 2 })) Checks.Add(new CheckItem(check));
            LastRun = DateTimeOffset.UtcNow;
            OnPropertyChanged(nameof(HasResults));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostics failed");
            await _dialogs.ShowErrorAsync(_loc.T("diagnostics.failed"), ex).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
            RefreshLog();
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_lastResults.Count == 0) await RunAsync().ConfigureAwait(true);
        var suggested = $"sanova-diagnostics-{DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.zip";
        var path = await _desktop.SaveFileAsync(_loc.T("diagnostics.exportTitle"), suggested, _loc.T("diagnostics.zipFilter"), "*.zip").ConfigureAwait(true);
        if (path is null) return;
        try
        {
            await DiagnosticReport.CreateAsync(path, _lastResults, AppInfo.Version, AppInfo.InformationalVersion, _paths.LogsDirectory, _paths.SettingsFile,
                new Redactor(), CancellationToken.None).ConfigureAwait(true);
            _notifications.Show(Tone.Success, _loc.T("diagnostics.exported"), _loc.T("diagnostics.exportedBody"), actionText: _loc.T("diagnostics.showFile"),
                action: () => ShellIntegration.RevealFile(path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Report export failed");
            await _dialogs.ShowErrorAsync(_loc.T("diagnostics.exportFailed"), ex).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CopySummaryAsync()
    {
        if (_lastResults.Count == 0) await RunAsync().ConfigureAwait(true);
        await _desktop.CopyTextAsync(DiagnosticReport.BuildSummary(_lastResults, AppInfo.Version, AppInfo.InformationalVersion, new Redactor())).ConfigureAwait(true);
        _notifications.Show(Tone.Success, _loc.T("diagnostics.copied"), null);
    }

    [RelayCommand]
    private void OpenLogs() => ShellIntegration.OpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private void RefreshLog() => RecentLog = string.Join(Environment.NewLine, LoggingSetup.Recent.Snapshot().TakeLast(120));
}
