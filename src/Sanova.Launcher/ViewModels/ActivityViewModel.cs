using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sanova.Core.Installation;
using Sanova.Core.IO;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

public sealed class HistoryItem
{
    public HistoryItem(OperationRecord record)
    {
        Record = record;
        Title = new LocalizedString(l => l.T($"op.{OperationRunner.Key(record.Kind)}.name"));
        When = new LocalizedString(l => l.Relative(record.FinishedAt));
        Outcome = new LocalizedString(l => record.Outcome switch
        {
            OperationOutcome.Succeeded => record.Note is { } note ? l.T("activity.succeededNote", ("note", note)) : l.T("activity.succeeded"),
            OperationOutcome.Cancelled => l.T("activity.cancelled"),
            _ => record.ErrorCode is { } code && l.Localizer.HasKey($"errorShort.{code}") ? l.T($"errorShort.{code}") : l.T("activity.failed"),
        });
    }

    public OperationRecord Record { get; }

    public LocalizedString Title { get; }

    public LocalizedString When { get; }

    public LocalizedString Outcome { get; }

    public bool IsSuccess => Record.Outcome == OperationOutcome.Succeeded;

    public bool IsFailure => Record.Outcome == OperationOutcome.Failed;

    public bool IsCancelled => Record.Outcome == OperationOutcome.Cancelled;

    public Geometry? Icon => Icons.Get(IsSuccess ? "CheckCircle" : IsFailure ? "Error" : "DismissCircle");
}

/// <summary>Downloads &amp; activity: the running operation, recent history and the download cache.</summary>
public sealed partial class ActivityViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly DialogService _dialogs;
    private readonly LocalizationService _loc;

    [ObservableProperty]
    private long _cacheBytes;

    public ActivityViewModel(AppState state, OperationRunner operations, DialogService dialogs, LocalizationService loc)
    {
        _state = state;
        _dialogs = dialogs;
        _loc = loc;
        Operations = operations;
        CacheText = new LocalizedString(l => CacheBytes > 0 ? l.T("activity.cache.size", ("size", l.Bytes(CacheBytes))) : l.T("activity.cache.empty"));
        foreach (var record in operations.History) History.Add(new HistoryItem(record));
        operations.History.CollectionChanged += OnHistoryChanged;
        operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationRunner.IsRunning))
            {
                OnPropertyChanged(nameof(IsIdle));
                if (!operations.IsRunning) RefreshCache();
            }
        };
    }

    public OperationRunner Operations { get; }

    public ObservableCollection<HistoryItem> History { get; } = [];

    public LocalizedString CacheText { get; }

    public bool IsIdle => !Operations.IsRunning;

    public bool HasHistory => History.Count > 0;

    public bool HasCache => CacheBytes > 0;

    partial void OnCacheBytesChanged(long value)
    {
        CacheText.Refresh();
        OnPropertyChanged(nameof(HasCache));
    }

    private string? CacheDirectory => _state.InstallPath is { } path ? InstallStateStore.CacheDirectory(path) : null;

    public override Task OnNavigatedToAsync()
    {
        RefreshCache();
        return Task.CompletedTask;
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        History.Clear();
        foreach (var record in Operations.History) History.Add(new HistoryItem(record));
        OnPropertyChanged(nameof(HasHistory));
    }

    private void RefreshCache()
    {
        var directory = CacheDirectory;
        CacheBytes = directory is null ? 0 : FileSystem.GetDirectorySize(directory);
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        var directory = CacheDirectory;
        if (directory is null || Operations.IsRunning) return;
        var confirmed = await _dialogs.ConfirmAsync(_loc.T("activity.cache.clearTitle"), _loc.T("activity.cache.clearBody"), _loc.T("activity.cache.clear")).ConfigureAwait(true);
        if (!confirmed) return;
        FileSystem.TryDeleteDirectory(directory);
        RefreshCache();
    }
}
