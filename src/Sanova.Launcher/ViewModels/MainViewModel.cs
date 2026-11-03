using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sanova.Core.Installation;
using Sanova.Core.Settings;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

/// <summary>The window shell: navigation, the first-run setup overlay, dialogs and notifications.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly AppState _state;
    private readonly LocalizationService _loc;
    private readonly LauncherUpdater _launcherUpdater;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private NavItem? _selectedPrimary;

    [ObservableProperty]
    private NavItem? _selectedSecondary;

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    [ObservableProperty]
    private SetupViewModel? _setup;

    public MainViewModel(IServiceProvider services, AppState state, LocalizationService loc, OperationRunner operations, DialogService dialogs,
        NotificationService notifications, LauncherUpdater launcherUpdater, ILogger<MainViewModel> logger)
    {
        _services = services;
        _state = state;
        _loc = loc;
        _launcherUpdater = launcherUpdater;
        _logger = logger;
        Operations = operations;
        Dialogs = dialogs;
        Toasts = notifications.Toasts;

        PrimaryNav =
        [
            new NavItem("home", "Home", services.GetRequiredService<HomeViewModel>()),
            new NavItem("game", "Games", services.GetRequiredService<GameViewModel>()),
            new NavItem("mods", "Mods", services.GetRequiredService<ModsViewModel>()),
            new NavItem("activity", "Download", services.GetRequiredService<ActivityViewModel>()),
            new NavItem("updates", "Sync", services.GetRequiredService<UpdatesViewModel>()),
        ];
        SecondaryNav =
        [
            new NavItem("settings", "Settings", services.GetRequiredService<SettingsViewModel>()),
            new NavItem("diagnostics", "Diagnostics", services.GetRequiredService<DiagnosticsViewModel>()),
            new NavItem("about", "Info", services.GetRequiredService<AboutViewModel>()),
        ];
        _selectedPrimary = PrimaryNav[0];
        _currentPage = PrimaryNav[0].Page;

        VersionText = new LocalizedString(l => l.T("shell.version", ("version", AppInfo.Version)));
        ActivityTitle = new LocalizedString(_ => string.Empty);
        _state.PropertyChanged += OnStateChanged;
        _launcherUpdater.PropertyChanged += (_, _) => UpdateBadges();
        Operations.PropertyChanged += OnOperationsChanged;
        Dialogs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DialogService.Current)) OnPropertyChanged(nameof(CanInteractWithPages));
        };
        SingleInstance.ActivationRequested += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => _services.GetRequiredService<DesktopServices>().ActivateMainWindow());
    }

    public IReadOnlyList<NavItem> PrimaryNav { get; }

    public IReadOnlyList<NavItem> SecondaryNav { get; }

    public OperationRunner Operations { get; }

    public DialogService Dialogs { get; }

    public ObservableCollection<ToastViewModel> Toasts { get; }

    public LocalizedString VersionText { get; }

    public LocalizedString ActivityTitle { get; }

    public bool IsSetupVisible => Setup is not null;

    public bool CanInteractWithPages => !IsSetupVisible && Dialogs.Current is null;

    partial void OnSetupChanged(SetupViewModel? value)
    {
        OnPropertyChanged(nameof(IsSetupVisible));
        OnPropertyChanged(nameof(CanInteractWithPages));
    }

    partial void OnSelectedPrimaryChanged(NavItem? value)
    {
        if (value is null) return;
        SelectedSecondary = null;
        Show(value);
    }

    partial void OnSelectedSecondaryChanged(NavItem? value)
    {
        if (value is null) return;
        SelectedPrimary = null;
        Show(value);
    }

    private void Show(NavItem item)
    {
        CurrentPage = item.Page;
        _ = item.Page.OnNavigatedToAsync();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _state.InitializeAsync().ConfigureAwait(true);

            var recovery = _state.RecoverInterruptedChanges();
            if (recovery == RecoveryOutcome.RolledBack)
            {
                _services.GetRequiredService<NotificationService>().Show(Tone.Warning, _loc.T("toast.recovered.title"), _loc.T("toast.recovered.body"));
            }
            else if (recovery == RecoveryOutcome.Failed)
            {
                await Dialogs.AlertAsync(_loc.T("dialog.recoveryFailed.title"), _loc.T("dialog.recoveryFailed.body"), Tone.Warning).ConfigureAwait(true);
            }

            if (_state.SettingsOutcome == SettingsLoadOutcome.RecoveredFromCorruption)
            {
                _services.GetRequiredService<NotificationService>().Show(Tone.Warning, _loc.T("toast.settingsReset.title"), _loc.T("toast.settingsReset.body"));
            }

            if (!_state.Settings.SetupCompleted)
            {
                OpenSetup();
            }

            if (_state.Settings.CheckContentUpdatesOnStartup)
            {
                await _state.CheckContentAsync().ConfigureAwait(true);
            }
            if (_state.Settings.CheckLauncherUpdatesOnStartup)
            {
                await _launcherUpdater.CheckAsync().ConfigureAwait(true);
            }
            UpdateBadges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup initialization failed");
            await Dialogs.ShowErrorAsync(_loc.T("dialog.startupFailed.title"), ex).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public void Navigate(string key)
    {
        if (PrimaryNav.FirstOrDefault(n => n.Key == key) is { } primary) SelectedPrimary = primary;
        else if (SecondaryNav.FirstOrDefault(n => n.Key == key) is { } secondary) SelectedSecondary = secondary;
    }

    [RelayCommand]
    public void OpenSetup()
    {
        var setup = _services.GetRequiredService<SetupViewModel>();
        setup.Closed += OnSetupClosed;
        setup.Start();
        Setup = setup;
    }

    private void OnSetupClosed(object? sender, EventArgs e)
    {
        if (Setup is not null) Setup.Closed -= OnSetupClosed;
        Setup = null;
        Navigate("home");
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => UpdateBadges();

    private void OnOperationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationRunner.IsRunning)) ActivityTitle.Refresh();
    }

    private void UpdateBadges()
    {
        var updates = PrimaryNav.First(n => n.Key == "updates");
        updates.HasBadge = _state.IsUpdateAvailable || _launcherUpdater.State is LauncherUpdateState.Available or LauncherUpdateState.ReadyToRestart;
        var game = PrimaryNav.First(n => n.Key == "game");
        game.HasBadge = _state.HasGame && _state.Compatibility is { CanProceed: false } || _state.NeedsRepair;
    }
}
