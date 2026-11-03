using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sanova.Core.Content;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

/// <summary>
/// Optional SANOVA components. Changes are collected first (with dependencies resolved) and
/// applied in one transaction, or remembered for installation when SANOVA is not installed yet.
/// </summary>
public sealed partial class ModsViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly LauncherActions _actions;
    private readonly NotificationService _notifications;
    private readonly OperationRunner _runner;
    private readonly LocalizationService _loc;
    private HashSet<string> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private ContentManifest? _manifest;

    [ObservableProperty]
    private bool _hasPendingChanges;

    public ModsViewModel(AppState state, LauncherActions actions, NotificationService notifications, OperationRunner runner, LocalizationService loc)
    {
        _state = state;
        _actions = actions;
        _notifications = notifications;
        _runner = runner;
        _loc = loc;
        PendingText = new LocalizedString(PendingTextValue);
        Summary = new LocalizedString(l => _manifest is null
            ? string.Empty
            : l.T("mods.summary", ("enabled", _selection.Count), ("total", _manifest.Components.Count), ("version", _manifest.ContentVersion)));
        EmptyTitle = new LocalizedString(l => l.T(_state.ContentStatus == ContentStatus.NotConfigured ? "mods.empty.notConfigured.title" : "mods.empty.title"));
        EmptyBody = new LocalizedString(l => l.T(_state.ContentStatus == ContentStatus.NotConfigured ? "mods.empty.notConfigured.body" : "mods.empty.body"));
        _state.PropertyChanged += OnStateChanged;
        _runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationRunner.IsRunning)) OnPropertyChanged(nameof(CanApply));
        };
        Rebuild();
    }

    public ObservableCollection<ComponentGroup> Groups { get; } = [];

    public LocalizedString PendingText { get; }

    public LocalizedString Summary { get; }

    public LocalizedString EmptyTitle { get; }

    public LocalizedString EmptyBody { get; }

    public bool IsEmpty => _manifest is null;

    public bool HasComponents => _manifest is not null;

    public bool IsInstalled => _state.IsInstalled;

    public bool CanApply => HasPendingChanges && !_runner.IsRunning;

    partial void OnHasPendingChangesChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    public override Task OnNavigatedToAsync()
    {
        if (!HasPendingChanges) Rebuild();
        return Task.CompletedTask;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.ComponentsManifest) or nameof(AppState.Install) or nameof(AppState.ContentStatus))
        {
            if (!HasPendingChanges) Rebuild();
            EmptyTitle.Refresh();
            EmptyBody.Refresh();
        }
    }

    private void Rebuild()
    {
        var resolved = _state.ComponentsManifest;
        _manifest = resolved?.Manifest;
        Groups.Clear();
        if (_manifest is not null)
        {
            _baseline = _state.IsInstalled
                ? new HashSet<string>(_state.Install!.EnabledComponents, StringComparer.OrdinalIgnoreCase)
                : ComponentResolver.InitialSelection(_manifest, _state.Settings.ComponentChoices);
            _selection = new HashSet<string>(_baseline, StringComparer.OrdinalIgnoreCase);

            var categories = _manifest.Categories.ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);
            foreach (var group in _manifest.Components
                         .GroupBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => categories.TryGetValue(g.Key, out var category) ? category.Order : int.MaxValue)
                         .ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var key = group.Key;
                var title = new LocalizedString(l => categories.TryGetValue(key, out var category) && category.Name is { Count: > 0 } name
                    ? l.Resolve(name)
                    : l.Localizer.HasKey($"mods.category.{key}") ? l.T($"mods.category.{key}") : key);
                var items = group
                    .OrderBy(c => c.Required ? 0 : 1).ThenBy(c => c.Order).ThenBy(c => c.Id, StringComparer.Ordinal)
                    .Select(c => new ComponentItemViewModel(c, _manifest, _selection.Contains(c.Id)) { Toggled = OnToggled })
                    .ToList();
                Groups.Add(new ComponentGroup(key, title, items));
            }
        }
        HasPendingChanges = false;
        PendingText.Refresh();
        Summary.Refresh();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasComponents));
        OnPropertyChanged(nameof(IsInstalled));
    }

    private void OnToggled(ComponentItemViewModel item, bool enabled)
    {
        if (_manifest is null) return;
        ComponentResolver.Toggle(_manifest, _selection, item.Id, enabled, out var changes);
        foreach (var other in Groups.SelectMany(g => g.Items)) other.Sync(_selection.Contains(other.Id));

        var cascaded = changes.Where(c => c.Reason != SelectionChangeReason.Requested).ToList();
        if (cascaded.Count > 0)
        {
            var names = string.Join(", ", cascaded.Select(c => _manifest.FindComponent(c.ComponentId)).OfType<ComponentSpec>().Select(c => _loc.Resolve(c.Name)));
            _notifications.Show(Tone.Info, _loc.T(enabled ? "mods.cascade.enabled" : "mods.cascade.disabled"), names);
        }

        HasPendingChanges = !_selection.SetEquals(_baseline);
        PendingText.Refresh();
        Summary.Refresh();
    }

    private string PendingTextValue(LocalizationService l)
    {
        var added = _selection.Except(_baseline).Count();
        var removed = _baseline.Except(_selection).Count();
        return l.T(IsInstalled ? "mods.pending" : "mods.pendingNotInstalled", ("added", added), ("removed", removed));
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_manifest is null) return;
        if (!_state.IsInstalled)
        {
            var choices = _manifest.Components.Where(c => !c.Required).ToDictionary(c => c.Id, c => _selection.Contains(c.Id), StringComparer.OrdinalIgnoreCase);
            _state.UpdateSettings(s => s.ComponentChoices = choices);
            _notifications.Show(Tone.Success, _loc.T("mods.saved"), null);
            Rebuild();
            return;
        }
        var selection = new HashSet<string>(_selection, StringComparer.OrdinalIgnoreCase);
        HasPendingChanges = false;
        await _actions.ApplyComponentsAsync(selection).ConfigureAwait(true);
        Rebuild();
    }

    [RelayCommand]
    private void Discard() => Rebuild();
}
