using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sanova.Core;
using Sanova.Core.IO;
using Sanova.Core.Settings;
using Sanova.Launcher.Infrastructure;

namespace Sanova.Launcher.Services;

public enum OperationKind
{
    Install,
    Update,
    Repair,
    Verify,
    ApplyComponents,
    Uninstall,
    BackupSaves,
    RestoreSaves,
    LauncherUpdate,
}

public enum OperationOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record OperationRecord(OperationKind Kind, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, OperationOutcome Outcome, string? ErrorCode, string? Note);

public sealed record OperationResult(OperationOutcome Outcome, Exception? Error)
{
    public bool Succeeded => Outcome == OperationOutcome.Succeeded;
}

/// <summary>
/// Runs one long operation at a time. Progress from worker threads is sampled ten times per
/// second onto the UI thread, where speed and time remaining are derived. Failures are
/// explained through the dialog service; every run is recorded in the activity history.
/// </summary>
public sealed partial class OperationRunner : ObservableObject
{
    private const int HistoryLimit = 50;

    private readonly LocalizationService _loc;
    private readonly DialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ILogger<OperationRunner> _logger;
    private readonly DispatcherTimer _sampler;
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private OperationProgress? _latest;
    private OperationStage? _sampledStage;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private OperationKind? _currentKind;

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private bool _isCancelling;

