using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.ViewModels;

namespace Sanova.Launcher.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// Closing during an install or update asks first, then cancels cleanly (the transaction rolls
    /// back and downloads stay resumable) before the window actually closes.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && DataContext is MainViewModel { Operations.IsRunning: true } shell)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync(shell);
        }
        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync(MainViewModel shell)
    {
        var loc = LocalizationService.Instance;
        if (loc is null) return;
        var confirmed = await shell.Dialogs.ConfirmAsync(loc.T("dialog.closeBusy.title"),
            loc.T(shell.Operations.CanCancel ? "dialog.closeBusy.body" : "dialog.closeBusy.wait"), loc.T("dialog.closeBusy.confirm")).ConfigureAwait(true);
        if (!confirmed) return;

        if (shell.Operations.CanCancel) shell.Operations.CancelCommand.Execute(null);
        while (shell.Operations.IsRunning)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }
        _closeConfirmed = true;
        Close();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && this.FindControl<PathIcon>("MaximizeIcon") is { } icon)
        {
            var key = WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize";
            if (this.TryFindResource(key, out var geometry) && geometry is Geometry data) icon.Data = data;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel { Dialogs.Current: { } dialog })
        {
            dialog.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
