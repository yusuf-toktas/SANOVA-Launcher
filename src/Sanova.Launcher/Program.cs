using Avalonia;
using Avalonia.Media;
using Sanova.Launcher.Infrastructure;
using Serilog;
using Velopack;

namespace Sanova.Launcher;

internal static class Program
{
    public const string FontFamilyUri = "avares://SanovaLauncher/Assets/Fonts#Overpass";

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack must run first: it handles install/update/uninstall hooks and may exit the process.
        VelopackApp.Build().Run();

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.SignalExistingInstance();
            return 0;
        }

        LoggingSetup.Initialize();
        try
        {
            Log.Information("SANOVA Launcher {Version} starting", AppInfo.InformationalVersion);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled exception; the launcher is closing");
            LoggingSetup.WriteCrashReport(ex);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Also used by the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // DirectComposition first: the WinUI composition backend deadlocked during start-up on some
            // Windows builds, and the launcher does not use the acrylic/mica effects it provides.
            .With(new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface],
            })
            .With(new FontManagerOptions { DefaultFamilyName = FontFamilyUri })
            .LogToTrace();
}
