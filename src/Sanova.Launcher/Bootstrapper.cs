using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sanova.Core.Diagnostics;
using Sanova.Core.Downloads;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Core.Settings;
using Sanova.Core.Updates;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;
using Sanova.Launcher.ViewModels;

namespace Sanova.Launcher;

/// <summary>Composition root: wires the engine and the UI services together.</summary>
public static class Bootstrapper
{
    /// <param name="overrides">Optional replacements applied last (used by UI tests for deterministic environments).</param>
    public static ServiceProvider Build(AppPaths paths, ILoggerFactory loggers, Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggers);
        services.AddLogging();
        services.Replace(ServiceDescriptor.Singleton(loggers));

        // Engine
        services.AddSingleton(paths);
        services.AddSingleton(sp =>
        {
            var store = new SettingsStore(paths, sp.GetRequiredService<ILogger<SettingsStore>>());
            store.Load();
            return store;
        });
        services.AddSingleton(_ =>
        {
            using var stream = typeof(Bootstrapper).Assembly.GetManifestResourceStream("Sanova.Launcher.distribution.json")
                ?? throw new InvalidOperationException("distribution.json is missing from the build.");
            return DistributionConfig.Load(stream);
        });
        services.AddSingleton(sp => new ApiHttp(SanovaHttp.CreateApiClient(AppInfo.UserAgent)));
        services.AddSingleton(sp => new DownloadHttp(SanovaHttp.CreateDownloadClient(AppInfo.UserAgent)));
        services.AddSingleton<BandwidthThrottle>();
        services.AddSingleton(sp => new ResumableDownloader(sp.GetRequiredService<DownloadHttp>().Client, sp.GetRequiredService<BandwidthThrottle>(),
            sp.GetRequiredService<ILogger<ResumableDownloader>>()));
        services.AddSingleton(sp => new ContentAcquirer(sp.GetRequiredService<ResumableDownloader>(), sp.GetRequiredService<DownloadHttp>().Client,
            sp.GetRequiredService<BandwidthThrottle>(), sp.GetRequiredService<ILogger<ContentAcquirer>>()));
        services.AddSingleton<BaseGameCopier>();
        services.AddSingleton<TransactionEngine>();
        services.AddSingleton(sp => new InstallationService(sp.GetRequiredService<ContentAcquirer>(), sp.GetRequiredService<BaseGameCopier>(),
            sp.GetRequiredService<TransactionEngine>(), sp.GetRequiredService<ILogger<InstallationService>>(), AppInfo.Version));
        services.AddSingleton(_ => GameVersionDatabase.LoadEmbedded());
        services.AddSingleton(sp => new CompatibilityChecker(sp.GetRequiredService<GameVersionDatabase>(), BaseGameReference.LoadEmbedded(),
            sp.GetRequiredService<ILogger<CompatibilityChecker>>()));
        services.AddSingleton<IRegistryReader>(_ => OperatingSystem.IsWindows() ? new WindowsRegistryReader() : new NullRegistryReader());
        services.AddSingleton(sp => new GameLocator(sp.GetRequiredService<IRegistryReader>(), sp.GetRequiredService<ILogger<GameLocator>>()));
        services.AddSingleton<GameLauncher>();
        services.AddSingleton(sp => new GameProcessMonitor(sp.GetRequiredService<ILogger<GameProcessMonitor>>()));
        services.AddSingleton(sp => new SaveGameService(paths, sp.GetRequiredService<ILogger<SaveGameService>>()));
        services.AddSingleton(sp => new GitHubReleasesClient(sp.GetRequiredService<ApiHttp>().Client, paths.HttpCacheDirectory, sp.GetRequiredService<ILogger<GitHubReleasesClient>>()));
        services.AddSingleton(sp => new ContentUpdateService(sp.GetRequiredService<ApiHttp>().Client, paths, sp.GetRequiredService<GitHubReleasesClient>(),
            sp.GetRequiredService<ILogger<ContentUpdateService>>()));
        services.AddSingleton(sp => new DiagnosticsService(paths, sp.GetRequiredService<CompatibilityChecker>(), sp.GetRequiredService<GameProcessMonitor>(),
            sp.GetRequiredService<ApiHttp>().Client, sp.GetRequiredService<ILogger<DiagnosticsService>>()));

        // UI services
        services.AddSingleton(_ => LocalizationService.Create(paths, loggers));
        services.AddSingleton<ThemeService>();
        services.AddSingleton<DesktopServices>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<OperationRunner>();
        services.AddSingleton<LauncherUpdater>();
        services.AddSingleton<AppState>();
        services.AddSingleton<LauncherActions>();

        // View models
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<GameViewModel>();
        services.AddSingleton<ModsViewModel>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddTransient<SetupViewModel>();

        overrides?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static void Replace(this IServiceCollection services, ServiceDescriptor descriptor)
    {
        var existing = services.Where(d => d.ServiceType == descriptor.ServiceType).ToList();
        foreach (var item in existing) services.Remove(item);
        services.Add(descriptor);
    }
}

/// <summary>HttpClient for APIs and manifests.</summary>
public sealed record ApiHttp(HttpClient Client);

/// <summary>HttpClient for package downloads.</summary>
public sealed record DownloadHttp(HttpClient Client);
