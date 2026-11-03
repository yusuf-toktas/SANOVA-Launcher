using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sanova.Core;
using Sanova.Core.Content;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Core.IO;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

public enum SetupStep
{
    Welcome,
    Game,
    Compatibility,
    Location,
    Components,
    Review,
}

public sealed partial class StepItem : ObservableObject
{
    public StepItem(SetupStep step, int number)
    {
        Step = step;
        Number = number;
        Title = new LocalizedString($"setup.steps.{step.ToString().ToLowerInvariant()}");
    }

    public SetupStep Step { get; }

    public int Number { get; }

    public LocalizedString Title { get; }

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isDone;
}

public sealed partial class LanguageOption : ObservableObject
{
    public LanguageOption(string code, LocalizedString name, string? nativeName)
    {
        Code = code;
        Name = name;
        NativeName = nativeName;
    }

    public string Code { get; }

    public LocalizedString Name { get; }

    public string? NativeName { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class CandidateItem : ObservableObject
{
    public CandidateItem(GameCandidate candidate)
    {
        Candidate = candidate;
        Source = new LocalizedString(l => Present.Source(l, candidate.Source));
    }

    public GameCandidate Candidate { get; }

    public string Directory => Candidate.Directory;

    public LocalizedString Source { get; }

    public bool IsDefinitive => Candidate.IsDefinitiveEdition;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>First-run setup: language → game folder → compatibility → install folder → mods → review.</summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly LocalizationService _loc;
    private readonly InstallationService _installation;
    private readonly DesktopServices _desktop;
    private readonly LauncherActions _actions;
    private readonly ILogger<SetupViewModel> _logger;
    private HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _checkCancellation;

    [ObservableProperty]
    private SetupStep _step;

    [ObservableProperty]
    private CandidateItem? _selectedCandidate;

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private CompatibilityReport? _report;

    [ObservableProperty]
    private string _installPath = string.Empty;

    [ObservableProperty]
    private PathValidation? _pathValidation;

    [ObservableProperty]
    private bool _isValidatingPath;

    public SetupViewModel(AppState state, LocalizationService loc, InstallationService installation, DesktopServices desktop, LauncherActions actions,
        ILogger<SetupViewModel> logger)
    {
        _state = state;
        _loc = loc;
        _installation = installation;
        _desktop = desktop;
        _actions = actions;
        _logger = logger;

        Steps = new ObservableCollection<StepItem>(Enum.GetValues<SetupStep>().Select((s, i) => new StepItem(s, i + 1)));
        VerdictTitle = new LocalizedString(l => Report is null ? string.Empty : l.T(Report.Verdict switch
        {
            CompatibilityVerdict.Compatible => "setup.compat.ok.title",
            CompatibilityVerdict.CompatibleWithWarnings => "setup.compat.warnings.title",
            _ => Report.CanProceed ? "setup.compat.override.title" : "setup.compat.blocked.title",
        }));
        VerdictBody = new LocalizedString(l => Report is null ? string.Empty : l.T(Report.Verdict switch
        {
            CompatibilityVerdict.Compatible => "setup.compat.ok.body",
            CompatibilityVerdict.CompatibleWithWarnings => "setup.compat.warnings.body",
            _ => Report.CanProceed ? "setup.compat.override.body" : "setup.compat.blocked.body",
        }));
        ExecutableText = new LocalizedString(l => Report is null ? string.Empty : Present.Executable(l, Report.Executable));
        PathMessage = new LocalizedString(PathMessageText);
        SpaceText = new LocalizedString(SpaceTextValue);
        ReviewGame = new LocalizedString(l => SelectedCandidate?.Directory ?? string.Empty);
        ReviewExecutable = new LocalizedString(l => Report is null ? string.Empty : Present.Executable(l, Report.Executable));
        ReviewComponents = new LocalizedString(l => Manifest is { } m
            ? l.Plural("setup.review.componentsValue", _selection.Count)
            : l.T("setup.review.noContent"));
        ReviewDownload = new LocalizedString(l => Manifest is { } m
            ? l.Bytes(m.Manifest.FilesOf(_selection).Sum(f => f.PackedSize ?? f.Size))
            : "—");
        ReviewSpace = new LocalizedString(l => l.Bytes(RequiredBytes()));
        ContentNotice = new LocalizedString(ContentNoticeText);
        NextLabel = new LocalizedString(l => l.T(Step == SetupStep.Welcome ? "setup.getStarted" : "common.continue"));
    }

    public LocalizedString NextLabel { get; }

    public bool CanNext => Step switch
    {
        SetupStep.Welcome => true,
        SetupStep.Game => SelectedCandidate is not null && !IsDetecting,
        SetupStep.Compatibility => CanProceedCompatibility,
        SetupStep.Location => IsPathValid,
        SetupStep.Components => true,
        _ => false,
    };

    public bool CanInstallNow => IsReview && HasContent;

    public bool CanFinishWithoutContent => IsReview && !HasContent;

    private void NotifyNavigation()
    {
        foreach (var name in new[] { nameof(CanNext), nameof(CanInstallNow), nameof(CanFinishWithoutContent) }) OnPropertyChanged(name);
        NextLabel.Refresh();
    }

    partial void OnIsDetectingChanged(bool value) => NotifyNavigation();

    public event EventHandler? Closed;

    public ObservableCollection<StepItem> Steps { get; }

    public ObservableCollection<LanguageOption> Languages { get; } = [];

    public ObservableCollection<CandidateItem> Candidates { get; } = [];

    public ObservableCollection<CandidateItem> ExistingInstallations { get; } = [];

    public ObservableCollection<FindingItem> Findings { get; } = [];

    public ObservableCollection<ComponentItemViewModel> OptionalComponents { get; } = [];

    public LocalizedString VerdictTitle { get; }

    public LocalizedString VerdictBody { get; }

    public LocalizedString ExecutableText { get; }

    public LocalizedString PathMessage { get; }

    public LocalizedString SpaceText { get; }

    public LocalizedString ReviewGame { get; }

    public LocalizedString ReviewExecutable { get; }

    public LocalizedString ReviewComponents { get; }

    public LocalizedString ReviewDownload { get; }

    public LocalizedString ReviewSpace { get; }

    public LocalizedString ContentNotice { get; }

    private Sanova.Core.Updates.ResolvedManifest? Manifest => _state.LatestManifest;

    public bool HasContent => Manifest is not null;

    public bool HasCandidates => Candidates.Count > 0;

    public bool HasExistingInstallations => ExistingInstallations.Count > 0;

    public bool IsWelcome => Step == SetupStep.Welcome;
    public bool IsGame => Step == SetupStep.Game;
    public bool IsCompatibility => Step == SetupStep.Compatibility;
    public bool IsLocation => Step == SetupStep.Location;
    public bool IsComponents => Step == SetupStep.Components;
    public bool IsReview => Step == SetupStep.Review;

    public bool CanGoBack => Step != SetupStep.Welcome;

    public bool CanProceedCompatibility => Report?.CanProceed == true && !IsChecking;

    public bool IsBlocked => Report is { CanProceed: false } && !IsChecking;

    public bool IsPathValid => PathValidation?.IsValid == true && !IsValidatingPath;

    public bool IsPathInvalid => PathValidation is { IsValid: false } && !IsValidatingPath;

    public void Start()
    {
        Languages.Clear();
        Languages.Add(new LanguageOption("auto", new LocalizedString("setup.welcome.systemLanguage"), null));
        foreach (var language in _loc.Languages)
        {
            Languages.Add(new LanguageOption(language.Code, new LocalizedString(_ => language.NativeName), language.EnglishName));
        }
        foreach (var option in Languages) option.IsSelected = string.Equals(option.Code, _state.Settings.Language, StringComparison.OrdinalIgnoreCase);
        Step = SetupStep.Welcome;
        UpdateSteps();
        _ = DetectAsync();
    }

    partial void OnStepChanged(SetupStep value)
    {
        UpdateSteps();
        foreach (var name in new[] { nameof(IsWelcome), nameof(IsGame), nameof(IsCompatibility), nameof(IsLocation), nameof(IsComponents), nameof(IsReview), nameof(CanGoBack), nameof(HasContent) })
        {
            OnPropertyChanged(name);
        }
        foreach (var text in new[] { ReviewGame, ReviewExecutable, ReviewComponents, ReviewDownload, ReviewSpace, ContentNotice, SpaceText }) text.Refresh();
        NotifyNavigation();
    }

    partial void OnReportChanged(CompatibilityReport? value)
    {
        Findings.Clear();
        foreach (var finding in value?.Findings.OrderByDescending(f => f.Severity) ?? Enumerable.Empty<CompatibilityFinding>()) Findings.Add(new FindingItem(finding));
        VerdictTitle.Refresh();
        VerdictBody.Refresh();
        ExecutableText.Refresh();
        OnPropertyChanged(nameof(CanProceedCompatibility));
        OnPropertyChanged(nameof(IsBlocked));
        NotifyNavigation();
    }

    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanProceedCompatibility));
        OnPropertyChanged(nameof(IsBlocked));
        NotifyNavigation();
    }

    partial void OnPathValidationChanged(PathValidation? value) => RefreshPathState();

    partial void OnIsValidatingPathChanged(bool value) => RefreshPathState();

    private void RefreshPathState()
    {
        PathMessage.Refresh();
        SpaceText.Refresh();
        OnPropertyChanged(nameof(IsPathValid));
        OnPropertyChanged(nameof(IsPathInvalid));
        NotifyNavigation();
    }

    partial void OnInstallPathChanged(string value) => _ = ValidatePathAsync();

    partial void OnSelectedCandidateChanged(CandidateItem? value)
    {
        foreach (var candidate in Candidates) candidate.IsSelected = ReferenceEquals(candidate, value);
        NotifyNavigation();
    }

    private void UpdateSteps()
    {
        foreach (var item in Steps)
        {
            item.IsCurrent = item.Step == Step;
            item.IsDone = item.Step < Step;
        }
    }

    [RelayCommand]
    private void SelectLanguage(LanguageOption option)
    {
        foreach (var language in Languages) language.IsSelected = ReferenceEquals(language, option);
        _state.UpdateSettings(s => s.Language = option.Code);
    }

    [RelayCommand]
    private void SelectCandidate(CandidateItem item) => SelectedCandidate = item;

    [RelayCommand]
    private async Task DetectAsync()
    {
        IsDetecting = true;
        try
        {
            await _state.DetectGamesAsync().ConfigureAwait(true);
            Candidates.Clear();
            ExistingInstallations.Clear();
            foreach (var candidate in _state.Candidates)
            {
                (candidate.IsSanovaInstallation ? ExistingInstallations : Candidates).Add(new CandidateItem(candidate));
            }
            SelectedCandidate = Candidates.FirstOrDefault(c => string.Equals(c.Directory, _state.Settings.OriginalGamePath, StringComparison.OrdinalIgnoreCase))
                                ?? Candidates.FirstOrDefault(c => !c.IsDefinitive);
            OnPropertyChanged(nameof(HasCandidates));
            OnPropertyChanged(nameof(HasExistingInstallations));
        }
        finally
        {
            IsDetecting = false;
        }
    }

    [RelayCommand]
    private async Task BrowseGameAsync()
    {
        var folder = await _desktop.PickFolderAsync(_loc.T("setup.game.pickTitle"), SelectedCandidate?.Directory).ConfigureAwait(true);
        if (folder is null) return;
        var candidate = _state.Inspect(folder);
        if (candidate is null)
        {
            // Still let the compatibility step explain what is wrong with the folder.
            candidate = new GameCandidate(SafePath.NormalizeRoot(folder), GameDetectionSource.Manual, null, InstallStateStore.Exists(folder), false);
        }
        if (candidate.IsSanovaInstallation)
        {
            var existing = new CandidateItem(candidate);
            ExistingInstallations.Add(existing);
            OnPropertyChanged(nameof(HasExistingInstallations));
            return;
        }
        var item = Candidates.FirstOrDefault(c => string.Equals(c.Directory, candidate.Directory, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new CandidateItem(candidate with { Source = GameDetectionSource.Manual });
            Candidates.Insert(0, item);
            OnPropertyChanged(nameof(HasCandidates));
        }
        SelectedCandidate = item;
    }

    /// <summary>Adopts an existing SANOVA installation (e.g. after reinstalling the launcher).</summary>
    [RelayCommand]
    private void UseExisting(CandidateItem item)
    {
        string? original = null;
        try
        {
            original = InstallStateStore.Load(item.Directory)?.OriginalGamePath;
        }
        catch (SanovaException ex)
        {
            _logger.LogWarning(ex, "Existing installation state unreadable");
        }
        _state.UpdateSettings(s =>
        {
            s.InstallPath = item.Directory;
            if (original is not null && Directory.Exists(original)) s.OriginalGamePath = original;
            s.SetupCompleted = true;
        });
        Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case SetupStep.Welcome:
                Step = SetupStep.Game;
                break;
            case SetupStep.Game when SelectedCandidate is not null:
                Step = SetupStep.Compatibility;
                await CheckAsync().ConfigureAwait(true);
                break;
            case SetupStep.Compatibility when CanProceedCompatibility:
                if (string.IsNullOrWhiteSpace(InstallPath)) InstallPath = SuggestInstallPath();
                Step = SetupStep.Location;
                await ValidatePathAsync().ConfigureAwait(true);
                break;
            case SetupStep.Location when IsPathValid:
                PrepareComponents();
                Step = OptionalComponents.Count > 0 ? SetupStep.Components : SetupStep.Review;
                break;
            case SetupStep.Components:
                Step = SetupStep.Review;
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        Step = Step switch
        {
            SetupStep.Review when OptionalComponents.Count > 0 => SetupStep.Components,
            SetupStep.Review => SetupStep.Location,
            SetupStep.Components => SetupStep.Location,
            SetupStep.Location => SetupStep.Compatibility,
            SetupStep.Compatibility => SetupStep.Game,
            _ => SetupStep.Welcome,
        };
    }

    [RelayCommand]
    private async Task RecheckAsync() => await CheckAsync().ConfigureAwait(true);

    private async Task CheckAsync()
    {
        if (SelectedCandidate is null) return;
        _checkCancellation?.Cancel();
        _checkCancellation = new CancellationTokenSource();
        IsChecking = true;
        Report = null;
        try
        {
            Report = await _state.CheckCompatibilityAsync(SelectedCandidate.Directory, _checkCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Compatibility check failed");
        }
        finally
        {
            IsChecking = false;
        }
    }

    private string SuggestInstallPath()
    {
        var candidates = new List<string>();
        if (SelectedCandidate?.Directory is { } game && Path.GetPathRoot(game) is { } root) candidates.Add(Path.Combine(root, "Games", "SANOVA"));
        candidates.Add(@"C:\Games\SANOVA");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "SANOVA"));
        foreach (var path in candidates)
        {
            var validation = _installation.ValidateInstallPath(path, SelectedCandidate?.Directory, 0);
            if (validation.IsValid || validation.ErrorCode == ErrorCodes.DiskSpace) return path;
        }
        return candidates[0];
    }

    private long RequiredBytes()
    {
        if (SelectedCandidate is null) return 0;
        var reference = Report?.Reference;
        if (Manifest is { } manifest)
        {
            return InstallationService.EstimateRequiredBytes(SelectedCandidate.Directory, reference, manifest, _selection);
        }
        return reference?.TotalSize ?? 0;
    }

    private async Task ValidatePathAsync()
    {
        if (Step != SetupStep.Location && Step != SetupStep.Review) return;
        var path = InstallPath;
        var game = SelectedCandidate?.Directory;
        if (Manifest is { } manifest && _selection.Count == 0)
        {
            _selection = ComponentResolver.InitialSelection(manifest.Manifest, _state.Settings.ComponentChoices);
        }
        IsValidatingPath = true;
        try
        {
            var required = await Task.Run(RequiredBytes).ConfigureAwait(true);
            var result = await Task.Run(() => _installation.ValidateInstallPath(path, game, required)).ConfigureAwait(true);
            if (string.Equals(path, InstallPath, StringComparison.Ordinal)) PathValidation = result;
        }
        finally
        {
            IsValidatingPath = false;
        }
    }

    [RelayCommand]
    private async Task BrowseLocationAsync()
    {
        var folder = await _desktop.PickFolderAsync(_loc.T("setup.location.pickTitle"), null).ConfigureAwait(true);
        if (folder is null) return;
        // Picking a parent folder is common: install into a SANOVA subfolder unless it is empty.
        var target = Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() && !InstallStateStore.Exists(folder)
            ? Path.Combine(folder, "SANOVA")
            : folder;
        InstallPath = target;
    }

    private void PrepareComponents()
    {
        OptionalComponents.Clear();
        if (Manifest is not { } manifest) return;
        _selection = ComponentResolver.InitialSelection(manifest.Manifest, _state.Settings.ComponentChoices);
        foreach (var component in manifest.Manifest.Components.Where(c => !c.Required).OrderBy(c => c.Order).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            var item = new ComponentItemViewModel(component, manifest.Manifest, _selection.Contains(component.Id)) { Toggled = OnComponentToggled };
            OptionalComponents.Add(item);
        }
    }

    private void OnComponentToggled(ComponentItemViewModel item, bool enabled)
    {
        if (Manifest is not { } manifest) return;
        ComponentResolver.Toggle(manifest.Manifest, _selection, item.Id, enabled, out _);
        foreach (var other in OptionalComponents) other.Sync(_selection.Contains(other.Id));
        ReviewComponents.Refresh();
        ReviewDownload.Refresh();
        ReviewSpace.Refresh();
    }

    private void SaveChoices()
    {
        var game = SelectedCandidate?.Directory;
        var path = InstallPath;
        var choices = Manifest is { } manifest
            ? manifest.Manifest.Components.Where(c => !c.Required).ToDictionary(c => c.Id, c => _selection.Contains(c.Id), StringComparer.OrdinalIgnoreCase)
            : _state.Settings.ComponentChoices;
        _state.UpdateSettings(s =>
        {
            s.OriginalGamePath = game;
            s.InstallPath = SafePath.NormalizeRoot(path);
            s.ComponentChoices = new Dictionary<string, bool>(choices, StringComparer.OrdinalIgnoreCase);
            s.SetupCompleted = true;
        });
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (!IsPathValid && Step == SetupStep.Review)
        {
            await ValidatePathAsync().ConfigureAwait(true);
            if (!IsPathValid) return;
        }
        SaveChoices();
        await _state.RefreshCompatibilityAsync().ConfigureAwait(true);
        Closed?.Invoke(this, EventArgs.Empty);
        if (HasContent) await _actions.InstallAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void FinishLater()
    {
        if (SelectedCandidate is not null && Report?.CanProceed == true && IsPathValid)
        {
            SaveChoices();
            _ = _state.RefreshCompatibilityAsync();
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private string PathMessageText(LocalizationService l)
    {
        if (IsValidatingPath || PathValidation is null) return string.Empty;
        if (PathValidation.IsValid)
        {
            return l.T(PathValidation.IsExistingInstallation ? "setup.location.existing" : "setup.location.ok");
        }
        var args = PathValidation.Args.ToDictionary(a => a.Key, a => a.Value is long bytes ? l.Bytes(bytes) : a.Value);
        return l.T($"error.{PathValidation.ErrorCode}", args);
    }

    private string SpaceTextValue(LocalizationService l)
    {
        var required = RequiredBytes();
        var free = PathValidation?.FreeBytes ?? FileSystem.GetAvailableFreeSpace(string.IsNullOrWhiteSpace(InstallPath) ? @"C:\" : InstallPath);
        return free is { } available
            ? l.T("setup.location.space", ("required", l.Bytes(required)), ("free", l.Bytes(available)))
            : l.T("setup.location.spaceUnknown", ("required", l.Bytes(required)));
    }

    private string ContentNoticeText(LocalizationService l) => _state.ContentStatus switch
    {
        ContentStatus.Available when HasContent => string.Empty,
        ContentStatus.NotConfigured => l.T("setup.review.notConfigured"),
        ContentStatus.Offline => l.T("setup.review.offline"),
        _ when HasContent => string.Empty,
        _ => l.T("setup.review.unavailable"),
    };
}
