using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Sanova.Core.Settings;

namespace Sanova.Launcher.Services;

/// <summary>Light/dark/system theme switching.</summary>
public sealed class ThemeService
{
    public void Apply(AppTheme theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.System => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
    }
}

/// <summary>File and folder pickers, clipboard and window access through the main window.</summary>
public sealed class DesktopServices
{
    private static TopLevel? MainTopLevel =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window } ? window : null;

    public async Task<string?> PickFolderAsync(string title, string? startPath = null)
    {
        var top = MainTopLevel;
        if (top is null) return null;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
        {
            options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(startPath).ConfigureAwait(true);
        }
        var result = await top.StorageProvider.OpenFolderPickerAsync(options).ConfigureAwait(true);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string filterName, params string[] patterns)
    {
        var top = MainTopLevel;
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }],
        }).ConfigureAwait(true);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern)
    {
        var top = MainTopLevel;
        if (top is null) return null;
        var result = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = [pattern] }],
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        return result?.TryGetLocalPath();
    }

    public async Task CopyTextAsync(string text)
    {
        var clipboard = MainTopLevel?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    public void MinimizeMainWindow()
    {
        if (MainTopLevel is Window window) window.WindowState = WindowState.Minimized;
    }

    public void ActivateMainWindow()
    {
        if (MainTopLevel is not Window window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
