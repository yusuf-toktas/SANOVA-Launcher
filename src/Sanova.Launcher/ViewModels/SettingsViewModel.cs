using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sanova.Core.Settings;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

/// <summary>Settings. Every change is validated and saved immediately.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly AppState _state;
    private readonly DesktopServices _desktop;
    private readonly DialogService _dialogs;
    private readonly LocalizationService _loc;
    private readonly AppPaths _paths;
    private readonly DispatcherTimer _argumentsDebounce;
    private bool _loading;

    [ObservableProperty]
    private string _launchArguments = string.Empty;

    [ObservableProperty]
    private ChoiceOption? _sourceKind;

    [ObservableProperty]
    private string _sourceValue = string.Empty;

    public SettingsViewModel(IServiceProvider services, AppState state, DesktopServices desktop, DialogService dialogs, LocalizationService loc, AppPaths paths)
    {
        _services = services;
        _state = state;
        _desktop = desktop;
        _dialogs = dialogs;
        _loc = loc;
        _paths = paths;

        Languages = [new ChoiceOption("auto", new LocalizedString("settings.language.auto")), .. loc.Languages.Select(l => new ChoiceOption(l.Code, new LocalizedString(_ => l.NativeName)))];
        Themes =
        [
            new ChoiceOption(nameof(AppTheme.Dark), new LocalizedString("settings.theme.dark")),
            new ChoiceOption(nameof(AppTheme.Light), new LocalizedString("settings.theme.light")),
            new ChoiceOption(nameof(AppTheme.System), new LocalizedString("settings.theme.system")),
        ];
        AfterLaunchOptions =
        [
            new ChoiceOption(nameof(AfterLaunchBehavior.Minimize), new LocalizedString("settings.afterLaunch.minimize")),
            new ChoiceOption(nameof(AfterLaunchBehavior.KeepOpen), new LocalizedString("settings.afterLaunch.keepOpen")),
            new ChoiceOption(nameof(AfterLaunchBehavior.Close), new LocalizedString("settings.afterLaunch.close")),
        ];
        ParallelOptions = Enumerable.Range(1, 6).Select(i => new ChoiceOption(i.ToString(CultureInfo.InvariantCulture), new LocalizedString(_ => i.ToString(CultureInfo.InvariantCulture)))).ToList();
        SpeedOptions = new[] { 0, 1, 2, 5, 10, 25, 50 }
            .Select(mb => new ChoiceOption((mb * 1024).ToString(CultureInfo.InvariantCulture),
                new LocalizedString(l => mb == 0 ? l.T("settings.speed.unlimited") : l.T("settings.speed.value", ("value", mb)))))
            .ToList();
        Channels =
        [
            new ChoiceOption("stable", new LocalizedString("settings.channel.stable")),
            new ChoiceOption("beta", new LocalizedString("settings.channel.beta")),
        ];
        SourceKinds =
        [
            new ChoiceOption(nameof(ContentSourceKind.Default), new LocalizedString("settings.source.default")),
            new ChoiceOption(nameof(ContentSourceKind.GitHub), new LocalizedString("settings.source.github")),
            new ChoiceOption(nameof(ContentSourceKind.ManifestUrl), new LocalizedString("settings.source.url")),
            new ChoiceOption(nameof(ContentSourceKind.LocalFolder), new LocalizedString("settings.source.local")),
        ];

        _argumentsDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _argumentsDebounce!.Stop();
            Save(s => s.LaunchArguments = LaunchArguments);
        });

        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.Settings)) Load();
        };
        Load();
    }

    public IReadOnlyList<ChoiceOption> Languages { get; }
    public IReadOnlyList<ChoiceOption> Themes { get; }
    public IReadOnlyList<ChoiceOption> AfterLaunchOptions { get; }
    public IReadOnlyList<ChoiceOption> ParallelOptions { get; }
    public IReadOnlyList<ChoiceOption> SpeedOptions { get; }
    public IReadOnlyList<ChoiceOption> Channels { get; }
    public IReadOnlyList<ChoiceOption> SourceKinds { get; }

    private LauncherSettings S => _state.Settings;

    public ChoiceOption? Language
    {
        get => Languages.FirstOrDefault(o => o.Value == S.Language) ?? Languages[0];
        set { if (value is not null) Save(s => s.Language = value.Value); }
    }

    public ChoiceOption? Theme
    {
        get => Themes.FirstOrDefault(o => o.Value == S.Theme.ToString());
        set { if (value is not null && Enum.TryParse<AppTheme>(value.Value, out var theme)) Save(s => s.Theme = theme); }
    }

    public ChoiceOption? AfterLaunch
    {
        get => AfterLaunchOptions.FirstOrDefault(o => o.Value == S.AfterLaunch.ToString());
        set { if (value is not null && Enum.TryParse<AfterLaunchBehavior>(value.Value, out var behavior)) Save(s => s.AfterLaunch = behavior); }
    }

    public ChoiceOption? Parallel
    {
        get => ParallelOptions.FirstOrDefault(o => o.Value == S.MaxParallelDownloads.ToString(CultureInfo.InvariantCulture));
        set { if (value is not null) Save(s => s.MaxParallelDownloads = int.Parse(value.Value, CultureInfo.InvariantCulture)); }
    }

    public ChoiceOption? Speed
    {
        get => SpeedOptions.FirstOrDefault(o => o.Value == S.DownloadSpeedLimitKBps.ToString(CultureInfo.InvariantCulture)) ?? SpeedOptions[0];
        set { if (value is not null) Save(s => s.DownloadSpeedLimitKBps = int.Parse(value.Value, CultureInfo.InvariantCulture)); }
    }

    public ChoiceOption? Channel
    {
        get => Channels.FirstOrDefault(o => o.Value == S.UpdateChannel);
        set { if (value is not null) Save(s => s.UpdateChannel = value.Value); }
    }

    public bool BackupSaves
    {
        get => S.BackupSavesBeforeChanges;
        set => Save(s => s.BackupSavesBeforeChanges = value);
    }

    public bool KeepCache
    {
        get => S.KeepDownloadCache;
        set => Save(s => s.KeepDownloadCache = value);
    }

    public bool CheckLauncherUpdates
    {
        get => S.CheckLauncherUpdatesOnStartup;
        set => Save(s => s.CheckLauncherUpdatesOnStartup = value);
    }

    public bool CheckContentUpdates
    {
        get => S.CheckContentUpdatesOnStartup;
        set => Save(s => s.CheckContentUpdatesOnStartup = value);
    }

    public bool VerboseLogging
    {
        get => S.VerboseLogging;
        set => Save(s => s.VerboseLogging = value);
    }

    public bool AllowUnsupported
    {
        get => S.AllowUnsupportedGameVersions;
        set
        {
            if (value == S.AllowUnsupportedGameVersions) return;
            if (!value)
            {
                Save(s => s.AllowUnsupportedGameVersions = false);
                return;
            }
            _ = ConfirmUnsupportedAsync();
        }
    }

    public bool DeveloperMode
    {
        get => S.DeveloperMode;
        set => Save(s => s.DeveloperMode = value);
    }

    public string? InstallPath => S.InstallPath;

    public string? GamePath => S.OriginalGamePath;

    public bool CanChangeInstallPath => !_state.IsInstalled && !_state.IsInstallIncomplete;

    public string DataFolder => _paths.Root;

    private async Task ConfirmUnsupportedAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(_loc.T("settings.unsupported.confirmTitle"), _loc.T("settings.unsupported.confirmBody"),
            _loc.T("settings.unsupported.confirm"), danger: true).ConfigureAwait(true);
        if (confirmed) Save(s => s.AllowUnsupportedGameVersions = true);
        else OnPropertyChanged(nameof(AllowUnsupported));
    }

    private void Save(Action<LauncherSettings> mutate)
    {
        if (_loading) return;
        try
        {
            _state.UpdateSettings(mutate);
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync(_loc.T("settings.saveFailed"), ex);
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            if (!string.Equals(LaunchArguments, S.LaunchArguments, StringComparison.Ordinal) && !_argumentsDebounce.IsEnabled) LaunchArguments = S.LaunchArguments;
            SourceKind = SourceKinds.FirstOrDefault(o => o.Value == S.ContentSource.Kind.ToString()) ?? SourceKinds[0];
            SourceValue = S.ContentSource.Value ?? string.Empty;
            foreach (var name in new[]
                     {
                         nameof(Language), nameof(Theme), nameof(AfterLaunch), nameof(Parallel), nameof(Speed), nameof(Channel), nameof(BackupSaves),
                         nameof(KeepCache), nameof(CheckLauncherUpdates), nameof(CheckContentUpdates), nameof(VerboseLogging), nameof(AllowUnsupported),
                         nameof(DeveloperMode), nameof(InstallPath), nameof(GamePath), nameof(CanChangeInstallPath),
                     })
            {
                OnPropertyChanged(name);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnLaunchArgumentsChanged(string value)
    {
        if (_loading) return;
        _argumentsDebounce.Stop();
        _argumentsDebounce.Start();
    }

    [RelayCommand]
    private async Task ChangeInstallPathAsync()
    {
        var folder = await _desktop.PickFolderAsync(_loc.T("setup.location.pickTitle"), InstallPath).ConfigureAwait(true);
        if (folder is not null) Save(s => s.InstallPath = folder);
    }

    [RelayCommand]
    private void ShowGamePage() => _services.GetRequiredService<MainViewModel>().Navigate("game");

    [RelayCommand]
    private void ApplySource()
    {
        if (SourceKind is null || !Enum.TryParse<ContentSourceKind>(SourceKind.Value, out var kind)) return;
        var value = SourceValue.Trim();
        Save(s => s.ContentSource = new ContentSourceOverride { Kind = kind, Value = value.Length == 0 ? null : value });
        _ = _state.CheckContentAsync();
    }

    [RelayCommand]
    private async Task BrowseSourceFolderAsync()
    {
        var folder = await _desktop.PickFolderAsync(_loc.T("settings.source.pickFolder"), SourceValue).ConfigureAwait(true);
        if (folder is not null)
        {
            SourceKind = SourceKinds.First(o => o.Value == nameof(ContentSourceKind.LocalFolder));
            SourceValue = folder;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => ShellIntegration.OpenFolder(_paths.Root);

    [RelayCommand]
    private void OpenLogsFolder() => ShellIntegration.OpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private void RunSetup() => _services.GetRequiredService<MainViewModel>().OpenSetup();
}
