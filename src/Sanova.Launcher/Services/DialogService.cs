using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sanova.Core;
using Sanova.Launcher.Infrastructure;

namespace Sanova.Launcher.Services;

public enum Tone
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger,
}

public enum DialogButtonKind
{
    Primary,
    Secondary,
    Danger,
}

public sealed record DialogButton(string Text, object? Result, DialogButtonKind Kind = DialogButtonKind.Secondary)
{
    public bool IsPrimary => Kind == DialogButtonKind.Primary;

    public bool IsDanger => Kind == DialogButtonKind.Danger;
}

public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Title { get; init; }

    public required string Message { get; init; }

    public string? Details { get; init; }

    public Tone Tone { get; init; } = Tone.Info;

    public required IReadOnlyList<DialogButton> Buttons { get; init; }

    /// <summary>Result used when the dialog is dismissed with Escape.</summary>
    public object? CancelResult { get; init; }

    [ObservableProperty]
    private bool _showDetails;

    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);

    public bool IsWarning => Tone == Tone.Warning;
    public bool IsDanger => Tone == Tone.Danger;
    public bool IsSuccess => Tone == Tone.Success;
    public bool IsInfo => Tone is Tone.Info or Tone.Neutral;

    public Task<object?> Completion => _completion.Task;

    [RelayCommand]
    private void Choose(DialogButton button) => _completion.TrySetResult(button.Result);

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(CancelResult);

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;
}

/// <summary>In-window modal dialogs (consistent with the launcher's look, queued one at a time).</summary>
public sealed partial class DialogService : ObservableObject
{
    private readonly LocalizationService _loc;
    private readonly Queue<DialogViewModel> _queue = new();

    [ObservableProperty]
    private DialogViewModel? _current;

    public DialogService(LocalizationService loc)
    {
        _loc = loc;
    }

    public async Task<object?> ShowAsync(DialogViewModel dialog)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Current is null) Current = dialog;
            else _queue.Enqueue(dialog);
        });
        var result = await dialog.Completion.ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(() => Current = _queue.Count > 0 ? _queue.Dequeue() : null);
        return result;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false, string? details = null)
    {
        var result = await ShowAsync(new DialogViewModel
        {
            Title = title,
            Message = message,
            Details = details,
            Tone = danger ? Tone.Danger : Tone.Warning,
            Buttons =
            [
                new DialogButton(_loc.T("common.cancel"), false),
                new DialogButton(confirmText, true, danger ? DialogButtonKind.Danger : DialogButtonKind.Primary),
            ],
            CancelResult = false,
        }).ConfigureAwait(true);
        return result is true;
    }

    public Task AlertAsync(string title, string message, Tone tone = Tone.Info, string? details = null)
        => ShowAsync(new DialogViewModel
        {
            Title = title,
            Message = message,
            Details = details,
            Tone = tone,
            Buttons = [new DialogButton(_loc.T("common.ok"), null, DialogButtonKind.Primary)],
        });

    /// <summary>Explains a failure in the player's language, with technical details for bug reports.</summary>
    public Task ShowErrorAsync(string title, Exception error)
    {
        var (message, details) = DescribeError(_loc, error);
        return AlertAsync(title, message, Tone.Danger, details);
    }

    public static (string Message, string Details) DescribeError(LocalizationService loc, Exception error)
    {
        if (error is SanovaException sanova)
        {
            var key = $"error.{sanova.Code}";
            var args = sanova.Args.ToDictionary(
                a => a.Key,
                a => a.Value is long bytes && a.Key is "required" or "available" or "size" ? loc.Bytes(bytes) : a.Value);
            var message = loc.Localizer.HasKey(key) ? loc.T(key, args) : loc.T("error.unexpected");
            return (message, $"{sanova.Code}: {sanova.Message}" + (sanova.InnerException is { } inner ? $"\n{inner.GetType().Name}: {inner.Message}" : string.Empty));
        }
        return (loc.T("error.unexpected"), $"{error.GetType().Name}: {error.Message}");
    }
}

public sealed partial class ToastViewModel : ObservableObject
{
    public required string Title { get; init; }

    public string? Message { get; init; }

    public Tone Tone { get; init; } = Tone.Info;

    public string? ActionText { get; init; }

    public Action? Action { get; init; }

    public Action<ToastViewModel>? OnDismiss { get; init; }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasAction => ActionText is not null && Action is not null;
    public bool IsWarning => Tone == Tone.Warning;
    public bool IsDanger => Tone == Tone.Danger;
    public bool IsSuccess => Tone == Tone.Success;
    public bool IsInfo => Tone is Tone.Info or Tone.Neutral;

    [RelayCommand]
    private void Dismiss() => OnDismiss?.Invoke(this);

    [RelayCommand]
    private void Invoke()
    {
        Action?.Invoke();
        OnDismiss?.Invoke(this);
    }
}

/// <summary>Short-lived notifications in the corner of the window.</summary>
public sealed class NotificationService
{
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public void Show(Tone tone, string title, string? message = null, TimeSpan? duration = null, string? actionText = null, Action? action = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastViewModel
            {
                Title = title,
                Message = message,
                Tone = tone,
                ActionText = actionText,
                Action = action,
                OnDismiss = t => Toasts.Remove(t),
            };
            Toasts.Add(toast);
            while (Toasts.Count > 4) Toasts.RemoveAt(0);
            var lifetime = duration ?? (tone is Tone.Danger or Tone.Warning ? TimeSpan.FromSeconds(9) : TimeSpan.FromSeconds(5));
            DispatcherTimer.RunOnce(() => Toasts.Remove(toast), lifetime);
        });
    }
}
