using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Sanova.Launcher.ViewModels;
using Sanova.Launcher.Views;

namespace Sanova.Launcher.Infrastructure;

/// <summary>Maps page view models to their views (explicit, trimming-safe, no reflection).</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data) => data switch
    {
        HomeViewModel => new HomeView(),
        GameViewModel => new GameView(),
        ModsViewModel => new ModsView(),
        ActivityViewModel => new ActivityView(),
        UpdatesViewModel => new UpdatesView(),
        SettingsViewModel => new SettingsView(),
        DiagnosticsViewModel => new DiagnosticsView(),
        AboutViewModel => new AboutView(),
        SetupViewModel => new SetupView(),
        _ => new TextBlock { Text = data?.GetType().Name },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
