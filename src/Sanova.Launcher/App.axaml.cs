using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Sanova.Core.Game;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.ViewModels;
using Sanova.Launcher.Views;
using Serilog;

namespace Sanova.Launcher;

public partial class App : Application
{
    private ServiceProvider? _services;

    public static IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = Bootstrapper.Build(LoggingSetup.Paths, LoggingSetup.CreateFactory());
            Services = _services;

            var shell = _services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.ShutdownRequested += (_, _) => Log.Information("Launcher closing");
            desktop.Exit += (_, _) => _services.Dispose();
            _ = shell.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