    public OperationRunner(LocalizationService loc, DialogService dialogs, AppPaths paths, ILogger<OperationRunner> logger)
    {
        _loc = loc;
        _dialogs = dialogs;
        _paths = paths;
        _logger = logger;
        Title = LocalizedString.Empty();
        StageText = LocalizedString.Empty();
        DetailText = LocalizedString.Empty();
        PercentText = LocalizedString.Empty();
        _sampler = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Sample());
        LoadHistory();
    }

    public LocalizedString Title { get; }

    public LocalizedString StageText { get; }

    /// <summary>"1.2 GB of 3.4 GB", "12 MB/s", "about 2 minutes left" composed for the current stage.</summary>
    public LocalizedString DetailText { get; }

    public LocalizedString PercentText { get; }

    public ObservableCollection<OperationRecord> History { get; } = [];

    public event EventHandler<OperationRecord>? Completed;

    public async Task<OperationResult> RunAsync(OperationKind kind, Func<IProgress<OperationProgress>, CancellationToken, Task> work,
        bool cancellable = true, bool showErrors = true, string? note = null)
    {
        if (IsRunning)
        {
            await _dialogs.AlertAsync(_loc.T("op.busyTitle"), _loc.T("op.busyBody"), Tone.Warning).ConfigureAwait(true);
            return new OperationResult(OperationOutcome.Cancelled, null);
        }

        var started = DateTimeOffset.UtcNow;
        _cancellation = new CancellationTokenSource();
        CurrentKind = kind;
        IsRunning = true;
        CanCancel = cancellable;
        IsCancelling = false;
        IsIndeterminate = true;
        Fraction = 0;
        _latest = new OperationProgress(OperationStage.Preparing);
        _sampledStage = null;
        _samples.Clear();
        Title.Set(l => l.T($"op.{Key(kind)}.running"));
        StageText.Set(l => l.T("stage.preparing"));
        DetailText.Set(_ => string.Empty);
        PercentText.Set(_ => string.Empty);
        _sampler.Start();
        _logger.LogInformation("Operation {Kind} started", kind);

        var progress = new InlineProgress<OperationProgress>(p => Volatile.Write(ref _latest, p));
        OperationOutcome outcome;
        Exception? error = null;
        var token = _cancellation.Token;
        try
        {
            await Task.Run(() => work(progress, token), token).ConfigureAwait(true);
            outcome = OperationOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = OperationOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            outcome = OperationOutcome.Failed;
            error = ex;
            if (ex is SanovaException sanova)
            {
                _logger.LogWarning(ex, "Operation {Kind} failed: {Code}", kind, sanova.Code);
            }
            else
            {
                _logger.LogError(ex, "Operation {Kind} failed unexpectedly", kind);
            }
        }
        finally
        {
            _sampler.Stop();
            IsRunning = false;
            CanCancel = false;
            CurrentKind = null;
            _cancellation.Dispose();
            _cancellation = null;
        }

        var record = new OperationRecord(kind, started, DateTimeOffset.UtcNow, outcome, (error as SanovaException)?.Code ?? (error is null ? null : ErrorCodes.Unexpected), note);
        History.Insert(0, record);
        while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
        SaveHistory();
        _logger.LogInformation("Operation {Kind} finished: {Outcome}", kind, outcome);
        Completed?.Invoke(this, record);

        if (outcome == OperationOutcome.Failed && showErrors && error is not null)
        {
            await _dialogs.ShowErrorAsync(_loc.T($"op.{Key(kind)}.failed"), error).ConfigureAwait(true);
        }
        return new OperationResult(outcome, error);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cancellation is null || !CanCancel) return;
        IsCancelling = true;
        StageText.Set(l => l.T("stage.cancelling"));
        _cancellation.Cancel();
    }

    public static string Key(OperationKind kind) => kind switch
    {
        OperationKind.Install => "install",
        OperationKind.Update => "update",
        OperationKind.Repair => "repair",
        OperationKind.Verify => "verify",
        OperationKind.ApplyComponents => "components",
        OperationKind.Uninstall => "uninstall",
        OperationKind.BackupSaves => "backup",
        OperationKind.RestoreSaves => "restore",
        _ => "launcherUpdate",
    };

    public static string StageKey(OperationStage stage) => stage switch
    {
        OperationStage.BackingUpSaves => "stage.backingUpSaves",
        OperationStage.CopyingGameFiles => "stage.copying",
        OperationStage.Downloading => "stage.downloading",
        OperationStage.Extracting => "stage.extracting",
        OperationStage.Applying => "stage.applying",
        OperationStage.Verifying => "stage.verifying",
        OperationStage.CleaningUp => "stage.cleaningUp",
        OperationStage.RollingBack => "stage.rollingBack",
        OperationStage.Removing => "stage.removing",
        OperationStage.Completed => "stage.completed",
        _ => "stage.preparing",
    };

    private void Sample()
    {
        var progress = Volatile.Read(ref _latest);
        if (progress is null || IsCancelling) return;

        if (_sampledStage != progress.Stage)
        {
            _sampledStage = progress.Stage;
            _samples.Clear();
            var stageKey = StageKey(progress.Stage);
            StageText.Set(l => l.T(stageKey));
        }

        var fraction = progress.Fraction;
        IsIndeterminate = fraction is null;
        Fraction = (fraction ?? 0) * 100;
        var percent = fraction;
        PercentText.Set(l => percent is { } f ? l.T("op.percent", ("value", (int)Math.Floor(f * 100))) : string.Empty);

        double speed = 0;
        if (progress.BytesTotal > 0)
        {
            var now = Stopwatch.GetTimestamp();
            _samples.Enqueue((now, progress.BytesDone));
            while (_samples.Count > 2 && Stopwatch.GetElapsedTime(_samples.Peek().Ticks, now) > TimeSpan.FromSeconds(5)) _samples.Dequeue();
            var (firstTicks, firstBytes) = _samples.Peek();
            var elapsed = Stopwatch.GetElapsedTime(firstTicks, now).TotalSeconds;
            if (elapsed > 0.5) speed = Math.Max(0, (progress.BytesDone - firstBytes) / elapsed);
        }

        var snapshot = progress;
        var bytesPerSecond = speed;
        DetailText.Set(l => ComposeDetail(l, snapshot, bytesPerSecond));
    }

    private static string ComposeDetail(LocalizationService l, OperationProgress p, double speed)
    {
        var parts = new List<string>();
        if (p.BytesTotal > 0)
        {
            parts.Add(l.T("op.bytesOf", ("done", l.Bytes(p.BytesDone)), ("total", l.Bytes(p.BytesTotal))));
            if (speed > 1024 && p.Stage is OperationStage.Downloading or OperationStage.CopyingGameFiles or OperationStage.Extracting or OperationStage.Verifying)
            {
                parts.Add(l.Speed(speed));
                var remaining = TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / speed);
                if (remaining > TimeSpan.FromSeconds(3)) parts.Add(l.T("op.remaining", ("time", l.Duration(remaining))));
            }
        }
        else if (p.ItemsTotal > 0)
        {
            parts.Add(l.T("op.itemsOf", ("done", p.ItemsDone), ("total", p.ItemsTotal)));
        }
        return string.Join("   ", parts);
    }

    private sealed record HistoryFile(List<OperationRecord> Records);

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_paths.HistoryFile)) return;
            var file = AtomicFile.ReadJson<HistoryFile>(_paths.HistoryFile);
            foreach (var record in file?.Records.Take(HistoryLimit) ?? []) History.Add(record);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Activity history is unreadable and was reset");
        }
    }

    private void SaveHistory()
    {
        try
        {
            AtomicFile.WriteJson(_paths.HistoryFile, new HistoryFile(History.ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save activity history");
        }
    }
}
